using System;
using System.Collections.Generic;
using System.Reflection;
using Impostors.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed unsafe class ImpostorShadowCullingTests
    {
        [TestCase(1, .2f)]
        [TestCase(2, .2f)]
        [TestCase(4, .2f)]
        [TestCase(1, 1f)]
        [TestCase(2, 1f)]
        [TestCase(4, 1f)]
        public void WideCascadesOnlySubmitBoundsWithinShadowDistance(int splitCount, float fadeWidth)
        {
            var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            float previousDistance = pipeline.shadowDistance;
            var origin = new Vector3(17, 5, -11);
            var definition = Object.Instantiate(ImpostorTestAssets.Definition());
            Mesh mesh = null;
            Material material = null;
            ImpostorWorldRenderer renderer = null;
            var planes = new NativeArray<Plane>(splitCount * 6, Allocator.TempJob);
            var splits = new NativeArray<CullingSplit>(splitCount, Allocator.TempJob);
            var output = new NativeArray<BatchCullingOutputDrawCommands>(1, Allocator.TempJob);
            try
            {
                pipeline.shadowDistance = 100;
                definition.Bounds = new Bounds(Vector3.zero, Vector3.one * 2);
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
                Object.DestroyImmediate(cube);
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                definition.LodData = new ImpostorLodData
                {
                    ReferencePoint = Vector3.zero, Size = 4,
                    MeshLevels = new[] { new ImpostorMeshLod {
                        TransitionScreenHeight = .2f, FadeWidth = fadeWidth,
                        Parts = new[] { new ImpostorLodPart { Mesh = mesh, Material = material } }
                    } }
                };
                var instances = new ImpostorInstance[4099];
                for (int i = 0; i < 4096; i++)
                    instances[i] = new ImpostorInstance { Scale = 1, Yaw = i * 137.508f,
                        Position = origin + new Vector3((i % 64 - 31.5f) * 8, 0, (i / 64 - 31.5f) * 8) };
                // Center outside the range, but bounds touch/overlap its edge.
                instances[4096] = new ImpostorInstance { Scale = 1, Position = origin + Vector3.forward * 101 };
                instances[4097] = new ImpostorInstance { Scale = 4, Position = origin + Vector3.forward * 103 };
                instances[4098] = new ImpostorInstance { Scale = 1, Position = origin + Vector3.forward * 8 };
                renderer = new ImpostorWorldRenderer { LiveShadows = true };
                renderer.SetData(new ImpostorRenderData(new[] { definition }, instances));

                var references = new Plane[splitCount][];
                for (int s = 0; s < splitCount; s++)
                {
                    references[s] = new[] {
                        new Plane(Vector3.left, origin.x + 5 + s * 40),
                        new Plane(Vector3.right, 10000), new Plane(Vector3.up, 10000),
                        new Plane(Vector3.down, 10000), new Plane(Vector3.forward, 10000),
                        new Plane(Vector3.back, 10000)
                    };
                    for (int p = 0; p < 6; p++) planes[s * 6 + p] = references[s][p];
                    // Reproduce the very large receiver spheres of an orthographic
                    // overview: cascade planes/spheres cannot enforce radial fading.
                    splits[s] = new CullingSplit { sphereCenter = origin, sphereRadius = 7000,
                        cullingPlaneOffset = s * 6, cullingPlaneCount = 6 };
                }
                object context = default(BatchCullingContext);
                Set(context, nameof(BatchCullingContext.cullingPlanes), planes);
                Set(context, nameof(BatchCullingContext.cullingSplits), splits);
                Set(context, nameof(BatchCullingContext.viewType), BatchCullingViewType.Light);
                Set(context, nameof(BatchCullingContext.projectionType), BatchCullingProjectionType.Orthographic);
                Set(context, nameof(BatchCullingContext.sceneCullingMask), ulong.MaxValue);
                Set(context, nameof(BatchCullingContext.cullingLayerMask), uint.MaxValue);
                Set(context, nameof(BatchCullingContext.lodParameters), new LODParameters {
                    cameraPosition = origin, fieldOfView = 60, cameraPixelHeight = 1080
                });
                object commands = default(BatchCullingOutput);
                Set(commands, nameof(BatchCullingOutput.drawCommands), output);
                var callback = typeof(ImpostorWorldRenderer).GetMethod("Cull", BindingFlags.Instance | BindingFlags.NonPublic);
                ((JobHandle)callback.Invoke(renderer, new[] { null, context, commands, (object)IntPtr.Zero })).Complete();

                var expected = new Dictionary<int, ushort>();
                var em = renderer.EntityWorld.EntityManager;
                using var query = em.CreateEntityQuery(typeof(RenderBounds), typeof(ImpostorGpuSlot));
                using var entities = query.ToEntityArray(Allocator.Temp);
                foreach (var entity in entities)
                {
                    var world = em.GetComponentData<RenderBounds>(entity);
                    var bounds = new Bounds(world.Center, world.Extents * 2);
                    if (bounds.SqrDistance(origin) > 100 * 100) continue;
                    ushort mask = 0;
                    for (int s = 0; s < splitCount; s++)
                        if (GeometryUtility.TestPlanesAABB(references[s], bounds)) mask |= (ushort)(1 << s);
                    if (mask != 0) expected.Add(em.GetComponentData<ImpostorGpuSlot>(entity).Value, mask);
                }
                var actual = new HashSet<int>();
                bool sawMesh = false, sawImpostor = false;
                var brg = (BatchRendererGroup)typeof(ImpostorWorldRenderer).GetField("_brg", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
                var result = output[0];
                Assert.That(result.drawCommandCount, Is.GreaterThan(0));
                for (int i = 0; i < result.drawCommandCount; i++)
                {
                    var draw = result.drawCommands[i];
                    bool isMesh = brg.GetRegisteredMesh(draw.meshID).name == mesh.name + " Runtime LOD";
                    sawMesh |= isMesh; sawImpostor |= !isMesh;
                    for (int v = 0; v < draw.visibleCount; v++)
                    {
                        int slot = result.visibleInstances[draw.visibleOffset + v] & 0x00ffffff;
                        Assert.That(expected.TryGetValue(slot, out ushort mask), Is.True, "A distant caster leaked into the shadow map.");
                        Assert.That(draw.splitVisibilityMask, Is.EqualTo(mask));
                        actual.Add(slot);
                    }
                }
                Assert.That(actual, Is.EquivalentTo(expected.Keys), "Range-boundary casters must be retained.");
                Assert.That(actual.Count, Is.InRange(3, 1000));
                Assert.That(sawMesh && sawImpostor, Is.True, "Both mesh LODs and impostors must use the same casting range.");
                Assert.That(definition.LodData.MeshLevels[0].FadeWidth, Is.EqualTo(fadeWidth));
                int[] perSplit = ImpostorTestCullingOutput.CountSplitInstances(result, splitCount);
                for (int s = 0; s < splitCount; s++)
                {
                    int expectedCasters = 0;
                    foreach (ushort mask in expected.Values) if ((mask & (1 << s)) != 0) expectedCasters++;
                    Assert.That(perSplit[s], Is.InRange(expectedCasters, expectedCasters * 2), "Only geometry crossfades may duplicate a caster.");
                }

                ImpostorTestCullingOutput.Release(output[0]); output[0] = default;
                renderer.LiveShadows = false;
                ((JobHandle)callback.Invoke(renderer, new[] { null, context, commands, (object)IntPtr.Zero })).Complete();
                Assert.That(output[0].visibleInstanceCount, Is.Zero);
            }
            finally
            {
                renderer?.Dispose();
                ImpostorTestCullingOutput.Release(output[0]);
                output.Dispose(); splits.Dispose(); planes.Dispose();
                Object.DestroyImmediate(material); Object.DestroyImmediate(mesh); Object.DestroyImmediate(definition);
                pipeline.shadowDistance = previousDistance;
            }
        }

        private static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    }
}
