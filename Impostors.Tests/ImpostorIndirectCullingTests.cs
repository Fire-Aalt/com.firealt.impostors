using System;
using System.Linq;
using System.Reflection;
using Impostors.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed unsafe class ImpostorIndirectCullingTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        // Above the smallest upload capacity, so the first frame has no history that fits.
        private const int OverflowingInstances = 20000;

        [TestCase(BatchCullingViewType.Camera)]
        [TestCase(BatchCullingViewType.Light)]
        public void IndirectBuffersMatchDirectDraws(BatchCullingViewType viewType)
        {
            using var scene = new Scene(viewType, 4096);
            BatchCullingOutputDrawCommands direct = scene.Cull(IntPtr.Zero);
            BatchCullingOutputDrawCommands indirect = scene.Cull(scene.EngineContext);
            scene.Finish();

            Assert.That(direct.drawCommandCount, Is.GreaterThan(0));
            Assert.That(indirect.drawCommandCount, Is.Zero);
            Assert.That(indirect.visibleInstanceCount, Is.Zero);
            Assert.That(indirect.indirectDrawCommandCount, Is.EqualTo(direct.drawCommandCount));
            Assert.That(indirect.drawRanges[0].drawCommandsType, Is.EqualTo(BatchDrawCommandType.Indirect));

            int[] visible = scene.Read("Visible");
            int[] arguments = scene.Read("Arguments");
            for (int c = 0; c < direct.drawCommandCount; c++)
            {
                BatchDrawCommand expected = direct.drawCommands[c];
                BatchDrawCommandIndirect actual = indirect.indirectDrawCommands[c];
                Assert.That(actual.meshID, Is.EqualTo(expected.meshID));
                Assert.That(actual.materialID, Is.EqualTo(expected.materialID));
                Assert.That(actual.batchID, Is.EqualTo(expected.batchID));
                Assert.That(actual.flags, Is.EqualTo(expected.flags));
                Assert.That(actual.splitVisibilityMask, Is.EqualTo(expected.splitVisibilityMask));
                Assert.That(actual.visibleOffset, Is.EqualTo(expected.visibleOffset));

                int word = (int)actual.indirectArgsBufferOffset / sizeof(int);
                Assert.That(arguments[word + 1], Is.EqualTo((int)expected.visibleCount), "Indirect instance count");
                var directList = new int[expected.visibleCount];
                for (int v = 0; v < directList.Length; v++) directList[v] = direct.visibleInstances[expected.visibleOffset + v];
                var indirectList = visible.Skip((int)actual.visibleOffset).Take(directList.Length).ToArray();
                Assert.That(indirectList, Is.EquivalentTo(directList));
            }
        }

        [Test]
        public void ViewThatOutgrowsItsBuffersDrawsDirectlyThenIndirectly()
        {
            using var scene = new Scene(BatchCullingViewType.Camera, OverflowingInstances);
            BatchCullingOutputDrawCommands first = scene.Cull(scene.EngineContext);
            scene.Finish();
            BatchCullingOutputDrawCommands second = scene.Cull(scene.EngineContext);
            scene.Finish();

            Assert.That(first.drawCommandCount, Is.GreaterThan(0), "No capacity history yet, so this frame must stay direct.");
            Assert.That(first.indirectDrawCommandCount, Is.Zero);
            Assert.That(first.visibleInstanceCount, Is.EqualTo(OverflowingInstances).Or.GreaterThan(OverflowingInstances));
            Assert.That(second.indirectDrawCommandCount, Is.EqualTo(first.drawCommandCount));
            Assert.That(second.drawCommandCount, Is.Zero);
        }

        /// <summary>
        /// Edit mode renders the Scene view, inspectors and previews many times without advancing the frame count.
        /// Finished culling passes must recycle their views instead of allocating new GPU buffers for every pass.
        /// </summary>
        [Test]
        public void RepeatedPassesWithinOneFrameReuseIndirectBuffers()
        {
            const int PASSES = 32;
            using var scene = new Scene(BatchCullingViewType.Camera, 4096);
            for (int pass = 0; pass < PASSES; pass++)
            {
                scene.Cull(scene.EngineContext);
                scene.Finish();
            }

            Assert.That(scene.IndirectViewCount, Is.LessThanOrEqualTo(3));
        }

        private sealed class Scene : IDisposable
        {
            private readonly ImpostorDefinition _definition = Object.Instantiate(ImpostorTestAssets.Definition());
            private readonly Mesh _mesh;
            private readonly Material _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            private readonly ImpostorWorldRenderer _renderer;
            private readonly UniversalRenderPipelineAsset _pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            private readonly float _previousShadowDistance;
            private readonly BatchCullingViewType _viewType;
            private readonly int _instanceCount;
            private NativeArray<Plane> _planes = new NativeArray<Plane>(6, Allocator.TempJob);
            private NativeArray<CullingSplit> _splits = new NativeArray<CullingSplit>(1, Allocator.TempJob);
            private readonly System.Collections.Generic.List<NativeArray<BatchCullingOutputDrawCommands>> _outputs = new();

            public IntPtr EngineContext => (IntPtr)typeof(ImpostorWorldRenderer).GetField("EngineCullingContext", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

            public Scene(BatchCullingViewType viewType, int instanceCount)
            {
                _viewType = viewType;
                _instanceCount = instanceCount;
                _previousShadowDistance = _pipeline.shadowDistance;
                _pipeline.shadowDistance = 100000;
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
                Object.DestroyImmediate(cube);
                _definition.Bounds = new Bounds(Vector3.zero, Vector3.one * 2);
                _definition.LodData = new ImpostorLodData
                {
                    ReferencePoint = Vector3.zero, Size = 4,
                    MeshLevels = new[] { new ImpostorMeshLod {
                        TransitionScreenHeight = .2f, FadeWidth = .2f,
                        Parts = new[] { new ImpostorLodPart { Mesh = _mesh, Material = _material } }
                    } }
                };

                int side = Mathf.CeilToInt(Mathf.Sqrt(instanceCount));
                var instances = new ImpostorInstance[instanceCount];
                for (int i = 0; i < instanceCount; i++)
                {
                    instances[i] = new ImpostorInstance { Scale = 1, Yaw = i * 137.508f, Position = new Vector3(i % side * 8, 0, i / side * 8) };
                }

                _renderer = new ImpostorWorldRenderer { LiveShadows = true };
                _renderer.SetData(new ImpostorRenderData(new[] { _definition }, instances));

                var bounds = new[] { Vector3.left, Vector3.right, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
                for (int p = 0; p < bounds.Length; p++) _planes[p] = new Plane(bounds[p], 100000);
                _splits[0] = new CullingSplit { sphereCenter = Vector3.zero, sphereRadius = 100000, cullingPlaneOffset = 0, cullingPlaneCount = 6 };
            }

            public BatchCullingOutputDrawCommands Cull(IntPtr user)
            {
                var output = new NativeArray<BatchCullingOutputDrawCommands>(1, Allocator.TempJob);
                _outputs.Add(output);
                object context = default(BatchCullingContext);
                Set(context, nameof(BatchCullingContext.cullingPlanes), _planes);
                Set(context, nameof(BatchCullingContext.cullingSplits), _splits);
                Set(context, nameof(BatchCullingContext.viewType), _viewType);
                Set(context, nameof(BatchCullingContext.projectionType), BatchCullingProjectionType.Perspective);
                Set(context, nameof(BatchCullingContext.sceneCullingMask), ulong.MaxValue);
                Set(context, nameof(BatchCullingContext.cullingLayerMask), uint.MaxValue);
                Set(context, nameof(BatchCullingContext.lodParameters), new LODParameters { cameraPosition = new Vector3(0, 50, 0), fieldOfView = 60, cameraPixelHeight = 1080 });
                object commands = default(BatchCullingOutput);
                Set(commands, nameof(BatchCullingOutput.drawCommands), output);
                var callback = typeof(ImpostorWorldRenderer).GetMethod("Cull", Hidden);
                ((JobHandle)callback.Invoke(_renderer, new[] { null, context, commands, (object)user })).Complete();
                return output[0];
            }

            /// <summary>Ends the culling pass the way Unity's finished-culling callback does.</summary>
            public void Finish()
            {
                typeof(ImpostorWorldRenderer).GetMethod("OnFinishedCulling", Hidden).Invoke(_renderer, new object[] { IntPtr.Zero });
            }

            public int IndirectViewCount => ((Array)typeof(ImpostorWorldRenderer).GetField("_views", Hidden).GetValue(_renderer))
                .Cast<object>()
                .Count(view => view?.GetType().GetField("Indirect").GetValue(view) != null);

            public int[] Read(string field)
            {
                foreach (object view in (Array)typeof(ImpostorWorldRenderer).GetField("_views", Hidden).GetValue(_renderer))
                {
                    object indirect = view?.GetType().GetField("Indirect").GetValue(view);
                    if (indirect == null) continue;
                    var buffer = (GraphicsBuffer)indirect.GetType().GetField(field).GetValue(indirect);
                    var data = new int[buffer.count];
                    buffer.GetData(data);
                    return data;
                }

                throw new InvalidOperationException("No view used indirect buffers.");
            }

            public void Dispose()
            {
                _renderer.Dispose();
                foreach (NativeArray<BatchCullingOutputDrawCommands> output in _outputs)
                {
                    ImpostorTestCullingOutput.Release(output[0]);
                    output.Dispose();
                }

                _planes.Dispose();
                _splits.Dispose();
                Object.DestroyImmediate(_material);
                Object.DestroyImmediate(_mesh);
                Object.DestroyImmediate(_definition);
                _pipeline.shadowDistance = _previousShadowDistance;
            }

            private static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
        }
    }
}
