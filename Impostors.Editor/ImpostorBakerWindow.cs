using System;
using Impostors.Data;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    public sealed class ImpostorBakerWindow : EditorWindow
    {
        private const float MINIMUM_WINDOW_WIDTH = 520;
        private const float MINIMUM_WINDOW_HEIGHT = 600;
        private const string MENU_PATH = "Tools/Impostors/Baker";
        private const string TITLE = "Impostor Baker";
        private const string SOURCE_PREFAB_LABEL = "Source prefab";
        private const string SETTINGS_LABEL = "Settings";
        private const string BAKE_LABEL = "Bake selected source LOD";
        private const string CANCEL_LABEL = "Cancel capture";
        private const string BAKE_CANCELLED = "Impostor bake cancelled. Previous assets were preserved.";

        [SerializeField]
        private Vector2 _scrollPosition;

        [SerializeField]
        private GameObject _source;

        [SerializeField]
        private ImpostorBakeSettings _settings;

        private bool _isBaking;

        [MenuItem(MENU_PATH)]
        private static void Open()
        {
            var window = GetWindow<ImpostorBakerWindow>(TITLE);
            window.minSize = new Vector2(MINIMUM_WINDOW_WIDTH, MINIMUM_WINDOW_HEIGHT);
        }

        private void OnGUI()
        {
            using var scroll = new EditorGUILayout.ScrollViewScope(_scrollPosition);
            _scrollPosition = scroll.scrollPosition;
            DrawContents();
        }

        private void DrawContents()
        {
            using (new EditorGUI.DisabledScope(_isBaking))
            {
                _source = (GameObject)EditorGUILayout.ObjectField(SOURCE_PREFAB_LABEL, _source, typeof(GameObject), false);
                _settings = (ImpostorBakeSettings)EditorGUILayout.ObjectField(SETTINGS_LABEL, _settings,
                    typeof(ImpostorBakeSettings), false);
                if (_settings)
                {
                    var serialized = new SerializedObject(_settings);
                    serialized.Update();
                    ImpostorBakeSettingsEditor.DrawSettings(serialized, _source);
                    serialized.ApplyModifiedProperties();
                }

                if (GUILayout.Button(BAKE_LABEL) && _source && _settings)
                {
                    Bake();
                }
            }

            if (_isBaking && GUILayout.Button(CANCEL_LABEL))
            {
                ImpostorBaker.RequestCancellation();
            }
        }

        private async void Bake()
        {
            _isBaking = true;
            try
            {
                ImpostorDefinition definition = await ImpostorAuthoring.Rebake(_source, _settings);
                Selection.activeObject = definition;
                EditorGUIUtility.PingObject(definition);
            }
            catch (OperationCanceledException)
            {
                Debug.Log(BAKE_CANCELLED);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                _isBaking = false;
                Repaint();
            }
        }
    }
}
