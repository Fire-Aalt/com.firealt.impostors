using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Impostors.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorLitRenderTests
    {
        [UnityTest]
        public IEnumerator BakedSunVisibilityLeavesOtherLightingUnchangedAndYieldsToCascades()
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
            float shadowDistance = pipeline.shadowDistance;
            var previousAmbient = RenderSettings.ambientProbe;
            var previousSun = RenderSettings.sun;
            var reflectionMode = RenderSettings.defaultReflectionMode;
            var reflection = RenderSettings.customReflectionTexture;
            float reflectionIntensity = RenderSettings.reflectionIntensity;
            bool suspended = ImpostorWorldRenderer.EditorRenderingSuspended;
            bool asyncShaders = ShaderUtil.allowAsyncCompilation;
            var active = RenderTexture.active;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var definition = Object.Instantiate(ImpostorTestAssets.Definition()); owned.Add(definition);
                definition.LodData = null;
                definition.Material = new Material(definition.Material); owned.Add(definition.Material);
                var camera = Make("Lit test camera", typeof(Camera)).GetComponent<Camera>();
                camera.enabled = false; camera.scene = scene;
                camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);
                camera.transform.SetPositionAndRotation(definition.Center + new Vector3(0, 3, 20), Quaternion.LookRotation(new Vector3(0, -3, -20)));
                camera.fieldOfView = 40; camera.nearClipPlane = .1f; camera.farClipPlane = 200;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true;
                var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGBFloat); owned.Add(target); target.Create(); camera.targetTexture = target;
                var pixels = new Texture2D(128, 128, TextureFormat.RGBAFloat, false, true); owned.Add(pixels);
                var sun = Make("Realtime sun", typeof(Light)).GetComponent<Light>();
                sun.type = LightType.Directional; sun.intensity = 2; sun.shadows = LightShadows.Soft;
                sun.transform.rotation = Quaternion.Euler(45, -20, 0); RenderSettings.sun = sun;
                var additional = Make("Additional light", typeof(Light)).GetComponent<Light>();
                additional.type = LightType.Directional; additional.intensity = 1;
                additional.transform.rotation = Quaternion.Euler(30, 140, 0); additional.enabled = false;
                var probe = new Cubemap(16, TextureFormat.RGBAHalf, true); owned.Add(probe);
                foreach (CubemapFace face in new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY, CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ })
                    probe.SetPixels(Enumerable.Repeat(new Color(.3f, .45f, .6f), 256).ToArray(), face);
                probe.Apply(true);
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = probe; RenderSettings.reflectionIntensity = 1;
                var reflectance = Array(new Color(.04f, .04f, .04f, .96f));
                var emission = Array(new Color(.3f, .2f, .1f));
                definition.Material.SetTexture(ImpostorShaderProperties.ReflectanceArray, reflectance);
                var placements = new ImpostorRenderData(new[] { definition }, new[] { new ImpostorInstance { Scale = 1, Yaw = 37 } });
                using var renderer = new ImpostorWorldRenderer { SceneMask = camera.overrideSceneCullingMask, LiveShadows = false };
                renderer.SetData(placements);
                // The test changes only the volume's strength, never base color or other surface inputs.
                foreach (string mode in new[] { "ambient", "reflection", "emission", "directional", "point" })
                {
                    var ambient = new SphericalHarmonicsL2();
                    if (mode == "ambient") ambient.AddAmbientLight(new Color(.6f, .5f, .4f));
                    RenderSettings.ambientProbe = ambient;
                    sun.color = Color.black;
                    additional.enabled = mode == "directional" || mode == "point";
                    additional.type = mode == "point" ? LightType.Point : LightType.Directional;
                    additional.transform.position = definition.Center + new Vector3(1, 3, 8);
                    additional.range = 50; additional.intensity = mode == "point" ? 50 : 1;
                    definition.Material.SetTexture(ImpostorShaderProperties.ReflectanceArray,
                        mode == "reflection" ? Array(new Color(.5f, .5f, .5f, 0)) : reflectance);
                    definition.Material.SetTexture(ImpostorShaderProperties.EmissionArray, emission);
                    definition.Material.SetFloat(ImpostorShaderProperties.HasEmission, mode == "emission" ? 1 : 0);
                    RenderSettings.reflectionIntensity = mode == "reflection" ? 1 : 0;
                    Color[] off = await Draw(0), on = await Draw(1);
                    Assert.That(Mean(on), Is.GreaterThan(.001), mode + " must produce measurable light.");
                    Assert.That(Difference(off, on), Is.LessThan(.00001), mode + " must be independent of baked sunlight visibility.");
                    definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowPower, .5f);
                    definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowNormalBias, 1);
                    Assert.That(Difference(on, await Draw(1)), Is.LessThan(.00001), mode + " must ignore baked-shadow response and bias too.");
                    definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowPower, 1);
                    definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowNormalBias, 0);
                    if (mode == "ambient")
                    {
                        var previousSurface = definition.Material.GetTexture(ImpostorShaderProperties.SurfaceArray);
                        definition.Material.SetTexture(ImpostorShaderProperties.SurfaceArray, Array(new Color(.5f,.5f,.25f,0)));
                        Color[] occluded = await Draw(0);
                        definition.Material.SetFloat(ImpostorShaderProperties.OcclusionStrength, 0);
                        Color[] noAO = await Draw(0);
                        Assert.That(Mean(noAO), Is.GreaterThan(Mean(occluded) * 2), "AO strength must adjust authored surface occlusion live.");
                        definition.Material.SetFloat(ImpostorShaderProperties.BaseColorGain, 1.5f);
                        Assert.That(Mean(await Draw(0)), Is.GreaterThan(Mean(noAO) * 1.3), "Albedo gain must reach native Lit shading live.");
                        definition.Material.SetFloat(ImpostorShaderProperties.BaseColorGain, 1);
                        definition.Material.SetFloat(ImpostorShaderProperties.OcclusionStrength, 1);
                        definition.Material.SetTexture(ImpostorShaderProperties.SurfaceArray, previousSurface);
                    }
                    if (mode == "reflection")
                    {
                        definition.Material.SetColor(ImpostorShaderProperties.BaseColor,Color.black);
                        Color[] blackWithReflection = await Draw(0);
                        Assert.That(Mean(blackWithReflection),Is.GreaterThan(.001),"Black diffuse tint alone must leave the stock reflection response visible.");
                        definition.Material.SetFloat(ImpostorShaderProperties.SpecularStrength,0);
                        Assert.That(Mean(await Draw(0)),Is.LessThan(.00001),"Zero specular must also remove the grazing reflection that washed out the canopy.");
                        definition.Material.SetFloat(ImpostorShaderProperties.SpecularStrength,1);
                        definition.Material.SetColor(ImpostorShaderProperties.SpecularTint,Color.black);
                        Assert.That(Mean(await Draw(0)),Is.LessThan(.00001),"Black specular tint must disable the full reflectance response.");
                        definition.Material.SetColor(ImpostorShaderProperties.BaseColor,Color.white);
                        definition.Material.SetColor(ImpostorShaderProperties.SpecularTint,Color.white);
                    }
                    if (mode == "emission")
                    {
                        definition.Material.SetFloat(ImpostorShaderProperties.EmissionStrength,0);
                        Color[] noEmission = await Draw(0);
                        definition.Material.SetFloat(ImpostorShaderProperties.EmissionStrength,2);
                        Color[] doubleEmission = await Draw(0);
                        Assert.That(Mean(doubleEmission)-Mean(noEmission),Is.EqualTo(2*(Mean(on)-Mean(noEmission))).Within(.0001),"HDR emission gain must remain independent of other surface lighting.");
                        definition.Material.SetFloat(ImpostorShaderProperties.EmissionStrength,1);
                    }
                }

                additional.enabled = false; sun.color = Color.white;
                RenderSettings.ambientProbe = new SphericalHarmonicsL2();
                RenderSettings.reflectionIntensity = 0;
                definition.Material.SetFloat(ImpostorShaderProperties.HasEmission, 0);
                definition.Material.SetTexture(ImpostorShaderProperties.ReflectanceArray, reflectance);
                Color[] unshadowed = await Draw(0), baked = await Draw(1);
                Assert.That(Mean(baked), Is.LessThan(Mean(unshadowed) * .7), "The isolated sunlight must actually use the volume.");
                definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowPower, .5f);
                Assert.That(Mean(await Draw(1)), Is.GreaterThan(Mean(baked)), "A lower response must lift intermediate sunlight visibility.");
                definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowPower, 2);
                Assert.That(Mean(await Draw(1)), Is.LessThan(Mean(baked)), "A higher response must deepen intermediate sunlight visibility.");
                definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowPower, 1);
                Assert.That(renderer.LightCallbacks, Is.Zero, "Self-shadowing does not require casting into scene shadow maps.");

                pipeline.shadowDistance = 100;
                renderer.LiveShadows = true;
                Color[] native = await Draw(0), withVolume = await Draw(1);
                Assert.That(renderer.LightCallbacks, Is.GreaterThan(0), "Realtime cascades must render the caster.");
                Assert.That(Difference(native, withVolume), Is.LessThan(.00001), "The volume must have zero weight inside full cascade coverage.");

                async Task<Color[]> Draw(float strength)
                {
                    definition.Material.SetFloat(ImpostorShaderProperties.SelfShadowStrength, strength);
                    renderer.RefreshMaterialProperties();
                    for (int i = 0; i < 3; i++)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        await Task.Delay(25);
                        ImpostorWorldRenderer.EditorRenderingSuspended = false;
                        try
                        {
                            camera.Render();
                            Assert.That(Shader.IsKeywordEnabled("_IMPOSTOR_NO_ADDITIONAL_LIGHTS"), Is.EqualTo(!additional.enabled),
                                "Select the full native light loop for additional lights, then reset it for sun-only cameras.");
                        }
                        finally { ImpostorWorldRenderer.EditorRenderingSuspended = true; }
                    }
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    return pixels.GetPixels();
                }
                Texture2DArray Array(Color color)
                {
                    var array = new Texture2DArray(4, 4, definition.Frames * definition.Frames, TextureFormat.RGBAHalf, false, true); owned.Add(array);
                    for (int i = 0; i < array.depth; i++) array.SetPixels(Enumerable.Repeat(color, 16).ToArray(), i);
                    array.Apply(); return array;
                }
            }
            finally
            {
                pipeline.shadowDistance = shadowDistance;
                RenderSettings.ambientProbe = previousAmbient; RenderSettings.sun = previousSun;
                RenderSettings.defaultReflectionMode = reflectionMode; RenderSettings.customReflectionTexture = reflection;
                RenderSettings.reflectionIntensity = reflectionIntensity;
                RenderTexture.active = active;
                ImpostorWorldRenderer.EditorRenderingSuspended = suspended;
                ShaderUtil.allowAsyncCompilation = asyncShaders;
                foreach (var value in owned) if (value) Object.DestroyImmediate(value);
                EditorSceneManager.ClosePreviewScene(scene);
            }
            GameObject Make(string name, Type component)
            {
                var go = new GameObject(name, component); owned.Add(go); SceneManager.MoveGameObjectToScene(go, scene); return go;
            }
        }

        private static double Mean(Color[] values) => values.Average(c => (double)c.r + c.g + c.b);
        private static double Difference(Color[] a, Color[] b) => a.Zip(b, (x, y) => (double)Mathf.Abs(x.r-y.r) + Mathf.Abs(x.g-y.g) + Mathf.Abs(x.b-y.b)).Average();
    }
}
