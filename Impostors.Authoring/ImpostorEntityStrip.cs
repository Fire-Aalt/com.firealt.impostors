using System;
using System.Collections.Generic;
using Impostors.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Hybrid.Baking;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using Unity.Transforms;
using UnityEngine;

namespace Impostors.Authoring
{
    /// <summary>
    /// Reduces every baked impostor to its runtime archetype. Children and additional entities are excluded from the
    /// output, and components added to the root by any other baker or baking system are removed.
    /// </summary>
    [NoAutoStaticsCleanup]
    internal static class ImpostorEntityStrip
    {
        private const string INVALID_PLACEMENT = "Impostor entity '{0}' needs a finite affine transform with positive "
            + "uniform scale. It is left out of the baked scene.";
        private const string EDITOR_RENDER_DATA_TYPE = "Unity.Entities.EditorRenderData, Unity.Scenes";
        private const string REMOVE_UNUSED_ENTITY_TYPE = "Unity.Entities.RemoveUnusedEntityInBake, Unity.Entities.Hybrid";
        private const int MAXIMUM_TYPE_SET_LENGTH = 15;

        private static HashSet<TypeIndex> _allowedTypes;

        private static HashSet<TypeIndex> AllowedTypes => _allowedTypes ??= CreateAllowedTypes();

        public static void Run(EntityManager entityManager, EntityQuery roots, bool reportInvalid)
        {
            NativeArray<Entity> rootEntities = roots.ToEntityArray(Allocator.Temp);
            NativeHashSet<Entity> discarded = CollectDiscarded(entityManager, rootEntities);

            // Roots first: an impostor nested in another impostor's hierarchy is discarded below even when valid.
            foreach (Entity root in rootEntities)
            {
                ApplyPlacement(entityManager, root, reportInvalid);
            }

            NativeArray<Entity> discardedArray = discarded.ToNativeArray(Allocator.Temp);
            entityManager.AddComponent<BakingOnlyEntity>(discardedArray);

            var all = new HashSet<Entity>();
            foreach (Entity root in rootEntities)
            {
                all.Add(root);
            }

            foreach (Entity entity in discardedArray)
            {
                all.Add(entity);
            }

            RemoveDisallowedComponents(entityManager, all);
        }

        private static NativeHashSet<Entity> CollectDiscarded(EntityManager entityManager, NativeArray<Entity> roots)
        {
            var discarded = new NativeHashSet<Entity>(roots.Length * 4, Allocator.Temp);
            foreach (Entity root in roots)
            {
                // Copy before any structural change invalidates the buffer.
                NativeArray<ImpostorBakedDescendant> descendants =
                    entityManager.GetBuffer<ImpostorBakedDescendant>(root, true).ToNativeArray(Allocator.Temp);
                foreach (ImpostorBakedDescendant descendant in descendants)
                {
                    if (descendant.Value != Entity.Null && entityManager.Exists(descendant.Value))
                    {
                        discarded.Add(descendant.Value);
                        AddAdditionalEntities(entityManager, descendant.Value, discarded);
                    }
                }

                AddAdditionalEntities(entityManager, root, discarded);
            }

            return discarded;
        }

        private static void AddAdditionalEntities(EntityManager entityManager, Entity entity, NativeHashSet<Entity> output)
        {
            if (!entityManager.HasBuffer<AdditionalEntitiesBakingData>(entity))
            {
                return;
            }

            foreach (AdditionalEntitiesBakingData additional in entityManager.GetBuffer<AdditionalEntitiesBakingData>(entity, true))
            {
                if (entityManager.Exists(additional.Value))
                {
                    output.Add(additional.Value);
                }
            }
        }

        private static void ApplyPlacement(EntityManager entityManager, Entity root, bool reportInvalid)
        {
            if (!entityManager.HasComponent<ImpostorBakedPrototype>(root) || !entityManager.HasComponent<TransformAuthoring>(root)
                || !entityManager.HasComponent<LocalToWorld>(root))
            {
                return;
            }

            // TransformAuthoring is refreshed on every pass, so parent-only moves are picked up too.
            float4x4 matrix = entityManager.GetComponentData<TransformAuthoring>(root).LocalToWorld;
            if (!ImpostorTransformValidation.IsValid(matrix))
            {
                // One bad placement must not stop the whole renderer from synchronizing.
                if (reportInvalid)
                {
                    Debug.LogError(string.Format(INVALID_PLACEMENT, entityManager.GetName(root)));
                }

                SetBakingOnly(entityManager, root, true);
                return;
            }

            SetBakingOnly(entityManager, root, false);
            if (!entityManager.GetComponentData<LocalToWorld>(root).Value.Equals(matrix))
            {
                entityManager.SetComponentData(root, new LocalToWorld { Value = matrix });
            }

            ImpostorSpatialCell cell = ImpostorSpatialCell.FromPosition(matrix.c3.xyz);
            if (!entityManager.GetSharedComponent<ImpostorSpatialCell>(root).Value.Equals(cell.Value))
            {
                entityManager.SetSharedComponent(root, cell);
            }
        }

