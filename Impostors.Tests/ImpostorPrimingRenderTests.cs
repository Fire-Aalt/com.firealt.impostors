using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Impostors.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorPrimingRenderTests
    {
        private const int SIZE = 256;

        [UnityTest]
        public IEnumerator DepthPrimingKeepsEveryImpostorPixel()
        {
            Task task = Validate();
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static async Task Validate()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var owned = new List<Object>();
            var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            var rendererList = (ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                .GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pipeline);
            var rendererData = (UniversalRendererData)rendererList[0];
            var feature = rendererData.rendererFeatures.OfType<ImpostorsRendererFeature>().Single();
            DepthPrimingMode priming = rendererData.depthPrimingMode;
            float renderScale = feature.RenderScale;
            var ambient = RenderSettings.ambientProbe;
            bool suspended = ImpostorWorldRenderer.EditorRenderingSuspended;
            bool asyncShaders = ShaderUtil.allowAsyncCompilation;
            var active = RenderTexture.active;
            PropertyInfo usesPriming = typeof(ScriptableRenderer).GetProperty("useDepthPriming", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var definition = Object.Instantiate(ImpostorTestAssets.Definition()); owned.Add(definition);
                definition.LodData = null;
                var cameraObject = new GameObject("Priming test camera", typeof(Camera)); owned.Add(cameraObject);
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false; camera.scene = scene;
                camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);
                float spacing = Mathf.Max(definition.Radius, .5f) * 1.2f;
                camera.transform.SetPositionAndRotation(definition.Center + new Vector3(0, spacing, -spacing * 6),
                    Quaternion.LookRotation(new Vector3(0, -spacing * .5f, spacing * 6)));
                camera.fieldOfView = 50; camera.nearClipPlane = .1f; camera.farClipPlane = spacing * 40;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowMSAA = false;
                var target = new RenderTexture(SIZE, SIZE, GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.D32_SFloat_S8_UInt); owned.Add(target);
                target.Create(); camera.targetTexture = target;
                var pixels = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false, true); owned.Add(pixels);
                var sunObject = new GameObject("Priming test sun", typeof(Light)); owned.Add(sunObject);
                SceneManager.MoveGameObjectToScene(sunObject, scene);
                var sun = sunObject.GetComponent<Light>();
                sun.type = LightType.Directional; sun.intensity = 2; sun.shadows = LightShadows.None;
                sun.transform.rotation = Quaternion.Euler(50, -30, 0);
                var probe = new SphericalHarmonicsL2();
                probe.AddAmbientLight(new Color(.3f, .3f, .3f));
                RenderSettings.ambientProbe = probe;

                // Rows of overlapping trees, so nearer impostors hide parts of farther ones.
                var instances = new List<ImpostorInstance>();
                for (int row = 0; row < 3; row++)
                {
                    for (int column = -2; column <= 2; column++)
                    {
                        instances.Add(new ImpostorInstance
                        {
                            Position = new Vector3((column + row * .5f) * spacing, 0, row * spacing),
                            Yaw = 37 * (row * 5 + column), Scale = 1,
                        });
                    }
                }

                using var renderer = new ImpostorWorldRenderer { SceneMask = camera.overrideSceneCullingMask, LiveShadows = false };
                renderer.SetData(new ImpostorRenderData(new[] { definition }, instances.ToArray()));

                Color32[] reference = await Draw(DepthPrimingMode.Disabled, 1);
                int[] covered = Enumerable.Range(0, reference.Length).Where(i => Luminance(reference[i]) > 4).ToArray();
                Assert.That(covered.Length, Is.GreaterThan(SIZE * SIZE / 20), "The trees must cover a measurable part of the view.");

                Color32[] primed = await Draw(DepthPrimingMode.Forced, 1);
                Assert.That(Holes(reference, primed, covered), Is.LessThanOrEqualTo(covered.Length / 500),
                    "Under depth priming URP's opaque pass keeps only impostor pixels whose depth matches its prepass.");
                Assert.That(Difference(reference, primed), Is.LessThan(.5),
                    "Primed native impostors must shade like unprimed ones.");

                Color32[] offscreenReference = await Draw(DepthPrimingMode.Disabled, .5f);
                Color32[] offscreen = await Draw(DepthPrimingMode.Forced, .5f);
                Assert.That(Black(offscreen, covered), Is.LessThanOrEqualTo(covered.Length / 1000),
                    "The primed composite must fill every pixel its stencil mask marks.");
                Assert.That(Holes(reference, offscreen, covered), Is.LessThanOrEqualTo(Holes(reference, offscreenReference, covered)),
                    "Native-resolution silhouettes must lose fewer edge pixels than the unprimed composite.");
                Assert.That(Difference(reference, offscreen), Is.LessThanOrEqualTo(Difference(reference, offscreenReference) + 1),
                    "The primed composite must match the native image at least as well as the unprimed composite.");

                async Task<Color32[]> Draw(DepthPrimingMode mode, float scale)
                {
                    rendererData.depthPrimingMode = mode;
                    feature.RenderScale = scale;
                    bool wasPrimed = false;
                    for (int i = 0; i < 3; i++)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        await Task.Delay(25);
                        ImpostorWorldRenderer.EditorRenderingSuspended = false;
                        try
                        {
                            camera.Render();
                            var scriptableRenderer = pipeline.GetRenderer(0);
                            wasPrimed = usesPriming != null && (bool)usesPriming.GetValue(scriptableRenderer);
                        }
                        finally { ImpostorWorldRenderer.EditorRenderingSuspended = true; }
                    }

                    Assert.That(wasPrimed, Is.EqualTo(mode != DepthPrimingMode.Disabled), $"URP must prime depth for {mode}.");
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, SIZE, SIZE), 0, 0); pixels.Apply();
                    return pixels.GetPixels32();
                }
            }
            finally
            {
                rendererData.depthPrimingMode = priming;
                feature.RenderScale = renderScale;
                RenderSettings.ambientProbe = ambient;
                RenderTexture.active = active;
                ImpostorWorldRenderer.EditorRenderingSuspended = suspended;
                ShaderUtil.allowAsyncCompilation = asyncShaders;
                foreach (var value in owned) if (value) Object.DestroyImmediate(value);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static int Luminance(Color32 c) => (c.r * 54 + c.g * 183 + c.b * 19) >> 8;

        // A tree pixel that turned (near) black is a pixel the primed path dropped.
        private static int Holes(Color32[] reference, Color32[] test, int[] covered) =>
            covered.Count(i => Luminance(test[i]) * 4 < Luminance(reference[i]));

        private static int Black(Color32[] test, int[] covered) =>
            covered.Count(i => Luminance(test[i]) <= 1);

        private static double Difference(Color32[] a, Color32[] b) =>
            a.Zip(b, (x, y) => (double)Mathf.Abs(x.r - y.r) + Mathf.Abs(x.g - y.g) + Mathf.Abs(x.b - y.b)).Average();
    }
}
