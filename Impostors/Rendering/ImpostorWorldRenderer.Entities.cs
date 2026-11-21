using System;
using System.Collections.Generic;
using Impostors.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        private const string PREVIEW_WORLD_NAME = "Impostor preview/test";
        private const string CHUNK_CAPACITY_EXCEEDED = "Impostor chunks exceed the 128-row visibility mask capacity.";
        private const string INVALID_PREFAB = "A prefab root with a valid baked Impostor component is required.";
        private const string INCOMPLETE_REFRESH = "Cannot refresh an incomplete impostor definition.";
        private const long CHUNK_BYTES = 16384;

        private readonly List<Entity> _templates = new();
        private readonly List<BlobAssetReference<ImpostorPrototypeData>> _prototypeBlobs = new();
        private readonly List<ImpostorDefinition> _definitions = new();
        private readonly List<int> _capacities = new();
        private readonly List<int> _nextSlots = new();
        private readonly List<Stack<int>> _freeSlots = new();

        private World _entityWorld;
        private bool _ownsEntityWorld;
        private bool _trackerDestroying;
        private EntityQuery _entityQuery;
        private EntityQuery _allInstances;
        private EntityQuery _cleanupQuery;
        private EntityQuery _pendingQuery;
        private EntityQuery _bakedInstances;
        private ImpostorChangeTracker _changeTracker;
        private float _appliedTileSize = ImpostorSpatialCell.DEFAULT_TILE_SIZE;
        private int _ownedChunkCount;

        /// <summary>Changes whenever a rebake refreshes adopted prototypes, so earlier adoption failures are retried.</summary>
        internal static int AdoptedDefinitionVersion { get; private set; }

        public long UploadedBytesLastSync { get; private set; }

        /// <summary>
        /// Contiguous slot ranges uploaded by the last synchronization. Each costs one GraphicsBuffer.SetData per stream.
        /// </summary>
        public int UploadRunsLastSync { get; private set; }

        public long InstanceChunkBytes => _ownedChunkCount * CHUNK_BYTES;
        public World EntityWorld => _entityWorld;

        /// <summary>Set on renderers that <see cref="ImpostorRenderingSystem"/> creates for baked entities.</summary>
        internal bool AdoptsBakedEntities { get; set; }

        internal bool IsDisposed => _disposed;

        /// <summary>Refreshes every renderer after a rebake, releasing baked entities bound to a replaced definition.</summary>
        public static void RefreshPrototypes(ImpostorDefinition previous, ImpostorDefinition current)
        {
            AdoptedDefinitionVersion++;
            foreach (ImpostorWorldRenderer renderer in new List<ImpostorWorldRenderer>(LiveRenderers))
            {
                if (renderer._disposed)
                {
                    continue;
                }

                if (renderer.AdoptsBakedEntities && previous && previous != current)
                {
                    renderer.ReleaseBakedInstances(previous);
                }

                if (!renderer.UsesDefinition(current))
                {
                    continue;
                }

                try
                {
                    renderer.RefreshPrototypeBindings();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        public Entity BakeEntityPrefab(GameObject prefab)
        {
            if (!prefab || !prefab.TryGetComponent(out Impostor authoring) || !authoring.Definition
                || !authoring.Definition.IsValid)
            {
                throw new ArgumentException(INVALID_PREFAB, nameof(prefab));
            }

            return _templates[EnsurePrototypeIndex(authoring.Definition, nameof(prefab))];
        }

        public void RefreshPrototypeBindings()
        {
            foreach (ImpostorDefinition definition in _definitions)
            {
                if (!definition || !definition.IsValid)
                {
                    throw new InvalidOperationException(INCOMPLETE_REFRESH);
                }

                if (definition.LodData != null && !definition.LodData.TryValidate(out string error))
                {
                    throw new InvalidOperationException(error);
                }
            }

            CompleteOutstandingViews();
            ReleaseRenderResources();
            for (int p = 0; p < _definitions.Count; p++)
            {
                RegisterAdditionalPrototype(_definitions[p], p);
            }

            for (int p = 0; p < _capacities.Count; p++)
            {
                ResizePrototypeBuffer(p, math.max(1, _capacities[p]));
            }

            SynchronizeEntities(true);
        }

        public bool UsesDefinition(ImpostorDefinition definition) => _definitions.Contains(definition);

        public ImpostorDefinition[] GetDefinitions() => _definitions.ToArray();

        public int[] GetPrototypeCounts()
        {
            var counts = new int[_definitions.Count];
            NativeArray<ArchetypeChunk> chunks = GatherInstanceChunks(_allInstances, Allocator.Temp);
            var prototypeType = _entityWorld.EntityManager.GetSharedComponentTypeHandle<ImpostorPrototype>();
            foreach (ArchetypeChunk chunk in chunks)
            {
                counts[chunk.GetSharedComponent(prototypeType).Data.Value.Index] += chunk.Count;
            }

            return counts;
        }

        public bool AreAllBoundsInsideFrustum(Camera camera)
        {
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            NativeArray<ArchetypeChunk> chunks = GatherInstanceChunks(_entityQuery, Allocator.Temp);
            var boundsType = _entityWorld.EntityManager.GetComponentTypeHandle<RenderBounds>(true);
            foreach (ArchetypeChunk chunk in chunks)
            {
                foreach (RenderBounds bounds in chunk.GetNativeArray(ref boundsType))
                {
                    if (!IsInsideFrustum(planes, bounds))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public Bounds[] CaptureWorldBounds()
        {
            _entityQuery.CompleteDependency();
            var bounds = new NativeList<Bounds>(InstanceCount, Allocator.Temp);
            NativeArray<ArchetypeChunk> chunks = GatherInstanceChunks(_entityQuery, Allocator.Temp);
            var boundsType = _entityWorld.EntityManager.GetComponentTypeHandle<RenderBounds>(true);
            foreach (ArchetypeChunk chunk in chunks)
            {
                foreach (RenderBounds value in chunk.GetNativeArray(ref boundsType))
                {
                    bounds.Add(new Bounds(value.Center, value.Extents * 2));
                }
            }

            return bounds.AsArray().ToArray();
        }

        public ImpostorRenderData CaptureRenderData()
        {
            EntityManager entityManager = _entityWorld.EntityManager;
            NativeArray<Entity> entities = GatherInstanceEntities(Allocator.Temp);
            var result = new List<ImpostorInstance>(InstanceCount);
            foreach (Entity entity in entities)
            {
                var prototype = entityManager.GetSharedComponent<ImpostorPrototype>(entity);
                Matrix4x4 matrix = entityManager.GetComponentData<LocalToWorld>(entity).Value;
                result.Add(new ImpostorInstance
                {
                    Position = matrix.GetColumn(3),
                    Yaw = matrix.rotation.eulerAngles.y,
                    Scale = matrix.MultiplyVector(Vector3.right).magnitude,
                    Prototype = prototype.Data.Value.Index,
                });
            }

            return new ImpostorRenderData(_definitions.ToArray(), result.ToArray());
        }

        internal static void ReleaseTracker(ImpostorChangeTracker tracker)
        {
            foreach (ImpostorWorldRenderer renderer in LiveRenderers)
            {
                if (renderer._changeTracker != tracker)
                {
                    continue;
                }

                if (!renderer._disposed)
                {
                    renderer._trackerDestroying = true;
                    renderer.Dispose();
                }

                return;
            }
        }

        internal static void SynchronizeWorld(World world)
        {
            foreach (ImpostorWorldRenderer renderer in LiveRenderers)
            {
                if (!renderer._disposed && renderer._entityWorld == world)
                {
                    renderer.SynchronizeEntities();
                }
            }
        }

        /// <summary>Registers a definition once and returns the blob that binds entities to it.</summary>
        internal BlobAssetReference<ImpostorPrototypeData> EnsurePrototype(ImpostorDefinition definition)
        {
            if (!definition || !definition.IsValid)
            {
                string name = definition ? definition.name : NULL_DEFINITION_NAME;
                throw new ArgumentException(INVALID_DEFINITION + name, nameof(definition));
            }

            return _prototypeBlobs[EnsurePrototypeIndex(definition, nameof(definition))];
        }

        private static bool IsInsideFrustum(Plane[] planes, RenderBounds bounds)
        {
            foreach (Plane plane in planes)
            {
                Vector3 normal = plane.normal;
                var absoluteNormal = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
                float distance = Vector3.Dot(normal, bounds.Center) + plane.distance;
                if (distance - Vector3.Dot(absoluteNormal, bounds.Extents) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static ImpostorPrototypeData CreatePrototypeData(ImpostorDefinition definition, int index, int firstLod,
            int lodCount, Bounds bounds, Vector3 reference, float size)
        {
            var data = new ImpostorPrototypeData
            {
                Index = index,
                Bounds = ConvertBounds(bounds),
                ReferencePoint = reference,
                Size = size,
                FirstLod = firstLod,
                LodCount = lodCount,
            };
#if UNITY_EDITOR
            // Diagnostic only. Long prefab names must not prevent registration.
            data.SourcePrefabName.CopyFromTruncated(definition.SourcePrefab ? definition.SourcePrefab.name : definition.name);
#endif
            return data;
        }

        private static void RequireChunkCapacity(ArchetypeChunk chunk)
        {
            if (chunk.Capacity > ChunkVisibilityMask.CAPACITY)
            {
                throw new InvalidOperationException(CHUNK_CAPACITY_EXCEEDED);
            }
        }

        private static void RemoveRuntimeBinding(EntityManager entityManager, EntityQuery query)
        {
            entityManager.RemoveComponent(query, new ComponentTypeSet(ComponentType.ReadWrite<ImpostorGpuSlot>(),
                ComponentType.ReadWrite<ImpostorCleanupPrototype>(), ComponentType.ReadWrite<ImpostorPrototype>()));
        }

        private void InitializeEntities(World suppliedWorld)
        {
            _entityWorld = suppliedWorld ?? (Application.isPlaying ? World.DefaultGameObjectInjectionWorld : null);
            _ownsEntityWorld = _entityWorld == null;
            if (_ownsEntityWorld)
            {
                _entityWorld = new World(PREVIEW_WORLD_NAME);
            }

            EntityManager entityManager = _entityWorld.EntityManager;
            _entityQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LocalToWorld, RenderBounds, ImpostorLodMetrics, ImpostorGpuSlot, ImpostorPrototype>()
                .WithAllChunkComponent<RenderChunk>()
                .Build(entityManager);
            _allInstances = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LocalToWorld, ImpostorPrototype>()
                .Build(entityManager);
            _pendingQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LocalToWorld, ImpostorPrototype>()
                .WithNone<ImpostorGpuSlot>()
                .Build(entityManager);
            _cleanupQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ImpostorGpuSlot, ImpostorCleanupPrototype>()
                .WithNone<LocalToWorld>()
                .Build(entityManager);
            _bakedInstances = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ImpostorPrototype, ImpostorBakedPrototype>()
                .WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities)
                .Build(entityManager);
            _changeTracker = _entityWorld.CreateSystemManaged<ImpostorChangeTracker>();
            _changeTracker.Configure(_prototypeBlobs);
        }

        private void AddEntityPrototype(ImpostorDefinition definition, int index, int firstLod, int lodCount,
            Bounds bounds, Vector3 reference, float size)
        {
            EntityManager entityManager = _entityWorld.EntityManager;
            var builder = new BlobBuilder(Allocator.Temp);
            ref ImpostorPrototypeData data = ref builder.ConstructRoot<ImpostorPrototypeData>();
            data = CreatePrototypeData(definition, index, firstLod, lodCount, bounds, reference, size);
            BlobAssetReference<ImpostorPrototypeData> record =
                builder.CreateBlobAssetReference<ImpostorPrototypeData>(Allocator.Persistent);
            Entity template = entityManager.CreateEntity(typeof(Prefab), typeof(LocalToWorld), typeof(RenderBounds),
                typeof(ImpostorLodMetrics), typeof(ImpostorSpatialCell), typeof(ImpostorPrototype));
            entityManager.AddComponent(template, ComponentType.ChunkComponent<RenderChunk>());
            entityManager.SetComponentData(template, new LocalToWorld { Value = float4x4.identity });
            entityManager.SetSharedComponent(template, new ImpostorPrototype { Data = record });
            _templates.Add(template);
            _prototypeBlobs.Add(record);
            _definitions.Add(definition);
            _capacities.Add(0);
            _nextSlots.Add(0);
            _freeSlots.Add(new Stack<int>());
            RequireChunkCapacity(entityManager.GetChunk(template));
        }

        private int EnsurePrototypeIndex(ImpostorDefinition definition, string parameter)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ImpostorWorldRenderer));
            }

            if (definition.LodData != null && !definition.LodData.TryValidate(out string error))
            {
                throw new ArgumentException(error, parameter);
            }

            int existing = _definitions.IndexOf(definition);
            if (existing >= 0)
            {
                return existing;
            }

            // Resource registration remains shared with the validated bulk path.
            // Preserve existing entities while extending the prototype tables.
            RegisterAdditionalPrototype(definition);
            return _templates.Count - 1;
        }

        /// <summary>Returns baked entities bound to a definition to the unadopted state, keeping the entities.</summary>
        private void ReleaseBakedInstances(ImpostorDefinition definition)
        {
            int prototype = _definitions.IndexOf(definition);
            if (prototype < 0 || _entityWorld is not { IsCreated: true })
            {
                return;
            }

            CompleteOutstandingViews();
            _bakedInstances.SetSharedComponentFilter(new ImpostorPrototype { Data = _prototypeBlobs[prototype] });
            try
            {
                // Slots are returned so a later adoption reuses them.
                NativeArray<ImpostorGpuSlot> slots = _bakedInstances.ToComponentDataArray<ImpostorGpuSlot>(Allocator.Temp);
                foreach (ImpostorGpuSlot slot in slots)
                {
                    _freeSlots[prototype].Push(slot.Value);
                }

                RemoveRuntimeBinding(_entityWorld.EntityManager, _bakedInstances);
            }
            finally
            {
                _bakedInstances.ResetFilter();
            }

            _changeTracker.RequireSynchronization();
        }

        private void InstantiateBatch(int prototype, NativeArray<ImpostorInstance> placements, int count)
        {
            EntityManager entityManager = _entityWorld.EntityManager;
            var entities = new NativeArray<Entity>(count, Allocator.Temp);
            entityManager.Instantiate(_templates[prototype], entities);
            for (int i = 0; i < count; i++)
            {
                entityManager.SetComponentData(entities[i], new LocalToWorld { Value = placements[i].Matrix });
            }
        }

        private NativeArray<ArchetypeChunk> GatherInstanceChunks(EntityQuery query, Allocator allocator)
        {
            var result = new NativeList<ArchetypeChunk>(Allocator.Temp);
            try
            {
                foreach (BlobAssetReference<ImpostorPrototypeData> record in _prototypeBlobs)
                {
                    query.SetSharedComponentFilter(new ImpostorPrototype { Data = record });
                    result.AddRange(query.ToArchetypeChunkArray(Allocator.Temp));
                }

                return new NativeArray<ArchetypeChunk>(result.AsArray(), allocator);
            }
            finally
            {
                query.ResetFilter();
            }
        }

        private NativeArray<Entity> GatherInstanceEntities(Allocator allocator)
        {
            NativeArray<ArchetypeChunk> chunks = GatherInstanceChunks(_allInstances, Allocator.Temp);
            var result = new NativeList<Entity>(Allocator.Temp);
            EntityTypeHandle entityType = _entityWorld.EntityManager.GetEntityTypeHandle();
            foreach (ArchetypeChunk chunk in chunks)
            {
                result.AddRange(chunk.GetNativeArray(entityType));
            }

            return new NativeArray<Entity>(result.AsArray(), allocator);
        }

        private void ResizePrototypeBuffer(int prototype, int capacity)
        {
            if (capacity > MAXIMUM_BATCH_INSTANCES)
            {
                throw new InvalidOperationException(BATCH_LIMIT);
            }

            int words = (RESERVED_INSTANCE_BYTES + capacity * INSTANCE_RECORD_BYTES) / sizeof(uint);
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, words, sizeof(uint));
            buffer.SetData(new uint[RESERVED_INSTANCE_BYTES / sizeof(uint)]);
            _brg.RemoveBatch(_batches[prototype]);
            _batches[prototype] = _brg.AddBatch(CreateInstanceMetadata(capacity), buffer.bufferHandle);
            _buffers[prototype].Dispose();
            _buffers[prototype] = buffer;
            _capacities[prototype] = capacity;

            ImpostorPrototypeData data = _prototypeBlobs[prototype].Value;
            for (int i = data.FirstLod; i < data.FirstLod + data.LodCount; i++)
            {
                RenderLod lod = _lods[i];
                lod.Batch = _batches[prototype];
                _lods[i] = lod;
            }

            InstanceBufferBytes = 0;
            foreach (GraphicsBuffer prototypeBuffer in _buffers)
            {
                InstanceBufferBytes += prototypeBuffer.count * (long)sizeof(uint);
            }
        }

        private void ClearEntities()
        {
            if (_entityWorld is { IsCreated: true })
            {
                RemoveOwnedEntities(_entityWorld.EntityManager);
            }

            // Release allocations only after instances and cleanup bindings have been removed.
            foreach (BlobAssetReference<ImpostorPrototypeData> blob in _prototypeBlobs)
            {
                blob.Dispose();
            }

            _ownedChunkCount = 0;
            _templates.Clear();
            _prototypeBlobs.Clear();
            _definitions.Clear();
            _capacities.Clear();
            _nextSlots.Clear();
            _freeSlots.Clear();
        }

        private void RemoveOwnedEntities(EntityManager entityManager)
        {
            // Baked entities belong to their scene. Unbind them instead of destroying them so they can be adopted again.
            foreach (BlobAssetReference<ImpostorPrototypeData> blob in _prototypeBlobs)
            {
                _bakedInstances.SetSharedComponentFilter(new ImpostorPrototype { Data = blob });
                RemoveRuntimeBinding(entityManager, _bakedInstances);
            }

            _bakedInstances.ResetFilter();

            // Filtered bulk changes keep other renderers' populations in a shared world untouched.
            foreach (BlobAssetReference<ImpostorPrototypeData> blob in _prototypeBlobs)
            {
                _allInstances.SetSharedComponentFilter(new ImpostorPrototype { Data = blob });
                entityManager.DestroyEntity(_allInstances);
            }

            _allInstances.ResetFilter();
            foreach (Entity template in _templates)
            {
                if (entityManager.Exists(template))
                {
                    entityManager.DestroyEntity(template);
                }
            }

            var allocation = new ComponentTypeSet(ComponentType.ReadWrite<ImpostorGpuSlot>(),
                ComponentType.ReadWrite<ImpostorCleanupPrototype>());
            foreach (BlobAssetReference<ImpostorPrototypeData> blob in _prototypeBlobs)
            {
                _cleanupQuery.SetSharedComponentFilter(new ImpostorCleanupPrototype { Data = blob });
                entityManager.RemoveComponent(_cleanupQuery, allocation);
            }

            _cleanupQuery.ResetFilter();
        }

        private void DisposeEntities()
        {
            if (_entityWorld is not { IsCreated: true })
            {
                return;
            }

            _entityQuery.Dispose();
            _allInstances.Dispose();
            _cleanupQuery.Dispose();
            _pendingQuery.Dispose();
            _bakedInstances.Dispose();
            if (!_trackerDestroying)
            {
                _entityWorld.DestroySystemManaged(_changeTracker);
            }

            if (_ownsEntityWorld)
            {
                _entityWorld.Dispose();
            }
        }
    }
}
