using UnityEditor;
using UnityEngine;

namespace Impostors.Samples.Benchmark.Editor
{
    public sealed class ImpostorBenchmarkWindow : EditorWindow
    {
        private const float MINIMUM_WIDTH = 330;
        private const float MINIMUM_HEIGHT = 285;
        private const string MENU_PATH = "Tools/Impostors/Benchmark Scaling";
        private const string WINDOW_TITLE = "Benchmark Scaling";
        private const string TARGET_LABEL = "Benchmark";
        private const string MISSING_BENCHMARK =
            "Open an included sample scene or assign a scene's ImpostorBenchmark component.";

        [SerializeField]
        private ImpostorBenchmark _benchmark;

        private SerializedObject _settings;

        [MenuItem(MENU_PATH)]
        public static void Open()
        {
            var window = GetWindow<ImpostorBenchmarkWindow>(WINDOW_TITLE);
            window.minSize = new Vector2(MINIMUM_WIDTH, MINIMUM_HEIGHT);
            window.Show();
        }

        private void OnEnable()
        {
            if (!_benchmark)
            {
                _benchmark = FindAnyObjectByType<ImpostorBenchmark>();
            }
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnGUI()
        {
            _benchmark = (ImpostorBenchmark)EditorGUILayout.ObjectField(TARGET_LABEL, _benchmark, typeof(ImpostorBenchmark),
                true);
            if (!_benchmark)
            {
                EditorGUILayout.HelpBox(MISSING_BENCHMARK, MessageType.Info);
                return;
            }

            if (_settings == null || _settings.targetObject != _benchmark)
            {
                _settings = new SerializedObject(_benchmark);
            }

            _settings.Update();
            ImpostorBenchmarkEditor.DrawScaling(_settings);
            _settings.ApplyModifiedProperties();
        }
    }
}
