using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorLitCaptureTests
    {
        [UnityTest]
        public IEnumerator CapturesMetallicSpecularAndReflectedGraphSurfaces()
        {
            Task task = Validate();
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static async Task Validate()
        {
            string folder = ImpostorTestAssets.NewFolder("LitCapture");
            GameObject root = null;
            try
            {
                var settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
                settings.Frames = 4; settings.Resolution = 32; settings.Border = 2;
                settings.HalfResolutionSurface = false; settings.CompactDepth = false; settings.KeepAdjustmentData = true;
                settings.Supersampling = 1; settings.AmbientSamples = 4; settings.SelfShadowing = false;
                AssetDatabase.CreateAsset(settings, folder + "/Settings.asset");
                for (int workflow = 0; workflow < 3; workflow++)
                {
                    Shader shader = workflow == 1
                        ? AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.firealt.impostors/Impostors.Tests/Fixtures/SpecularCapture.shadergraph")
                        : ImpostorTestAssets.CaptureShader;
                    Assert.That(shader, Is.Not.Null);
                    var material = new Material(shader);
                    if (workflow != 2)
                    {
                        material.SetColor("_BaseColor", new Color(.2f, .4f, .6f));
                        material.SetFloat("_Smoothness", .63f);
                        material.SetFloat("_Metallic", .7f);
                        material.SetColor("_EmissionColor", new Color(3, 2, 1));
                        material.SetVector("_Normal", new Vector4(.7071068f, 0, .7071068f, 0));
                        if (workflow == 1)
                        {
                            material.SetColor("_SpecColor", new Color(.15f, .25f, .35f));
                        }
                    }
                    AssetDatabase.CreateAsset(material, folder + "/Material" + workflow + ".mat");
                    root = new GameObject("Lit surface", typeof(LODGroup));
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.transform.SetParent(root.transform, false);
                    cube.transform.localPosition = new Vector3(.4f, 2, -.3f);
                    cube.transform.localRotation = Quaternion.Euler(0, 30, 0);
                    cube.transform.localScale = new Vector3(2, 3, 1);
                    cube.GetComponent<Renderer>().sharedMaterial = material;
                    root.GetComponent<LODGroup>().SetLODs(new[] { new LOD(.15f, new[] { cube.GetComponent<Renderer>() }) { fadeTransitionWidth = .2f } });
                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Source" + workflow + ".prefab");
                    Object.DestroyImmediate(root); root = null;
                    var baked = await ImpostorAuthoring.Rebake(prefab, settings);
                    Assert.That(baked.IsValid, Is.True);
                    Assert.That(baked.LodData.MeshLevels.SelectMany(level => level.Parts).All(part => part.Material.shader == shader), Is.True);
                    Assert.That(material.IsKeywordEnabled(ImpostorShaderKeywords.CAPTURE_ENABLED), Is.False);
                    Color[] colors = Read(baked.BaseColor), surfaces = Read(baked.Surface), depths = Read(baked.Depth);
                    // A uniform surface stores its reflectance as a material constant instead of an array.
                    Color[] reflectance = baked.Reflectance ? Read(baked.Reflectance)
                        : Enumerable.Repeat((Color)baked.Material.GetVector(ImpostorShaderProperties.UniformReflectance), colors.Length).ToArray();
                    Color[] materialData = Read(baked.MaterialData);
                    int[] covered = Enumerable.Range(0, colors.Length).Where(i => colors[i].a > .99f).ToArray();
                    Assert.That(covered.Length, Is.GreaterThan(100), "The capture must contain geometry.");
                    Assert.That(baked.MaterialData.isDataSRGB, Is.False);
                    Assert.That(covered.Average(i => materialData[i].b), Is.EqualTo(workflow == 0 ? .7f : 0).Within(.025), "Original metallic data must be captured.");
                    Assert.That(covered.Average(i => materialData[i].a), Is.EqualTo(workflow == 1 ? 1 : 0).Within(.025), "The source workflow must be retained per pixel.");
                    Assert.That(covered.All(i => float.IsFinite(ImpostorMath.DecodeNormal(new Vector2(materialData[i].r,materialData[i].g)).sqrMagnitude)), Is.True);
                    double normalDifference = covered.Average(i => Vector3.Angle(
                        ImpostorMath.DecodeNormal(new Vector2(materialData[i].r,materialData[i].g)),
                        ImpostorMath.DecodeNormal(new Vector2(surfaces[i].r,surfaces[i].g))));
                    Assert.That(normalDifference, workflow == 2 ? Is.LessThan(2) : Is.GreaterThan(10), "Geometric normals must be stored before the authored normal map.");
                    Assert.That(covered.Average(i => depths[i].r), Is.InRange(.25, .85));
                    float smoothness = workflow == 2 ? .35f : .63f;
                    Assert.That(covered.Average(i => surfaces[i].a), Is.EqualTo(smoothness).Within(.025));
                    Color baseLinear = new Color(.2f,.4f,.6f);
                    Color specularLinear = new Color(.15f,.25f,.35f);
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear) { baseLinear = baseLinear.linear; specularLinear = specularLinear.linear; }
                    Color expected = workflow == 0 ? Color.Lerp(new Color(.04f,.04f,.04f),baseLinear,.7f)
                        : workflow == 1 ? specularLinear : new Color(.04f,.04f,.04f);
                    expected.a = workflow == 0 ? .288f : workflow == 1 ? 1-specularLinear.maxColorComponent : .96f;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int c = channel;
                        Assert.That(covered.Average(i => reflectance[i][c]), Is.EqualTo(expected[c]).Within(.025), "Workflow " + workflow + ", reflectance channel " + c);
                    }
                    if (workflow == 2) Assert.That(baked.Emission, Is.Null);
                    else
                    {
                        Assert.That((bool)baked.Emission, Is.True, "Missing HDR emission for workflow " + workflow + "; keyword=" + material.IsKeywordEnabled("_EMISSION"));
                        Color[] emission = Read(baked.Emission);
                        // Shader Graph evaluates HDR properties in the project's
                        // surface color space before they reach the capture node.
                        float expectedEmission = QualitySettings.activeColorSpace == ColorSpace.Linear ? 3 : 1.055f * Mathf.Pow(3, 1f / 2.4f) - .055f;
                        Assert.That(covered.Average(i => emission[i].r), Is.EqualTo(expectedEmission).Within(.03));
                    }
                    baked.DepthFilterVersion = 0;
                    await ImpostorBaker.RefineDepth(prefab, baked, settings);
                    Color[] refined = Read(baked.Depth);
                    Assert.That(covered.Average(i => Mathf.Abs(refined[i].r-depths[i].r)), Is.LessThan(.001), $"Refinement workflow {workflow}; full mean={covered.Average(i=>depths[i].r)}, refined mean={covered.Average(i=>refined[i].r)}");
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(baked, out string guid, out long id);
                    var rebaked = await ImpostorAuthoring.Rebake(prefab, settings);
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(rebaked, out string newGuid, out long newId);
                    Assert.That((newGuid, newId), Is.EqualTo((guid, id)), "Rebake preserves definition identity.");
                }
            }
            finally
            {
                if (root) Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static Color[] Read(Texture2DArray texture)
        {
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.firealt.impostors/Impostors.Tests/Fixtures/ReadSurface.compute");
            using var pixels = new ComputeBuffer(texture.width * texture.width, 16);
            compute.SetTexture(0, "_Source", texture);
            compute.SetBuffer(0, "_Pixels", pixels);
            compute.SetInt("_Size", texture.width);
            compute.Dispatch(0, (texture.width+7)/8, (texture.width+7)/8, 1);
            var result = new Color[texture.width * texture.width];
            pixels.GetData(result);
            return result;
        }

        [UnityTest]
        public IEnumerator ExposedGraphTexturesClipAndCaptureAllNormalSpaces()
        {
            Task task = ValidateTextures();
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static async Task ValidateTextures()
        {
            string folder = ImpostorTestAssets.NewFolder("ExposedCapture");
            GameObject root = null;
            try
            {
                var settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
                settings.Frames = 4; settings.Resolution = 32; settings.Border = 2;
                settings.HalfResolutionSurface = false; settings.CompactDepth = false; settings.KeepAdjustmentData = true;
                settings.Supersampling = 1; settings.SelfShadowing = false; settings.AmbientSamples = 4;
                AssetDatabase.CreateAsset(settings, folder + "/Settings.asset");
                var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
                texture.SetPixels(Enumerable.Repeat(new Color(.25f, .5f, .75f, 1), 16).ToArray());
                texture.Apply(); AssetDatabase.CreateAsset(texture, folder + "/Source.asset");
                int originalCoverage = 0;
                for (int space = 0; space < 3; space++)
                {
                    string suffix = space == 0 ? "" : space == 1 ? "Object" : "World";
                    string path = "Packages/com.firealt.impostors/Impostors.Tests/Fixtures/ExposedTexture" + suffix + ".shadergraph";
                    var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(path));
                    material.SetTexture("_SourceTexture", texture);
                    AssetDatabase.CreateAsset(material, folder + "/Material" + space + ".mat");
                    root = new GameObject("Textured", typeof(LODGroup));
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(root.transform, false);
                    cube.transform.localRotation = Quaternion.Euler(0, 30, 0); cube.transform.localScale = new Vector3(2, 3, 1);
                    cube.GetComponent<Renderer>().sharedMaterial = material;
                    root.GetComponent<LODGroup>().SetLODs(new[] { new LOD(.15f, new[] { cube.GetComponent<Renderer>() }) });
                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Source" + space + ".prefab");
                    Object.DestroyImmediate(root); root = null;
                    var baked = await ImpostorAuthoring.Rebake(prefab, settings);
                    Color[] colors = Read(baked.BaseColor), surfaces = Read(baked.Surface);
                    int[] covered = Enumerable.Range(0, colors.Length).Where(i => colors[i].a > .99f).ToArray();
                    Assert.That(covered.Length, Is.GreaterThan(100));
                    Assert.That(covered.Average(i => colors[i].r), Is.EqualTo(.25f).Within(.025));
                    Assert.That(covered.Average(i => colors[i].b), Is.EqualTo(.75f).Within(.025));
                    // Frame zero sees the rotated cube's -X and -Z faces. Their
                    // projected areas differ because the child scale is 2:3:1.
                    Vector3 expected = space == 0 ? new Vector3(-3.75f, 0, -1.299038f).normalized : space == 1 ? new Vector3(.5f, 0, .8660254f) : Vector3.forward;
                    Vector3 average = covered.Select(i => ImpostorMath.DecodeNormal(new Vector2(surfaces[i].r, surfaces[i].g))).Aggregate(Vector3.zero, (a,b) => a+b).normalized;
                    Assert.That(Vector3.Dot(average, expected), Is.GreaterThan(.98f), "Normal space " + space + ": " + average);
                    if (space == 0) originalCoverage = covered.Length;
                }
                // An assigned exposed texture drives clipping, rather than an inferred hidden default.
                texture.SetPixels(Enumerable.Range(0, 16).Select(i => new Color(.25f, .5f, .75f, i%4 < 2 ? 0 : 1)).ToArray()); texture.Apply();
                EditorUtility.SetDirty(texture); AssetDatabase.SaveAssetIfDirty(texture);
                var clipped = await ImpostorAuthoring.Rebake(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Source0.prefab"), settings);
                int clippedCoverage = Read(clipped.BaseColor).Count(c => c.a > .99f);
                Assert.That(clippedCoverage, Is.InRange(originalCoverage * .2, originalCoverage * .8));


            }
            finally
            {
                if (root) Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
