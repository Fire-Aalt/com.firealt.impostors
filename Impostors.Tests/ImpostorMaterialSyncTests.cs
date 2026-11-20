using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorMaterialSyncTests
    {
        private ImpostorDefinition _definition;
        private Material _source, _live;
        private ImpostorWorldRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _definition = Object.Instantiate(ImpostorTestAssets.Definition());
            _source = new Material(_definition.Material) { name = "Material sync " + Guid.NewGuid().ToString("N") };
            _definition.Material = _source;
            _definition.LodData = null;
            _renderer = new ImpostorWorldRenderer();
            _renderer.SetData(new ImpostorRenderData(new[] { _definition }, new[] { new ImpostorInstance { Scale = 1 } }));
            _live = Resources.FindObjectsOfTypeAll<Material>().Single(m => m != _source && m.name == _source.name);
            _renderer.SynchronizeEntities();
        }

        [TearDown]
        public void TearDown()
        {
            _renderer?.Dispose();
            Undo.ClearUndo(_source);
            Object.DestroyImmediate(_source);
            Object.DestroyImmediate(_definition);
        }

        [Test]
        public void FadingImpostorsStayNativeWhenOtherImpostorsShadeOffscreen()
        {
            MethodInfo setOffscreen = typeof(ImpostorWorldRenderer).GetMethod("SetOffscreenShading", BindingFlags.NonPublic | BindingFlags.Static);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(cube);
            var meshMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            ImpostorDefinition definition = Object.Instantiate(_definition);
            var source = new Material(_source) { name = "Native fade " + Guid.NewGuid().ToString("N") };
            definition.Material = source;
            definition.LodData = new ImpostorLodData
            {
                ReferencePoint = Vector3.zero, Size = 4,
                MeshLevels = new[] { new ImpostorMeshLod {
                    TransitionScreenHeight = .2f, FadeWidth = .2f,
                    Parts = new[] { new ImpostorLodPart { Mesh = mesh, Material = meshMaterial } }
                } }
            };
            var renderer = new ImpostorWorldRenderer();
            try
            {
                renderer.SetData(new ImpostorRenderData(new[] { definition }, new[] { new ImpostorInstance { Scale = 1 } }));
                Material[] live = Resources.FindObjectsOfTypeAll<Material>().Where(m => m != source && m.name == source.name).ToArray();
                Assert.That(live, Has.Length.EqualTo(2), "Fading impostors need their own material.");
                Material fading = live.Single(m => !m.GetShaderPassEnabled(ImpostorShaderPasses.OFFSCREEN_LIGHT_MODE));
                Material steady = live.Single(m => m != fading);

                setOffscreen.Invoke(null, new object[] { true });
                Assert.That(fading.GetShaderPassEnabled(ImpostorShaderPasses.FORWARD_LIGHT_MODE), Is.True);
                Assert.That(steady.GetShaderPassEnabled(ImpostorShaderPasses.FORWARD_LIGHT_MODE), Is.False);
                Assert.That(steady.GetShaderPassEnabled(ImpostorShaderPasses.OFFSCREEN_LIGHT_MODE), Is.True);

                source.SetFloat(ImpostorShaderProperties.SelfShadowStrength, .25f);
                Assert.That(renderer.RefreshMaterialProperties(), Is.True);
                Assert.That(fading.GetFloat(ImpostorShaderProperties.SelfShadowStrength), Is.EqualTo(.25f));
                Assert.That(fading.GetShaderPassEnabled(ImpostorShaderPasses.FORWARD_LIGHT_MODE), Is.True);
                Assert.That(fading.GetShaderPassEnabled(ImpostorShaderPasses.OFFSCREEN_LIGHT_MODE), Is.False);
            }
            finally
            {
                setOffscreen.Invoke(null, new object[] { false });
                renderer.Dispose();
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(definition);
                Object.DestroyImmediate(meshMaterial);
                Object.DestroyImmediate(mesh);
            }
        }

        [UnityTest]
        public IEnumerator EditorDetectsUnsavedMaterialChangesAutomatically()
        {
            _source.SetFloat(ImpostorShaderProperties.SelfShadowStrength, .125f);
            for (int i = 0; i < 4 && _live.GetFloat(ImpostorShaderProperties.SelfShadowStrength) != .125f; i++) yield return null;
            Assert.That(_live.GetFloat(ImpostorShaderProperties.SelfShadowStrength), Is.EqualTo(.125f));
            Assert.That(_renderer.UploadedBytesLastSync, Is.Zero);
        }
    }
}
