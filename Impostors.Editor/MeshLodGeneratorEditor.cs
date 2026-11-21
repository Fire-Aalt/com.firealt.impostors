using System;
using System.Collections.Generic;
using System.Linq;
using Impostors.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    [CustomEditor(typeof(MeshLodGenerator))]
    public sealed class MeshLodGeneratorEditor : UnityEditor.Editor
    {
        private const string COUNT_LABEL = "LOD Count";
        private const string REDUCTION_LABEL = "Reduction per LOD";
        private const string GENERATE_LABEL = "Generate LODs";
        private const string SAVE_AND_GENERATE_LABEL = "Save and generate LODs";
        private const string AUTO_LABEL = "Auto LOD distances";
        private const string AUTO_TOOLTIP = "Spaces the transitions between LOD0 and the impostor so on-screen triangle "
            + "density stays roughly constant. Needs three or more up-to-date LODs.";
        private const string AUTO_UNDO = "Auto LOD distances";
        private const string LOD_LABEL = "LOD{0}";
        private const string TARGET_LABEL = "Target";
        private const string ACTUAL_LABEL = "Actual";
        private const string OVERRIDE_LABEL = "Override";
        private const string TRIANGLES_LABEL = "{0:N0} tris ({1:P0})";
        private const string DUPLICATE_TOOLTIP = "Same triangle count as LOD{0}. Raise Reduction or lower the LOD count.";
        private const string MISSING_MODEL = "Assign a model or prefab. Its mesh renderers become LOD0.";
        private const string NOT_GENERATED = "Not generated.";
        private const string OUTDATED = "Outdated: the source model or the LOD settings have changed.";
        private const string UP_TO_DATE = "Up to date. Rebake the impostor after regenerating.";
        private const string INVALID_SOURCE = "Generate from the prefab asset or Prefab Mode, on the prefab root.";
        private const string CLAMPED = "Some transitions were moved to keep them above the impostor start.";
        private const float LOD_WIDTH = 42;
        private const float TARGET_WIDTH = 48;
        private const float ACTUAL_WIDTH = 140;
        private const float TOGGLE_WIDTH = 16;
        private const int AUTO_MIN_LODS = 3;

        // Chain triangle counts per source mesh, so slider changes update the counts without generating anything.
        private static readonly Dictionary<Mesh, (Hash128 Dependency, int[] Levels)> ChainLevels = new();

        private string _autoMessage;

        private static bool IsBusy => ImpostorAuthoring.IsBaking || ImpostorBaker.IsBaking
            || EditorApplication.isPlayingOrWillChangePlaymode;

        public override void OnInspectorGUI()
        {
            var generator = (MeshLodGenerator)target;
            PrefabStage stage = PrefabStageUtility.GetPrefabStage(generator.gameObject);
            GameObject source = ImpostorEditor.ResolveSource(generator, stage);
            bool instance = !EditorUtility.IsPersistent(generator) && stage == null;
            bool validSource = source && !source.transform.parent && !generator.transform.parent && !instance;
            bool wasUnsaved = stage != null && stage.scene.isDirty;
            bool hadModel = generator.SourceModel;

            DrawSettings();
            MeshRenderer[] renderers = TrySourceRenderers(generator.SourceModel, out string error);
            int[] triangles = renderers != null ? DrawLevels(generator, renderers) : null;
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, generator.SourceModel ? MessageType.Error : MessageType.Info);
            }

            DrawStatus(generator);
            if (!validSource)
            {
                EditorGUILayout.HelpBox(INVALID_SOURCE, MessageType.Info);
            }

            bool unsaved = stage != null && stage.scene.isDirty;
            bool canGenerate = validSource && renderers != null && !IsBusy;
            DrawButtons(generator, triangles, canGenerate, unsaved, stage);
            if (!hadModel && generator.SourceModel && !generator.GeneratedRoot && canGenerate && !wasUnsaved)
            {
                ScheduleGenerate(AssetDatabase.GetAssetPath(source), unsaved ? stage : null);
            }
        }

        private void DrawSettings()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(MeshLodGenerator.SourceModel)));
            SerializedProperty count = serializedObject.FindProperty(nameof(MeshLodGenerator.LodCount));
            EditorGUILayout.PropertyField(count, new GUIContent(COUNT_LABEL, count.tooltip));
            SerializedProperty reduction = serializedObject.FindProperty(nameof(MeshLodGenerator.Reduction));
            EditorGUILayout.PropertyField(reduction, new GUIContent(REDUCTION_LABEL, reduction.tooltip));
            ResizeOverrides(serializedObject.FindProperty(nameof(MeshLodGenerator.Overrides)), count.intValue);
            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>Keeps one override per LOD. New LODs start without an override.</summary>
        private static void ResizeOverrides(SerializedProperty overrides, int count)
        {
            int previous = overrides.arraySize;
            overrides.arraySize = count;
            for (int lod = previous; lod < count; lod++)
            {
                SerializedProperty element = overrides.GetArrayElementAtIndex(lod);
                element.FindPropertyRelative(nameof(MeshLodOverride.Enabled)).boolValue = false;
                element.FindPropertyRelative(nameof(MeshLodOverride.Keep)).floatValue = 1;
            }
        }

        private static MeshRenderer[] TrySourceRenderers(GameObject model, out string error)
        {
            error = null;
            if (!model)
            {
                error = MISSING_MODEL;
                return null;
            }

            try
            {
                return MeshLodGeneratorAuthoring.SourceRenderers(model);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                error = exception.Message;
                return null;
            }
        }

        /// <summary>Draws the per-LOD table and returns each LOD's actual triangle count.</summary>
        private int[] DrawLevels(MeshLodGenerator generator, MeshRenderer[] renderers)
        {
            float[] ratios = MeshLodGeneratorAuthoring.TargetRatios(generator);
            int[] triangles = MeshLodGeneration.LodTriangles(renderers.Select(renderer => CachedLevels(SharedMesh(renderer))),
                ratios);
            serializedObject.Update();
            SerializedProperty overrides = serializedObject.FindProperty(nameof(MeshLodGenerator.Overrides));
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(GUIContent.none, GUILayout.Width(LOD_WIDTH));
                GUILayout.Label(TARGET_LABEL, EditorStyles.miniBoldLabel, GUILayout.Width(TARGET_WIDTH));
                GUILayout.Label(ACTUAL_LABEL, EditorStyles.miniBoldLabel, GUILayout.Width(ACTUAL_WIDTH));
                GUILayout.Label(OVERRIDE_LABEL, EditorStyles.miniBoldLabel);
            }

            for (int lod = 0; lod < ratios.Length; lod++)
            {
                DrawLevel(lod, ratios[lod], triangles, overrides);
            }

            serializedObject.ApplyModifiedProperties();
            return triangles;
        }

        private static void DrawLevel(int lod, float ratio, int[] triangles, SerializedProperty overrides)
        {
            using var row = new EditorGUILayout.HorizontalScope();
            GUILayout.Label(string.Format(LOD_LABEL, lod), GUILayout.Width(LOD_WIDTH));
            GUILayout.Label(ratio.ToString("P0"), GUILayout.Width(TARGET_WIDTH));
            GUILayout.Label(string.Format(TRIANGLES_LABEL, triangles[lod], (float)triangles[lod] / triangles[0]),
                GUILayout.Width(ACTUAL_WIDTH));
            if (lod == 0)
            {
                return;
            }

            SerializedProperty element = overrides.GetArrayElementAtIndex(lod);
            SerializedProperty enabled = element.FindPropertyRelative(nameof(MeshLodOverride.Enabled));
            SerializedProperty keep = element.FindPropertyRelative(nameof(MeshLodOverride.Keep));
            bool wasEnabled = enabled.boolValue;
            enabled.boolValue = EditorGUILayout.Toggle(enabled.boolValue, GUILayout.Width(TOGGLE_WIDTH));
            if (enabled.boolValue && !wasEnabled)
            {
                keep.floatValue = ratio;
            }

            if (enabled.boolValue)
            {
                keep.floatValue = EditorGUILayout.Slider(keep.floatValue, 0, 1);
            }
            else
            {
                GUILayout.FlexibleSpace();
            }

            if (triangles[lod] == triangles[lod - 1])
            {
                GUIContent warning = EditorGUIUtility.IconContent("console.warnicon.sml");
                warning.tooltip = string.Format(DUPLICATE_TOOLTIP, lod - 1);
                GUILayout.Label(warning, GUILayout.Width(EditorGUIUtility.singleLineHeight));
            }
        }

        private void DrawStatus(MeshLodGenerator generator)
        {
            if (!generator.SourceModel)
            {
                return;
            }

            bool generated = generator.GeneratedRoot;
            bool outdated = MeshLodGeneratorAuthoring.IsOutdated(generator);
            string status = !generated ? NOT_GENERATED : outdated ? OUTDATED : UP_TO_DATE;
            EditorGUILayout.HelpBox(status, outdated ? MessageType.Warning : MessageType.Info);
            if (_autoMessage != null)
            {
                EditorGUILayout.HelpBox(_autoMessage, MessageType.Info);
            }
        }

        private void DrawButtons(MeshLodGenerator generator, int[] triangles, bool canGenerate, bool unsaved, PrefabStage stage)
        {
            using var row = new EditorGUILayout.HorizontalScope();
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button(unsaved ? SAVE_AND_GENERATE_LABEL : GENERATE_LABEL))
                {
                    GameObject source = ImpostorEditor.ResolveSource(generator, stage);
                    ScheduleGenerate(AssetDatabase.GetAssetPath(source), unsaved ? stage : null);
                }
            }

            LODGroup group = generator.GetComponentInChildren<LODGroup>(true);
            bool canAuto = canGenerate && triangles != null && generator.LodCount >= AUTO_MIN_LODS && group
                && group.lodCount == generator.LodCount && !MeshLodGeneratorAuthoring.IsOutdated(generator);
            using (new EditorGUI.DisabledScope(!canAuto))
            {
                if (GUILayout.Button(new GUIContent(AUTO_LABEL, AUTO_TOOLTIP)))
                {
                    _autoMessage = ApplyAutoDistances(group, triangles) ? CLAMPED : null;
                }
            }
        }

        /// <summary>Generates after the current GUI event, saving Prefab Mode's changes first when a stage is given.</summary>
        private void ScheduleGenerate(string path, PrefabStage save)
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (save != null)
                    {
                        PrefabUtility.SaveAsPrefabAsset(save.prefabContentsRoot, save.assetPath);
                        save.ClearDirtiness();
                    }

                    MeshLodGeneratorAuthoring.Generate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, AssetDatabase.LoadAssetAtPath<GameObject>(path));
                }

                if (this)
                {
                    Repaint();
                }
            };
        }

        /// <summary>Applies density-preserving transitions with Undo. Returns whether any level had to be clamped.</summary>
        private static bool ApplyAutoDistances(LODGroup group, int[] triangles)
        {
            LOD[] lods = group.GetLODs();
            float[] ratios = triangles.Select(count => (float)count / triangles[0]).ToArray();
            float[] heights = MeshLodGeneration.AutoTransitions(lods[0].screenRelativeTransitionHeight,
                lods[^1].screenRelativeTransitionHeight, ratios, out bool clamped);
            Undo.RecordObject(group, AUTO_UNDO);
            for (int lod = 0; lod < lods.Length; lod++)
            {
                lods[lod].screenRelativeTransitionHeight = heights[lod];
            }

            group.SetLODs(lods);
            if (EditorUtility.IsPersistent(group))
            {
                PrefabUtility.SavePrefabAsset(group.transform.root.gameObject);
            }

            return clamped;
        }

        private static int[] CachedLevels(Mesh mesh)
        {
            Hash128 dependency = AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(mesh));
            if (ChainLevels.TryGetValue(mesh, out (Hash128 Dependency, int[] Levels) cached) && cached.Dependency == dependency)
            {
                return cached.Levels;
            }

            Mesh chain = MeshLodGeneration.GenerateChain(mesh);
            try
            {
                int[] levels = MeshLodGeneration.LevelTriangleCounts(chain);
                ChainLevels[mesh] = (dependency, levels);
                return levels;
            }
            finally
            {
                Object.DestroyImmediate(chain);
            }
        }

        private static Mesh SharedMesh(Renderer renderer) => renderer.GetComponent<MeshFilter>().sharedMesh;
    }
}
