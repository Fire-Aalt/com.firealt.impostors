using System.IO;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorBakeSettingsTests
    {
        private string _folder;

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
        public void NewSettingsCaptureTheLastLodWithEveryOptimization()
        {
            var settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
            try
            {
                Assert.That(settings.SourceLod, Is.EqualTo(ImpostorSourceLod.LastLod));
                Assert.That(settings.OptimizationFlags, Is.EqualTo(ImpostorBakeSettings.DEFAULT_OPTIMIZATIONS));
                Assert.That(settings.HalfResolutionSurface && settings.CompactDepth && settings.DiscardAdjustmentData, Is.True);

                settings.CompactDepth = false;
                Assert.That(settings.OptimizationFlags, Is.EqualTo(ImpostorOptimizationFlags.HalfResolutionSurface
                    | ImpostorOptimizationFlags.DiscardAdjustmentData));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [TestCase(0, 0, 0, 1, ImpostorSourceLod.Lod0, ImpostorOptimizationFlags.None)]
        [TestCase(2, 1, 0, 0, ImpostorSourceLod.LastLod,
            ImpostorOptimizationFlags.HalfResolutionSurface | ImpostorOptimizationFlags.DiscardAdjustmentData)]
        [TestCase(1, 0, 1, 1, ImpostorSourceLod.LastLod, ImpostorOptimizationFlags.CompactDepth)]
        public void LegacySettingsMigrateToSourceLodAndOptimizationFlags(int sourceLod, int halfSurface, int compactDepth,
            int keepAdjustment, ImpostorSourceLod expectedLod, ImpostorOptimizationFlags expectedFlags)
        {
            _folder = ImpostorTestAssets.NewFolder("Settings");
            string path = _folder + "/Legacy.asset";
            var probe = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
            string script = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(probe)));
            Object.DestroyImmediate(probe);
            File.WriteAllText(path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n"
                + "  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n"
                + "  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: 0}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n"
                + $"  m_Script: {{fileID: 11500000, guid: {script}, type: 3}}\n  m_Name: Legacy\n  m_EditorClassIdentifier: \n"
                + $"  Frames: 8\n  Resolution: 64\n  Border: 8\n  Supersampling: 2\n  SourceLod: {sourceLod}\n"
                + $"  HalfResolutionSurface: {halfSurface}\n  CompactDepth: {compactDepth}\n"
                + $"  KeepAdjustmentData: {keepAdjustment}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var settings = AssetDatabase.LoadAssetAtPath<ImpostorBakeSettings>(path);

            Assert.That(settings.SourceLod, Is.EqualTo(expectedLod));
            Assert.That(settings.OptimizationFlags, Is.EqualTo(expectedFlags));
        }

        [Test]
        public void SourceLodResolvesAgainstTheGroupAtBakeTime()
        {
            var root = new GameObject("Group", typeof(LODGroup));
            try
            {
                var group = root.GetComponent<LODGroup>();
                group.SetLODs(new[] { new LOD(.5f, new Renderer[0]), new LOD(.25f, new Renderer[0]), new LOD(.1f, new Renderer[0]) });

                Assert.That(ImpostorBakeSettings.ResolveSourceLod(ImpostorSourceLod.Lod0, group), Is.Zero);
                Assert.That(ImpostorBakeSettings.ResolveSourceLod(ImpostorSourceLod.LastLod, group), Is.EqualTo(2));
                group.SetLODs(new[] { new LOD(.1f, new Renderer[0]) });
                Assert.That(ImpostorBakeSettings.ResolveSourceLod(ImpostorSourceLod.LastLod, group), Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RealtimeShadowsNeedEveryLodRendererToCastShadows()
        {
            var root = new GameObject("Impostor", typeof(Impostor), typeof(LODGroup));
            try
            {
                Renderer near = GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<Renderer>();
                Renderer far = GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<Renderer>();
                near.transform.SetParent(root.transform);
                far.transform.SetParent(root.transform);
                root.GetComponent<LODGroup>().SetLODs(new[] { new LOD(.5f, new[] { near }), new LOD(.1f, new[] { far }) });
                var impostor = root.GetComponent<Impostor>();

                Assert.That(impostor.RealtimeShadows, Is.True);
                far.shadowCastingMode = ShadowCastingMode.Off;
                Assert.That(impostor.RealtimeShadows, Is.False);
                far.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                Assert.That(impostor.RealtimeShadows, Is.True);
                root.GetComponent<LODGroup>().SetLODs(new LOD[0]);
                Assert.That(Impostor.CastsRealtimeShadows(root.GetComponent<LODGroup>(), new Renderer[0]), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
