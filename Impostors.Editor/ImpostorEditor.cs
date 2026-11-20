using System;
using Impostors.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

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

        private double _nextCheck;
        private string _status;
        private MessageType _severity;
        private bool _showSettings;

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
            }

            EditorGUILayout.HelpBox(unsaved ? UNSAVED : _status ?? CHECKING, unsaved ? MessageType.Warning : _severity);
            if (!validSource)
            {
                EditorGUILayout.HelpBox(INVALID_SOURCE, MessageType.Info);
            }

            bool instanceOverride = !EditorUtility.IsPersistent(component) && stage == null;
            DrawBakeControls(component, source, settings, validSource && !unsaved && !instanceOverride);
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

        private static GameObject ResolveSource(Impostor component, PrefabStage stage)
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
