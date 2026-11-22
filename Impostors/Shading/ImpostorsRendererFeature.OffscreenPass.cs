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
        /// <summary>Shades impostors into a scaled buffer, then composites it over the camera target with a depth test.</summary>
        [NoAutoStaticsCleanup]
        private sealed class OffscreenPass : ScriptableRenderPass
        {
            private const string SHADE_PASS_NAME = "Impostors Offscreen Shade";
            private const string COMPOSITE_PASS_NAME = "Impostors Offscreen Composite";
            private const string COLOR_NAME = "_ImpostorOffscreenColor";
            private const string DEPTH_NAME = "_ImpostorOffscreenDepth";
            private const string BUFFER_MSAA_KEYWORD = "_IMPOSTOR_BUFFER_MSAA";

            // Matches the composite shader's Texture2DMS sample count.
            private const int MSAA_SAMPLES = 4;

            private const int COMPOSITE_SHADER_PASS = 0;
            private const int PRIMED_COMPOSITE_SHADER_PASS = 2;

            private static readonly ShaderTagId ShaderTag = new(ImpostorShaderPasses.OFFSCREEN_LIGHT_MODE);
            private static readonly int ScaledScreenParams = Shader.PropertyToID("_ScaledScreenParams");
            private static readonly int NativeScreenHeight = Shader.PropertyToID("_ImpostorNativeScreenHeight");
            private static readonly int AlphaToMaskAvailable = Shader.PropertyToID("_AlphaToMaskAvailable");
            private static readonly int ImpostorColor = Shader.PropertyToID("_ImpostorColor");
            private static readonly int ImpostorDepth = Shader.PropertyToID("_ImpostorDepth");
            private static readonly int ImpostorSize = Shader.PropertyToID("_ImpostorSize");
            private static readonly int ImpostorSharpness = Shader.PropertyToID("_ImpostorSharpness");

            private PrepassHandoffPass _handoff;
            private float _scale;
            private float _sharpness;
            private bool _msaa;
            private int _stencilBit;
            private Material _composite;

            public void Setup(PrepassHandoffPass handoff, float scale, float sharpness, bool msaa, Material composite,
                int stencilBit)
            {
                _handoff = handoff;
                _stencilBit = stencilBit;
                _scale = scale;
                _sharpness = sharpness;
                _msaa = msaa;
                CoreUtils.SetKeyword(composite, BUFFER_MSAA_KEYWORD, msaa);
                // URP then guarantees scene depth by this pass's event, from its prepass or an early copy.
                ConfigureInput(ScriptableRenderPassInput.Depth);
                _composite = composite;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                if (!_handoff.OffscreenActive)
                {
                    return;
                }

                var resources = frameData.Get<UniversalResourceData>();
                var camera = frameData.Get<UniversalCameraData>();
                var rendering = frameData.Get<UniversalRenderingData>();
                var lights = frameData.Get<UniversalLightData>();
                RenderTextureDescriptor native = camera.cameraTargetDescriptor;
                int width = Mathf.Max(1, Mathf.RoundToInt(native.width * _scale));
                int height = Mathf.Max(1, Mathf.RoundToInt(native.height * _scale));
                CreateTargets(graph, native, width, height, out TextureHandle color, out TextureHandle depth);

                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(ShaderTag, rendering, camera, lights,
                    SortingCriteria.CommonOpaque);
                var filtering = new FilteringSettings(RenderQueueRange.opaque);
                var parameters = new RendererListParams(rendering.cullResults, drawing, filtering);
                AddShadePass(graph, parameters, native, width, height, color, depth, resources.cameraDepthTexture.IsValid());
                AddCompositePass(graph, resources, width, height, color, depth, _handoff.PrimedComposite);
            }

            private void CreateTargets(RenderGraph graph, RenderTextureDescriptor native, int width, int height,
                out TextureHandle color, out TextureHandle depth)
            {
                RenderTextureDescriptor colorDescriptor = native;
                colorDescriptor.width = width;
                colorDescriptor.height = height;
                colorDescriptor.msaaSamples = _msaa ? MSAA_SAMPLES : 1;
                colorDescriptor.bindMS = false;
                colorDescriptor.depthBufferBits = 0;
                colorDescriptor.depthStencilFormat = GraphicsFormat.None;
                color = UniversalRenderer.CreateRenderGraphTexture(graph, colorDescriptor, COLOR_NAME, false, FilterMode.Point);

                RenderTextureDescriptor depthDescriptor = colorDescriptor;
                depthDescriptor.graphicsFormat = GraphicsFormat.None;
                depthDescriptor.depthStencilFormat = GraphicsFormat.D32_SFloat;
                depthDescriptor.bindMS = _msaa;
                depth = UniversalRenderer.CreateRenderGraphTexture(graph, depthDescriptor, DEPTH_NAME, false, FilterMode.Point);
            }

            private void AddShadePass(RenderGraph graph, RendererListParams parameters, RenderTextureDescriptor native,
                int width, int height, TextureHandle color, TextureHandle depth, bool sceneDepth)
            {
                using IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass(SHADE_PASS_NAME, out ShadeData data);
                data.Renderers = graph.CreateRendererList(parameters);
                data.ScaledParams = new Vector4(width, height, 1 + 1f / width, 1 + 1f / height);
                data.NativeParams = new Vector4(native.width, native.height, 1 + 1f / native.width, 1 + 1f / native.height);
                data.AlphaToMask = native.msaaSamples > 1 ? 1 : 0;
                data.BufferAlphaToMask = _msaa ? 1 : 0;
                data.SceneDepth = sceneDepth ? 1 : 0;
                builder.UseRendererList(data.Renderers);
                builder.SetRenderAttachment(color, 0);
                builder.SetRenderAttachmentDepth(depth);
                builder.UseAllGlobalTextures(true);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ShadeData pass, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    cmd.ClearRenderTarget(true, true, Color.clear);
                    cmd.SetGlobalFloat(AlphaToMaskAvailable, pass.BufferAlphaToMask);
                    cmd.SetGlobalVector(ScaledScreenParams, pass.ScaledParams);
                    cmd.SetGlobalFloat(NativeScreenHeight, pass.NativeParams.y);
                    cmd.SetGlobalFloat(SceneDepthTest, pass.SceneDepth);
                    cmd.DrawRendererList(pass.Renderers);
                    cmd.SetGlobalFloat(SceneDepthTest, 0);
                    cmd.SetGlobalVector(ScaledScreenParams, pass.NativeParams);
                    cmd.SetGlobalFloat(AlphaToMaskAvailable, pass.AlphaToMask);
                });
            }

            private void AddCompositePass(RenderGraph graph, UniversalResourceData resources, int width, int height,
                TextureHandle color, TextureHandle depth, bool primed)
            {
                using IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass(COMPOSITE_PASS_NAME, out CompositeData data);
                data.Color = color;
                data.Depth = depth;
                data.Size = new Vector4(width, height, 0, 0);
                data.Sharpness = _sharpness;
                data.Material = _composite;
                data.ShaderPass = primed ? PRIMED_COMPOSITE_SHADER_PASS : COMPOSITE_SHADER_PASS;
                data.StencilRef = primed ? _stencilBit : 0;
                builder.UseTexture(color);
                builder.UseTexture(depth);
                builder.SetRenderAttachment(resources.activeColorTexture, 0);
                if (primed)
                {
                    // The primed attachment already holds the final depth; only its stencil mask is read.
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture);
                }
                else
                {
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture);
                }

                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                {
                    pass.Material.SetTexture(ImpostorColor, pass.Color);
                    pass.Material.SetTexture(ImpostorDepth, pass.Depth);
                    pass.Material.SetVector(ImpostorSize, pass.Size);
                    pass.Material.SetFloat(ImpostorSharpness, pass.Sharpness);
                    pass.Material.SetFloat(ImpostorShaderProperties.StencilRef, pass.StencilRef);
                    Blitter.BlitTexture(context.cmd, new Vector4(1, 1, 0, 0), pass.Material, pass.ShaderPass);
                });
            }

            private sealed class ShadeData
            {
                public RendererListHandle Renderers;
                public Vector4 ScaledParams;
                public Vector4 NativeParams;
                public float AlphaToMask;
                public float BufferAlphaToMask;
                public float SceneDepth;
            }

            private sealed class CompositeData
            {
                public TextureHandle Color;
                public TextureHandle Depth;
                public Vector4 Size;
                public float Sharpness;
                public Material Material;
                public int ShaderPass;
                public float StencilRef;
            }
        }
    }
}
