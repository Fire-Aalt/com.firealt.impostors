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
    [CustomEditor(typeof(Impostor))]
    public sealed class ImpostorEditor : UnityEditor.Editor
    {
        private const double CHECK_INTERVAL = 1;
        private const string DEFINITION_LABEL = "Impostor Definition";
        private const string BAKE_LABEL = "Bake impostor";
        private const string REBAKE_LABEL = "Rebake impostor";
        private const string CANCEL_LABEL = "Cancel bake";
        private const string SETTINGS_LABEL = "Bake settings";
        private const string UNSAVED = "Save the prefab before checking its bake state or rebaking.";
        private const string CHECKING = "Checking bake state…";
        private const string INVALID_SOURCE =
            "Add Impostor to the source prefab root. Open the prefab to edit its bake settings and rebake.";
        private const string INSTANCE_OVERRIDE =
            "Rebake from the prefab asset or Prefab Mode. Scene instance overrides are not baked.";
        private const string NOT_BAKED = "Not baked.";
        private const string MISSING_SETTINGS = "Assign an Impostor Bake Settings asset to rebake and check for changes.";
        private const string OUTDATED = "Outdated: the prefab, its dependencies, or bake settings have changed.";
        private const string UNVERIFIED = "Bake state unverified: rebake once to record the current inputs.";
        private const string UP_TO_DATE = "Up to date.";
        private const string CHECK_FAILED = "Cannot check bake state: ";
        private const string BAKE_CANCELLED = "Impostor bake cancelled. The previous definition remains active.";
        private const string UNCAPTURABLE = "These materials can't be captured: {0}. Pick a capture-ready shader or "
            + "ShaderGraph for each and remap. The copies are saved next to the source materials and used by this prefab only.";
        private const string REMAP_LABEL = "Remap materials";
        private const string REMAP_HEADER = "Source material";
        private const string SHADER_HEADER = "Capture shader";
        private const string NOT_A_SHADER = "Not a shader or ShaderGraph: ";

        private double _nextCheck;
        private string _status;
        private MessageType _severity;
        private bool _showSettings;
        private Material[] _uncapturable = Array.Empty<Material>();
        private readonly Dictionary<Material, Object> _shaders = new();
        // Checking a shader builds a probe material, so results are kept until the next status refresh.
        private readonly Dictionary<Object, (Shader Shader, bool Ready, string Reason)> _shaderChecks = new();
        private bool _remapping;

        private static bool IsBaking => ImpostorAuthoring.IsBaking || ImpostorBaker.IsBaking;

        public override bool RequiresConstantRepaint() => true;

        public override void OnInspectorGUI()
        {
            DrawProperties();
            var component = (Impostor)target;
            var settings = component.BakeSettings as ImpostorBakeSettings;
            PrefabStage stage = PrefabStageUtility.GetPrefabStage(component.gameObject);
            GameObject source = ResolveSource(component, stage);
            bool unsaved = stage != null && stage.scene.isDirty;
            bool validSource = source && !source.transform.parent;
            if (EditorApplication.timeSinceStartup >= _nextCheck)
            {
                _nextCheck = EditorApplication.timeSinceStartup + CHECK_INTERVAL;
                CheckStatus(source, settings, component.Definition);
                _uncapturable = ImpostorMaterialRemap.UncapturableMaterials(source, settings ? settings.SourceLod : 0);
                _shaderChecks.Clear();
            }

            EditorGUILayout.HelpBox(unsaved ? UNSAVED : _status ?? CHECKING, unsaved ? MessageType.Warning : _severity);
            if (!validSource)
            {
                EditorGUILayout.HelpBox(INVALID_SOURCE, MessageType.Info);
            }

            bool instanceOverride = !EditorUtility.IsPersistent(component) && stage == null;
            bool canEdit = validSource && !unsaved && !instanceOverride;
            DrawRemap(source, canEdit);
            DrawBakeControls(component, source, settings, canEdit && _uncapturable.Length == 0);
            if (instanceOverride)
            {
                EditorGUILayout.HelpBox(INSTANCE_OVERRIDE, MessageType.None);
            }

            if (IsBaking && GUILayout.Button(CANCEL_LABEL))
            {
                ImpostorBaker.RequestCancellation();
            }

            DrawSettings(settings, source);
        }

        internal static GameObject ResolveSource(Component component, PrefabStage stage)
        {
            if (EditorUtility.IsPersistent(component))
            {
                return component.gameObject;
            }

            return stage != null
                ? AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath)
                : PrefabUtility.GetCorrespondingObjectFromSource(component.gameObject);
        }

        private void DrawProperties()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(Impostor.Definition)),
                    new GUIContent(DEFINITION_LABEL));
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(Impostor.BakeSettings)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(Impostor.RealtimeShadows)));
            if (serializedObject.ApplyModifiedProperties())
            {
                _nextCheck = 0;
            }
        }

        /// <summary>The materials the bake can't capture, each with the shader its capture-ready copy will use.</summary>
        private void DrawRemap(GameObject source, bool canEdit)
        {
            if (_uncapturable.Length == 0)
            {
                return;
            }

            EditorGUILayout.HelpBox(string.Format(UNCAPTURABLE, string.Join(", ", _uncapturable.Select(material => material.name))),
                MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(REMAP_HEADER, EditorStyles.miniBoldLabel, GUILayout.Width(EditorGUIUtility.labelWidth));
                GUILayout.Label(SHADER_HEADER, EditorStyles.miniBoldLabel);
            }

            var shaders = new Dictionary<Material, Shader>();
            bool valid = true;
            foreach (Material material in _uncapturable)
            {
                Shader shader = DrawRemapRow(material, out bool ready);
                shaders[material] = shader;
                valid &= ready;
            }

            using (new EditorGUI.DisabledScope(!canEdit || !valid || _remapping || IsBaking
                || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button(REMAP_LABEL))
                {
                    ScheduleRemap(AssetDatabase.GetAssetPath(source), shaders);
                }
            }
        }

        private Shader DrawRemapRow(Material material, out bool ready)
        {
            if (!_shaders.TryGetValue(material, out Object choice))
            {
                choice = _shaders[material] = ImpostorMaterialRemap.DefaultShader(material);
            }

            using var row = new EditorGUILayout.HorizontalScope();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(material, typeof(Material), false, GUILayout.Width(EditorGUIUtility.labelWidth));
            }

            // Typed as Object so a ShaderGraph asset can be dropped as well as a shader picked.
            Object picked = EditorGUILayout.ObjectField(choice, typeof(Object), false);
            if (picked != choice)
            {
                _shaders[material] = picked;
            }

            (Shader shader, bool isReady, string reason) = CheckShader(picked);
            ready = isReady;
            if (!ready)
            {
                GUIContent error = EditorGUIUtility.IconContent("console.erroricon.sml");
                error.tooltip = reason;
                GUILayout.Label(error, GUILayout.Width(EditorGUIUtility.singleLineHeight));
            }

            return shader;
        }

        private (Shader Shader, bool Ready, string Reason) CheckShader(Object picked)
        {
            if (!picked)
            {
                return (null, false, NOT_A_SHADER + "none");
            }

            if (!_shaderChecks.TryGetValue(picked, out (Shader Shader, bool Ready, string Reason) check))
            {
                Shader shader = ImpostorMaterialRemap.ResolveShader(picked);
                string reason = NOT_A_SHADER + picked.name;
                bool ready = shader && ImpostorCaptureMaterial.IsCaptureReadyShader(shader, out reason);
                check = _shaderChecks[picked] = (shader, ready, reason);
            }

            return check;
        }

        /// <summary>Remaps after the current GUI event, since it saves and regenerates the prefab.</summary>
        private void ScheduleRemap(string path, Dictionary<Material, Shader> shaders)
        {
            _remapping = true;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    ImpostorMaterialRemap.Remap(AssetDatabase.LoadAssetAtPath<GameObject>(path), shaders);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, AssetDatabase.LoadAssetAtPath<GameObject>(path));
                }

                _remapping = false;
                _nextCheck = 0;
                if (this)
                {
                    Repaint();
                }
            };
        }

        private void DrawBakeControls(Impostor component, GameObject source, ImpostorBakeSettings settings, bool canBake)
        {
            bool disabled = !canBake || !settings || IsBaking || EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(disabled))
            {
                if (GUILayout.Button(component.Definition ? REBAKE_LABEL : BAKE_LABEL))
                {
                    Bake(source, settings);
                }
            }
        }

        private void DrawSettings(ImpostorBakeSettings settings, GameObject source)
        {
            if (!settings)
            {
                return;
            }

            _showSettings = EditorGUILayout.Foldout(_showSettings, SETTINGS_LABEL, true);
            if (!_showSettings)
            {
                return;
            }

            var serialized = new SerializedObject(settings);
            serialized.Update();
            using (new EditorGUI.DisabledScope(IsBaking))
            {
                ImpostorBakeSettingsEditor.DrawSettings(serialized, source);
            }

            if (serialized.ApplyModifiedProperties())
            {
                _nextCheck = 0;
            }
        }

        private void CheckStatus(GameObject source, ImpostorBakeSettings settings, ImpostorDefinition definition)
        {
            _severity = MessageType.Warning;
            if (!definition)
            {
                _status = NOT_BAKED;
                return;
            }

            if (!settings)
            {
                _status = MISSING_SETTINGS;
                return;
            }

            try
            {
                if (!ImpostorAuthoring.IsOutdated(source, settings, definition))
                {
                    _status = UP_TO_DATE;
                    _severity = MessageType.Info;
                    return;
                }

                bool recorded = !string.IsNullOrEmpty(definition.BakeData?.InputFingerprint);
                _status = recorded ? OUTDATED : UNVERIFIED;
            }
            catch (Exception exception)
            {
                _status = CHECK_FAILED + exception.Message;
            }
        }

        private async void Bake(GameObject source, ImpostorBakeSettings settings)
        {
            try
            {
                await ImpostorAuthoring.Rebake(source, settings);
            }
            catch (OperationCanceledException)
            {
                Debug.Log(BAKE_CANCELLED);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, target);
            }
            finally
            {
                _nextCheck = 0;
                if (this)
                {
                    Repaint();
                }
            }
        }
    }
}
