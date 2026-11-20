using System.Linq;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorCaptureContractTests
    {

        [Test]
        public void RawSurfaceCaptureIgnoresSceneEffectsAndRestoresDrawState()
        {
            string[] keywords = { "FOG_LINEAR", "DEBUG_DISPLAY", "_DBUFFER_MRT3", "_SCREEN_SPACE_OCCLUSION" };
            bool[] originalKeywords = keywords.Select(Shader.IsKeywordEnabled).ToArray();
            string[] lightingNames = { "_MainLightPosition", "_MainLightColor", "unity_SHAr", "unity_SHAg", "unity_SHAb", "unity_SpecCube0_HDR" };
            Vector4[] originalLighting = lightingNames.Select(Shader.GetGlobalVector).ToArray();
            Texture originalReflection = Shader.GetGlobalTexture("unity_SpecCube0");
            Matrix4x4 originalView = Shader.GetGlobalMatrix("unity_MatrixV");
            Vector4 originalTime = Shader.GetGlobalVector("_TimeParameters");
            var source = new Material(ImpostorTestAssets.CaptureShader);
            Material capture = null;
            var shape = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var target = new RenderTexture(16, 16, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(16, 16, TextureFormat.RGBAFloat, false, true);
            var reflection = new Cubemap(2, TextureFormat.RGBAHalf, false);
            RenderTexture previousTarget = RenderTexture.active;
            string[] shaderAssets = AssetDatabase.FindAssets("t:Shader");
            try
            {
                source.SetFloat("_Metallic", .7f);
                source.SetFloat("_Smoothness", .63f);
                source.SetFloat("_Occlusion", .4f);
                source.SetColor("_EmissionColor", new Color(3, 2, 1));
                capture = ImpostorCaptureMaterial.Create(source);
                int checksum = source.ComputeCRC();
                target.Create();
                Mesh mesh = shape.GetComponent<MeshFilter>().sharedMesh;
                Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1))
                    * Matrix4x4.TRS(new Vector3(0, 0, -2), Quaternion.identity, Vector3.one).inverse;
                Matrix4x4 projection = Matrix4x4.Ortho(-1, 1, -1, 1, .1f, 10);
                Color[] neutral = Draw(false);
                Color[] effects = Draw(true);
                for (int mode = 0; mode < neutral.Length; mode++)
                {
                    Assert.That(Vector4.Distance(neutral[mode], effects[mode]), Is.LessThan(.002), "Capture channel " + mode);
                }
                Assert.That(neutral[2].r, Is.EqualTo(.4f).Within(.002));
                Assert.That(neutral[2].g, Is.EqualTo(.63f).Within(.002));
                Assert.That(neutral[2].b, Is.EqualTo(.288f).Within(.002));
                float expectedEmission = QualitySettings.activeColorSpace == ColorSpace.Linear ? 3 : 1.055f * Mathf.Pow(3, 1f / 2.4f) - .055f;
                Assert.That(neutral[4].r, Is.EqualTo(expectedEmission).Within(.003));
                Assert.That(neutral[6].b, Is.EqualTo(.7f).Within(.002));
                Assert.That(source.ComputeCRC(), Is.EqualTo(checksum));
                Assert.That(AssetDatabase.FindAssets("t:Shader"), Is.EquivalentTo(shaderAssets));

                Color[] Draw(bool effectsEnabled)
                {
                    Shader.SetGlobalVector("_MainLightPosition", new Vector4(0, 0, -1, 0));
                    Shader.SetGlobalVector("_MainLightColor", effectsEnabled ? new Vector4(9, 4, 2, 1) : Vector4.zero);
                    for (int i = 2; i < 5; i++)
                        Shader.SetGlobalVector(lightingNames[i], new Vector4(0, 0, 0, effectsEnabled ? 3 : 0));
                    Shader.SetGlobalVector("unity_SpecCube0_HDR", new Vector4(1, 1, 0, 0));
                    Color reflectionColor = effectsEnabled ? new Color(5, 4, 3, 1) : Color.black;
                    for (int face = 0; face < 6; face++)
                        reflection.SetPixels(Enumerable.Repeat(reflectionColor, 4).ToArray(), (CubemapFace)face);
                    reflection.Apply();
                    Shader.SetGlobalTexture("unity_SpecCube0", reflection);
                    foreach (string keyword in keywords)
                    {
                        if (effectsEnabled) Shader.EnableKeyword(keyword);
                        else Shader.DisableKeyword(keyword);
                    }
                    var sentinel = Matrix4x4.Translate(new Vector3(11, 12, 13));
                    var time = new Vector4(123, .2f, .3f, 0);
                    Shader.SetGlobalMatrix("unity_MatrixV", sentinel);
                    Shader.SetGlobalVector("_TimeParameters", time);
                    var result = new Color[7];
                    for (int mode = 0; mode < result.Length; mode++)
                    {
                        using var commands = new CommandBuffer();
                        commands.SetRenderTarget(target);
                        commands.ClearRenderTarget(true, true, Color.clear);
                        using (new ImpostorCaptureState(commands, view, GL.GetGPUProjectionMatrix(projection, true), projection, 16))
                        {
                            var properties = new MaterialPropertyBlock();
                            properties.SetInteger(ImpostorShaderProperties.ImpostorCaptureMode, mode);
                            properties.SetFloat(ImpostorShaderProperties.CaptureRadius, 1);
                            properties.SetVector(ImpostorShaderProperties.CaptureDirection, Vector3.back);
                            properties.SetVector(ImpostorShaderProperties.CaptureCenter, Vector3.zero);
                            commands.DrawMesh(mesh, Matrix4x4.identity, capture, 0, capture.FindPass(ImpostorShaderPasses.FORWARD_LIT), properties);
                        }
                        Graphics.ExecuteCommandBuffer(commands);
                        Assert.That(Shader.GetGlobalMatrix("unity_MatrixV"), Is.EqualTo(sentinel));
                        Assert.That(Shader.GetGlobalVector("_TimeParameters"), Is.EqualTo(time));
                        foreach (string keyword in keywords) Assert.That(Shader.IsKeywordEnabled(keyword), Is.EqualTo(effectsEnabled), keyword);
                        RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
                        pixels.Apply();
                        result[mode] = pixels.GetPixel(8, 8);
                    }
                    return result;
                }
            }
            finally
            {
                for (int i = 0; i < keywords.Length; i++)
                {
                    if (originalKeywords[i]) Shader.EnableKeyword(keywords[i]);
                    else Shader.DisableKeyword(keywords[i]);
                }
                Shader.SetGlobalMatrix("unity_MatrixV", originalView);
                Shader.SetGlobalVector("_TimeParameters", originalTime);
                for (int i = 0; i < lightingNames.Length; i++) Shader.SetGlobalVector(lightingNames[i], originalLighting[i]);
                Shader.SetGlobalTexture("unity_SpecCube0", originalReflection);
                RenderTexture.active = previousTarget;
                Object.DestroyImmediate(shape);
                Object.DestroyImmediate(capture);
                Object.DestroyImmediate(source);
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(reflection);
            }
        }
    }
}
