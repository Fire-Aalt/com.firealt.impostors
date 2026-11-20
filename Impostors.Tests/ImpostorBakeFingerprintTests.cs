using System;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorBakeFingerprintTests
    {
        private string _folder;
        private GameObject _source;
        private ImpostorBakeSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/ImpostorsTestTemp/AuthoringTests-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(ImpostorTestAssets.TemporaryRoot, _folder.Substring(_folder.LastIndexOf('/') + 1));
            _settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
            AssetDatabase.CreateAsset(_settings, _folder + "/Settings.asset");
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                go.AddComponent<Impostor>().BakeSettings = _settings;
                _source = PrefabUtility.SaveAsPrefabAsset(go, _folder + "/Source.prefab");
            }
            finally { Object.DestroyImmediate(go); }
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(_folder);

        [Test]
        public void SavingInputsDoesNotPersistReferencedPackageAssets()
        {
            const string path = "Packages/com.firealt.impostors/Impostors.Tests/Fixtures/ReadOnlyInput.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Assert.That(material, Is.Not.Null);
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            string diskPath = System.IO.Path.Combine(package.resolvedPath, "Impostors.Tests/Fixtures/ReadOnlyInput.mat");
            byte[] previousBytes = System.IO.File.ReadAllBytes(diskPath);
            string previousState = EditorJsonUtility.ToJson(material);
            bool wasDirty = EditorUtility.IsDirty(material);
            EditPrefab(contents => contents.GetComponent<MeshRenderer>().sharedMaterial = material);
            try
            {
                material.SetColor("_BaseColor", Color.magenta);
                EditorUtility.SetDirty(material);
                ImpostorBakeFingerprint.SaveInputs(_source, _settings);
                Assert.That(System.IO.File.ReadAllBytes(diskPath), Is.EqualTo(previousBytes));
                Assert.That(EditorUtility.IsDirty(material), Is.True, "Package inputs must remain unsaved.");
            }
            finally
            {
                EditorJsonUtility.FromJsonOverwrite(previousState, material);
                if (wasDirty) EditorUtility.SetDirty(material);
                else EditorUtility.ClearDirty(material);
            }
        }

        private void EditPrefab(Action<GameObject> edit)
        {
            string path = AssetDatabase.GetAssetPath(_source);
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try { edit(contents); PrefabUtility.SaveAsPrefabAsset(contents, path); }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
    }
}
