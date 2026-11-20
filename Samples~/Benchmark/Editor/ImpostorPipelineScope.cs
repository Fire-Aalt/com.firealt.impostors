using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Impostors.Data;
using Impostors.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Impostors.Samples.Benchmark.Editor
{
    /// <summary>Scoped only to an explicit sample build; no startup callbacks or persistent preferences.</summary>
    public sealed class ImpostorPipelineScope : IDisposable
    {
        private readonly RenderPipelineAsset _quality;
        private readonly RenderPipelineAsset _default;
        private readonly int _qualityLevel;
        public ImpostorPipelineScope(RenderPipelineAsset pipeline)
        {
            _qualityLevel = QualitySettings.GetQualityLevel();
            _quality = QualitySettings.renderPipeline;
            _default = GraphicsSettings.defaultRenderPipeline;
            QualitySettings.renderPipeline = pipeline;
            GraphicsSettings.defaultRenderPipeline = pipeline;
        }
        public void Dispose()
        {
            if (QualitySettings.GetQualityLevel() != _qualityLevel)
                QualitySettings.SetQualityLevel(_qualityLevel, false);
            QualitySettings.renderPipeline = _quality;
            GraphicsSettings.defaultRenderPipeline = _default;
            // The API overrides and native serialized settings may diverge during BuildPlayer.
            // Restore both, so a later ordinary settings save cannot reapply the build pipeline.
            using var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            graphics.FindProperty("m_CustomRenderPipeline").objectReferenceValue = _default;
            graphics.ApplyModifiedPropertiesWithoutUndo();
            using var quality = new SerializedObject(QualitySettings.GetQualitySettings());
            var levels = quality.FindProperty("m_QualitySettings");
            levels.GetArrayElementAtIndex(_qualityLevel).FindPropertyRelative("customRenderPipeline").objectReferenceValue = _quality;
            quality.FindProperty("m_CurrentQuality").intValue = _qualityLevel;
            quality.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
