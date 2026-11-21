using Impostors.Data;
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

        /// <summary>Whether URP's prepass renders straight into a depth-format camera depth texture the package can extend.</summary>
        private static bool CanOwnPrepass(RenderGraph graph, UniversalResourceData resources, UniversalCameraData camera)
        {
            if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid())
            {
                return false;
            }

            // URP gives the depth texture a depth format only when its forward prepass draws into it directly
            // (no deferred, no depth priming, no copy), unless the platform cannot render to R32_SFloat at all.
            bool r32Renderable = SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Render);
            return r32Renderable && GraphicsFormatUtility.IsDepthFormat(graph.GetTextureDesc(resources.cameraDepthTexture).format);
        }

        /// <summary>
        /// Runs before URP's prepass and decides whether impostors leave it. Impostors write per-pixel depth, which
        /// costs hardware early-Z wherever conservative depth is unavailable, so inside URP's prepass every hidden
        /// impostor fragment was fully reconstructed.
        /// </summary>
        [NoAutoStaticsCleanup]
        private sealed class PrepassHandoffPass : ScriptableRenderPass
        {
            private const string PASS_NAME = "Impostors Prepass Handoff";

            public bool Owned;

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                Owned = CanOwnPrepass(graph, frameData.Get<UniversalResourceData>(), frameData.Get<UniversalCameraData>());
                using IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass(PASS_NAME, out HandoffData data);
                data.Owned = Owned ? 1 : 0;
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (HandoffData pass, UnsafeGraphContext context) =>
                    context.cmd.SetGlobalFloat(OwnsPrepass, pass.Owned));
            }

            private sealed class HandoffData
            {
                public float Owned;
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
            private const string DRAW_PASS_NAME = "Impostors Depth";
            private const string OPAQUE_DEPTH_NAME = "_ImpostorOpaqueDepth";
            private const int COPY_SHADER_PASS = 1;

            private static readonly ShaderTagId DepthTag = new(ImpostorShaderPasses.PREPASS_DEPTH_LIGHT_MODE);
            private static readonly ShaderTagId DepthNormalsTag = new(ImpostorShaderPasses.PREPASS_DEPTH_NORMALS_LIGHT_MODE);
            private static readonly int OpaqueDepth = Shader.PropertyToID(OPAQUE_DEPTH_NAME);
            private static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            private static readonly int CameraNormalsTexture = Shader.PropertyToID("_CameraNormalsTexture");

            private PrepassHandoffPass _handoff;
            private Material _copy;

            public void Setup(PrepassHandoffPass handoff, Material copy)
            {
                _handoff = handoff;
                _copy = copy;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                if (!_handoff.Owned)
                {
                    return;
                }

                var resources = frameData.Get<UniversalResourceData>();
                var camera = frameData.Get<UniversalCameraData>();
                var rendering = frameData.Get<UniversalRenderingData>();
                var lights = frameData.Get<UniversalLightData>();
                TextureHandle depth = resources.cameraDepthTexture;
                // In forward paths the normals texture exists only when URP's prepass writes it.
                TextureHandle normals = resources.cameraNormalsTexture;
                bool writeNormals = normals.IsValid();

                TextureDesc copyDescriptor = graph.GetTextureDesc(depth);
                copyDescriptor.name = OPAQUE_DEPTH_NAME;
                copyDescriptor.format = GraphicsFormat.R32_SFloat;
                copyDescriptor.msaaSamples = MSAASamples.None;
                copyDescriptor.bindTextureMS = false;
                copyDescriptor.clearBuffer = false;
                copyDescriptor.filterMode = FilterMode.Point;
                TextureHandle opaqueDepth = graph.CreateTexture(copyDescriptor);

                using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass(COPY_PASS_NAME, out CopyData copy))
                {
                    copy.Source = depth;
                    copy.Material = _copy;
                    builder.UseTexture(depth);
                    builder.SetRenderAttachment(opaqueDepth, 0);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (CopyData pass, RasterGraphContext context) =>
                        Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1, 1, 0, 0), pass.Material, COPY_SHADER_PASS));
                }

                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(writeNormals ? DepthNormalsTag : DepthTag,
                    rendering, camera, lights, SortingCriteria.CommonOpaque);
                drawing.perObjectData = PerObjectData.None;
                var parameters = new RendererListParams(rendering.cullResults, drawing, new FilteringSettings(RenderQueueRange.opaque));

                using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass(DRAW_PASS_NAME, out DrawData draw))
                {
                    draw.Renderers = graph.CreateRendererList(parameters);
                    draw.OpaqueDepth = opaqueDepth;
                    builder.UseRendererList(draw.Renderers);
                    builder.UseTexture(opaqueDepth);
                    if (writeNormals)
                    {
                        builder.SetRenderAttachment(normals, 0);
                        builder.SetGlobalTextureAfterPass(normals, CameraNormalsTexture);
                    }

                    builder.SetRenderAttachmentDepth(depth);
                    builder.SetGlobalTextureAfterPass(depth, CameraDepthTexture);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (DrawData pass, RasterGraphContext context) =>
                    {
                        RasterCommandBuffer cmd = context.cmd;
                        cmd.SetGlobalTexture(OpaqueDepth, pass.OpaqueDepth);
                        cmd.DrawRendererList(pass.Renderers);
                        cmd.SetGlobalFloat(OwnsPrepass, 0);
                    });
                }
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
            }
        }
    }
}
