using System.Linq;
using Impostors.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Impostors.Tests
{
    public sealed class ImpostorRenderingSystemTests
    {
        private World _world;
        private ImpostorRenderingSystem _system;

        [SetUp]
        public void CreateWorld()
        {
            _world = new World("Impostor rendering system test");
            _system = _world.CreateSystemManaged<ImpostorRenderingSystem>();
        }

        [TearDown]
        public void DisposeWorld()
        {
            _world.Dispose();
        }

        [Test]
        public void RespawningAPopulationUploadsOneContiguousRun()
        {
            const int count = 5000;
            Entity prefab = _system.CreatePrefab(ImpostorTestAssets.Definition(1));
            using var transforms = new NativeArray<float4x4>(count, Allocator.Temp);
            var values = transforms;
            for (int i = 0; i < count; i++)
                values[i] = float4x4.Translate(new float3(i % 100 * 10, 0, i / 100 * 10));
            var em = _world.EntityManager;

            using (var first = _system.Instantiate(prefab, transforms, Allocator.Temp))
            {
                _system.Update();
                ImpostorWorldRenderer renderer = _system.GetRenderer(0, false);
                renderer.SynchronizeEntities();
                // A fresh load uploads at most one run per spatial-cell chunk.
                Assert.That(renderer.UploadRunsLastSync, Is.InRange(1, renderer.ChunkCount));
                // Released slots return in reverse, which used to scatter the respawn into one upload per instance.
                em.DestroyEntity(first);
            }

            using var second = _system.Instantiate(prefab, transforms, Allocator.Temp);
            ImpostorWorldRenderer owner = _system.GetRenderer(0, false);
            owner.SynchronizeEntities();
            Assert.That(owner.InstanceCount, Is.EqualTo(count));
            Assert.That(owner.UploadRunsLastSync, Is.EqualTo(1));
            var slots = second.ToArray().Select(entity => em.GetComponentData<ImpostorGpuSlot>(entity).Value).ToArray();
            Assert.That(slots.Distinct().Count(), Is.EqualTo(count));
            Assert.That(slots.Max(), Is.EqualTo(count - 1));
        }
    }
}
