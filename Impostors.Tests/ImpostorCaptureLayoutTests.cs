using Impostors.Data;
using NUnit.Framework;
using UnityEngine;

namespace Impostors.Tests
{
    public sealed class ImpostorCaptureLayoutTests
    {
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator DefaultBakeStoresCompactArrays()
        {
            var bake = ImpostorTestAssets.BakePrimitive();
            while (!bake.IsCompleted) yield return null;
            if (bake.IsFaulted) throw bake.Exception.GetBaseException();
            var (definition, folder) = bake.Result;
            try
            {
                Assert.That(definition.IsValid, Is.True);
                Assert.That(definition.BaseColor.width, Is.EqualTo(64));
                Assert.That(definition.Surface.width, Is.EqualTo(32));
                Assert.That(definition.Depth.width, Is.EqualTo(32));
                Assert.That(definition.Depth.format, Is.EqualTo(TextureFormat.BC4));
                Assert.That(definition.MaterialData, Is.Null);
                Assert.That(definition.Reflectance, Is.Null, "A uniform dielectric bake must not keep its reflectance array.");
                Assert.That(definition.Material.GetFloat(ImpostorShaderProperties.HasUniformReflectance), Is.EqualTo(1));
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }
    }
}
