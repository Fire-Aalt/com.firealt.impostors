using System.Collections.Generic;
using System.Linq;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorMaterialRemapTests
    {
        private const string URP_LIT = "Universal Render Pipeline/Lit";

        private string _folder;

        private static Shader ImpostorLit => AssetDatabase.LoadAssetAtPath<Shader>(
            "Packages/com.firealt.impostors/" + ImpostorMaterialRemap.LIT_SHADER_PATH);

        private static Shader ImpostorFoliage => AssetDatabase.LoadAssetAtPath<Shader>(
            "Packages/com.firealt.impostors/" + ImpostorMaterialRemap.FOLIAGE_SHADER_PATH);

        [TearDown]
        public void TearDown()
        {
            if (_folder != null)
            {
                AssetDatabase.DeleteAsset(_folder);
                _folder = null;
            }
        }

        [Test]
        public void TransferMapsUrpLitPropertiesByNameAndAlias()
        {
            var texture = new Texture2D(4, 4);
            var normal = new Texture2D(4, 4);
            var source = new Material(Shader.Find(URP_LIT));
            var target = new Material(ImpostorLit);
            try
            {
                source.SetTexture("_BaseMap", texture);
                source.SetTextureScale("_BaseMap", new Vector2(2, 3));
                source.SetTextureOffset("_BaseMap", new Vector2(.25f, .5f));
                source.SetColor("_BaseColor", new Color(.1f, .2f, .3f, 1));
                source.SetTexture("_BumpMap", normal);
                source.SetFloat("_BumpScale", .7f);
                source.SetFloat("_Smoothness", .35f);
                source.SetColor("_EmissionColor", Color.white);
                source.DisableKeyword("_EMISSION");

                List<string> copied = ImpostorMaterialRemap.Transfer(source, target);

                Assert.That(target.GetTexture("_BaseMap"), Is.EqualTo(texture));
                Assert.That(target.GetTextureScale("_BaseMap"), Is.EqualTo(new Vector2(2, 3)));
                Assert.That(target.GetTextureOffset("_BaseMap"), Is.EqualTo(new Vector2(.25f, .5f)));
                AssertColor(target.GetColor("_BaseColor"), new Color(.1f, .2f, .3f, 1));
                Assert.That(target.GetTexture("_BumpMap"), Is.EqualTo(normal));
                Assert.That(target.GetFloat("_BumpScale"), Is.EqualTo(.7f));
                Assert.That(target.GetFloat("_Smoothness"), Is.EqualTo(.35f));
                // URP Lit keeps an emission colour while emission is switched off; the impostor must not glow.
                Assert.That(target.GetColor("_EmissionColor"), Is.EqualTo(Color.black));
                Assert.That(copied, Has.Member("_BaseMap←_BaseMap"));
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(normal);
            }
        }

        [Test]
        public void TransferFallsBackToAliasesAndInvertsRoughness()
        {
            var texture = new Texture2D(4, 4);
            var source = new Material(Shader.Find(URP_LIT));
            var target = new Material(ImpostorFoliage);
            try
            {
                // Only the legacy spellings carry values, so the base map and colour must come through the aliases.
                source.SetTexture("_BaseMap", null);
                source.SetTexture("_MainTex", texture);
                source.SetFloat("_Cutoff", .3f);

                ImpostorMaterialRemap.Transfer(source, target);

                Assert.That(target.GetTexture("_BaseMap"), Is.EqualTo(texture));
                Assert.That(target.GetFloat("_Cutoff"), Is.EqualTo(.3f));
                // URP's metallic map has no occlusion in G, so it must not become the foliage mask map.
                Assert.That(target.GetTexture("_MaskMap"), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ShaderGraphAssetsResolveAndStockLitIsRejected()
        {
            Object graph = AssetDatabase.LoadMainAssetAtPath(ImpostorTestAssets.CaptureGraphPath);

            Shader shader = ImpostorMaterialRemap.ResolveShader(graph);

            Assert.That(shader, Is.EqualTo(ImpostorTestAssets.CaptureShader));
            Assert.That(ImpostorCaptureMaterial.IsCaptureReadyShader(shader, out _), Is.True);
            Assert.That(ImpostorCaptureMaterial.IsCaptureReadyShader(Shader.Find(URP_LIT), out string reason), Is.False);
            Assert.That(reason, Is.Not.Empty);
            Assert.That(ImpostorMaterialRemap.ResolveShader(ImpostorTestAssets.CaptureShader), Is.EqualTo(shader));
            Assert.That(ImpostorMaterialRemap.ResolveShader(null), Is.Null);
        }

        [Test]
        public void RemapIsStoredOnTheImpostorAndSurvivesRegeneration()
        {
            GameObject prefab = CreateGeneratorPrefab(out Material source);
            Assert.That(ImpostorMaterialRemap.UncapturableMaterials(prefab, ImpostorSourceLod.Lod0), Is.EqualTo(new[] { source }));

            Dictionary<Material, Material> targets = ImpostorMaterialRemap.Remap(prefab,
                new Dictionary<Material, Shader> { [source] = ImpostorLit });

            Material copy = targets[source];
            Assert.That(AssetDatabase.GetAssetPath(copy), Is.EqualTo(_folder + "/Source_Impostor.mat"));
            Assert.That(copy.shader, Is.EqualTo(ImpostorLit));
            AssertColor(copy.GetColor("_BaseColor"), source.GetColor("_BaseColor"));
            Assert.That(prefab.GetComponent<Impostor>().MaterialRemaps.Single().Target, Is.EqualTo(copy));
            AssertAllLodsUse(prefab, copy);
            Assert.That(ImpostorMaterialRemap.UncapturableMaterials(prefab, ImpostorSourceLod.Lod0), Is.Empty);

            MeshLodGeneratorAuthoring.Generate(prefab);
            AssertAllLodsUse(prefab, copy);
        }

        [Test]
        public void RemapAgainUpdatesTheCopyInPlaceAndKeepsItsSettings()
        {
            GameObject prefab = CreateGeneratorPrefab(out Material source);
            Material copy = ImpostorMaterialRemap.Remap(prefab, new Dictionary<Material, Shader> { [source] = ImpostorLit })[source];
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(copy));
            copy.SetFloat("_Smoothness", .9f);
            copy.SetColor("_BaseColor", Color.red);
            AssetDatabase.SaveAssetIfDirty(copy);
            prefab.GetComponent<Impostor>().MaterialRemaps = new MaterialRemap[0];
            PrefabUtility.SavePrefabAsset(prefab);
            MeshLodGeneratorAuthoring.Generate(prefab);

            Material again = ImpostorMaterialRemap.Remap(prefab,
                new Dictionary<Material, Shader> { [source] = ImpostorFoliage })[source];

            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(again)), Is.EqualTo(guid));
            Assert.That(again, Is.EqualTo(copy));
            Assert.That(again.shader, Is.EqualTo(ImpostorFoliage));
            Assert.That(again.GetFloat("_Smoothness"), Is.EqualTo(.9f));
            Assert.That(again.GetColor("_BaseColor"), Is.EqualTo(Color.red));
            AssertAllLodsUse(prefab, again);
        }

        [Test]
        public void RemapingARemapTargetRepointsTheOriginalRemap()
        {
            GameObject prefab = CreateGeneratorPrefab(out Material source);
            Material copy = ImpostorMaterialRemap.Remap(prefab, new Dictionary<Material, Shader> { [source] = ImpostorLit })[source];
            var plain = new Material(Shader.Find(URP_LIT));
            AssetDatabase.CreateAsset(plain, _folder + "/Plain.mat");
            prefab.GetComponent<Impostor>().MaterialRemaps = new[] { new MaterialRemap { Source = source, Target = plain } };
            PrefabUtility.SavePrefabAsset(prefab);
            MeshLodGeneratorAuthoring.Generate(prefab);
            Assert.That(ImpostorMaterialRemap.UncapturableMaterials(prefab, ImpostorSourceLod.Lod0), Is.EqualTo(new[] { plain }));

            Material fixedCopy = ImpostorMaterialRemap.Remap(prefab,
                new Dictionary<Material, Shader> { [plain] = ImpostorLit })[plain];

            MaterialRemap remap = prefab.GetComponent<Impostor>().MaterialRemaps.Single();
            Assert.That(remap.Source, Is.EqualTo(source));
            Assert.That(remap.Target, Is.EqualTo(fixedCopy));
            Assert.That(fixedCopy, Is.Not.EqualTo(copy));
            AssertAllLodsUse(prefab, fixedCopy);
        }

        [Test]
        public void RemapPlainPrefabPatchesItsRenderers()
        {
            _folder = ImpostorTestAssets.NewFolder("Remap");
            Material source = CreateSourceMaterial();
            var root = new GameObject("Plain", typeof(Impostor), typeof(LODGroup));
            GameObject prefab;
            try
            {
                var shape = GameObject.CreatePrimitive(PrimitiveType.Cube);
                shape.transform.SetParent(root.transform, false);
                shape.GetComponent<MeshRenderer>().sharedMaterial = source;
                root.GetComponent<LODGroup>().SetLODs(new[] { new LOD(.1f, new Renderer[] { shape.GetComponent<MeshRenderer>() }) });
                prefab = PrefabUtility.SaveAsPrefabAsset(root, _folder + "/Plain.prefab");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            Material copy = ImpostorMaterialRemap.Remap(prefab, new Dictionary<Material, Shader> { [source] = ImpostorLit })[source];

            Assert.That(prefab.GetComponentInChildren<MeshRenderer>().sharedMaterial, Is.EqualTo(copy));
            Assert.That(prefab.GetComponent<Impostor>().MaterialRemaps.Single().Source, Is.EqualTo(source));
            Assert.That(ImpostorMaterialRemap.UncapturableMaterials(prefab, ImpostorSourceLod.Lod0), Is.Empty);
        }

        [Test]
        public void SyncRemapsRestoresTheSourceOfARemovedRemap()
        {
            var source = new Material(Shader.Find(URP_LIT));
            var copy = new Material(ImpostorLit);
            var other = new Material(Shader.Find(URP_LIT));
            var otherCopy = new Material(ImpostorLit);
            var root = new GameObject("Root");
            try
            {
                var first = GameObject.CreatePrimitive(PrimitiveType.Cube);
                first.transform.SetParent(root.transform);
                first.GetComponent<MeshRenderer>().sharedMaterials = new[] { copy, otherCopy };
                var previous = new Dictionary<Material, Material> { [source] = copy, [other] = otherCopy };
                var current = new Dictionary<Material, Material> { [other] = otherCopy };

                Assert.That(ImpostorMaterialRemap.SyncRemaps(root, previous, current), Is.True);

                Assert.That(first.GetComponent<MeshRenderer>().sharedMaterials, Is.EqualTo(new[] { source, otherCopy }));
                Assert.That(ImpostorMaterialRemap.SyncRemaps(root, current, current), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(copy);
                Object.DestroyImmediate(other);
                Object.DestroyImmediate(otherCopy);
            }
        }

        [Test]
        public void RemapRejectsShadersThatCantBeCaptured()
        {
            GameObject prefab = CreateGeneratorPrefab(out Material source);

            Assert.That(() => ImpostorMaterialRemap.Remap(prefab,
                new Dictionary<Material, Shader> { [source] = Shader.Find(URP_LIT) }), Throws.ArgumentException);
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(_folder + "/Source_Impostor.mat"), Is.Null);
        }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(Vector4.Distance(actual, expected), Is.LessThan(1e-4f), $"{actual} != {expected}");
        }

        private static void AssertAllLodsUse(GameObject prefab, Material material)
        {
            Renderer[] renderers = prefab.GetComponent<LODGroup>().GetLODs().SelectMany(lod => lod.renderers).ToArray();
            Assert.That(renderers, Is.Not.Empty);
            Assert.That(renderers.SelectMany(renderer => renderer.sharedMaterials), Is.All.EqualTo(material));
        }

        private Material CreateSourceMaterial()
        {
            var material = new Material(Shader.Find(URP_LIT));
            material.SetColor("_BaseColor", new Color(.2f, .6f, .3f, 1));
            AssetDatabase.CreateAsset(material, _folder + "/Source.mat");
            return material;
        }

        /// <summary>A model on URP Lit and a generated impostor prefab built from it.</summary>
        private GameObject CreateGeneratorPrefab(out Material source)
        {
            _folder = ImpostorTestAssets.NewFolder("Remap");
            source = CreateSourceMaterial();
            var model = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(model.GetComponent<Collider>());
            model.GetComponent<MeshRenderer>().sharedMaterial = source;
            var root = new GameObject("Impostor", typeof(Impostor), typeof(MeshLodGenerator));
            GameObject prefab;
            try
            {
                var generator = root.GetComponent<MeshLodGenerator>();
                generator.SourceModel = PrefabUtility.SaveAsPrefabAsset(model, _folder + "/Model.prefab");
                generator.LodCount = 2;
                prefab = PrefabUtility.SaveAsPrefabAsset(root, _folder + "/Impostor.prefab");
            }
            finally
            {
                Object.DestroyImmediate(model);
                Object.DestroyImmediate(root);
            }

            MeshLodGeneratorAuthoring.Generate(prefab);
            return prefab;
        }
    }
}
