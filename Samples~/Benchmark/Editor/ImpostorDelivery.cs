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
    /// <summary>Explicit sample authoring and building. No work starts on import or entering Play.</summary>
    public static class ImpostorDelivery
    {
        public const string DefaultSampleFolder = "Assets/Samples/Impostors/Portable";
        // Resolved by GUID so the editable example works wherever Package Manager imported this sample.
        private const string ImpostorLitGraphGuid = "a4dbd1a1cb615034ca46f49bd14c0f00";
        public static string Status { get; private set; } = "Idle";

        [MenuItem("Tools/Impostors/Samples/Create Editable Primitive Example")]
        private static async void CreatePortableMenu()
        {
            try { await CreatePortableSample(DefaultSampleFolder); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        [MenuItem("Tools/Impostors/Samples/Build Selected Benchmark for Windows DX12")]
        private static void BuildSelectedMenu()
        {
            var benchmark = Selection.activeGameObject ? Selection.activeGameObject.GetComponent<ImpostorBenchmark>() : null;
            if (!benchmark) throw new InvalidOperationException("Select a scene object containing an ImpostorBenchmark.");
            string output = EditorUtility.SaveFolderPanel("Benchmark build output", Path.GetFullPath("Builds"), "ImpostorBenchmark");
            if (!string.IsNullOrEmpty(output)) BuildBenchmark(benchmark, output, false);
        }

        public static async Task BakeDefinitions(GameObject[] prefabs, ImpostorBakeSettings settings)
        {
            if (prefabs == null || prefabs.Length == 0 || !settings) throw new ArgumentException("Assign source prefabs and settings.");
            foreach (var prefab in prefabs)
            {
                Status = "Baking " + (prefab ? prefab.name : "missing prefab");
                await ImpostorAuthoring.Rebake(prefab, settings);
            }
            Status = "Ready";
        }

        /// <summary>Creates and bakes two primitive props, then saves a 100-placement example in an unused Assets folder.</summary>
        public static async Task<string> CreatePortableSample(string outputFolder = DefaultSampleFolder)
        {
            outputFolder = WritableFolder(outputFolder);
            if (Directory.Exists(outputFolder)) throw new IOException("Choose an unused sample destination: " + outputFolder);
            Directory.CreateDirectory(outputFolder);
            AssetDatabase.Refresh();
            var settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
            settings.Frames = 4;
            settings.Resolution = 64;
            settings.Border = 2;
            settings.Supersampling = 1;
            settings.AmbientSamples = 4;
            settings.SelfShadowing = true;
            AssetDatabase.CreateAsset(settings, outputFolder + "/Capture Settings.asset");
            var prefabs = new GameObject[2];
            string graphPath = AssetDatabase.GUIDToAssetPath(ImpostorLitGraphGuid);
            if (string.IsNullOrEmpty(graphPath)) throw new FileNotFoundException("Import the Benchmark sample before creating the editable example.");
            AssetDatabase.CopyAsset(graphPath, outputFolder + "/Impostor Lit.shadergraph");
            for (int index = 0; index < prefabs.Length; index++)
            {
                var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(outputFolder + "/Impostor Lit.shadergraph"));
                if (material.HasColor(ImpostorShaderProperties.BaseColor))
                    material.SetColor(ImpostorShaderProperties.BaseColor, index == 0 ? new Color(.18f, .55f, .24f) : new Color(.64f, .28f, .12f));
                AssetDatabase.CreateAsset(material, outputFolder + "/Prop " + (index + 1) + ".mat");
                var root = new GameObject("Prop " + (index + 1));
                try
                {
                    var high = GameObject.CreatePrimitive(index == 0 ? PrimitiveType.Capsule : PrimitiveType.Cube);
                    high.name = "LOD0";
                    high.transform.SetParent(root.transform, false);
                    high.transform.localPosition = new Vector3(0, 2, 0);
                    high.transform.localScale = new Vector3(2, index == 0 ? 2 : 4, 2);
                    Object.DestroyImmediate(high.GetComponent<Collider>());
                    high.GetComponent<Renderer>().sharedMaterial = material;
                    var low = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    low.name = "LOD1";
                    low.transform.SetParent(root.transform, false);
                    low.transform.localPosition = new Vector3(0, 2, 0);
                    low.transform.localScale = new Vector3(2, 4, 2);
                    Object.DestroyImmediate(low.GetComponent<Collider>());
                    low.GetComponent<Renderer>().sharedMaterial = material;
                    var lods = root.AddComponent<LODGroup>();
                    lods.fadeMode = LODFadeMode.CrossFade;
                    lods.SetLODs(new[] {
                        new LOD(.35f, new[] { high.GetComponent<Renderer>() }) { fadeTransitionWidth = .2f },
                        new LOD(.08f, new[] { low.GetComponent<Renderer>() }) { fadeTransitionWidth = .2f }
                    });
                    lods.RecalculateBounds();
                    prefabs[index] = PrefabUtility.SaveAsPrefabAsset(root, outputFolder + "/Prop " + (index + 1) + ".prefab");
                }
                finally { Object.DestroyImmediate(root); }
            }
            await BakeDefinitions(prefabs, settings);
            GetBenchmarkPipeline(outputFolder);
            return CreateBenchmark(prefabs, outputFolder);
        }

        public static UniversalRenderPipelineAsset GetBenchmarkPipeline(string outputFolder)
        {
            outputFolder = WritableFolder(outputFolder);
            Directory.CreateDirectory(outputFolder);
            AssetDatabase.Refresh();
            string path = outputFolder + "/Benchmark Pipeline.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (pipeline) return pipeline;
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            using (var serialized = new SerializedObject(renderer))
            {
                serialized.FindProperty("m_RenderingMode").intValue = 2;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.CreateAsset(renderer, outputFolder + "/Benchmark Renderer.asset");
            var mainLight = ScriptableObject.CreateInstance<ImpostorsRendererFeature>();
            mainLight.name = "Impostors";
            AssetDatabase.AddObjectToAsset(mainLight, renderer);
            renderer.rendererFeatures.Add(mainLight);
            renderer.SetDirty();
            AssetDatabase.SaveAssetIfDirty(renderer);
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.name = "Impostor Sample Pipeline";
            pipeline.renderScale = 1;
            pipeline.msaaSampleCount = 4;
            pipeline.supportsHDR = false;
            pipeline.useSRPBatcher = true;
            pipeline.shadowDistance = 100;
            pipeline.shadowCascadeCount = 2;
            pipeline.mainLightShadowmapResolution = 2048;
            using (var serialized = new SerializedObject(pipeline))
            {
                serialized.FindProperty("m_SoftShadowQuality").intValue = 2;
                serialized.FindProperty("m_SoftShadowsSupported").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.CreateAsset(pipeline, path);
            return pipeline;
        }

        public static string CreateBenchmark(GameObject[] prefabs, string outputFolder, int dimensionX = 10, int dimensionY = 10)
        {
            outputFolder = WritableFolder(outputFolder);
            if (prefabs == null || prefabs.Length == 0 || prefabs.Any(p => !p || !p.GetComponent<Impostor>() || !p.GetComponent<Impostor>().Definition))
                throw new ArgumentException("Assign baked source prefabs.");
            string scenePath = outputFolder + "/Primitive Benchmark.unity";
            if (File.Exists(scenePath)) throw new IOException("The sample scene already exists: " + scenePath);
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var cameraObject = new GameObject("Benchmark camera", typeof(Camera));
                cameraObject.tag = "MainCamera";
                var camera = cameraObject.GetComponent<Camera>();
                camera.aspect = 16f / 9;
                camera.allowHDR = false;
                camera.allowMSAA = true;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.3f, .4f, .5f);
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                var lightObject = new GameObject("Sample sun", typeof(Light));
                var light = lightObject.GetComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.7f;
                light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(48, -35, 0);
                var host = new GameObject("Instanced props");
                host.SetActive(false);
                var benchmark = host.AddComponent<ImpostorBenchmark>();
                benchmark.Prefabs = prefabs;
                benchmark.Camera = camera;
                benchmark.DimensionX = dimensionX;
                benchmark.DimensionY = dimensionY;
                host.SetActive(true);
                ImpostorBenchmarkLayout.CalculateCameraFit(benchmark.CreateRenderData(), camera.aspect, 0).Apply(camera);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(.26f, .3f, .36f);
                RenderSettings.sun = light;
                RenderSettings.fog = false;
                EditorSceneManager.SaveScene(scene, scenePath);
                AssetDatabase.SaveAssets();
                return scenePath;
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        public static void BuildBenchmark(ImpostorBenchmark benchmark, string outputDirectory, bool development)
        {
            if (!benchmark || !benchmark.Camera) throw new ArgumentException("Assign a configured scene benchmark.");
            string scenePath = benchmark.gameObject.scene.path;
            if (!scenePath.StartsWith("Assets/", StringComparison.Ordinal) || benchmark.gameObject.scene.isDirty
                || !AssetDatabase.IsOpenForEdit(scenePath) || (File.GetAttributes(scenePath) & FileAttributes.ReadOnly) != 0)
                throw new InvalidOperationException("Save the editable benchmark scene under Assets before building.");
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Choose a build directory.", nameof(outputDirectory));
            outputDirectory = NormalizeDirectory(outputDirectory);
            string projectAssets = NormalizeDirectory(Application.dataPath);
            string packageRoot = NormalizeDirectory("Packages");
            if (IsWithinOrEqual(outputDirectory, projectAssets) || IsWithinOrEqual(outputDirectory, packageRoot))
                throw new ArgumentException("Choose a build directory outside Assets and Packages.");
            if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                throw new IOException("Choose an empty build directory.");
            Directory.CreateDirectory(outputDirectory);
            // Probe actual write access before touching settings. DeleteOnClose also cleans up if the build is aborted.
            using (new FileStream(Path.Combine(outputDirectory, ".impostor-build-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose)) { }
            using var settingsFiles = new BuildSettingsSnapshot();
            using var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            var stripping = graphics.FindProperty("m_BrgStripping");
            int previousStripping = stripping.intValue;
            bool previousDefaultApi = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            bool previousTimings = PlayerSettings.enableFrameTimingStats;
            var pipeline = GetBenchmarkPipeline(Path.GetDirectoryName(scenePath).Replace('\\', '/'));
            try
            {
                stripping.intValue = (int)UnityEditor.Rendering.BatchRendererGroupStrippingMode.KeepAll;
                graphics.ApplyModifiedPropertiesWithoutUndo();
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
                PlayerSettings.enableFrameTimingStats = true;
                using var scope = new ImpostorPipelineScope(pipeline);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { scenePath }, target = BuildTarget.StandaloneWindows64,
                    locationPathName = Path.Combine(outputDirectory, "ImpostorBenchmark.exe"),
                    options = development ? BuildOptions.Development : BuildOptions.None
                });
                if (report == null || report.summary.result != BuildResult.Succeeded)
                    throw new BuildFailedException("Benchmark build failed. See the build log.");
            }
            finally
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, previousDefaultApi);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, previousApis);
                PlayerSettings.enableFrameTimingStats = previousTimings;
                using var restored = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
                restored.FindProperty("m_BrgStripping").intValue = previousStripping;
                restored.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static string NormalizeDirectory(string directory)
        {
            string full = Path.GetFullPath(directory);
            return full.Length > Path.GetPathRoot(full).Length
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        private static bool IsWithinOrEqual(string candidate, string directory)
        {
            return candidate.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>BuildPlayer can save temporary overrides to disk. Restore the original bytes after native/API cleanup.</summary>
        private sealed class BuildSettingsSnapshot : IDisposable
        {
            private readonly string[] _paths = {
                "ProjectSettings/GraphicsSettings.asset", "ProjectSettings/QualitySettings.asset", "ProjectSettings/ProjectSettings.asset"
            };
            private readonly byte[][] _contents;

            public BuildSettingsSnapshot()
            {
                _contents = new byte[_paths.Length][];
                for (int index = 0; index < _paths.Length; index++)
                {
                    if ((File.GetAttributes(_paths[index]) & FileAttributes.ReadOnly) != 0)
                        throw new IOException("The sample build requires writable settings: " + _paths[index]);
                    using (new FileStream(_paths[index], FileMode.Open, FileAccess.Write, FileShare.Read)) { }
                    _contents[index] = File.ReadAllBytes(_paths[index]);
                }
            }

            public void Dispose()
            {
                for (int index = 0; index < _paths.Length; index++)
                    if (!File.ReadAllBytes(_paths[index]).SequenceEqual(_contents[index]))
                        File.WriteAllBytes(_paths[index], _contents[index]);
            }
        }

        private static string WritableFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose a project Assets folder.");
            folder = folder.Replace('\\', '/').TrimEnd('/');
            string root = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal) || !Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a writable project Assets folder.");
            return folder;
        }
    }
}
