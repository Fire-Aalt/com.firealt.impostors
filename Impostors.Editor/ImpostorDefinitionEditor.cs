using Impostors.Data;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    [CustomEditor(typeof(ImpostorDefinition))]
    public sealed class ImpostorDefinitionEditor : UnityEditor.Editor
    {
        private const string PREFAB_LABEL = "Prefab";
        private const string TARGET_PREFAB_LABEL = "Target prefab";
        private const string LIGHTING_LABEL = "Lighting and surface";
        private const string BAKED_DATA_LABEL = "Baked assets and metadata (read only)";
        private const string LOD_PREVIEW_LABEL = "LOD ranges preview";
        private const string DEFINITION_DATA_LABEL = "Definition data";
        private const string MISSING_LOD_DATA =
            "Generate mesh LOD data by rebaking from the source prefab's Impostor component.";
        private const string PREVIEW_CAMERA_LABEL = "Distance preview camera";
        private const string PREVIEW_FIELD_OF_VIEW_LABEL = "Preview field of view";
        private const string LOD_EDIT_HINT = "Edit the prefab LODGroup to change transitions, then rebake from its Impostor "
            + "component. Preview distances assume one full-size instance and include Quality LOD Bias.";
        private const string ORTHOGRAPHIC_DISTANCE = "orthographic size controls LOD";
        private const string CULLED_RANGE_LABEL = "Prefab Culled range";
        private const string CULLED_RANGE_VALUE = "Impostor (no size-based culling)";
        private const string IMPOSTOR_LEVEL_NAME = "Impostor";

        private bool _bakedData;
        private Camera _previewCamera;
        private float _previewFov = 60;

        public override void OnInspectorGUI()
        {
            var definition = (ImpostorDefinition)target;
            serializedObject.Update();
            EditorGUILayout.LabelField(PREFAB_LABEL, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ImpostorDefinition.SourcePrefab)),
                new GUIContent(TARGET_PREFAB_LABEL));
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(LIGHTING_LABEL, EditorStyles.boldLabel);
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ImpostorDefinition.Cutoff)));
            bool lightingChanged = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            if (lightingChanged)
            {
                EditorApplication.delayCall += () => Reload(definition);
            }

            _bakedData = EditorGUILayout.Foldout(_bakedData, BAKED_DATA_LABEL, true);
            if (!_bakedData)
            {
                return;
            }

            EditorGUI.indentLevel++;
            try
            {
                DrawBakedData(definition);
            }
            finally
            {
                EditorGUI.indentLevel--;
            }
        }

        internal static void DrawLodSummary(ImpostorLodData profile, ref Camera camera, ref float fov)
        {
            camera = (Camera)EditorGUILayout.ObjectField(PREVIEW_CAMERA_LABEL, camera, typeof(Camera), true);
            if (!camera)
            {
                fov = EditorGUILayout.Slider(PREVIEW_FIELD_OF_VIEW_LABEL, fov, 1, 179);
            }

            EditorGUILayout.HelpBox(LOD_EDIT_HINT, MessageType.None);
            float fieldOfView = camera ? camera.fieldOfView : fov;
            float factor = profile.Size * QualitySettings.lodBias / (2 * Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f));
            bool orthographic = camera && camera.orthographic;
            using (new EditorGUI.DisabledScope(true))
            {
                for (int i = 0; i < profile.MeshLevels.Length; i++)
                {
                    ImpostorMeshLod level = profile.MeshLevels[i];
                    string next = i == profile.MeshLevels.Length - 1 ? IMPOSTOR_LEVEL_NAME : $"LOD {i + 1}";
                    string distance = orthographic ? ORTHOGRAPHIC_DISTANCE : $"about {factor / level.TransitionScreenHeight:N1} m";
                    EditorGUILayout.TextField($"LOD {i} → {next}",
                        $"{level.TransitionScreenHeight:P2} · {distance} · fade {level.FadeWidth:P0}");
                }

                EditorGUILayout.LabelField(CULLED_RANGE_LABEL, CULLED_RANGE_VALUE);
            }
        }

        private static void Reload(ImpostorDefinition definition)
        {
            if (!definition)
            {
                return;
            }

            ImpostorWorldRenderer.RefreshMaterialProperties(definition);
            SceneView.RepaintAll();
        }

        private void DrawBakedData(ImpostorDefinition definition)
        {
            EditorGUILayout.LabelField(LOD_PREVIEW_LABEL, EditorStyles.boldLabel);
            if (definition.LodData != null)
            {
                DrawLodSummary(definition.LodData, ref _previewCamera, ref _previewFov);
                if (!definition.LodData.TryValidate(out string error))
                {
                    EditorGUILayout.HelpBox(error, MessageType.Error);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(MISSING_LOD_DATA, MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(DEFINITION_DATA_LABEL, EditorStyles.boldLabel);
            ImpostorReadOnlyInspector.Draw(serializedObject, nameof(ImpostorDefinition.SourcePrefab),
                nameof(ImpostorDefinition.Cutoff));
        }
    }
}
