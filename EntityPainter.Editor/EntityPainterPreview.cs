using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using Hash128 = UnityEngine.Hash128;

namespace EntityPainter.Editor
{
    /// <summary>
    /// Shows a stroke in the Editor world before it is committed: painted records become preview entities, erased
    /// records' spawned entities are disabled. After a commit the previews stay until the spawner respawns from the
    /// rebaked data, so the stroke never flickers out.
    /// </summary>
    internal sealed class EntityPainterPreview
    {
        private const double RESPAWN_TIMEOUT = 3d;

        private readonly Dictionary<Entity, PaintedTemplate> _templates = new();
        private readonly List<Entity> _hidden = new();

        private World _world;
        private Entity _awaitedSpawner;
        private Hash128 _awaitedHash;
        private double _awaitDeadline;
        private bool _awaiting;

        /// <summary>The world painted instances are previewed in: the Editor world, outside Play mode.</summary>
        private static World EditorWorld
        {
            get
            {
                World world = World.DefaultGameObjectInjectionWorld;
                return !EditorApplication.isPlayingOrWillChangePlaymode && world is { IsCreated: true } ? world : null;
            }
        }

        /// <summary>Finds the live-baked spawner entity of <paramref name="target"/> in the Editor world.</summary>
        public bool TryGetSpawner(PaintedEntities target, out Entity spawner)
        {
            spawner = Entity.Null;
            _world = EditorWorld;
            if (_world == null || !target)
            {
                return false;
            }

            EntityManager entityManager = _world.EntityManager;
            EntityId id = target.gameObject.GetEntityId();
            using EntityQuery query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PaintedInstances>(),
                ComponentType.ReadOnly<EntityGuid>());
            using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            using NativeArray<EntityGuid> guids = query.ToComponentDataArray<EntityGuid>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (guids[i].OriginatingEntityId == id && guids[i].Serial == 0)
                {
                    spawner = entities[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>Instantiates a preview of one painted record, once the prototype's prefab has been baked.</summary>
        public void Spawn(Entity spawner, int prototype, in PaintedRecord record)
        {
            if (!IsAlive(spawner))
            {
                return;
            }

            EntityManager entityManager = _world.EntityManager;
            DynamicBuffer<PaintedPrefab> prefabs = entityManager.GetBuffer<PaintedPrefab>(spawner, true);
            if (prototype >= prefabs.Length)
            {
                return;
            }

            Entity prefab = prefabs[prototype].Prefab;
            if (!entityManager.Exists(prefab) || !entityManager.HasComponent<Prefab>(prefab))
            {
                return;
            }

            if (!_templates.TryGetValue(prefab, out PaintedTemplate template) || !entityManager.Exists(template.Entity))
            {
                template = PaintedTemplate.Create(entityManager, prefab, default);
                _templates[prefab] = template;
            }

            Entity instance = entityManager.Instantiate(template.Entity);
            template.Write(entityManager, instance, record);
        }

        /// <summary>
        /// Disables the spawned entities of erased records. Skipped while the Editor world is out of sync with
        /// <paramref name="target"/>, because record indices would not match.
        /// </summary>
        public void Hide(Entity spawner, PaintedEntities target, List<int> records)
        {
            if (!IsAlive(spawner) || records.Count == 0)
            {
                return;
            }

            EntityManager entityManager = _world.EntityManager;
            if (!entityManager.HasBuffer<PaintedSpawnedEntity>(spawner))
            {
                return;
            }

            DynamicBuffer<PaintedSpawnedEntity> spawned = entityManager.GetBuffer<PaintedSpawnedEntity>(spawner, true);
            if (spawned.Length != target.InstanceCount)
            {
                return;
            }

            int first = _hidden.Count;
            foreach (int record in records)
            {
                _hidden.Add(spawned[record].Value);
            }

            // SetEnabled is a structural change, which invalidates the buffer read above.
            for (int i = first; i < _hidden.Count; i++)
            {
                if (entityManager.Exists(_hidden[i]))
                {
                    entityManager.SetEnabled(_hidden[i], false);
                }
            }
        }

        /// <summary>Keeps the previews until the spawner respawns from new data, or a timeout passes.</summary>
        public void ClearAfterRespawn(Entity spawner)
        {
            if (!IsAlive(spawner) || !_world.EntityManager.HasComponent<PaintedSpawnerState>(spawner))
            {
                Clear();
                return;
            }

            _awaiting = true;
            _awaitedSpawner = spawner;
            _awaitedHash = _world.EntityManager.GetComponentData<PaintedSpawnerState>(spawner).Hash;
            _awaitDeadline = EditorApplication.timeSinceStartup + RESPAWN_TIMEOUT;
        }

        /// <summary>Call every editor update while a commit awaits its respawn.</summary>
        public void Update()
        {
            if (!_awaiting)
            {
                return;
            }

            bool respawned = !IsAlive(_awaitedSpawner)
                || !_world.EntityManager.HasComponent<PaintedSpawnerState>(_awaitedSpawner)
                || _world.EntityManager.GetComponentData<PaintedSpawnerState>(_awaitedSpawner).Hash != _awaitedHash;
            if (respawned || EditorApplication.timeSinceStartup > _awaitDeadline)
            {
                Clear();
            }
        }

        /// <summary>Destroys every preview and re-enables entities hidden by an uncommitted erase.</summary>
        public void Clear()
        {
            _awaiting = false;
            _templates.Clear();
            if (_world is not { IsCreated: true })
            {
                _hidden.Clear();
                return;
            }

            EntityManager entityManager = _world.EntityManager;
            foreach (Entity entity in _hidden)
            {
                if (entityManager.Exists(entity))
                {
                    entityManager.SetEnabled(entity, true);
                }
            }

            _hidden.Clear();

            using EntityQuery previews = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PaintedInstanceOwner>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            });
            previews.SetSharedComponentFilter(default(PaintedInstanceOwner));
            entityManager.DestroyEntity(previews);
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private bool IsAlive(Entity spawner)
        {
            return _world is { IsCreated: true } && _world.EntityManager.Exists(spawner)
                && _world.EntityManager.HasComponent<PaintedInstances>(spawner);
        }
    }
}
