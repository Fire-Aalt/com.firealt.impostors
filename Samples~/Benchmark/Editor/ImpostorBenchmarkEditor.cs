using System;
using UnityEditor;
using UnityEngine;

namespace Impostors.Samples.Benchmark.Editor
{
    [CustomEditor(typeof(ImpostorBenchmark))]
    public sealed class ImpostorBenchmarkEditor : UnityEditor.Editor
    {
        private const string SCRIPT_PROPERTY = "m_Script";
        private const string LAYOUT_LABEL = "Benchmark scaling";
        private const string X_LABEL = "X dimension";
        private const string Y_LABEL = "Y dimension";
        private const string CULLED_LABEL = "Culled %";
        private const string TOTAL_LABEL = "Instance count";
        private const string CURRENT_LABEL = "Current instances";
        private const string COUNT_FORMAT = "{0:N0}";
        private const string APPLY_LABEL = "Apply";
        private const string HELP = "Apply generates placements in memory, updates ground and fits the camera. Only "
            + "dimensions and definitions are saved. No instance array is serialized. Culled % controls camera framing. "
            + "The prefab LODGroup can additionally cull tiny trees.";
        private const string INVALID_DIMENSIONS =
            "X and Y must be positive, with a product no larger than 16,777,215 instances.";
        private const float MAXIMUM_CULLED_PERCENT = 100;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawScaling(serializedObject);
            EditorGUILayout.Space();
            DrawPropertiesExcluding(serializedObject, SCRIPT_PROPERTY, nameof(ImpostorBenchmark.DimensionX),
                nameof(ImpostorBenchmark.DimensionY), nameof(ImpostorBenchmark.CulledPercent));
            serializedObject.ApplyModifiedProperties();
        }

        internal static void DrawScaling(SerializedObject settings)
        {
            var benchmark = (ImpostorBenchmark)settings.targetObject;
            SerializedProperty x = settings.FindProperty(nameof(ImpostorBenchmark.DimensionX));
            SerializedProperty y = settings.FindProperty(nameof(ImpostorBenchmark.DimensionY));
            SerializedProperty culled = settings.FindProperty(nameof(ImpostorBenchmark.CulledPercent));
            EditorGUILayout.LabelField(LAYOUT_LABEL, EditorStyles.boldLabel);
            x.intValue = Mathf.Max(1, EditorGUILayout.IntField(X_LABEL, x.intValue));
            y.intValue = Mathf.Max(1, EditorGUILayout.IntField(Y_LABEL, y.intValue));
            long count = (long)x.intValue * y.intValue;
            EditorGUILayout.LabelField(TOTAL_LABEL, string.Format(COUNT_FORMAT, count));
            float percentage = EditorGUILayout.Slider(CULLED_LABEL, culled.floatValue, 0, MAXIMUM_CULLED_PERCENT);
            culled.floatValue = float.IsFinite(percentage) ? Mathf.Clamp(percentage, 0, MAXIMUM_CULLED_PERCENT) : 0;
            EditorGUILayout.HelpBox(HELP, MessageType.None);
            bool validCount = count <= ImpostorBenchmarkLayout.MaximumInstanceCount;
            if (!validCount)
            {
                EditorGUILayout.HelpBox(INVALID_DIMENSIONS, MessageType.Error);
            }

            using (new EditorGUI.DisabledScope(!validCount || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button(APPLY_LABEL))
                {
                    Apply(settings, benchmark);
                }
            }

            if (benchmark.InstanceCount > 0)
            {
                EditorGUILayout.LabelField(CURRENT_LABEL, string.Format(COUNT_FORMAT, benchmark.InstanceCount));
            }
        }

        private static void Apply(SerializedObject settings, ImpostorBenchmark benchmark)
        {
            settings.ApplyModifiedProperties();
            try
            {
                ImpostorBenchmarkLayout.Apply(benchmark);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, benchmark);
            }

            settings.Update();
        }
    }
}
