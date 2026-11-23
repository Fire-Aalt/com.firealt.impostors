using System;
using System.Collections.Generic;
using System.Reflection;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed unsafe class ImpostorLodTests
    {
        private const string CullMethod = "Cull";
        private const string ChunksField = "_chunks";
        private const string CentersField = "_centers";
        private const string ExtentsField = "_extents";
        private const string ReferencesField = "_referencePoints";
        private const string HeightsField = "_heights";
        private const string LodsField = "_lods";
        private const string PartsField = "_parts";

        public struct GameplayPayload : IComponentData { public float4x4 Value; }

        [Test]
        public void FailedSynchronizationProducesNoDrawsUntilCorrected()
        {
            using var renderer = new ImpostorWorldRenderer();
            var em = renderer.EntityWorld.EntityManager;
            var entity = em.Instantiate(renderer.BakeEntityPrefab(ImpostorTestAssets.Prefab(1)));
            renderer.SynchronizeEntities();
            using var planes = new NativeArray<Plane>(0, Allocator.TempJob);
            using var splits = new NativeArray<CullingSplit>(0, Allocator.TempJob);
            var commands = new NativeArray<BatchCullingOutputDrawCommands>(1, Allocator.TempJob);
            object context = default(BatchCullingContext);
            Set(context, nameof(BatchCullingContext.cullingPlanes), planes);
            Set(context, nameof(BatchCullingContext.cullingSplits), splits);
            Set(context, nameof(BatchCullingContext.viewType), BatchCullingViewType.Camera);
            Set(context, nameof(BatchCullingContext.sceneCullingMask), ulong.MaxValue);
            Set(context, nameof(BatchCullingContext.cullingLayerMask), uint.MaxValue);
            object output = default(BatchCullingOutput);
            Set(output, nameof(BatchCullingOutput.drawCommands), commands);
            var callback = typeof(ImpostorWorldRenderer).GetMethod(CullMethod, BindingFlags.Instance | BindingFlags.NonPublic);
            void CullView() => ((JobHandle)callback.Invoke(renderer, new[] { null, context, output, (object)IntPtr.Zero })).Complete();
            try
            {
                CullView();
                Assert.That(commands[0].visibleInstanceCount, Is.EqualTo(1));
                ImpostorTestCullingOutput.Release(commands[0]); commands[0] = default;

                em.SetComponentData(entity, new LocalToWorld { Value = float4x4.Scale(new float3(1, 2, 1)) });
                Assert.Throws<InvalidOperationException>(() => renderer.SynchronizeEntities());
                CullView();
                Assert.That(commands[0].drawCommandCount, Is.Zero);
                Assert.That(commands[0].visibleInstanceCount, Is.Zero);

                em.SetComponentData(entity, new LocalToWorld { Value = float4x4.identity });
                renderer.SynchronizeEntities();
                CullView();
                Assert.That(commands[0].visibleInstanceCount, Is.EqualTo(1));
            }
            finally { ImpostorTestCullingOutput.Release(commands[0]); commands.Dispose(); }
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, true, true)]
        public void ConcurrentChunkLodsAndLightSplitsMatchScalarMembership(bool orthographic, bool light, bool smallerChunks)
        {
            var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            float shadowDistance = pipeline.shadowDistance;
            // These synthetic light contexts exercise split/LOD membership over a
            // 500-unit fixture rather than the environment's default disabled maps.
            if (light) pipeline.shadowDistance = 1000;
            var owned = new List<Object>();
            var definition = Object.Instantiate(ImpostorTestAssets.Definition(1));
            owned.Add(definition);
            var profile = new ImpostorLodData();

            var cubeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = Object.Instantiate(cubeObject.GetComponent<MeshFilter>().sharedMesh);
            owned.Add(cube);
            Mesh cubeLow = Object.Instantiate(cube);
            owned.Add(cubeLow);
            Object.DestroyImmediate(cubeObject);
            var material = new Material(Shader.Find(ImpostorShaderNames.HEMI_OCTAHEDRAL));
            owned.Add(material);
            profile.ReferencePoint = definition.Bounds.center;
            profile.Size = 24;
            profile.MeshLevels = new[]
            {
                new ImpostorMeshLod { TransitionScreenHeight = 0.4f, FadeWidth = 0.2f,
                    Parts = new[] { new ImpostorLodPart { Mesh = cube, Material = material }, new ImpostorLodPart { Mesh = cube, Material = material } } },
                new ImpostorMeshLod { TransitionScreenHeight = 0.08f, FadeWidth = 0.2f,
                    Parts = new[] { new ImpostorLodPart { Mesh = cubeLow, Material = material } } }
            };
            Assert.That(profile.TryValidate(out string error), Is.True, error);
            definition.LodData = profile;
            var data = new ImpostorRenderData(new[] { definition }, new ImpostorInstance[2049]);
            var random = new System.Random(918271);
            for (int i = 0; i < data.Instances.Length; i++)
            {
                data.Instances[i] = new ImpostorInstance
                {
                    Scale = 0.5f + (float)random.NextDouble() * 1.5f,
                    Position = i < 128 ? new Vector3(1, 0, 100) : new Vector3((float)random.NextDouble() * 500 - 250, 0, (float)random.NextDouble() * 500),
                    Yaw = (float)random.NextDouble() * 360,
                };
            }

            var planes = new NativeArray<Plane>[3];
            var splits = new NativeArray<CullingSplit>[3];
            var outputs = new NativeArray<BatchCullingOutputDrawCommands>[3];
            var handles = new JobHandle[3];
            var cameras = new Camera[3];
            var referencePlanes = new Plane[3][][];
            using var renderer = new ImpostorWorldRenderer { LiveShadows = light };
            try
            {
                renderer.SetData(data);
                if (smallerChunks)
                {
                    var em = renderer.EntityWorld.EntityManager;
                    using var query = em.CreateEntityQuery(typeof(ImpostorGpuSlot));
                    using var entities = query.ToEntityArray(Allocator.Temp);
                    // Reverse migration scatters GPU slots within the smaller ECS
                    // chunks, exercising both row masks and actual slot expansion.
                    for (int i = entities.Length - 1; i >= 0; i--)
                        em.AddComponentData(entities[i], new GameplayPayload { Value = float4x4.identity });
                    renderer.SynchronizeEntities();
                    Assert.That(em.GetChunk(entities[0]).Capacity, Is.LessThan(ChunkVisibilityMask.CAPACITY));
                }
                for (int view = 0; view < 3; view++)
                {
                    var cameraObject = new GameObject(CullMethod, typeof(Camera));
                    owned.Add(cameraObject);
                    Camera camera = cameras[view] = cameraObject.GetComponent<Camera>();
                    camera.enabled = false;
                    camera.orthographic = orthographic;
                    camera.orthographicSize = 60 + view * 60;
                    camera.fieldOfView = 50 + view * 15;
                    camera.aspect = 1.5f;
                    camera.nearClipPlane = 0.1f;
                    camera.farClipPlane = 500;
                    camera.transform.SetPositionAndRotation(new Vector3(view * 40 - 40, 30, -30), Quaternion.Euler(5, view * 7, 0));
                    referencePlanes[view] = new Plane[light ? 3 : 1][];
                    var allPlanes = new List<Plane>();
                    splits[view] = new NativeArray<CullingSplit>(light ? 3 : 0, Allocator.TempJob);
                    for (int split = 0; split < referencePlanes[view].Length; split++)
                    {
                        camera.farClipPlane = 160 + split * 180;
                        referencePlanes[view][split] = GeometryUtility.CalculateFrustumPlanes(camera);
                        if (light)
                        {
                            splits[view][split] = new CullingSplit { cullingPlaneOffset = allPlanes.Count, cullingPlaneCount = 6 };
                        }

                        allPlanes.AddRange(referencePlanes[view][split]);
                    }
                    planes[view] = new NativeArray<Plane>(allPlanes.ToArray(), Allocator.TempJob);
                    outputs[view] = new NativeArray<BatchCullingOutputDrawCommands>(1, Allocator.TempJob);
                    object context = default(BatchCullingContext);
                    Set(context, nameof(BatchCullingContext.cullingPlanes), planes[view]);
                    Set(context, nameof(BatchCullingContext.cullingSplits), splits[view]);
                    Set(context, nameof(BatchCullingContext.viewType), light ? BatchCullingViewType.Light : BatchCullingViewType.Camera);
                    Set(context, nameof(BatchCullingContext.sceneCullingMask), ulong.MaxValue);
                    Set(context, nameof(BatchCullingContext.cullingLayerMask), uint.MaxValue);
                    Set(context, nameof(BatchCullingContext.lodParameters), new LODParameters
                    {
                        cameraPosition = camera.transform.position,
                        isOrthographic = orthographic,
                        orthoSize = camera.orthographicSize,
                        fieldOfView = camera.fieldOfView,
                        cameraPixelHeight = 1080
                    });
                    object output = default(BatchCullingOutput);
                    Set(output, nameof(BatchCullingOutput.drawCommands), outputs[view]);
                    handles[view] = (JobHandle)typeof(ImpostorWorldRenderer).GetMethod(CullMethod, BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(renderer, new[] { null, context, output, (object)IntPtr.Zero });
                }
                foreach (JobHandle handle in handles)
                {
                    handle.Complete();
                }

                for (int view = 0; view < 3; view++)
                {
                    AssertMembership(renderer, outputs[view][0], referencePlanes[view], cameras[view]);
                }

                renderer.SetData(null);
                Assert.That(renderer.SubmittedCount, Is.Zero);
            }
            finally
            {
                foreach (JobHandle handle in handles)
                {
                    handle.Complete();
                }

                renderer.Dispose();
                for (int i = 0; i < 3; i++)
                {
                    if (outputs[i].IsCreated)
                    {
                        ImpostorTestCullingOutput.Release(outputs[i][0]);
                        outputs[i].Dispose();
                    }
                    if (planes[i].IsCreated)
                    {
                        planes[i].Dispose();
                    }

                    if (splits[i].IsCreated)
                    {
                        splits[i].Dispose();
                    }
                }
                foreach (Object value in owned)
                {
                    Object.DestroyImmediate(value);
                }
                pipeline.shadowDistance = shadowDistance;
            }
        }

        private static void AssertMembership(ImpostorWorldRenderer renderer, BatchCullingOutputDrawCommands output, Plane[][] frusta, Camera camera)
        {
            NativeArray<ArchetypeChunk> chunks = Field<ArchetypeChunk>(renderer, ChunksField);
            var em = renderer.EntityWorld.EntityManager;
            NativeArray<RenderLod> lods = Field<RenderLod>(renderer, LodsField);
            NativeArray<RenderPart> parts = Field<RenderPart>(renderer, PartsField);
            var expected = new HashSet<(int Level, int Local, int Splits, int Fade)>();
            foreach (var ecsChunk in chunks)
            {
                var prototype = ecsChunk.GetSharedComponent(em.GetSharedComponentTypeHandle<ImpostorPrototype>());
                var data = prototype.Data.Value;
                var bt = em.GetComponentTypeHandle<RenderBounds>(true); var mt = em.GetComponentTypeHandle<ImpostorLodMetrics>(true);
                var st = em.GetComponentTypeHandle<ImpostorGpuSlot>(true);
                var bounds = ecsChunk.GetNativeArray(ref bt); var metrics = ecsChunk.GetNativeArray(ref mt); var slots = ecsChunk.GetNativeArray(ref st);
                Assert.That(ecsChunk.Count, Is.InRange(1, ChunkVisibilityMask.CAPACITY));
                for (int b = 0; b < ecsChunk.Count; b++)
                {
                    int mask = 0;
                    for (int split = 0; split < frusta.Length; split++)
                    {
                        if (GeometryUtility.TestPlanesAABB(frusta[split], new Bounds(bounds[b].Center, bounds[b].Extents * 2)))
                        {
                            mask |= 1 << split;
                        }
                    }

                    if (mask == 0)
                    {
                        continue;
                    }

                    float distance = Vector3.Distance(camera.transform.position, metrics[b].ReferencePoint);
                    float factor = QualitySettings.lodBias / (2 * (camera.orthographic ? camera.orthographicSize : Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f)));
                    float h = metrics[b].Height * factor / (camera.orthographic ? 1 : distance);
                    bool found = false;
                    for (int level = data.FirstLod; level < data.FirstLod + data.LodCount - 1; level++)
                    {
                        RenderLod transition = lods[level];
                        float q = h / transition.TransitionScreenHeight;
                        if (q <= 1 - transition.FadeWidth)
                        {
                            continue;
                        }

                        int fade = q >= 1 ? 0 : Mathf.Clamp(Mathf.CeilToInt((q - 1 + transition.FadeWidth) / transition.FadeWidth * 127), 1, 126);
                        expected.Add((level, slots[b].Value, mask, fade));
                        if (fade != 0)
                        {
                            expected.Add((level + 1, slots[b].Value, mask, -fade));
                        }

                        found = true;
                        break;
                    }
                    if (!found)
                    {
                        expected.Add((data.FirstLod + data.LodCount - 1, slots[b].Value, mask, 0));
                    }
                }
            }
            var actual = new HashSet<(int Level, int Local, int Splits, int Fade)>();
            var visitedSpans = new HashSet<(uint Offset, uint Count)>();
            for (int c = 0; c < output.drawCommandCount; c++)
            {
                BatchDrawCommand command = output.drawCommands[c];
                Assert.That((command.flags & BatchDrawCommandFlags.HasSortingPosition) == 0, Is.True);
                int selected = -1;
                for (int level = 0; level < lods.Length; level++)
                {
                    for (int part = 0; part < lods[level].PartCount; part++)
                    {
                        if (command.meshID.Equals(parts[lods[level].FirstPart + part].Mesh))
                        {
                            selected = level;
                        }
                    }
                }

                Assert.That(selected, Is.GreaterThanOrEqualTo(0));
                if (!visitedSpans.Add((command.visibleOffset, command.visibleCount)))
                {
                    continue;
                }

                for (int i = 0; i < command.visibleCount; i++)
                {
                    int packed = output.visibleInstances[command.visibleOffset + i];
                    int fade = (command.flags & BatchDrawCommandFlags.LODCrossFadeValuePacked) != 0 ? packed >> 24 : 0;
                    Assert.That(actual.Add((selected, packed & 0x00ffffff, command.splitVisibilityMask, fade)), Is.True);
                }
            }
            Assert.That(output.visibleInstanceCount, Is.EqualTo(expected.Count));
            Assert.That(actual.SetEquals(expected), Is.True);
        }

        private static NativeArray<T> Field<T>(ImpostorWorldRenderer renderer, string field) where T : unmanaged
            => (NativeArray<T>)typeof(ImpostorWorldRenderer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
    }
}