        private static void SetBakingOnly(EntityManager entityManager, Entity entity, bool bakingOnly)
        {
            if (entityManager.HasComponent<BakingOnlyEntity>(entity) == bakingOnly)
            {
                return;
            }

            if (bakingOnly)
            {
                entityManager.AddComponent<BakingOnlyEntity>(entity);
            }
            else
            {
                entityManager.RemoveComponent<BakingOnlyEntity>(entity);
            }
        }

        private static void RemoveDisallowedComponents(EntityManager entityManager, HashSet<Entity> entities)
        {
            var groups = new Dictionary<EntityArchetype, List<Entity>>();
            foreach (Entity entity in entities)
            {
                EntityArchetype archetype = entityManager.GetChunk(entity).Archetype;
                if (!groups.TryGetValue(archetype, out List<Entity> group))
                {
                    groups.Add(archetype, group = new List<Entity>());
                }

                group.Add(entity);
            }

            foreach (KeyValuePair<EntityArchetype, List<Entity>> group in groups)
            {
                var members = new NativeArray<Entity>(group.Value.ToArray(), Allocator.Temp);
                RemoveDisallowedComponents(entityManager, group.Key, members);
            }
        }

        private static void RemoveDisallowedComponents(EntityManager entityManager, EntityArchetype archetype,
            NativeArray<Entity> members)
        {
            NativeArray<ComponentType> types = archetype.GetComponentTypes(Allocator.Temp);
            var removal = new FixedList128Bytes<ComponentType>();
            foreach (ComponentType type in types)
            {
                if (IsAllowed(type))
                {
                    continue;
                }

                // Batched removal rejects chunk components, which Entities Graphics adds to renderer entities.
                if (type.IsChunkComponent)
                {
                    foreach (Entity member in members)
                    {
                        entityManager.RemoveComponent(member, type);
                    }

                    continue;
                }

                removal.Add(type);
                if (removal.Length == MAXIMUM_TYPE_SET_LENGTH)
                {
                    entityManager.RemoveComponent(members, new ComponentTypeSet(removal));
                    removal.Clear();
                }
            }

            if (removal.Length > 0)
            {
                entityManager.RemoveComponent(members, new ComponentTypeSet(removal));
            }
        }

        private static bool IsAllowed(ComponentType type)
        {
            // Baking bookkeeping is removed by Unity itself before the scene is written.
            if (type.TypeIndex.IsBakingOnlyType || type.TypeIndex.IsTemporaryBakingType)
            {
                return true;
            }

            return !type.IsChunkComponent && AllowedTypes.Contains(type.TypeIndex);
        }

        private static HashSet<TypeIndex> CreateAllowedTypes()
        {
            var allowed = new HashSet<TypeIndex>
            {
                TypeManager.GetTypeIndex<LocalToWorld>(),
                TypeManager.GetTypeIndex<RenderBounds>(),
                TypeManager.GetTypeIndex<ImpostorLodMetrics>(),
                TypeManager.GetTypeIndex<ImpostorSpatialCell>(),
                TypeManager.GetTypeIndex<ImpostorBakedPrototype>(),
                TypeManager.GetTypeIndex<Prefab>(),
                TypeManager.GetTypeIndex<Disabled>(),
                TypeManager.GetTypeIndex<SceneSection>(),
                TypeManager.GetTypeIndex<SceneTag>(),
                TypeManager.GetTypeIndex<EntityGuid>(),
                TypeManager.GetTypeIndex<Simulate>(),
                TypeManager.GetTypeIndex<BakingOnlyEntity>(),
            };

            // Internal Entities bookkeeping: live baking adds the render data after each pass, and reference
            // tracking adds the unused marker. Removing either would only churn the next pass.
            foreach (string name in new[] { EDITOR_RENDER_DATA_TYPE, REMOVE_UNUSED_ENTITY_TYPE })
            {
                Type internalType = Type.GetType(name);
                if (internalType != null)
                {
                    allowed.Add(TypeManager.GetTypeIndex(internalType));
                }
            }

            return allowed;
        }
    }
}
