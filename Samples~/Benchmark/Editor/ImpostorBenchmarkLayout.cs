using System;
using System.Linq;
using Impostors.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors.Samples.Benchmark.Editor
{
    [InitializeOnLoad]
    public static class ImpostorBenchmarkLayout
    {
        private const string INVALID_CAMERA_FIT = "Camera fitting requires instances, a positive aspect, and a finite culling percentage.";
        private const string MISSING_SETUP = "Assign a forest with valid baked definitions and a benchmark camera first.";
        private const string APPLY_UNDO = "Regenerate impostor benchmark";
        private const string GROUND_NAME = "Valley ground";

        public const float Spacing = ImpostorBenchmarkGenerator.Spacing;
        // The renderer packs each prototype's instance records into an int-sized buffer.
        public const int MaximumInstanceCount = ImpostorBenchmarkGenerator.MaximumInstanceCount;
        private const int GroundAxisVertices = 129;
        private const float MinimumCameraSize = 0.01f;
        private const float CameraClearance = 10;
        private const float NearPlane = 0.1f;
        private const float RelativeFitPadding = 0.00001f;
        private const float MinimumFitPadding = 0.005f;

        static ImpostorBenchmarkLayout()
        {
            Undo.undoRedoEvent += OnUndoRedo;
        }

        private static void OnUndoRedo(in UndoRedoInfo info)
        {
            if (info.undoName != APPLY_UNDO)
            {
                return;
            }
            foreach (ImpostorBenchmark benchmark in UnityEngine.Object.FindObjectsByType<ImpostorBenchmark>())
            {
                benchmark.RebuildInstances();
            }
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        public readonly struct CameraFit
        {
            public readonly Bounds Bounds;
            public readonly float HalfHeight;
            public readonly int VisibleCount;
            public Vector3 Position => new Vector3(Bounds.center.x, Bounds.max.y + CameraClearance, Bounds.center.z);
            public float FarPlane => Bounds.size.y + CameraClearance * 2;

            internal CameraFit(Bounds bounds, float halfHeight, int visibleCount)
            {
                Bounds = bounds;
                HalfHeight = halfHeight;
                VisibleCount = visibleCount;
            }

            public void Apply(Camera camera)
            {
                camera.transform.SetPositionAndRotation(Position, Quaternion.Euler(90, 0, 0));
                camera.orthographic = true;
                camera.orthographicSize = HalfHeight;
                camera.nearClipPlane = NearPlane;
                camera.farClipPlane = FarPlane;
                camera.ResetWorldToCameraMatrix();
                camera.ResetProjectionMatrix();
            }
        }

        public static ImpostorInstance[] CreateInstances(int dimensionX, int dimensionY, int prototypeCount)
            => ImpostorBenchmarkGenerator.CreateInstances(dimensionX, dimensionY, prototypeCount);

        public static float Height(float x, float z) => ImpostorBenchmarkGenerator.Height(x, z);

        public static Bounds WorldBounds(ImpostorInstance instance, ImpostorDefinition definition)
        {
            Bounds local = definition.Bounds;
            Matrix4x4 matrix = instance.Matrix;
            Vector3 x = matrix.MultiplyVector(new Vector3(local.extents.x, 0, 0));
            Vector3 y = matrix.MultiplyVector(new Vector3(0, local.extents.y, 0));
            Vector3 z = matrix.MultiplyVector(new Vector3(0, 0, local.extents.z));
            var extent = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(matrix.MultiplyPoint3x4(local.center), extent * 2);
        }

        public static CameraFit CalculateCameraFit(ImpostorInstanceSet data, float aspect, float culledPercent)
            => CalculateCameraFit(new ImpostorRenderData(data.ResolveDefinitions(), data.Instances), aspect, culledPercent);

        public static CameraFit CalculateCameraFit(ImpostorWorldRenderer renderer, float aspect, float culledPercent)
            => CalculateCameraFit(renderer.CaptureWorldBounds(), aspect, culledPercent);

        public static CameraFit CalculateCameraFit(ImpostorRenderData data, float aspect, float culledPercent)
        {
            if (data == null) throw new ArgumentException(INVALID_CAMERA_FIT);
            var bounds = new Bounds[data.Instances.Length];
            for (int i = 0; i < bounds.Length; i++) bounds[i] = WorldBounds(data.Instances[i], data.Definitions[data.Instances[i].Prototype]);
            return CalculateCameraFit(bounds, aspect, culledPercent);
        }

        private static CameraFit CalculateCameraFit(Bounds[] worldBounds, float aspect, float culledPercent)
        {
            if (worldBounds.Length == 0 || !float.IsFinite(aspect) || aspect <= 0 || !float.IsFinite(culledPercent))
                throw new ArgumentException(INVALID_CAMERA_FIT);
            Bounds bounds = worldBounds[0];
            foreach (var world in worldBounds) bounds.Encapsulate(world);
            float fullSize = Mathf.Max(bounds.extents.z, bounds.extents.x / aspect);
            fullSize += Mathf.Max(MinimumFitPadding, fullSize * RelativeFitPadding);
            int desiredVisible = Mathf.RoundToInt(worldBounds.Length * (1 - Mathf.Clamp(culledPercent, 0, 100) / 100));
            if (desiredVisible >= worldBounds.Length)
            {
                return new CameraFit(bounds, fullSize, worldBounds.Length);
            }

            // A top-down orthographic view intersects each AABB at this half-height.
            // Selecting adjacent thresholds finds the closest attainable count without
            // repeatedly traversing the entire forest during a zoom search.
            var thresholds = new float[worldBounds.Length];
            for (int i = 0; i < thresholds.Length; i++)
            {
                Bounds world = worldBounds[i];
                float dx = Mathf.Abs(world.center.x - bounds.center.x) - world.extents.x;
                float dz = Mathf.Abs(world.center.z - bounds.center.z) - world.extents.z;
                thresholds[i] = Mathf.Max(MinimumCameraSize, dx / aspect, dz);
            }
            Array.Sort(thresholds);
            int minimumVisible = UpperBound(thresholds, MinimumCameraSize);
            if (desiredVisible <= minimumVisible)
            {
                return new CameraFit(bounds, MinimumCameraSize, minimumVisible);
            }

            float threshold = thresholds[desiredVisible - 1];
            int below = Array.BinarySearch(thresholds, threshold);
            while (below > 0 && thresholds[below - 1] == threshold)
            {
                below--;
            }
            int above = UpperBound(thresholds, threshold);
            bool keepAbove = above - desiredVisible <= desiredVisible - below;
            int visible = keepAbove ? above : below;
            float lower = visible > 0 ? thresholds[visible - 1] : MinimumCameraSize;
            float upper = visible < thresholds.Length ? thresholds[visible] : fullSize;
            float size = lower + (upper - lower) * 0.5f;
            return new CameraFit(bounds, Mathf.Max(MinimumCameraSize, size), visible);
        }

        private static int UpperBound(float[] values, float value)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (values[mid] <= value)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
            return low;
        }

        public static void UpdateGround(Mesh mesh, Bounds bounds)
        {
            int cells = GroundAxisVertices - 1;
            var vertices = new Vector3[GroundAxisVertices * GroundAxisVertices];
            var indices = new int[cells * cells * 6];
            Vector3 min = bounds.min - new Vector3(Spacing, 0, Spacing);
            Vector3 max = bounds.max + new Vector3(Spacing, 0, Spacing);
            for (int z = 0; z < GroundAxisVertices; z++)
            {
                for (int x = 0; x < GroundAxisVertices; x++)
                {
                    float px = Mathf.Lerp(min.x, max.x, x / (float)cells);
                    float pz = Mathf.Lerp(min.z, max.z, z / (float)cells);
                    vertices[z * GroundAxisVertices + x] = new Vector3(px, Height(px, pz), pz);
                    if (x == cells || z == cells)
                    {
                        continue;
                    }
                    int i = (z * cells + x) * 6;
                    int a = z * GroundAxisVertices + x;
                    indices[i] = a;
                    indices[i + 1] = a + GroundAxisVertices;
                    indices[i + 2] = a + 1;
                    indices[i + 3] = a + 1;
                    indices[i + 4] = a + GroundAxisVertices;
                    indices[i + 5] = a + GroundAxisVertices + 1;
                }
            }
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        public static CameraFit Apply(ImpostorBenchmark benchmark)
        {
            if (!benchmark || benchmark.Definitions == null || !benchmark.Camera || !float.IsFinite(benchmark.CulledPercent)
                || benchmark.Definitions.Length == 0
                || benchmark.Definitions.Any(definition => !definition || !definition.IsValid))
            {
                throw new InvalidOperationException(MISSING_SETUP);
            }

            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(APPLY_UNDO);
            Undo.RecordObjects(new UnityEngine.Object[] { benchmark, benchmark.Camera, benchmark.Camera.transform }, APPLY_UNDO);
            benchmark.RebuildInstances();
            benchmark.CulledPercent = Mathf.Clamp(benchmark.CulledPercent, 0, 100);
            CameraFit fit = CalculateCameraFit(benchmark.CreateRenderData(), benchmark.Camera.aspect, benchmark.CulledPercent);
            fit.Apply(benchmark.Camera);

            if (!benchmark.Ground)
            {
                benchmark.Ground = benchmark.gameObject.scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true))
                    .FirstOrDefault(filter => filter.name == GROUND_NAME);
            }
            if (benchmark.Ground && benchmark.Ground.sharedMesh)
            {
                Mesh mesh = benchmark.Ground.sharedMesh;
                Undo.RegisterCompleteObjectUndo(mesh, APPLY_UNDO);
                UpdateGround(mesh, fit.Bounds);
                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssetIfDirty(mesh);
            }

            EditorUtility.SetDirty(benchmark);
            EditorSceneManager.MarkSceneDirty(benchmark.gameObject.scene);
            EditorSceneManager.MarkSceneDirty(benchmark.Camera.gameObject.scene);
            Undo.CollapseUndoOperations(undo);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            return fit;
        }
    }
}
