using System;
using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Writes one work item's visible GPU slots into its range of the view's visible-instance list.</summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct ExpandDrawMasksJob : IJobParallelForDefer
        {
            // A chunk spans at most a 128 m tile, about 181 m diagonally, so bins stay finer than the spacing between trees.
            private const int DEPTH_BIN_COUNT = 32;
            private const int WORD_BITS = 64;
            private static readonly ProfilerMarker Marker = new("Impostors.PackVisible");

            [ReadOnly] public NativeArray<DrawWorkItem> WorkItems;
            [ReadOnly] public NativeArray<byte> Fades;
            [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly] public ComponentTypeHandle<ImpostorGpuSlot> SlotType;
            [ReadOnly] public NativeArray<DrawBinOutput> Bins;
            [ReadOnly] public ComponentTypeHandle<ImpostorLodMetrics> MetricsType;
            [ReadOnly] public ComponentTypeHandle<RenderChunk> ChunkType;

            [NativeDisableContainerSafetyRestriction] public NativeArray<CullingWorker> Workers;
            [NativeDisableUnsafePtrRestriction] public IndirectDrawTarget* Target;
            public float3 Origin;

            /// <summary>
            /// Draws each chunk's rows nearest first. Stored row order is the authored order, so without this
            /// overdraw depends on the view direction.
            /// </summary>
            public bool SortRows;

            /// <summary>Chunks whose centre is within this squared distance get an exact sort. Farther chunks use depth bins.</summary>
            public float ExactRowSortWithinSquared;

            public void Execute(int index)
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                DrawWorkItem work = WorkItems[index];
                ref CullingWorker worker = ref ((CullingWorker*)Workers.GetUnsafePtr())[work.Worker];
                WorkerDrawBin bin = worker.Bins[work.Bin];
                int first = Bins[bin.OutputBinIndex].VisibleOffset + bin.WorkerOffset + work.LocalVisibleOffset;
                int destination = first;
                for (int m = work.FirstMask; m < work.FirstMask + work.MaskCount; m++)
                {
                    ExpandMask(bin.Masks[m], bin.Key.Fade, ref destination);
                }

                if (Target->Indirect && destination > first)
                {
                    int bytes = (destination - first) * sizeof(int);
                    Target->VisibleUploader.AddUpload(Target->VisibleOutput + first, bytes, first * sizeof(int));
                }
            }

            private void ExpandMask(DrawMask mask, int fade, ref int destination)
            {
                if (SortRows)
                {
                    ExpandSorted(mask, fade, ref destination);
                    return;
                }

                NativeArray<ImpostorGpuSlot> slots = Chunks[mask.ChunkIndex].GetNativeArray(ref SlotType);
                if (fade == 0 && AreContiguous(slots))
                {
                    ExpandContiguousWord(Target->VisibleOutput, ref destination, mask.Visible.Lower, slots[0].Value);
                    ExpandContiguousWord(Target->VisibleOutput, ref destination, mask.Visible.Upper,
                        slots[0].Value + WORD_BITS);
                    return;
                }

                for (int word = 0; word < 2; word++)
                {
                    ulong remaining = word == 0 ? mask.Visible.Lower : mask.Visible.Upper;
                    while (remaining != 0)
                    {
                        int bit = math.tzcnt(remaining) + word * WORD_BITS;
                        remaining &= remaining - 1;
                        Target->VisibleOutput[destination++] = PackedSlot(slots, mask.ChunkIndex, bit, fade);
                    }
                }
            }

            private void ExpandSorted(DrawMask mask, int fade, ref int destination)
            {
                RenderChunk chunk = Chunks[mask.ChunkIndex].GetChunkComponentData(ref ChunkType);
                if (math.distancesq(chunk.Bounds.Center, Origin) <= ExactRowSortWithinSquared)
                {
                    ExpandNearestFirst(mask, fade, ref destination);
                }
                else
                {
                    ExpandDepthBinned(mask, chunk.ReferenceBounds, fade, ref destination);
                }
            }

            private int PackedSlot(NativeArray<ImpostorGpuSlot> slots, int chunkIndex, int bit, int fade)
            {
                if (fade == 0)
                {
                    return slots[bit].Value;
                }

                byte opacity = Fades[chunkIndex * ChunkVisibilityMask.CAPACITY + bit];
                return ImpostorLodSelection.PackIndex(slots[bit].Value, opacity, fade == 2);
            }

            /// <summary>
            /// A comparison sort per chunk is too slow for a million-instance view, so far chunks use a counting sort
            /// along the ray to the chunk.
            /// </summary>
            private void ExpandDepthBinned(DrawMask mask, RenderBounds bounds, int fade, ref int destination)
            {
                ArchetypeChunk chunk = Chunks[mask.ChunkIndex];
                NativeArray<ImpostorGpuSlot> slots = chunk.GetNativeArray(ref SlotType);
                NativeArray<ImpostorLodMetrics> metrics = chunk.GetNativeArray(ref MetricsType);
                float3 axis = math.normalizesafe(bounds.Center - Origin);
                float radius = math.csum(math.abs(axis) * bounds.Extents);
                float binsPerMetre = radius > 0 ? DEPTH_BIN_COUNT * 0.5f / radius : 0;

                byte* rows = stackalloc byte[ChunkVisibilityMask.CAPACITY];
                byte* bins = stackalloc byte[ChunkVisibilityMask.CAPACITY];
                int* offsets = stackalloc int[DEPTH_BIN_COUNT];
                UnsafeUtility.MemClear(offsets, sizeof(int) * DEPTH_BIN_COUNT);
                int count = 0;
                for (int word = 0; word < 2; word++)
                {
                    ulong remaining = word == 0 ? mask.Visible.Lower : mask.Visible.Upper;
                    while (remaining != 0)
                    {
                        int bit = math.tzcnt(remaining) + word * WORD_BITS;
                        remaining &= remaining - 1;
                        float depth = math.dot(metrics[bit].ReferencePoint - bounds.Center, axis) + radius;
                        int bin = math.clamp((int)(depth * binsPerMetre), 0, DEPTH_BIN_COUNT - 1);
                        rows[count] = (byte)bit;
                        bins[count] = (byte)bin;
                        offsets[bin]++;
                        count++;
                    }
                }

                int next = destination;
                for (int bin = 0; bin < DEPTH_BIN_COUNT; bin++)
                {
                    int binCount = offsets[bin];
                    offsets[bin] = next;
                    next += binCount;
                }

                for (int i = 0; i < count; i++)
                {
                    Target->VisibleOutput[offsets[bins[i]]++] = PackedSlot(slots, mask.ChunkIndex, rows[i], fade);
                }

                destination = next;
            }

            private void ExpandNearestFirst(DrawMask mask, int fade, ref int destination)
            {
                ArchetypeChunk chunk = Chunks[mask.ChunkIndex];
                NativeArray<ImpostorGpuSlot> slots = chunk.GetNativeArray(ref SlotType);
                NativeArray<ImpostorLodMetrics> metrics = chunk.GetNativeArray(ref MetricsType);
                Row* rows = stackalloc Row[ChunkVisibilityMask.CAPACITY];
                int count = 0;
                for (int word = 0; word < 2; word++)
                {
                    ulong remaining = word == 0 ? mask.Visible.Lower : mask.Visible.Upper;
                    while (remaining != 0)
                    {
                        int bit = math.tzcnt(remaining) + word * WORD_BITS;
                        remaining &= remaining - 1;
                        float distanceSquared = math.distancesq(metrics[bit].ReferencePoint, Origin);
                        rows[count++] = new Row { DistanceSquared = distanceSquared, Bit = bit };
                    }
                }

                NativeSortExtension.Sort(rows, count);
                for (int i = 0; i < count; i++)
                {
                    Target->VisibleOutput[destination++] = PackedSlot(slots, mask.ChunkIndex, rows[i].Bit, fade);
                }
            }

            private static bool AreContiguous(NativeArray<ImpostorGpuSlot> slots)
            {
                bool contiguous = true;
                for (int i = 1; i < slots.Length; i++)
                {
                    contiguous &= slots[i].Value == slots[0].Value + i;
                }

                return contiguous;
            }

            /// <summary>Writes the slots of one mask word of a chunk whose rows hold consecutive slots from <paramref name="first"/>.</summary>
            private static void ExpandContiguousWord(int* output, ref int destination, ulong visible, int first)
            {
                if (visible == 0)
                {
                    return;
                }

                // Full words and low-bit prefixes represent contiguous instance indices.
                // Write four at a time without a bit scan or fade test for each tree.
                if ((visible & unchecked(visible + 1)) == 0)
                {
                    int count = math.countbits(visible);
                    int i = 0;
                    for (; i + 4 <= count; i += 4)
                    {
                        *(int4*)(output + destination + i) = new int4(first + i) + new int4(0, 1, 2, 3);
                    }

                    for (; i < count; i++)
                    {
                        output[destination + i] = first + i;
                    }

                    destination += count;
                    return;
                }

                while (visible != 0)
                {
                    output[destination++] = first + math.tzcnt(visible);
                    visible &= visible - 1;
                }
            }

            private struct Row : IComparable<Row>
            {
                public float DistanceSquared;
                public int Bit;

                public readonly int CompareTo(Row other) => DistanceSquared.CompareTo(other.DistanceSquared);
            }
        }
    }
}
