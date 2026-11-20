using System;
using System.Linq;
using Impostors.Data;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    [CustomEditor(typeof(ImpostorBakeSettings))]
    [NoAutoStaticsCleanup]
    public sealed class ImpostorBakeSettingsEditor : UnityEditor.Editor
    {
        private const string LOD_SOURCE_LABEL = "LOD source prefab";
        private const string LAYOUT_LABEL = "Capture layout";
        private const string MATRIX_LABEL = "Angle matrix";
        private const string FINAL_RESOLUTION_LABEL = "Final resolution";
        private const string MEMORY_LABEL = "Baked texture memory";
        private const string SOURCE_LOD_LABEL = "Source LOD";
        private const string NO_LOD_GROUP_LABEL = "Choose a prefab with a LODGroup";
        private const string UNKNOWN_MEMORY = "n/a";
        private const string SQUARE_FORMAT = "{0:N0} × {0:N0}";
        private const string SQUARE_PIXELS_FORMAT = SQUARE_FORMAT + " px";
        private const string MEMORY_FORMAT = "{0:F1} MiB";
        private const string INVALID_PADDING =
            "Padding leaves no usable pixels. Reduce Border or increase the final resolution.";
        private const string UNKNOWN_SHADOW_MEMORY = "Choose a valid LOD source prefab to estimate self-shadow memory.";
        private const int DEFAULT_FRAMES = 16;
        private const int MINIMUM_FRAMES = 2;
        private const int BYTES_PER_MEBIBYTE = 1024 * 1024;
        private const float CONTROL_LABEL_WIDTH = 190;

        private static readonly int[] FrameOptions = { 4, 8, 12, 16 };

        [SerializeField]
        private GameObject _lodSource;

        public static void DrawSettings(SerializedObject settings, GameObject source = null)
        {
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            try
            {
                EditorGUIUtility.labelWidth = CONTROL_LABEL_WIDTH;
                int frames = DrawLayout(settings, out int resolution);
                DrawMemory(settings, source, frames, resolution);
                if (settings.FindProperty(nameof(ImpostorBakeSettings.Border)).intValue * 2 >= resolution)
                {
                    EditorGUILayout.HelpBox(INVALID_PADDING, MessageType.Error);
                }

                EditorGUILayout.Space();
                DrawSourceLod(settings.FindProperty(nameof(ImpostorBakeSettings.SourceLod)), source);
                DrawRemainingProperties(settings);
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }

        public static string[] SourceLodLabels(LODGroup group)
        {
            if (!group)
            {
                return Array.Empty<string>();
            }

            int count = group.lodCount;
            return Enumerable.Range(0, count).Select(index => SourceLodLabel(index, count)).ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            _lodSource = (GameObject)EditorGUILayout.ObjectField(LOD_SOURCE_LABEL, _lodSource, typeof(GameObject), false);
            DrawSettings(serializedObject, _lodSource);
            serializedObject.ApplyModifiedProperties();
        }

        private static string SourceLodLabel(int index, int count)
        {
            if (count == 1)
            {
                return "LOD0 - Best Quality / Best Transition";
            }

            if (index == 0)
            {
                return "LOD0 - Best Quality";
            }

            return index == count - 1 ? $"LOD{index} - Best Transition" : $"LOD{index}";
        }

        /// <summary>Draws the angle matrix and final resolution. Returns the frame count and the per-view resolution.</summary>
        private static int DrawLayout(SerializedObject settings, out int viewResolution)
        {
            SerializedProperty frames = settings.FindProperty(nameof(ImpostorBakeSettings.Frames));
            SerializedProperty resolution = settings.FindProperty(nameof(ImpostorBakeSettings.Resolution));
            EditorGUILayout.LabelField(LAYOUT_LABEL, EditorStyles.boldLabel);
            if (frames.intValue < MINIMUM_FRAMES
                || frames.intValue > ImpostorCaptureLayout.MAXIMUM_ATLAS_RESOLUTION / ImpostorCaptureLayout.MINIMUM_VIEW_RESOLUTION)
            {
                frames.intValue = DEFAULT_FRAMES;
            }

            int previousTotal = (int)Math.Min(ImpostorCaptureLayout.MAXIMUM_ATLAS_RESOLUTION,
                Math.Max(1L, (long)frames.intValue * resolution.intValue));
            int[] frameOptions = FrameOptions.Contains(frames.intValue)
                ? FrameOptions
                : FrameOptions.Append(frames.intValue).ToArray();
            frames.intValue = EditorGUILayout.IntPopup(MATRIX_LABEL, frames.intValue, SquareLabels(frameOptions), frameOptions);
            resolution.intValue = ImpostorCaptureLayout.ResolveViewResolution(frames.intValue, previousTotal);

            int[] totals = ImpostorCaptureLayout.ViewResolutionOptions(frames.intValue)
                .Select(value => value * frames.intValue)
                .ToArray();
            int selectedTotal = EditorGUILayout.IntPopup(FINAL_RESOLUTION_LABEL, frames.intValue * resolution.intValue,
                SquareLabels(totals), totals);
            resolution.intValue = selectedTotal / Mathf.Max(frames.intValue, 1);

            bool halfSurface = settings.FindProperty(nameof(ImpostorBakeSettings.HalfResolutionSurface)).boolValue;
            bool compactDepth = settings.FindProperty(nameof(ImpostorBakeSettings.CompactDepth)).boolValue;
            EditorGUILayout.LabelField("Base colour per angle", string.Format(SQUARE_PIXELS_FORMAT, resolution.intValue));
            EditorGUILayout.LabelField("Surface per angle",
                string.Format(SQUARE_PIXELS_FORMAT, halfSurface ? resolution.intValue / 2 : resolution.intValue));
            EditorGUILayout.LabelField("Depth per angle",
                string.Format(SQUARE_PIXELS_FORMAT, compactDepth ? resolution.intValue / 2 : resolution.intValue));
            EditorGUILayout.Space();
            viewResolution = resolution.intValue;
            return frames.intValue;
        }

        private static void DrawMemory(SerializedObject settings, GameObject source, int frames, int resolution)
        {
            EditorGUILayout.LabelField(MEMORY_LABEL, EditorStyles.boldLabel);
            bool halfSurface = settings.FindProperty(nameof(ImpostorBakeSettings.HalfResolutionSurface)).boolValue;
            bool compactDepth = settings.FindProperty(nameof(ImpostorBakeSettings.CompactDepth)).boolValue;
            bool adjustmentData = settings.FindProperty(nameof(ImpostorBakeSettings.KeepAdjustmentData)).boolValue;
            ImpostorCaptureLayout.ArrayMemory arrays = ImpostorCaptureLayout.EstimateArrays(frames, resolution, true,
                halfSurface, compactDepth, adjustmentData);
            ImpostorCaptureLayout.ArrayMemory rawArrays = ImpostorCaptureLayout.EstimateArrays(frames, resolution, false,
                halfSurface, compactDepth, adjustmentData);

            // A bake that proved its reflectance uniform stores no array, so count what it really holds.
            bool reflectanceOmitted = source && source.TryGetComponent(out Impostor impostor) && impostor.Definition
                && !impostor.Definition.Reflectance;
            long reflectance = reflectanceOmitted ? 0 : arrays.Reflectance;
            long rawReflectance = reflectanceOmitted ? 0 : rawArrays.Reflectance;
            long arrayTotal = arrays.Total - arrays.Reflectance + reflectance;
            long rawArrayTotal = rawArrays.Total - rawArrays.Reflectance + rawReflectance;
            bool estimateKnown = TryEstimateSelfShadow(settings, source, frames, resolution, out long shadow, out long rawShadow);

            DrawMemoryRow(string.Empty, "Compressed", "Uncompressed");
            DrawMemoryRow("Base colour", Memory(arrays.BaseColor), Memory(rawArrays.BaseColor));
            DrawMemoryRow("Surface", Memory(arrays.Surface), Memory(rawArrays.Surface));
            DrawMemoryRow("Depth", Memory(arrays.Depth), Memory(rawArrays.Depth));
            string reflectanceLabel = reflectanceOmitted ? "Reflectance (constant)" : "Reflectance (0 if uniform)";
            DrawMemoryRow(reflectanceLabel, Memory(reflectance), Memory(rawReflectance));
            if (adjustmentData)
            {
                DrawMemoryRow("Adjustment data", Memory(arrays.AdjustmentData), Memory(rawArrays.AdjustmentData));
            }

            DrawMemoryRow("Self-shadow", Memory(shadow, estimateKnown), Memory(rawShadow, estimateKnown));
            DrawMemoryRow("Total", Memory(arrayTotal + shadow, estimateKnown), Memory(rawArrayTotal + rawShadow, estimateKnown),
                true);
            if (!estimateKnown)
            {
                EditorGUILayout.HelpBox(UNKNOWN_SHADOW_MEMORY, MessageType.None);
            }
        }

        /// <summary>Estimates self-shadow volume bytes. Returns false when no valid source prefab defines its layout.</summary>
        private static bool TryEstimateSelfShadow(SerializedObject settings, GameObject source, int frames, int resolution,
            out long compressed, out long uncompressed)
        {
            compressed = 0;
            uncompressed = 0;
            if (!settings.FindProperty(nameof(ImpostorBakeSettings.SelfShadowing)).boolValue)
            {
                return true;
            }

            var quality = (ImpostorSelfShadowQuality)settings.FindProperty(nameof(ImpostorBakeSettings.SelfShadowQuality))
                .intValue;
            int sourceLod = settings.FindProperty(nameof(ImpostorBakeSettings.SourceLod)).intValue;
            if (!ImpostorSelfShadowPreview.TryGetLayout(source, sourceLod, resolution, quality, out ImpostorSelfShadowLayout layout))
            {
                return false;
            }

            compressed = layout.Bytes(frames);
            uncompressed = layout.Bytes(frames, false);
            return true;
        }

        private static void DrawRemainingProperties(SerializedObject settings)
        {
            bool selfShadows = settings.FindProperty(nameof(ImpostorBakeSettings.SelfShadowing)).boolValue;
            SerializedProperty property = settings.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.propertyPath is nameof(ImpostorBakeSettings.Frames) or nameof(ImpostorBakeSettings.Resolution)
                    or nameof(ImpostorBakeSettings.SourceLod))
                {
                    continue;
                }

                bool script = property.propertyType == SerializedPropertyType.ObjectReference
                    && property.objectReferenceValue is MonoScript;
                bool inactiveQuality = property.propertyPath == nameof(ImpostorBakeSettings.SelfShadowQuality) && !selfShadows;
                using (new EditorGUI.DisabledScope(script || inactiveQuality))
                {
                    EditorGUILayout.PropertyField(property, true);
                }
            }
        }

        private static string[] SquareLabels(int[] values)
        {
            return values.Select(value => string.Format(SQUARE_FORMAT, value)).ToArray();
        }

        private static string Memory(long bytes, bool known = true)
        {
            return known ? string.Format(MEMORY_FORMAT, (double)bytes / BYTES_PER_MEBIBYTE) : UNKNOWN_MEMORY;
        }

        private static void DrawMemoryRow(string label, string compressed, string uncompressed, bool bold = false)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUIStyle style = bold ? EditorStyles.boldLabel : EditorStyles.label;
                GUILayout.Label(label, style, GUILayout.Width(CONTROL_LABEL_WIDTH));
                GUILayout.Label(compressed, style);
                GUILayout.Label(uncompressed, style);
            }
        }

        private static void DrawSourceLod(SerializedProperty property, GameObject source)
        {
            LODGroup[] groups = source ? source.GetComponentsInChildren<LODGroup>(true) : Array.Empty<LODGroup>();
            string[] labels = groups.Length == 1 ? SourceLodLabels(groups[0]) : Array.Empty<string>();
            if (labels.Length == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Popup(SOURCE_LOD_LABEL, 0, new[] { NO_LOD_GROUP_LABEL });
                }

                return;
            }

            property.intValue = EditorGUILayout.Popup(new GUIContent(SOURCE_LOD_LABEL, property.tooltip),
                Mathf.Clamp(property.intValue, 0, labels.Length - 1), labels);
        }

        private void OnEnable()
        {
            if (_lodSource)
            {
                return;
            }

            // Opening settings must not load baked textures or search the project's assets.
            // The prefab inspector already has its authoring component loaded when opening its settings.
            foreach (Impostor authoring in Resources.FindObjectsOfTypeAll<Impostor>())
            {
                if (EditorUtility.IsPersistent(authoring) && authoring.BakeSettings == target)
                {
                    _lodSource = authoring.gameObject;
                    return;
                }
            }
        }
    }
}
