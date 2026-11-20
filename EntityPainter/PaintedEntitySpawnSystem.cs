using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace EntityPainter
{
    /// <summary>
    /// Instantiates the instances of every baked <see cref="PaintedEntities"/> spawner, in Play mode and in the Editor
    /// world. Instances inherit the prefab's SceneSection and unload with their SubScene. A rebake that changes
    /// <see cref="PaintedInstances.Hash"/> replaces all of a spawner's instances.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial class PaintedEntitySpawnSystem : SystemBase
    {
        private const int TRANSFORM_BATCH = 256;

        private readonly List<PendingWrite> _writes = new();

        private EntityQuery _spawners;
        private EntityQuery _orphans;
        private EntityQuery _owned;

        protected override void OnCreate()
        {
            _spawners = SystemAPI.QueryBuilder().WithAll<PaintedInstances, PaintedPrefab>().Build();
            _orphans = SystemAPI.QueryBuilder().WithAll<PaintedSpawnerState>().WithNone<PaintedInstances>().Build();
            _owned = SystemAPI.QueryBuilder()
                .WithAll<PaintedInstanceOwner>()
                .WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities)
                .Build();
            RequireAnyForUpdate(_spawners, _orphans);
        }

        protected override void OnUpdate()
        {
            RemoveOrphans();

            bool respawned = false;
            using (NativeArray<Entity> spawners = _spawners.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity spawner in spawners)
                {
                    PaintedInstances instances = EntityManager.GetComponentData<PaintedInstances>(spawner);
                    if (EntityManager.HasComponent<PaintedSpawnerState>(spawner)
                        && EntityManager.GetComponentData<PaintedSpawnerState>(spawner).Hash == instances.Hash)
                    {
                        continue;
                    }

                    DestroyOwned(spawner);
                    Spawn(spawner, instances);
                    respawned = true;
                }
            }

            // A respawn follows an editor commit, which makes the stroke previews redundant.
            if (respawned)
            {
                DestroyOwned(Entity.Null);
            }

            ScheduleWrites();
        }

        private void RemoveOrphans()
        {
            if (_orphans.IsEmpty)
            {
                return;
            }

            using NativeArray<Entity> orphans = _orphans.ToEntityArray(Allocator.Temp);
            foreach (Entity orphan in orphans)
            {
                DestroyOwned(orphan);
            }

            EntityManager.RemoveComponent<PaintedSpawnedEntity>(orphans);
            EntityManager.RemoveComponent<PaintedSpawnerState>(orphans);
        }

        private void DestroyOwned(Entity spawner)
        {
            _owned.SetSharedComponentFilter(new PaintedInstanceOwner { Spawner = spawner });
            EntityManager.DestroyEntity(_owned);
            _owned.ResetFilter();
        }

        [SuppressMessage("FireAlt.Analyzers", "FA1001", Justification = "Each template copies a baked prefab once per "
            + "respawn and swaps EntityGuid for the owner, so every instance is created in its final archetype. "
            + "Respawns only follow a bake or a SubScene load.")]
        private void Spawn(Entity spawner, PaintedInstances instances)
        {
            ref PaintedInstanceBlob blob = ref instances.Value.Value;
            int total = blob.Records.Length;
            NativeArray<Entity> spawned = CollectionHelper.CreateNativeArray<Entity>(total, WorldUpdateAllocator);
            var owner = new PaintedInstanceOwner { Spawner = spawner };

            using (NativeArray<PaintedPrefab> prefabs = EntityManager.GetBuffer<PaintedPrefab>(spawner).ToNativeArray(Allocator.Temp))
            {
                int start = 0;
                int prototypes = math.min(prefabs.Length, blob.Counts.Length);
                for (int i = 0; i < prototypes; i++)
                {
                    int count = blob.Counts[i];
                    Entity prefab = prefabs[i].Prefab;
                    bool valid = count > 0 && start + count <= total && EntityManager.Exists(prefab)
                        && EntityManager.HasComponent<Prefab>(prefab);
                    if (valid)
                    {
                        PaintedTemplate template = PaintedTemplate.Create(EntityManager, prefab, owner);
                        NativeArray<Entity> output = spawned.GetSubArray(start, count);
                        EntityManager.Instantiate(template.Entity, output);
                        _writes.Add(new PendingWrite
                        {
                            Entities = output,
                            Instances = instances.Value,
                            Start = start,
                            Template = template,
                        });
                    }

                    start += count;
                }
            }

            EntityManager.AddComponentData(spawner, new PaintedSpawnerState { Hash = instances.Hash });
            if (!EntityManager.HasBuffer<PaintedSpawnedEntity>(spawner))
            {
                EntityManager.AddBuffer<PaintedSpawnedEntity>(spawner);
            }

            DynamicBuffer<PaintedSpawnedEntity> buffer = EntityManager.GetBuffer<PaintedSpawnedEntity>(spawner);
            buffer.ResizeUninitialized(total);
            buffer.Reinterpret<Entity>().AsNativeArray().CopyFrom(spawned);
        }

        /// <summary>Writes every placement after the structural changes, so the jobs never race a later Instantiate.</summary>
        private void ScheduleWrites()
        {
            if (_writes.Count == 0)
            {
                return;
            }

            ComponentLookup<LocalTransform> localTransforms = GetComponentLookup<LocalTransform>();
            ComponentLookup<LocalToWorld> localToWorlds = GetComponentLookup<LocalToWorld>();
            JobHandle handle = Dependency;
            foreach (PendingWrite write in _writes)
            {
                switch (write.Template.Kind)
                {
                    case PaintedTransformKind.LocalTransform:
                        handle = new WriteLocalTransformsJob
                        {
                            Entities = write.Entities,
                            Instances = write.Instances,
                            Start = write.Start,
                            Template = write.Template,
                            Transforms = localTransforms,
                        }.Schedule(write.Entities.Length, TRANSFORM_BATCH, handle);
                        break;
                    case PaintedTransformKind.LocalToWorld:
                        handle = new WriteLocalToWorldJob
                        {
                            Entities = write.Entities,
                            Instances = write.Instances,
                            Start = write.Start,
                            Template = write.Template,
                            Transforms = localToWorlds,
                        }.Schedule(write.Entities.Length, TRANSFORM_BATCH, handle);
                        break;
                }
            }

            _writes.Clear();
            Dependency = handle;
        }
    }
}
