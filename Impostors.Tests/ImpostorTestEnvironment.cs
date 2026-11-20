using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    [SetUpFixture]
    public sealed class ImpostorTestEnvironment
    {
        private RenderPipelineAsset _graphics;
        private RenderPipelineAsset _quality;
        private UniversalRendererData _renderer;
        private ImpostorsRendererFeature _mainLight;
        private UniversalRenderPipelineAsset _pipeline;
        private int _antiAliasing;
        private string _graphicsDisk;
        private string _qualityDisk;

        [OneTimeSetUp]
        public void SetUp()
        {
            _graphics = GraphicsSettings.defaultRenderPipeline;
            _quality = QualitySettings.renderPipeline;
            _antiAliasing = QualitySettings.antiAliasing;
            _graphicsDisk = File.ReadAllText("ProjectSettings/GraphicsSettings.asset");
            _qualityDisk = File.ReadAllText("ProjectSettings/QualitySettings.asset");
            ImpostorTestAssets.Initialize();
            _renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            using (var serialized = new SerializedObject(_renderer))
            {
                serialized.FindProperty("m_RenderingMode").intValue = 2;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.CreateAsset(_renderer, ImpostorTestAssets.Folder + "/Renderer.asset");
            _mainLight = ScriptableObject.CreateInstance<ImpostorsRendererFeature>();
            AssetDatabase.AddObjectToAsset(_mainLight, _renderer);
            _renderer.rendererFeatures.Add(_mainLight);
            EditorUtility.SetDirty(_renderer);
            AssetDatabase.SaveAssetIfDirty(_renderer);
            _pipeline = UniversalRenderPipelineAsset.Create(_renderer);
            _pipeline.useSRPBatcher = true;
            _pipeline.shadowDistance = 0;
            _pipeline.supportsHDR = false;
            _pipeline.msaaSampleCount = 1;
            AssetDatabase.CreateAsset(_pipeline, ImpostorTestAssets.Folder + "/Pipeline.asset");
            AssetDatabase.SaveAssetIfDirty(_pipeline);
            GraphicsSettings.defaultRenderPipeline = _pipeline;
            QualitySettings.renderPipeline = _pipeline;
            var cameraObject = new GameObject("Activate test render pipeline", typeof(Camera));
            var target = new RenderTexture(16, 16, 24);
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.targetTexture = target;
                target.Create();
                camera.Render();
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.SameAs(_pipeline), "The test's persistent URP asset must be active before constructing BRG renderers.");
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            GraphicsSettings.defaultRenderPipeline = _graphics;
            QualitySettings.renderPipeline = _quality;
            QualitySettings.antiAliasing = _antiAliasing;
            using (var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings()))
            {
                graphics.FindProperty("m_CustomRenderPipeline").objectReferenceValue = _graphics;
                graphics.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var quality = new SerializedObject(QualitySettings.GetQualitySettings()))
            {
                quality.FindProperty("m_QualitySettings").GetArrayElementAtIndex(QualitySettings.GetQualityLevel())
                    .FindPropertyRelative("customRenderPipeline").objectReferenceValue = _quality;
                quality.ApplyModifiedPropertiesWithoutUndo();
            }
            ImpostorTestAssets.Dispose();
            RestoreSettingsFields("ProjectSettings/GraphicsSettings.asset", _graphicsDisk, "m_CustomRenderPipeline");
            RestoreSettingsFields("ProjectSettings/QualitySettings.asset", _qualityDisk, "customRenderPipeline|antiAliasing");
        }

        private static void RestoreSettingsFields(string path, string snapshot, string names)
        {
            // Asset imports during bake tests can serialize temporary pipeline overrides.
            // Restore only the fields this fixture owns, preserving unrelated settings.
            string pattern = @"(?m)^(?<prefix>\s*(?:" + names + @"):\s*)[^\r\n]*";
            MatchCollection previous = Regex.Matches(snapshot, pattern);
            string current = File.ReadAllText(path);
            int index = 0;
            string restored = Regex.Replace(current, pattern, match => previous[index++].Value);
            if (restored != current) File.WriteAllText(path, restored);
        }
    }
}
