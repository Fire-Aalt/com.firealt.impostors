using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorSelfShadowBakeTests
    {
        [UnityTest]
        public IEnumerator SelfShadowToggleRegeneratesAndRemovesOnlyItsVolume()
        {
            if (!SystemInfo.supportsCompressed3DTextures || !SystemInfo.SupportsTextureFormat(TextureFormat.BC4))
                Assert.Ignore("This GPU cannot bake compressed 3D textures.");
            Task operation = Validate();
            while (!operation.IsCompleted) yield return null;
            if (operation.IsFaulted) throw operation.Exception.GetBaseException();
        }

        private static async Task Validate()
        {
            string name = "ShadowToggle-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(ImpostorTestAssets.TemporaryRoot, name);
            string folder = ImpostorTestAssets.TemporaryRoot + "/" + name;
            GameObject root = null;
            try
            {
                var settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
                settings.Frames = 2; settings.Resolution = 32; settings.Border = 2;
                settings.Supersampling = 1; settings.AmbientSamples = 4; settings.SelfShadowing = false;
                settings.DiscardAdjustmentData = false;
                AssetDatabase.CreateAsset(settings, folder + "/Settings.asset");
                var material = new Material(ImpostorTestAssets.CaptureShader);
                AssetDatabase.CreateAsset(material, folder + "/Source.mat");
                root = new GameObject(name);
                var shape = GameObject.CreatePrimitive(PrimitiveType.Cube);
                shape.transform.SetParent(root.transform, false);
                shape.transform.localScale = new Vector3(4, 6, 5);
                shape.transform.localRotation = Quaternion.Euler(20, 35, 10);
                shape.GetComponent<MeshRenderer>().sharedMaterial = material;
                root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(.2f, new[] { shape.GetComponent<Renderer>() }) });
                var source = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Target.prefab");
                Object.DestroyImmediate(root); root = null;

                var first = await ImpostorAuthoring.Rebake(source, settings);
                Assert.That(first.SelfShadowVolume == null, Is.True);
                Assert.That(first.Material.IsKeywordEnabled(ImpostorShaderKeywords.SELF_SHADOW_VOLUME), Is.False);
                first.Material.SetColor(ImpostorShaderProperties.BaseColor, new Color(.8f,.9f,1));
                first.Material.SetFloat(ImpostorShaderProperties.BaseColorGain, 1.5f);
                first.Material.SetFloat(ImpostorShaderProperties.OcclusionStrength, .3f);
                first.Material.SetFloat(ImpostorShaderProperties.SmoothnessScale, .7f);
                first.Material.SetFloat(ImpostorShaderProperties.SelfShadowStrength, .25f);
                first.Material.SetFloat(ImpostorShaderProperties.SelfShadowNormalBias, .5f);
                first.Material.SetFloat(ImpostorShaderProperties.SelfShadowPower, .75f);
                var extraScalars = new[]
                {
                    (ImpostorShaderProperties.AlbedoContrast, 1.4f), (ImpostorShaderProperties.AlbedoSaturation, .8f),
                    (ImpostorShaderProperties.AlbedoLift, -.02f), (ImpostorShaderProperties.SpecularStrength, .2f),
                    (ImpostorShaderProperties.SpecularHighlights, 0f), (ImpostorShaderProperties.EnvironmentReflections, 0f),
                    (ImpostorShaderProperties.SmoothnessOffset, -.1f), (ImpostorShaderProperties.OcclusionContrast, 1.3f),
                    (ImpostorShaderProperties.OcclusionOffset, -.2f), (ImpostorShaderProperties.EmissionStrength, 2f),
                    (ImpostorShaderProperties.NormalStrength, .6f), (ImpostorShaderProperties.MetallicInfluence, .4f)
                };
                var extraColors = new[]
                {
                    (ImpostorShaderProperties.HueVariation, new Color(.7f, .9f, .6f, .2f)),
                    (ImpostorShaderProperties.SpecularTint, new Color(.8f, .9f, 1)),
                    (ImpostorShaderProperties.EmissionTint, new Color(2, 1, .5f))
                };
                foreach (var (property, value) in extraScalars) first.Material.SetFloat(property, value);
                foreach (var (property, value) in extraColors) first.Material.SetColor(property, value);

                settings.SelfShadowing = true;
                Assert.That(ImpostorAuthoring.IsOutdated(source, settings, first), Is.True);
                var enabled = first;
                long previousBytes = 0;
                foreach (ImpostorSelfShadowQuality quality in Enum.GetValues(typeof(ImpostorSelfShadowQuality)))
                {
                    settings.SelfShadowQuality = quality;
                    Assert.That(ImpostorAuthoring.IsOutdated(source, settings, enabled), Is.True);
                    var bake = ImpostorAuthoring.Rebake(source, settings);
                    // Subsequent edits cannot change the quality of an in-flight bake.
                    settings.SelfShadowQuality = quality == ImpostorSelfShadowQuality.High
                        ? ImpostorSelfShadowQuality.Low : ImpostorSelfShadowQuality.High;
                    enabled = await bake;
                    Assert.That(enabled, Is.EqualTo(first));
                    Assert.That(enabled.Material.GetColor(ImpostorShaderProperties.BaseColor), Is.EqualTo(new Color(.8f,.9f,1)));
                    Assert.That(enabled.Material.GetFloat(ImpostorShaderProperties.BaseColorGain), Is.EqualTo(1.5f));
                    Assert.That(enabled.Material.GetFloat(ImpostorShaderProperties.OcclusionStrength), Is.EqualTo(.3f));
                    Assert.That(enabled.Material.GetFloat(ImpostorShaderProperties.SmoothnessScale), Is.EqualTo(.7f));
                    Assert.That(enabled.Material.GetFloat(ImpostorShaderProperties.SelfShadowStrength), Is.EqualTo(.25f));
                    Assert.That(enabled.Material.GetFloat(ImpostorShaderProperties.SelfShadowNormalBias), Is.EqualTo(.5f));
                    Assert.That(enabled.Material.GetFloat(ImpostorShaderProperties.SelfShadowPower), Is.EqualTo(.75f));
                    foreach (var (property, value) in extraScalars) Assert.That(enabled.Material.GetFloat(property), Is.EqualTo(value));
                    foreach (var (property, value) in extraColors) Assert.That(enabled.Material.GetColor(property), Is.EqualTo(value));
                    Assert.That(enabled.Material.IsKeywordEnabled(ImpostorShaderKeywords.MATERIAL_ADJUSTMENTS), Is.True);
                    Assert.That(enabled.Material.IsKeywordEnabled(ImpostorShaderKeywords.COLOR_ADJUSTMENTS), Is.True);
                    Assert.That(enabled.Material.IsKeywordEnabled(ImpostorShaderKeywords.REFLECTANCE_ADJUSTMENTS), Is.True);
                    Assert.That(enabled.Material.IsKeywordEnabled(ImpostorShaderKeywords.SPECULAR_HIGHLIGHTS_OFF), Is.True);
                    Assert.That(enabled.Material.IsKeywordEnabled(ImpostorShaderKeywords.ENVIRONMENT_REFLECTIONS_OFF), Is.True);
                    Assert.That(enabled.HasValidSelfShadowVolume, Is.True);
                    Assert.That(enabled.SelfShadowVolume.format, Is.EqualTo(TextureFormat.BC4));
                    var expected = ImpostorSelfShadowLayout.ForBounds(enabled.Bounds, quality);
                    Assert.That(enabled.SelfShadowLayout, Is.EqualTo(expected));
                    Assert.That(expected.Bytes(2), Is.GreaterThan(previousBytes));
                    previousBytes = expected.Bytes(2);
                    Assert.That(enabled.Material.GetVector(ImpostorShaderProperties.SelfShadowInterior),
                        Is.EqualTo((Vector4)(Vector3)expected.Interior));
                    Assert.That(enabled.Material.IsKeywordEnabled(ImpostorShaderKeywords.SELF_SHADOW_VOLUME), Is.True);
                    Assert.That(ImpostorAuthoring.IsOutdated(source, settings, enabled), Is.True);
                    settings.SelfShadowQuality = quality;
                    Assert.That(ImpostorAuthoring.IsOutdated(source, settings, enabled), Is.False);
                    Assert.That(ImpostorSelfShadowPreview.TryGetLayout(source, settings.SourceLod, settings.Resolution, quality, out var preview), Is.True);
                    Assert.That(preview, Is.EqualTo(expected), "Memory preview must match the baked grid.");
                    var reloaded = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(source)).OfType<Texture3D>().ToArray();
                    Assert.That(reloaded.Length, Is.EqualTo(1), "Quality changes must replace the previous volume.");
                    Assert.That(reloaded[0], Is.EqualTo(enabled.SelfShadowVolume));
                }

                var savedVolume = enabled.SelfShadowVolume;
                var savedLayout = enabled.SelfShadowLayout;
                EditorApplication.CallbackFunction cancelCompression = () =>
                {
                    if (ImpostorBaker.CurrentCapture.StartsWith("Compressing self-shadow volume:"))
                        ImpostorBaker.RequestCancellation();
                };
                bool cancelled = false;
                EditorApplication.update += cancelCompression;
                try { await ImpostorBaker.BakeDerivedSelfShadow(enabled, folder + "/Cancelled.asset", ImpostorSelfShadowQuality.High); }
                catch (OperationCanceledException) { cancelled = true; }
                finally { EditorApplication.update -= cancelCompression; }
                Assert.That(cancelled, Is.True, "Compression must remain cancellable.");
                Assert.That(enabled.SelfShadowVolume, Is.EqualTo(savedVolume));
                Assert.That(enabled.SelfShadowLayout, Is.EqualTo(savedLayout));
                Assert.That(enabled.HasValidSelfShadowVolume, Is.True);
                Assert.That(AssetDatabase.LoadAssetAtPath<Texture3D>(folder + "/Cancelled.asset"), Is.Null);

                settings.SelfShadowing = false;
                var disabled = await ImpostorAuthoring.Rebake(source, settings);
                Assert.That(disabled, Is.EqualTo(first));
                Assert.That(disabled.SelfShadowVolume == null, Is.True);
                Assert.That(disabled.Material.IsKeywordEnabled(ImpostorShaderKeywords.SELF_SHADOW_VOLUME), Is.False);
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(source)).OfType<Texture3D>(), Is.Empty);
                Assert.That(disabled.LodData.TryValidate(out string error), Is.True, error);
                AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(source), ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(source))
                    .GetComponent<Impostor>().Definition.SelfShadowVolume == null, Is.True);
            }
            finally
            {
                if (root) Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
