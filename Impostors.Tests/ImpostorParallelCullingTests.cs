using System;
using System.Collections.Generic;
using System.Reflection;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed unsafe class ImpostorParallelCullingTests
    {
        private const string CULL = "Cull";
        private const string PARALLEL_CULLING_FIXTURE = "Parallel culling fixture ";
        private const string CULLING_PLANES = "cullingPlanes";
        private const string CULLING_SPLITS = "cullingSplits";
        private const string VIEW_TYPE = "viewType";
        private const string SCENE_CULLING_MASK = "sceneCullingMask";
        private const string CULLING_LAYER_MASK = "cullingLayerMask";
        private const string LOD_PARAMETERS = "lodParameters";
        private const string DRAW_COMMANDS = "drawCommands";
        private const string BRG = "_brg";
        private const string STATISTICS_MUST_PUBLISH_THE_NEWEST_COMPLETED_CAMERA_REGARDLESS = "Statistics must publish the newest completed camera, regardless of completion order.";
        private const string RUNTIME_PROXY = " Runtime proxy";
        private const string A_VIEW_PACKED_ONE_INSTANCE_MORE_THAN_ONCE = "A view packed one instance more than once.";

        [Test]
        public void ConcurrentViewsKeepIndependentFrustumCountsAndPackedIndices()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            ImpostorInstanceSet data = null;
            ImpostorWorldRenderer renderer = null;
            var outputs = new NativeArray<BatchCullingOutputDrawCommands>[3];
            var planes = new NativeArray<Plane>[3];
            var splits = new NativeArray<CullingSplit>[3];
            var handles = new JobHandle[3];
            try
            {
                data = ScriptableObject.CreateInstance<ImpostorInstanceSet>();
                data.Definitions = new ImpostorDefinition[2];
                for (int prototype = 0; prototype < data.Definitions.Length; prototype++)
                {
                    data.Definitions[prototype] = Object.Instantiate(ImpostorTestAssets.Definition(prototype + 1));
                    // Embedded definitions share a display name. This fixture identifies its proxy meshes by name.
                    data.Definitions[prototype].name = "Culling fixture prototype " + prototype;
                    Assert.That(data.Definitions[prototype] && data.Definitions[prototype].IsValid, Is.True);
                    // This fixture asserts impostor-only draw membership, independently of authored mesh LODs.
                    data.Definitions[prototype].LodData = null;
                }
                data.Instances = new ImpostorInstance[8192];
                for (int index = 0; index < data.Instances.Length; index++)
                {
                    data.Instances[index] = new ImpostorInstance
                    {
                        Prototype = index % 2,
                        Position = new Vector3((index % 128 - 63.5f) * 12, 0, (index / 128 - 31.5f) * 12),
                        Yaw = index * 137.508f,
                        Scale = 1,
                    };
                }
                renderer = new ImpostorWorldRenderer();
                renderer.SetData(data);
                MethodInfo callback = typeof(ImpostorWorldRenderer).GetMethod(CULL, BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(callback, Is.Not.Null);
                var expected = new int[3];
                for (int view = 0; view < outputs.Length; view++)
                {
                    var cameraObject = new GameObject(PARALLEL_CULLING_FIXTURE + view, typeof(Camera));
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
                    Camera camera = cameraObject.GetComponent<Camera>();
                    camera.enabled = false;
                    camera.orthographic = true;
                    camera.aspect = 16f / 9;
                    camera.orthographicSize = view == 0 ? 900 : view == 1 ? 220 : 140;
                    camera.nearClipPlane = 0.1f;
                    camera.farClipPlane = 300;
                    camera.transform.SetPositionAndRotation(
                        view == 0 ? new Vector3(0, 150, 0) : view == 1 ? new Vector3(300, 150, 120) : new Vector3(-460, 150, -180),
                        Quaternion.Euler(90, 0, 0));
                    Plane[] referencePlanes = GeometryUtility.CalculateFrustumPlanes(camera);
                    foreach (ImpostorInstance instance in data.Instances)
                    {
                        Bounds bounds = WorldBounds(data.Definitions[instance.Prototype].Bounds, instance.Matrix);
                        expected[view] += GeometryUtility.TestPlanesAABB(referencePlanes, bounds) ? 1 : 0;
                    }
                    planes[view] = new NativeArray<Plane>(referencePlanes, Allocator.TempJob);
                    splits[view] = new NativeArray<CullingSplit>(0, Allocator.TempJob);
                    outputs[view] = new NativeArray<BatchCullingOutputDrawCommands>(1, Allocator.TempJob);
                    object context = default(BatchCullingContext);
                    SetField(context, CULLING_PLANES, planes[view]);
                    SetField(context, CULLING_SPLITS, splits[view]);
                    SetField(context, VIEW_TYPE, BatchCullingViewType.Camera);
                    SetField(context, SCENE_CULLING_MASK, ulong.MaxValue);
                    SetField(context, CULLING_LAYER_MASK, uint.MaxValue);
                    SetField(context, LOD_PARAMETERS, new LODParameters { cameraPosition = camera.transform.position });
                    object output = default(BatchCullingOutput);
                    SetField(output, DRAW_COMMANDS, outputs[view]);
                    // Schedule all views before completing any. Shared dataset reads are
                    // legal, while scratch ranges and statistics must belong to a view.
                    handles[view] = (JobHandle)callback.Invoke(renderer, new[] { null, context, output, (object)IntPtr.Zero });
                }
                JobHandle.CombineDependencies(JobHandle.CombineDependencies(handles[0], handles[1]), handles[2]).Complete();
                Assert.That(expected[0], Is.EqualTo(data.Instances.Length));
                Assert.That(expected[1], Is.InRange(1, data.Instances.Length - 1));
                Assert.That(expected[2], Is.InRange(1, data.Instances.Length - 1));
                Assert.That(expected[1], Is.Not.EqualTo(expected[2]));
                var brg = (BatchRendererGroup)typeof(ImpostorWorldRenderer).GetField(BRG, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
                for (int view = 0; view < outputs.Length; view++)
                {
                    AssertOutput(outputs[view][0], expected[view], data, brg);
                }
                Assert.That(renderer.SubmittedCount, Is.EqualTo(expected[2]), STATISTICS_MUST_PUBLISH_THE_NEWEST_COMPLETED_CAMERA_REGARDLESS);
                Assert.That(renderer.DrawCommandCount, Is.EqualTo(outputs[2][0].drawCommandCount));
                renderer.SetData(null);
                Assert.That(renderer.SubmittedCount, Is.Zero);
                Assert.That(renderer.DrawCommandCount, Is.Zero);
            }
            finally
            {
                foreach (JobHandle handle in handles)
                {
                    handle.Complete();
                }
                renderer?.Dispose();
                for (int view = 0; view < outputs.Length; view++)
                {
                    if (outputs[view].IsCreated)
                    {
                        BatchCullingOutputDrawCommands output = outputs[view][0];
                        UnsafeUtility.Free(output.drawCommands, Allocator.TempJob);
                        UnsafeUtility.Free(output.drawRanges, Allocator.TempJob);
                        UnsafeUtility.Free(output.visibleInstances, Allocator.TempJob);
                        outputs[view].Dispose();
                    }
                    if (planes[view].IsCreated)
                    {
                        planes[view].Dispose();
                    }
                    if (splits[view].IsCreated)
                    {
                        splits[view].Dispose();
                    }
                }
                if (data && data.Definitions != null)
                    foreach (var definition in data.Definitions)
                        if (definition) Object.DestroyImmediate(definition);
                Object.DestroyImmediate(data);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        private static Bounds WorldBounds(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 x = matrix.MultiplyVector(new Vector3(bounds.extents.x, 0, 0));
            Vector3 y = matrix.MultiplyVector(new Vector3(0, bounds.extents.y, 0));
            Vector3 z = matrix.MultiplyVector(new Vector3(0, 0, bounds.extents.z));
            var extent = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center), extent * 2);
        }

        private static void AssertOutput(BatchCullingOutputDrawCommands output, int expected, ImpostorInstanceSet data, BatchRendererGroup brg)
        {
            Assert.That(output.visibleInstanceCount, Is.EqualTo(expected));
            Assert.That(output.drawCommandCount, Is.InRange(1, data.Definitions.Length * 8));
            var seen = new HashSet<long>();
            int visible = 0;
            for (int commandIndex = 0; commandIndex < output.drawCommandCount; commandIndex++)
            {
                BatchDrawCommand command = output.drawCommands[commandIndex];
                Assert.That(command.flags, Is.EqualTo(BatchDrawCommandFlags.None));
                string meshName = brg.GetRegisteredMesh(command.meshID).name;
                int prototype = Array.FindIndex(data.Definitions, definition => meshName == definition.name + RUNTIME_PROXY);
                Assert.That(prototype, Is.GreaterThanOrEqualTo(0));
                for (int offset = 0; offset < command.visibleCount; offset++)
                {
                    int index = output.visibleInstances[(int)command.visibleOffset + offset];
                    Assert.That(index, Is.InRange(0, data.Instances.Length / 2 - 1));
                    Assert.That(seen.Add(((long)prototype << 32) | (uint)index), Is.True, A_VIEW_PACKED_ONE_INSTANCE_MORE_THAN_ONCE);
                    visible++;
                }
            }
            Assert.That(visible, Is.EqualTo(expected));
        }
    }
}
