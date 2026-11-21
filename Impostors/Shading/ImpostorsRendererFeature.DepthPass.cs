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
                    AddCopyPass(graph, depth, opaqueDepth);
                    AddDrawPass(graph, new RendererListParams(rendering.cullResults, drawing, filtering), depth,
                        writeNormals ? normals : TextureHandle.nullHandle, opaqueDepth, band == _bands - 1);
                }

                if (_occlusion && camera.cameraType == CameraType.Game)
                {
                    AddOcclusionCapture(graph, camera, depth);
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

            private void AddCopyPass(RenderGraph graph, TextureHandle depth, TextureHandle opaqueDepth)
            {
                using IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass(COPY_PASS_NAME, out CopyData copy);
                copy.Source = depth;
                copy.Material = _copy;
                builder.UseTexture(depth);
                builder.SetRenderAttachment(opaqueDepth, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CopyData pass, RasterGraphContext context) =>
                    Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1, 1, 0, 0), pass.Material, COPY_SHADER_PASS));
            }

            private static void AddDrawPass(RenderGraph graph, RendererListParams parameters, TextureHandle depth,
                TextureHandle normals, TextureHandle opaqueDepth, bool last)
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
                builder.SetGlobalTextureAfterPass(depth, CameraDepthTexture);
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
    
        /// <summary>Ends the window in which forward impostors may test against the camera depth texture.</summary>
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
                    context.cmd.SetGlobalFloat(SceneDepthTest, 0));
            }

            private sealed class ResetData
            {
            }
        }
    }
}
