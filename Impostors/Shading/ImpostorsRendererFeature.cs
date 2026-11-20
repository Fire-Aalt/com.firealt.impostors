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
        private const string NO_ADDITIONAL_LIGHTS_KEYWORD = "_IMPOSTOR_NO_ADDITIONAL_LIGHTS";
        private static readonly int ShadowStrength = Shader.PropertyToID("_ImpostorMainLightShadowStrength");

        private static Camera _cullingCamera;
        private static RenderPipelineAsset _cullingPipeline;
        private static Vector3 _cullingOrigin;
        private static float _cullingDistance;

        [Tooltip("Resolution of the impostor shading buffer relative to the camera. 1 renders impostors natively in "
            + "URP's opaque pass. Impostors crossfading with a mesh LOD are always native.")]
        [Range(.1f, 1)]
        public float RenderScale = 1;

        [Tooltip("Contrast-adaptive sharpening applied while upscaling the impostor buffer. Has no effect at Render Scale 1.")]
        [Range(0, 1)]
        public float Sharpness = .5f;

        [Tooltip("Antialiases the impostor buffer with 4x MSAA and alpha-to-coverage. Removes stair-stepping on "
            + "silhouettes at a small GPU cost.")]
        public bool Msaa = true;

        [SerializeField]
        [HideInInspector]
        private Shader _compositeShader;

        private GlobalKeyword _noAdditionalLights;
        private Material _composite;
        private OffscreenPass _offscreenPass;

        public override void Create()
        {
            _noAdditionalLights = GlobalKeyword.Create(NO_ADDITIONAL_LIGHTS_KEYWORD);
            if (!_compositeShader)
            {
                _compositeShader = Shader.Find(COMPOSITE_SHADER_NAME);
            }

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
            LightData lights = renderingData.lightData;
            Light main = lights.mainLightIndex >= 0 ? lights.visibleLights[lights.mainLightIndex].light : null;
            // URP enables its Forward+ loop for a sun-only camera too. Select a
            // cheaper stock-PBR variant only when that loop has no work to do.
            Shader.SetKeyword(_noAdditionalLights, lights.additionalLightsCount == 0);
            // One global per camera, shared by every tree. No material updates or extra rendering pass.
            Shader.SetGlobalFloat(ShadowStrength, main ? Mathf.Clamp01(main.shadowStrength) : 0);

            bool offscreen = UsesOffscreenShading(renderingData.cameraData);
            ImpostorWorldRenderer.SetOffscreenShading(offscreen);
            if (!offscreen)
            {
                return;
            }

            if (!_composite)
            {
                _composite = CoreUtils.CreateEngineMaterial(_compositeShader);
            }

            _offscreenPass.Setup(RenderScale, Sharpness, Msaa, _composite);
            renderer.EnqueuePass(_offscreenPass);
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

        private bool UsesOffscreenShading(in CameraData cameraData)
        {
            CameraType type = cameraData.cameraType;
            return RenderScale < NATIVE_SCALE && _compositeShader && type is CameraType.Game or CameraType.SceneView
                && !IsDebugViewActive(cameraData.camera);
        }
    }
}
