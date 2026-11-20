using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorSelfShadowRenderTests
    {
        [UnityTest]
        public IEnumerator OnlyImpostorsSelfShadowWithoutRealtimeShadowMaps()
        {
            Task operation = Validate();
            while (!operation.IsCompleted) yield return null;
            if (operation.IsFaulted) throw operation.Exception.GetBaseException();
        }

        private static async Task Validate()
        {
            var pipeline = (UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset)UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            Assert.That(pipeline.shadowDistance, Is.Zero, "This regression requires no realtime shadow maps.");
            var preview = EditorSceneManager.NewPreviewScene();
            var owned = new List<Object>();
            bool suspended = ImpostorWorldRenderer.EditorRenderingSuspended;
            bool asynchronousShaders = ShaderUtil.allowAsyncCompilation;
            var active = RenderTexture.active;
            try
            {
                // Compile the actual files on disk, rather than passing with a previously cached variant.
                ShaderUtil.allowAsyncCompilation = false;
                var shaderPaths = Enumerable.Range(1, 2).SelectMany(i => ImpostorTestAssets.Definition(i)
                    .LodData.MeshLevels.SelectMany(level => level.Parts).Select(part => AssetDatabase.GetAssetPath(part.Material.shader)))
                    .Append(AssetDatabase.GetAssetPath(Shader.Find(ImpostorShaderNames.HEMI_OCTAHEDRAL))).Distinct();
                foreach (string path in shaderPaths)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                        | ImportAssetOptions.DontDownloadFromCacheServer);
                var cameraObject = new GameObject("Self-shadow regression camera", typeof(Camera));
                owned.Add(cameraObject); SceneManager.MoveGameObjectToScene(cameraObject, preview);
                var camera = cameraObject.GetComponent<Camera>();
                camera.scene = preview; camera.enabled = false;
                camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(preview);
                camera.fieldOfView = 45; camera.nearClipPlane = .1f; camera.farClipPlane = 1000;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.allowHDR = false;
                var sunObject = new GameObject("Self-shadow regression sun", typeof(Light));
                owned.Add(sunObject); SceneManager.MoveGameObjectToScene(sunObject, preview);
                var sun = sunObject.GetComponent<Light>();
                sun.type = LightType.Directional; sun.intensity = 1.7f; sun.shadows = LightShadows.Soft;
                sun.transform.rotation = Quaternion.Euler(48, -35, 0);
                var target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                owned.Add(target); target.Create(); camera.targetTexture = target;
                var pixels = new Texture2D(256, 256, TextureFormat.RGBA32, false, true); owned.Add(pixels);

                for (int prototype = 1; prototype <= 2; prototype++)
                {
                    var source = ImpostorTestAssets.Definition(prototype);
                    Assert.That(source.HasValidSelfShadowVolume, Is.True);
                    var definition = Object.Instantiate(source); owned.Add(definition);
                    var mesh = source.LodData.Copy();
                    mesh.MeshLevels = new[] { new ImpostorMeshLod
                    {
                        Parts = (ImpostorLodPart[])mesh.MeshLevels[0].Parts.Clone(),
                        TransitionScreenHeight = .000001f, FadeWidth = 0
                    } };
                    for (int part = 0; part < mesh.MeshLevels[0].Parts.Length; part++)
                    {
                        var item = mesh.MeshLevels[0].Parts[part];
                        var material = Object.Instantiate(item.Material); owned.Add(material);
                        if (material.HasProperty(ImpostorShaderProperties.BendPower)) material.SetFloat(ImpostorShaderProperties.BendPower, 0);
                        item.Material = material; mesh.MeshLevels[0].Parts[part] = item;
                    }
                    Vector3 direction = new Vector3(.3f, .3f, 1).normalized;
                    float distance = source.LodData.Size / (2 * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * .7f);
                    camera.transform.SetPositionAndRotation(source.LodData.ReferencePoint + direction * distance, Quaternion.LookRotation(-direction));
                    var data = new ImpostorRenderData(new[] { definition }, new[] { new ImpostorInstance { Scale = 1, Yaw = 37 } });
                    using var renderer = new ImpostorWorldRenderer { SceneMask = camera.overrideSceneCullingMask };
                    for (int representation = 0; representation < 2; representation++)
                    {
                        definition.LodData = representation == 0 ? null : mesh;
                        var luminance = new double[3];
                        for (int state = 0; state < 3; state++)
                        {
                            sun.shadowStrength = state == 0 ? 0 : 1;
                            definition.SelfShadowVolume = state == 2 ? null : source.SelfShadowVolume;
                            renderer.SetData(data);
                            for (int warm = 0; warm < 3; warm++)
                            {
                                ImpostorWorldRenderer.EditorRenderingSuspended = false;
                                try { camera.Render(); }
                                finally { ImpostorWorldRenderer.EditorRenderingSuspended = true; }
                                await Task.Delay(20);
                            }
                            RenderTexture.active = target;
                            pixels.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); pixels.Apply();
                            luminance[state] = pixels.GetPixels().Average(c => (double)c.r * .2126 + c.g * .7152 + c.b * .0722);
                        }
                        string context = $"Portable prototype {prototype}, representation {representation}: {string.Join(", ", luminance)}";
                        Assert.That(luminance[0], Is.GreaterThan(.001), context);
                        if (representation == 0) Assert.That(luminance[1], Is.LessThan(luminance[0] - .0005), context);
                        else Assert.That(luminance[1], Is.EqualTo(luminance[0]).Within(.00001), context);
                        Assert.That(luminance[2], Is.EqualTo(luminance[0]).Within(.00001), context);
                    }
                }
            }
            finally
            {
                ImpostorWorldRenderer.EditorRenderingSuspended = suspended;
                ShaderUtil.allowAsyncCompilation = asynchronousShaders;
                RenderTexture.active = active;
                foreach (var value in owned) if (value) Object.DestroyImmediate(value);
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }
    }
}
