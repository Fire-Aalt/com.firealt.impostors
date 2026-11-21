using System;
using Impostors.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        private const int SYNC_JOB_BATCH = 16;
        private const int MINIMUM_GROWN_CAPACITY = 128;

        // Above this many separate SetData ranges in one synchronization, slots are compacted and uploaded whole.
        private const int MAXIMUM_UPLOAD_RUNS = 1024;
        private const string INVALID_TRANSFORM =
            "Invalid impostor LocalToWorld on {0}; expected a finite affine transform with positive uniform scale.";

        /// <summary>
        /// Brings GPU slots, spatial cells, chunk metadata and instance buffers in line with the ECS population.
        /// Unchanged populations return immediately unless <paramref name="force"/> is set.
        /// </summary>
        public void SynchronizeEntities(bool force = false)
        {
            if (_disposed || _entityWorld is not { IsCreated: true })
            {
                return;
            }

            CompleteOutstandingViews();
            _entityQuery.CompleteDependency();
            EntityManager entityManager = _entityWorld.EntityManager;
            _changeTracker.Update();
            if (_appliedTileSize != ImpostorSpatialCell.TileSize)
            {
                _appliedTileSize = ImpostorSpatialCell.TileSize;
                force = true;
            }

            if (!force && !_changeTracker.Changed && _cleanupQuery.IsEmpty)
            {
                UploadedBytesLastSync = 0;
                return;
            }

            // Keep the population dirty if synchronization throws. Culling waits for
            // a successful retry, and invalid input is reported on every attempt.
            _changeTracker.RequireSynchronization();
            UploadedBytesLastSync = 0;
            ReleaseDestroyedAllocations();
            RegroupChangedEntities(entityManager, force);
            // Assign slots after spatial regrouping so fresh full chunks receive contiguous GPU ranges.
            AssignGpuSlots(entityManager);
            force |= GrowPrototypeBuffers();
            if (_chunks.IsCreated)
            {
                _chunks.Dispose();
            }

            _chunks = GatherInstanceChunks(_allInstances, Allocator.Persistent);
            // Reused slots come back in release order, so a mass destroy and respawn can scatter one upload into a run
            // per instance. Millions of small SetData calls exhaust the frame's graphics ring buffer and remove the
            // D3D12 device. Renumbering slots in chunk order restores one contiguous run per prototype.
            if (CountUploadRuns(entityManager, force) > MAXIMUM_UPLOAD_RUNS)
            {
                CompactSlots(entityManager);
                force = true;
            }

            UploadChangedChunks(entityManager, force);
            _changeTracker.Update();
            _changeTracker.AcceptChanges();
        }

        private static void ValidateTransform(Entity entity, float4x4 matrix)
        {
            if (!ImpostorTransformValidation.IsValid(matrix))
            {
                throw new InvalidOperationException(string.Format(INVALID_TRANSFORM, entity));
            }
        }

        private static bool IsChunkCurrent(EntityManager entityManager, ArchetypeChunk chunk,
            ref ComponentTypeHandle<LocalToWorld> transformType)
        {
            var metadata = entityManager.GetChunkComponentData<RenderChunk>(chunk);
            return metadata.TransformVersion == chunk.GetChangeVersion(ref transformType)
                && metadata.OrderVersion == chunk.GetOrderVersion();
        }

        private static Bounds ToBounds(RenderBounds bounds) => new(bounds.Center, bounds.Extents * 2);

        private static void Encapsulate(ref Bounds global, ref bool hasGlobal, Bounds bounds)
        {
            if (hasGlobal)
            {
                global.Encapsulate(bounds);
            }
            else
            {
                global = bounds;
                hasGlobal = true;
            }
        }

        /// <summary>Returns the GPU slots of destroyed entities that still hold this renderer's cleanup binding.</summary>
        private void ReleaseDestroyedAllocations()
        {
            EntityManager entityManager = _entityWorld.EntityManager;
            NativeArray<Entity> destroyed = _cleanupQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in destroyed)
            {
                var prototype = entityManager.GetSharedComponent<ImpostorCleanupPrototype>(entity);
                if (!_prototypeBlobs.Contains(prototype.Data))
                {
                    continue;
                }

                int index = prototype.Data.Value.Index;
                _freeSlots[index].Push(entityManager.GetComponentData<ImpostorGpuSlot>(entity).Value);
                entityManager.RemoveComponent<ImpostorGpuSlot>(entity);
                entityManager.RemoveComponent<ImpostorCleanupPrototype>(entity);
            }
        }

        /// <summary>Doubles the instance buffer of every prototype whose slots outgrew it. Returns whether any grew.</summary>
        private bool GrowPrototypeBuffers()
        {
            bool grown = false;
            for (int p = 0; p < _capacities.Count; p++)
            {
                if (_nextSlots[p] <= _capacities[p])
                {
                    continue;
                }

                int capacity = math.max(_nextSlots[p], math.max(MINIMUM_GROWN_CAPACITY, _capacities[p] * 2));
                ResizePrototypeBuffer(p, capacity);
                grown = true;
            }

            return grown;
        }

        /// <summary>Validates the whole change set, then applies spatial cells as one bulk change per destination.</summary>
        private void RegroupChangedEntities(EntityManager entityManager, bool force)
        {
            NativeArray<ArchetypeChunk> input = GatherInstanceChunks(_allInstances, Allocator.Temp);
            var transformType = entityManager.GetComponentTypeHandle<LocalToWorld>(true);
            var slotType = entityManager.GetComponentTypeHandle<ImpostorGpuSlot>(true);
            var cellType = entityManager.GetSharedComponentTypeHandle<ImpostorSpatialCell>();
            using var chunks = new NativeList<ArchetypeChunk>(input.Length, Allocator.TempJob);
            using var cells = new NativeList<int2>(input.Length, Allocator.TempJob);
            using var firstRows = new NativeList<int>(input.Length, Allocator.TempJob);
            int rows = 0;
            foreach (ArchetypeChunk chunk in input)
            {
                if (!force && chunk.Has(ref slotType) && IsChunkCurrent(entityManager, chunk, ref transformType))
                {
                    continue;
                }

                chunks.Add(chunk);
                cells.Add(chunk.GetSharedComponent(cellType).Value);
                firstRows.Add(rows);
                rows += chunk.Count;
            }

            if (chunks.Length == 0)
            {
                return;
            }

            ValidateTransforms(entityManager, chunks.AsArray(), transformType);
            using var moves = new NativeArray<CellMove>(rows, Allocator.TempJob);
            using var sorted = new NativeList<CellMove>(rows, Allocator.TempJob);
            using var sortedEntities = new NativeList<Entity>(rows, Allocator.TempJob);
            JobHandle find = new FindCellMovesJob
            {
                Chunks = chunks.AsArray(),
                CurrentCells = cells.AsArray(),
                FirstRows = firstRows.AsArray(),
                TransformType = transformType,
                EntityType = entityManager.GetEntityTypeHandle(),
                TileSize = ImpostorSpatialCell.TileSize,
                Moves = moves,
            }.Schedule(chunks.Length, SYNC_JOB_BATCH);
            new SortCellMovesJob { Moves = moves, Sorted = sorted, Entities = sortedEntities }.Schedule(find).Complete();
            ApplyCellMoves(entityManager, sorted, sortedEntities);
        }

        /// <summary>Throws for the first invalid transform, naming the entity that holds it.</summary>
        private static void ValidateTransforms(EntityManager entityManager, NativeArray<ArchetypeChunk> chunks,
            ComponentTypeHandle<LocalToWorld> transformType)
        {
            using var invalid = new NativeArray<byte>(chunks.Length, Allocator.TempJob);
            new ValidateTransformsJob { Chunks = chunks, TransformType = transformType, Invalid = invalid }
                .Schedule(chunks.Length, SYNC_JOB_BATCH).Complete();
            for (int i = 0; i < invalid.Length; i++)
            {
                if (invalid[i] == 0)
                {
                    continue;
                }

                // Burst found a bad row. Report it with the entity that holds it.
                NativeArray<Entity> entities = chunks[i].GetNativeArray(entityManager.GetEntityTypeHandle());
                NativeArray<LocalToWorld> transforms = chunks[i].GetNativeArray(ref transformType);
                for (int row = 0; row < transforms.Length; row++)
                {
                    ValidateTransform(entities[row], transforms[row].Value);
                }
            }
        }

        private static void ApplyCellMoves(EntityManager entityManager, NativeList<CellMove> sorted,
            NativeList<Entity> sortedEntities)
        {
            for (int start = 0; start < sorted.Length;)
            {
                int2 cell = sorted[start].Cell;
                int end = start + 1;
                while (end < sorted.Length && sorted[end].Cell.Equals(cell))
                {
                    end++;
                }

                NativeArray<Entity> entities = sortedEntities.AsArray().GetSubArray(start, end - start);
                entityManager.SetSharedComponent(entities, new ImpostorSpatialCell { Value = cell });
                start = end;
            }
        }

        /// <summary>Gives new instances GPU slots and cleanup bindings as one bulk change per prototype.</summary>
        private void AssignGpuSlots(EntityManager entityManager)
        {
            try
            {
                foreach (BlobAssetReference<ImpostorPrototypeData> blob in _prototypeBlobs)
                {
                    _pendingQuery.SetSharedComponentFilter(new ImpostorPrototype { Data = blob });
                    int count = _pendingQuery.CalculateEntityCount();
                    if (count == 0)
                    {
                        continue;
                    }

                    int index = blob.Value.Index;
                    using var slots = new NativeArray<ImpostorGpuSlot>(count, Allocator.TempJob);
                    NativeArray<ImpostorGpuSlot> values = slots;
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = new ImpostorGpuSlot { Value = AllocateSlot(index) };
                    }

                    entityManager.AddSharedComponent(_pendingQuery, new ImpostorCleanupPrototype { Data = blob });
                    entityManager.AddComponentData(_pendingQuery, slots);
                }
            }
            finally
            {
                _pendingQuery.ResetFilter();
            }
        }

        private int AllocateSlot(int prototype)
        {
            return _freeSlots[prototype].Count > 0 ? _freeSlots[prototype].Pop() : _nextSlots[prototype]++;
        }

        /// <summary>Counts the contiguous slot ranges <see cref="UploadChangedChunks"/> would upload, without uploading.</summary>
        private int CountUploadRuns(EntityManager entityManager, bool force)
        {
            var transformType = entityManager.GetComponentTypeHandle<LocalToWorld>(true);
            var slotType = entityManager.GetComponentTypeHandle<ImpostorGpuSlot>(true);
            var prototypeType = entityManager.GetSharedComponentTypeHandle<ImpostorPrototype>();
            int runs = 0;
            int prototype = -1;
            int nextSlot = -1;
            foreach (ArchetypeChunk chunk in _chunks)
            {
                if (!chunk.Has(ref slotType) || (!force && IsChunkCurrent(entityManager, chunk, ref transformType)))
                {
                    continue;
                }

                int index = chunk.GetSharedComponent(prototypeType).Data.Value.Index;
                NativeArray<ImpostorGpuSlot> slots = chunk.GetNativeArray(ref slotType);
                for (int row = 0; row < slots.Length; row++)
                {
                    if (index != prototype || slots[row].Value != nextSlot)
                    {
                        runs++;
                    }

                    prototype = index;
                    nextSlot = slots[row].Value + 1;
                }
            }

            return runs;
        }

        /// <summary>Renumbers every owned instance densely in chunk order. The caller must upload all chunks afterwards.</summary>
        private void CompactSlots(EntityManager entityManager)
        {
            var slotType = entityManager.GetComponentTypeHandle<ImpostorGpuSlot>(false);
            var prototypeType = entityManager.GetSharedComponentTypeHandle<ImpostorPrototype>();
            for (int p = 0; p < _nextSlots.Count; p++)
            {
                _nextSlots[p] = 0;
                _freeSlots[p].Clear();
            }

            foreach (ArchetypeChunk chunk in _chunks)
            {
                if (!chunk.Has(ref slotType))
                {
                    continue;
                }

                int index = chunk.GetSharedComponent(prototypeType).Data.Value.Index;
                NativeArray<ImpostorGpuSlot> slots = chunk.GetNativeArray(ref slotType);
                for (int row = 0; row < slots.Length; row++)
                {
                    slots[row] = new ImpostorGpuSlot { Value = _nextSlots[index]++ };
                }
            }
        }

        private void UploadChangedChunks(EntityManager entityManager, bool force)
        {
            var transformType = entityManager.GetComponentTypeHandle<LocalToWorld>(true);
            var slotType = entityManager.GetComponentTypeHandle<ImpostorGpuSlot>(true);
            var prototypeType = entityManager.GetSharedComponentTypeHandle<ImpostorPrototype>();
            InstanceCount = 0;
            _ownedChunkCount = 0;
            UploadedBytesLastSync = 0;
            UploadRunsLastSync = 0;
            Bounds global = default;
            bool hasGlobal = false;
            using var work = new NativeList<ChunkUpload>(_chunks.Length, Allocator.TempJob);
            int rows = 0;
            foreach (ArchetypeChunk chunk in _chunks)
            {
                if (!chunk.Has(ref slotType))
                {
                    continue;
                }

                RequireChunkCapacity(chunk);
                InstanceCount += chunk.Count;
                _ownedChunkCount++;
                if (!force && IsChunkCurrent(entityManager, chunk, ref transformType))
                {
                    Encapsulate(ref global, ref hasGlobal, ToBounds(entityManager.GetChunkComponentData<RenderChunk>(chunk).Bounds));
                    continue;
                }

                ImpostorPrototypeData data = chunk.GetSharedComponent(prototypeType).Data.Value;
                work.Add(new ChunkUpload
                {
                    Chunk = chunk,
                    FirstRow = rows,
                    Prototype = data.Index,
                    Bounds = data.Bounds,
                    ReferencePoint = data.ReferencePoint,
                    Size = data.Size,
                });
                rows += chunk.Count;
            }

            if (work.Length > 0)
            {
                PackAndUpload(entityManager, work.AsArray(), rows, ref global, ref hasGlobal);
            }

            if (hasGlobal)
            {
                _brg.SetGlobalBounds(global);
            }
        }

        private void PackAndUpload(EntityManager entityManager, NativeArray<ChunkUpload> work, int rows, ref Bounds global,
            ref bool hasGlobal)
        {
            const NativeArrayOptions UNINITIALIZED = NativeArrayOptions.UninitializedMemory;
            var transformType = entityManager.GetComponentTypeHandle<LocalToWorld>(true);
            var slotType = entityManager.GetComponentTypeHandle<ImpostorGpuSlot>(true);
            var records = new PackedRecords
            {
                Matrices = new NativeArray<uint>(rows * PACKED_MATRIX_WORDS, Allocator.TempJob, UNINITIALIZED),
                Inverses = new NativeArray<uint>(rows * PACKED_MATRIX_WORDS, Allocator.TempJob, UNINITIALIZED),
                EntityIds = new NativeArray<uint>(rows * ENTITY_ID_WORDS, Allocator.TempJob, UNINITIALIZED),
            };
            using var results = new NativeArray<ChunkUploadResult>(work.Length, Allocator.TempJob, UNINITIALIZED);
            try
            {
                new PackChunkUploadsJob
                {
                    Work = work,
                    TransformType = transformType,
                    EntityType = entityManager.GetEntityTypeHandle(),
                    BoundsType = entityManager.GetComponentTypeHandle<RenderBounds>(false),
                    MetricsType = entityManager.GetComponentTypeHandle<ImpostorLodMetrics>(false),
                    Matrices = records.Matrices,
                    Inverses = records.Inverses,
                    EntityWords = records.EntityIds,
                    Results = results,
                }.Schedule(work.Length, SYNC_JOB_BATCH).Complete();

                // Slots follow chunk order, so a bulk load collapses into one upload per stream and prototype.
                var run = new UploadRun { Prototype = -1 };
                for (int i = 0; i < work.Length; i++)
                {
                    ChunkUpload upload = work[i];
                    NativeArray<ImpostorGpuSlot> slots = upload.Chunk.GetNativeArray(ref slotType);
                    for (int row = 0; row < slots.Length; row++)
                    {
                        int flat = upload.FirstRow + row;
                        if (!run.TryExtend(upload.Prototype, slots[row].Value, flat))
                        {
                            UploadRecords(records, run);
                            run = new UploadRun { Prototype = upload.Prototype, FirstRow = flat, FirstSlot = slots[row].Value, Length = 1 };
                        }
                    }

                    ChunkUploadResult result = results[i];
                    entityManager.SetChunkComponentData(upload.Chunk, new RenderChunk
                    {
                        Bounds = result.Render,
                        ReferenceBounds = result.References,
                        MinimumHeight = result.MinimumHeight,
                        MaximumHeight = result.MaximumHeight,
                        TransformVersion = upload.Chunk.GetChangeVersion(ref transformType),
                        OrderVersion = upload.Chunk.GetOrderVersion(),
                    });
                    Encapsulate(ref global, ref hasGlobal, ToBounds(result.Render));
                }

                UploadRecords(records, run);
            }
            finally
            {
                records.Dispose();
            }
        }

        /// <summary>Copies one contiguous slot range of every stream into its prototype's instance buffer.</summary>
        private void UploadRecords(PackedRecords records, UploadRun run)
        {
            if (run.Length == 0)
            {
                return;
            }

            UploadRunsLastSync++;
            UploadedBytesLastSync += run.Length * (long)INSTANCE_RECORD_BYTES;
            GraphicsBuffer buffer = _buffers[run.Prototype];
            int capacity = _capacities[run.Prototype];
            const int RESERVED_WORDS = RESERVED_INSTANCE_BYTES / sizeof(uint);
            int matrixWords = run.Length * PACKED_MATRIX_WORDS;
            int matrixSource = run.FirstRow * PACKED_MATRIX_WORDS;
            int matrixDestination = RESERVED_WORDS + run.FirstSlot * PACKED_MATRIX_WORDS;
            buffer.SetData(records.Matrices, matrixSource, matrixDestination, matrixWords);
            buffer.SetData(records.Inverses, matrixSource, matrixDestination + capacity * PACKED_MATRIX_WORDS, matrixWords);
            int idDestination = RESERVED_WORDS + capacity * PACKED_MATRIX_WORDS * 2 + run.FirstSlot * ENTITY_ID_WORDS;
            buffer.SetData(records.EntityIds, run.FirstRow * ENTITY_ID_WORDS, idDestination, run.Length * ENTITY_ID_WORDS);
        }

        /// <summary>Packed GPU streams of the changed rows, indexed by row in upload order.</summary>
        private struct PackedRecords : IDisposable
        {
            public NativeArray<uint> Matrices;
            public NativeArray<uint> Inverses;
            public NativeArray<uint> EntityIds;

            public void Dispose()
            {
                Matrices.Dispose();
                Inverses.Dispose();
                EntityIds.Dispose();
            }
        }

        /// <summary>A range of rows whose slots are consecutive within one prototype's buffer.</summary>
        private struct UploadRun
        {
            public int Prototype;
            public int FirstRow;
            public int FirstSlot;
            public int Length;

            public bool TryExtend(int prototype, int slot, int row)
            {
                if (prototype != Prototype || slot != FirstSlot + Length || row != FirstRow + Length)
                {
                    return false;
                }

                Length++;
                return true;
            }
        }
    }
}
