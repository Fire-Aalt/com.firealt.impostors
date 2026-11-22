using Impostors.Data;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Impostors
{
    public sealed partial class ImpostorsRendererFeature
    {
        private static readonly int OwnsPrepass = Shader.PropertyToID("_ImpostorOwnsPrepass");
        private static readonly int SceneDepthTest = Shader.PropertyToID("_ImpostorSceneDepthTest");
        private static readonly int DepthPrimed = Shader.PropertyToID("_ImpostorDepthPrimed");

        private static System.Func<ScriptableRenderer, bool> _useDepthPriming;
        private static bool _useDepthPrimingResolved;

        private enum PrepassOwnership
        {
            None,

            /// <summary>URP's prepass draws into a depth-format camera depth texture, which the package extends.</summary>
            Classic,

            /// <summary>URP primes the camera depth attachment and copies it to the depth texture afterwards.</summary>
            Primed,
        }

        private static bool R32Renderable => SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Render);

        /// <summary>Whether URP's prepass renders straight into a depth-format camera depth texture the package can extend.</summary>
        private static bool CanOwnPrepass(RenderGraph graph, UniversalResourceData resources)
        {
            if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid())
            {
                return false;
            }

            // URP gives the depth texture a depth format only when its forward prepass draws into it directly
            // (no deferred, no depth priming, no copy), unless the platform cannot render to R32_SFloat at all.
            return R32Renderable && GraphicsFormatUtility.IsDepthFormat(graph.GetTextureDesc(resources.cameraDepthTexture).format);
        }

        /// <summary>Whether impostor depth can be added to a primed depth attachment and copied on to the depth texture.</summary>
        private static bool CanOwnPrimedPrepass(RenderGraph graph, UniversalResourceData resources)
        {
            return !resources.isActiveTargetBackBuffer && resources.activeDepthTexture.IsValid() && R32Renderable
                && GraphicsFormatUtility.IsDepthFormat(graph.GetTextureDesc(resources.activeDepthTexture).format);
        }

        private static bool HasStencil(RenderGraph graph, UniversalResourceData resources)
        {
            return !resources.isActiveTargetBackBuffer && resources.activeDepthTexture.IsValid()
                && GraphicsFormatUtility.IsStencilFormat(graph.GetTextureDesc(resources.activeDepthTexture).format);
        }

        /// <summary>Whether URP primes the depth attachment for this camera. Valid from its prepass events on.</summary>
        private static bool IsPrimed(UniversalCameraData camera)
        {
            if (!_useDepthPrimingResolved)
            {
                _useDepthPrimingResolved = true;
                System.Reflection.MethodInfo getter = typeof(ScriptableRenderer).GetProperty("useDepthPriming",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetGetMethod(true);
                if (getter != null)
                {
                    _useDepthPriming = (System.Func<ScriptableRenderer, bool>)System.Delegate.CreateDelegate(
                        typeof(System.Func<ScriptableRenderer, bool>), getter);
                }
                else
                {
                    Debug.LogWarning("Impostors: URP no longer exposes ScriptableRenderer.useDepthPriming, so impostors render "
                        + "as if depth priming were off. Disable depth priming on the renderer if trees lose pixels.");
                }
            }

            return _useDepthPriming != null && camera.renderer != null && _useDepthPriming(camera.renderer);
        }

        /// <summary>
        /// Predicts URP's priming decision, which it makes after the render passes are added, so the depth texture can be
        /// requested early enough. A false positive costs a depth copy; a false negative keeps the unprimed shading.
        /// </summary>
        private static bool MayPrime(ScriptableRenderer renderer, in CameraData camera)
        {
            return renderer is UniversalRenderer { depthPrimingMode: not DepthPrimingMode.Disabled }
                && camera.cameraTargetDescriptor.msaaSamples == 1
                && (camera.renderType == CameraRenderType.Base || camera.clearDepth);
        }

        /// <summary>
        /// Runs before URP's prepass and decides whether impostors leave it. Impostors write per-pixel depth, which
        /// costs hardware early-Z wherever conservative depth is unavailable, so inside URP's prepass every hidden
        /// impostor fragment was fully reconstructed. Also tells forward impostors whether URP primes depth.
        /// </summary>
        [NoAutoStaticsCleanup]
        private sealed class PrepassHandoffPass : ScriptableRenderPass
        {
            private const string PASS_NAME = "Impostors Prepass Handoff";

            private static bool _stencilWarningLogged;

            private bool _ownDepth;
            private bool _offscreen;
            private int _stencilBit;

            public PrepassOwnership Owned { get; private set; }

            /// <summary>Whether impostors are shaded into the offscreen buffer for this camera.</summary>
            public bool OffscreenActive { get; private set; }

            /// <summary>Whether the offscreen buffer is composited through the primed stencil mask.</summary>
            public bool PrimedComposite { get; private set; }

            public void Setup(bool ownDepth, bool mayPrime, bool offscreen, int stencilBit)
            {
                _ownDepth = ownDepth;
                _offscreen = offscreen;
                _stencilBit = stencilBit;
                // Primed forward impostors read the depth texture in URP's opaque pass, so its copy must follow the prepass.
                ConfigureInput(mayPrime ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                bool primed = IsPrimed(frameData.Get<UniversalCameraData>()) && resources.cameraDepthTexture.IsValid();
                Owned = !_ownDepth ? PrepassOwnership.None
                    : primed ? CanOwnPrimedPrepass(graph, resources) ? PrepassOwnership.Primed : PrepassOwnership.None
                    : CanOwnPrepass(graph, resources) ? PrepassOwnership.Classic : PrepassOwnership.None;

                OffscreenActive = _offscreen;
                PrimedComposite = _offscreen && primed && HasStencil(graph, resources);
                if (_offscreen && primed && !PrimedComposite)
                {
                    OffscreenActive = false;
                    if (!_stencilWarningLogged)
                    {
                        _stencilWarningLogged = true;
                        Debug.LogWarning("Impostors: Render Scale below 1 needs a depth attachment with stencil while URP primes "
                            + "depth, so impostors render at native resolution. Use a depth-stencil Depth Attachment Format.");
                    }
                }

                // Material pass state is read when URP builds its renderer lists, after this camera's passes are recorded.
                ImpostorWorldRenderer.SetOffscreenShading(OffscreenActive);
                ImpostorWorldRenderer.SetPrimedStencilBit(PrimedComposite ? _stencilBit : 0);

                using IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass(PASS_NAME, out HandoffData data);
                data.Owned = Owned != PrepassOwnership.None ? 1 : 0;
                data.Primed = primed ? 1 : 0;
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (HandoffData pass, UnsafeGraphContext context) =>
                {
                    context.cmd.SetGlobalFloat(OwnsPrepass, pass.Owned);
                    context.cmd.SetGlobalFloat(DepthPrimed, pass.Primed);
                });
            }

            private sealed class HandoffData
            {
                public float Owned;
                public float Primed;
            }
        }

        /// <summary>
        /// Draws impostor depth (and normals) after URP's prepass. The opaque-only depth is copied first, so the shader
        /// can skip quads hidden behind opaque geometry before reconstructing them.
        /// </summary>
        [NoAutoStaticsCleanup]
        private sealed class DepthPass : ScriptableRenderPass
        {
            private const string COPY_PASS_NAME = "Impostors Copy Opaque Depth";
            private const string REFRESH_PASS_NAME = "Impostors Refresh Depth Texture";
            private const string DRAW_PASS_NAME = "Impostors Depth";
            private const string OPAQUE_DEPTH_NAME = "_ImpostorOpaqueDepth";
            private const int COPY_SHADER_PASS = 1;

            private static readonly ShaderTagId DepthTag = new(ImpostorShaderPasses.PREPASS_DEPTH_LIGHT_MODE);
            private static readonly ShaderTagId DepthNormalsTag = new(ImpostorShaderPasses.PREPASS_DEPTH_NORMALS_LIGHT_MODE);
            private static readonly int OpaqueDepth = Shader.PropertyToID(OPAQUE_DEPTH_NAME);
            private static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            private static readonly int CameraNormalsTexture = Shader.PropertyToID("_CameraNormalsTexture");

            private const string OCCLUSION_PASS_NAME = "Impostors Occlusion Depth";
            private const string READBACK_PASS_NAME = "Impostors Occlusion Readback";
            private static readonly int OcclusionSource = Shader.PropertyToID("_Source");
            private static readonly int OcclusionTarget = Shader.PropertyToID("_Target");
            private static readonly int OcclusionSourceSize = Shader.PropertyToID("_SourceSize");
            private static readonly int OcclusionTile = Shader.PropertyToID("_Tile");
            private static readonly int OcclusionReversedZ = Shader.PropertyToID("_ReversedZ");

            private PrepassHandoffPass _handoff;
            private Material _copy;
            private int _bands;
            private ComputeShader _occlusion;

            public void Setup(PrepassHandoffPass handoff, Material copy, int bands, ComputeShader occlusion)
            {
                _handoff = handoff;
                _copy = copy;
                _bands = Mathf.Clamp(bands, 1, ImpostorLodSelection.DEPTH_BUCKET_COUNT);
                _occlusion = occlusion;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                if (_handoff.Owned == PrepassOwnership.None)
                {
                    return;
                }

                var resources = frameData.Get<UniversalResourceData>();
                var camera = frameData.Get<UniversalCameraData>();
                var rendering = frameData.Get<UniversalRenderingData>();
                var lights = frameData.Get<UniversalLightData>();
                bool primed = _handoff.Owned == PrepassOwnership.Primed;
                // A primed prepass fills the depth attachment, and URP has already copied it, opaques only, to the texture.
                TextureHandle depth = primed ? resources.activeDepthTexture : resources.cameraDepthTexture;
                // In forward paths the normals texture exists only when URP's prepass writes it.
                TextureHandle normals = resources.cameraNormalsTexture;
                bool writeNormals = normals.IsValid();

                TextureHandle opaqueDepth = TextureHandle.nullHandle;
                if (!primed || _bands > 1)
                {
                    TextureDesc copyDescriptor = graph.GetTextureDesc(depth);
                    copyDescriptor.name = OPAQUE_DEPTH_NAME;
                    copyDescriptor.format = GraphicsFormat.R32_SFloat;
                    copyDescriptor.msaaSamples = MSAASamples.None;
                    copyDescriptor.bindTextureMS = false;
                    copyDescriptor.clearBuffer = false;
                    copyDescriptor.filterMode = FilterMode.Point;
                    opaqueDepth = graph.CreateTexture(copyDescriptor);
                }

                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(writeNormals ? DepthNormalsTag : DepthTag,
                    rendering, camera, lights, SortingCriteria.CommonOpaque);
                drawing.perObjectData = PerObjectData.None;
                // Camera draw ranges carry their depth bucket as a rendering layer, so bands draw near to far. Each band
                // tests against a fresh copy holding the nearer bands, so hidden impostor layers skip reconstruction too.
                for (int band = 0; band < _bands; band++)
                {
                    int bucketCount = ImpostorLodSelection.DEPTH_BUCKET_COUNT;
                    uint layers = _bands == 1 ? uint.MaxValue
                        : ImpostorLodSelection.DepthBucketLayers(band * bucketCount / _bands, (band + 1) * bucketCount / _bands);
                    var filtering = new FilteringSettings(RenderQueueRange.opaque, -1, layers);
                    TextureHandle bandDepth = opaqueDepth;
                    if (primed && band == 0)
                    {
                        bandDepth = resources.cameraDepthTexture;
                    }
                    else
                    {
                        AddCopyPass(graph, depth, opaqueDepth, false);
                    }

                    AddDrawPass(graph, new RendererListParams(rendering.cullResults, drawing, filtering), depth,
                        writeNormals ? normals : TextureHandle.nullHandle, bandDepth, band == _bands - 1, !primed);
                }

                if (primed)
                {
                    // URP's opaque pass reads the impostor depth from the texture while the attachment stays bound.
                    AddCopyPass(graph, depth, resources.cameraDepthTexture, true);
                }

                if (_occlusion && camera.cameraType == CameraType.Game)
                {
                    AddOcclusionCapture(graph, camera, resources.cameraDepthTexture);
                }
            }

            /// <summary>Reduces this frame's depth to one farthest value per tile and reads it back for later culling.</summary>
            private void AddOcclusionCapture(RenderGraph graph, UniversalCameraData camera, TextureHandle depth)
            {
                TextureDesc source = graph.GetTextureDesc(depth);
                int width = (source.width + ImpostorOcclusion.TILE - 1) / ImpostorOcclusion.TILE;
                int height = (source.height + ImpostorOcclusion.TILE - 1) / ImpostorOcclusion.TILE;
                TextureHandle target = graph.CreateTexture(new TextureDesc(width, height)
                {
                    name = "_ImpostorOcclusionDepth",
                    format = GraphicsFormat.R32_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Point,
                });

                using (IComputeRenderGraphBuilder builder = graph.AddComputePass(OCCLUSION_PASS_NAME, out OcclusionData data))
                {
                    data.Shader = _occlusion;
                    data.Source = depth;
                    data.Target = target;
                    data.SourceSize = new Vector4(source.width, source.height, 0, 0);
                    data.Groups = new int2((width + 7) / 8, (height + 7) / 8);
                    builder.UseTexture(depth);
                    builder.UseTexture(target, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (OcclusionData pass, ComputeGraphContext context) =>
                    {
                        ComputeCommandBuffer cmd = context.cmd;
                        cmd.SetComputeTextureParam(pass.Shader, 0, OcclusionSource, pass.Source);
                        cmd.SetComputeTextureParam(pass.Shader, 0, OcclusionTarget, pass.Target);
                        cmd.SetComputeIntParams(pass.Shader, OcclusionSourceSize, (int)pass.SourceSize.x, (int)pass.SourceSize.y);
                        cmd.SetComputeIntParam(pass.Shader, OcclusionTile, ImpostorOcclusion.TILE);
                        cmd.SetComputeIntParam(pass.Shader, OcclusionReversedZ, SystemInfo.usesReversedZBuffer ? 1 : 0);
                        cmd.DispatchCompute(pass.Shader, 0, pass.Groups.x, pass.Groups.y, 1);
                    });
                }

                using (IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass(READBACK_PASS_NAME, out ReadbackData data))
                {
                    data.Target = target;
                    data.ViewProjection = math.mul((float4x4)GL.GetGPUProjectionMatrix(camera.GetProjectionMatrix(), true), (float4x4)camera.GetViewMatrix());
                    data.Position = camera.worldSpaceCameraPos;
                    builder.UseTexture(target);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (ReadbackData pass, UnsafeGraphContext context) =>
                    {
                        float4x4 viewProjection = pass.ViewProjection;
                        float3 position = pass.Position;
                        RTHandle handle = pass.Target;
                        CommandBufferHelpers.GetNativeCommandBuffer(context.cmd).RequestAsyncReadback(handle.rt,
                            request => ImpostorOcclusion.Store(request, viewProjection, position));
                    });
                }
            }

            private void AddCopyPass(RenderGraph graph, TextureHandle depth, TextureHandle target, bool depthTexture)
            {
                using IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass(depthTexture ? REFRESH_PASS_NAME : COPY_PASS_NAME,
                    out CopyData copy);
                copy.Source = depth;
                copy.Material = _copy;
                builder.UseTexture(depth);
                builder.SetRenderAttachment(target, 0);
                if (depthTexture)
                {
                    builder.SetGlobalTextureAfterPass(target, CameraDepthTexture);
                }

                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CopyData pass, RasterGraphContext context) =>
                    Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1, 1, 0, 0), pass.Material, COPY_SHADER_PASS));
            }

            private static void AddDrawPass(RenderGraph graph, RendererListParams parameters, TextureHandle depth,
                TextureHandle normals, TextureHandle opaqueDepth, bool last, bool depthIsTexture)
            {
                using IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass(DRAW_PASS_NAME, out DrawData draw);
                draw.Renderers = graph.CreateRendererList(parameters);
                draw.OpaqueDepth = opaqueDepth;
                draw.Last = last;
                builder.UseRendererList(draw.Renderers);
                builder.UseTexture(opaqueDepth);
                if (normals.IsValid())
                {
                    builder.SetRenderAttachment(normals, 0);
                    builder.SetGlobalTextureAfterPass(normals, CameraNormalsTexture);
                }

                builder.SetRenderAttachmentDepth(depth);
                if (depthIsTexture)
                {
                    builder.SetGlobalTextureAfterPass(depth, CameraDepthTexture);
                }

                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (DrawData pass, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    cmd.SetGlobalTexture(OpaqueDepth, pass.OpaqueDepth);
                    cmd.DrawRendererList(pass.Renderers);
                    if (pass.Last)
                    {
                        cmd.SetGlobalFloat(OwnsPrepass, 0);
                        // Forward impostors in URP's opaque pass can now skip what this depth hides.
                        cmd.SetGlobalFloat(SceneDepthTest, 1);
                    }
                });
            }

            private sealed class OcclusionData
            {
                public ComputeShader Shader;
                public TextureHandle Source;
                public TextureHandle Target;
                public Vector4 SourceSize;
                public int2 Groups;
            }

            private sealed class ReadbackData
            {
                public TextureHandle Target;
                public float4x4 ViewProjection;
                public float3 Position;
            }

            private sealed class CopyData
            {
                public TextureHandle Source;
                public Material Material;
            }

            private sealed class DrawData
            {
                public RendererListHandle Renderers;
                public TextureHandle OpaqueDepth;
                public bool Last;
            }
        }
    
        /// <summary>Ends the window in which forward impostors may test against the camera depth texture or match primed depth.</summary>
        [NoAutoStaticsCleanup]
        private sealed class SceneDepthResetPass : ScriptableRenderPass
        {
            private const string PASS_NAME = "Impostors Scene Depth Reset";

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                using IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass(PASS_NAME, out ResetData _);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ResetData _, UnsafeGraphContext context) =>
                {
                    context.cmd.SetGlobalFloat(SceneDepthTest, 0);
                    context.cmd.SetGlobalFloat(DepthPrimed, 0);
                });
            }

            private sealed class ResetData
            {
            }
        }
    }
}
