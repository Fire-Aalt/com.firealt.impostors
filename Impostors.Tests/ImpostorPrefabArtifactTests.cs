using System;
using System.Linq;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorPrefabArtifactTests
    {
        private string _folder;
        private GameObject _prefab;
        private ImpostorDefinition _definition;

        [SetUp]
        public void SetUp()
        {
            string name = "ArtifactTest-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(ImpostorTestAssets.TemporaryRoot, name);
            _folder = ImpostorTestAssets.TemporaryRoot + "/" + name;
            var root = new GameObject(name, typeof(Impostor));
            try { _prefab = PrefabUtility.SaveAsPrefabAsset(root, _folder + "/Target.prefab"); }
            finally { Object.DestroyImmediate(root); }
            _definition = Embed(ScriptableObject.CreateInstance<ImpostorDefinition>(), "Impostor Definition");
            _definition.SourcePrefab = _prefab;
            _definition.BakeData = new ImpostorBakeData();
            _definition.Proxy = Embed(new Mesh(), "Proxy");
            ImpostorAuthoring.Attach(_prefab, null, _definition);
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(_folder);

        [Test]
        public void CleanupPreservesArtifactsReferencedByUnrelatedUserSubassets()
        {
            var protectedMesh = Embed(Object.Instantiate(_definition.Proxy), _definition.Proxy.name);
            var userMaterial = Embed(new Material(Shader.Find("Universal Render Pipeline/Lit")), "User material");
            var userTexture = Embed(new Texture2D(2, 2), "User texture");
            userMaterial.SetTexture("_BaseMap", userTexture);
            // A prefab component deliberately references an older proxy. It is still in use.
            var contents = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(_prefab));
            try
            {
                contents.AddComponent<MeshFilter>().sharedMesh = protectedMesh;
                PrefabUtility.SaveAsPrefabAsset(contents, AssetDatabase.GetAssetPath(_prefab));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }

            Assert.That(ImpostorPrefabArtifacts.PruneUnused(_definition), Is.Zero);
            Assert.That(ImpostorPrefabArtifacts.PruneUnused(_definition), Is.Zero);
            Assert.That(protectedMesh && userMaterial && userTexture, Is.True);
            Assert.That(userMaterial.GetTexture("_BaseMap"), Is.EqualTo(userTexture));
            Assert.That(_definition.BakeData.Artifacts, Has.No.Member(userMaterial));
        }

        private T Embed<T>(T value, string name) where T : Object
        {
            value.name = name;
            AssetDatabase.AddObjectToAsset(value, _prefab);
            return value;
        }
    }
}
