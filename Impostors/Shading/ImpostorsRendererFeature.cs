using Impostors.Data;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Impostors
{
    /// <summary>
    /// Supplies impostor lighting state and optionally shades impostors into a smaller buffer,
    /// which is composited with a depth test so mesh LODs stay at native resolution.
    /// Impostors inside a LOD fade band stay native so their dither complements the mesh LOD's.
    /// </summary>
    [NoAutoStaticsCleanup]
    public sealed partial class ImpostorsRendererFeature : ScriptableRendererFeature
    {
        private const float NATIVE_SCALE = .999f;
        private const string COMPOSITE_SHADER_NAME = "Hidden/Impostors/OffscreenComposite";
        private static readonly int ShadowStrength = Shader.PropertyToID("_ImpostorMainLightShadowStrength");

        private static Camera _cullingCamera;
        private static RenderPipelineAsset _cullingPipeline;
        private static Vector3 _cullingOrigin;
        private static float _cullingDistance;

        [Tooltip("Resolution of the impostor shading buffer relative to the camera. 1 renders impostors natively in "
            + "URP's opaque pass. Impostors crossfading with a mesh LOD are always native. While URP primes depth, the buffer "
            + "is composited through a stencil mask over native impostor depth, which needs a depth attachment with stencil.")]
        [Range(.1f, 1)]
        public float RenderScale = 1;

        [Tooltip("Bit of URP's user stencil bits (0-3) that marks visible impostors for the offscreen composite while URP "
            + "primes depth. Only written when Render Scale is below 1.")]
        [Range(0, 3)]
        public int PrimedStencilBit = 3;

        [Tooltip("Contrast-adaptive sharpening applied while upscaling the impostor buffer. Has no effect at Render Scale 1.")]
        [Range(0, 1)]
        public float Sharpness = .5f;

        [Tooltip("Antialiases the impostor buffer with 4x MSAA and alpha-to-coverage. Removes stair-stepping on "
            + "silhouettes at a small GPU cost.")]
        public bool Msaa = true;

        [Tooltip("Draws impostor depth after URP's prepass instead of inside it, skipping pixels hidden behind opaque "
            + "geometry. Impostors write per-pixel depth, so without this hidden impostors are fully shaded wherever the "
            + "graphics API loses conservative depth (e.g. Vulkan).")]
        public bool OccludedDepthRejection = true;

        [Tooltip("Edge length in meters of the square X/Z cells that group impostor instances into chunks. Chunks are "
            + "culled and depth sorted as a whole, so smaller cells cull and order more tightly at the cost of more chunks "
            + "and draw commands. Shared by every impostor renderer; changing it regroups all instances.")]
        [Min(ImpostorSpatialCell.MINIMUM_TILE_SIZE)]
        public float SpatialCellSize = ImpostorSpatialCell.DEFAULT_TILE_SIZE;

        [Tooltip("Splits the impostor depth pass into this many distance bands drawn near to far. Each band skips quads "
            + "hidden behind the nearer bands, at the cost of one depth copy per band. 1 draws all impostors at once.")]
        [Range(1, 8)]
        public int DepthBands = 1;

        [Tooltip("Experimental. Culls instances hidden behind the previous frames' depth of the Game camera. Saves every "
            + "camera pass for hidden trees, but a tree revealed by fast camera motion can appear one to three frames late.")]
        public bool OcclusionCulling;

        [Tooltip("Plane count of the convex hull each impostor is drawn as. Low is about 44 triangles, Medium 84 and High "
            + "300 for a conifer; each step down covers a few percent more empty pixels. Applies to renderers created "
            + "afterwards, for example on entering Play Mode.")]
        public ImpostorHullDetail HullDetail = ImpostorHullDetail.Medium;

        [SerializeField]
        [HideInInspector]
        private Shader _compositeShader;

        private GlobalKeyword _noAdditionalLights;
        private Material _composite;
        private OffscreenPass _offscreenPass;
        private PrepassHandoffPass _handoffPass;
        private DepthPass _depthPass;
        private SceneDepthResetPass _resetPass;
        private ComputeShader _occlusionShader;

        public override void Create()
        {
            ApplyGlobalSettings();
            _occlusionShader = Resources.Load<ComputeShader>("ImpostorOcclusion");
            _noAdditionalLights = GlobalKeyword.Create(ImpostorShaderKeywords.NO_ADDITIONAL_LIGHTS);
            if (!_compositeShader)
            {
                _compositeShader = Shader.Find(COMPOSITE_SHADER_NAME);
            }

            _handoffPass = new PrepassHandoffPass { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses };
            _depthPass = new DepthPass { renderPassEvent = RenderPassEvent.AfterRenderingPrePasses };
            _resetPass = new SceneDepthResetPass { renderPassEvent = RenderPassEvent.AfterRenderingOpaques };
            _offscreenPass = new OffscreenPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques,
                requiresIntermediateTexture = true,
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            _cullingCamera = cameraData.camera;
            _cullingPipeline = GraphicsSettings.currentRenderPipeline;
            _cullingOrigin = cameraData.worldSpaceCameraPos;
            _cullingDistance = cameraData.maxShadowDistance;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            ApplyGlobalSettings();
            bool occlusion = OcclusionCulling && _occlusionShader && SystemInfo.supportsAsyncGPUReadback;
            if (ImpostorOcclusion.Enabled && !occlusion)
            {
                ImpostorOcclusion.Invalidate();
            }

            ImpostorOcclusion.Enabled = occlusion;
            LightData lights = renderingData.lightData;
            Light main = lights.mainLightIndex >= 0 ? lights.visibleLights[lights.mainLightIndex].light : null;
            // URP enables its Forward+ loop for a sun-only camera too. Select a
            // cheaper stock-PBR variant only when that loop has no work to do.
            Shader.SetKeyword(_noAdditionalLights, lights.additionalLightsCount == 0);
            // One global per camera, shared by every tree. No material updates or extra rendering pass.
            Shader.SetGlobalFloat(ShadowStrength, main ? Mathf.Clamp01(main.shadowStrength) : 0);

            bool mayPrime = MayPrime(renderer, renderingData.cameraData);
            bool offscreen = UsesOffscreenShading(renderingData.cameraData);
            ImpostorWorldRenderer.SetOffscreenShading(offscreen);
            bool ownDepth = UsesOwnedDepth(renderingData.cameraData);
            if (!offscreen && !ownDepth && !mayPrime)
            {
                ImpostorWorldRenderer.SetPrimedStencilBit(0);
                return;
            }

            if (!_composite && (offscreen || ownDepth))
            {
                _composite = CoreUtils.CreateEngineMaterial(_compositeShader);
            }

            int stencilBit = 1 << Mathf.Clamp(PrimedStencilBit, 0, 3);
            _handoffPass.Setup(ownDepth, mayPrime, offscreen, stencilBit);
            renderer.EnqueuePass(_handoffPass);
            if (ownDepth)
            {
                _depthPass.Setup(_handoffPass, _composite, DepthBands, OcclusionCulling ? _occlusionShader : null);
                renderer.EnqueuePass(_depthPass);
            }

            if (offscreen)
            {
                _offscreenPass.Setup(_handoffPass, RenderScale, Sharpness, Msaa, _composite, stencilBit);
                renderer.EnqueuePass(_offscreenPass);
            }

            renderer.EnqueuePass(_resetPass);
        }

        private void ApplyGlobalSettings()
        {
            ImpostorWorldRenderer.HullDetail = HullDetail;
            ImpostorSpatialCell.TileSize = Mathf.Max(ImpostorSpatialCell.MINIMUM_TILE_SIZE, SpatialCellSize);
        }

        /// <summary>Returns the shadow distance of the camera now being culled, or the pipeline's when it is unknown.</summary>
        internal static float ShadowCullingDistance(Vector3 origin)
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (_cullingCamera && _cullingPipeline == pipeline && _cullingOrigin == origin)
            {
                return Mathf.Max(0, _cullingDistance);
            }

            // Also bound casting when this renderer has no package feature. The feature
            // supplies URP's exact per-camera limit, including the far clip and overrides.
            return pipeline is UniversalRenderPipelineAsset urp ? Mathf.Max(0, urp.shadowDistance) : float.PositiveInfinity;
        }

        protected override void Dispose(bool disposing)
        {
            ImpostorWorldRenderer.SetOffscreenShading(false);
            ImpostorWorldRenderer.SetPrimedStencilBit(0);
            CoreUtils.Destroy(_composite);
        }

        // URP applies Rendering Debugger overrides (overdraw, material and lighting views) only in its own passes,
        // so impostors must stay in the native path while one is active. Scene View draw modes work the same way.
        private static bool IsDebugViewActive(Camera camera)
        {
#if UNITY_ENABLE_CHECKS || UNITY_EDITOR
            if (UniversalRenderPipelineDebugDisplaySettings.Instance.AreAnySettingsActive)
            {
                return true;
            }
#endif
#if UNITY_EDITOR
            if (camera.cameraType != CameraType.SceneView)
            {
                return false;
            }

            foreach (UnityEditor.SceneView view in UnityEditor.SceneView.sceneViews)
            {
                if (view.camera == camera)
                {
                    return view.cameraMode.drawMode != UnityEditor.DrawCameraMode.Textured;
                }
            }
#endif
            return false;
        }

        private bool UsesOwnedDepth(in CameraData cameraData)
        {
            CameraType type = cameraData.cameraType;
            return OccludedDepthRejection && _compositeShader && type is CameraType.Game or CameraType.SceneView
                && !IsDebugViewActive(cameraData.camera);
        }

        private bool UsesOffscreenShading(in CameraData cameraData)
        {
            CameraType type = cameraData.cameraType;
            return RenderScale < NATIVE_SCALE && _compositeShader && type is CameraType.Game or CameraType.SceneView
                && !IsDebugViewActive(cameraData.camera);
        }
    }
}
