using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Persistent containers of one culling view. They grow on demand and are reused every frame.</summary>
        private sealed class CullingViewScratch : IDisposable
        {
            public const int MASKS_PER_WORK_ITEM = 32;
            private const int MAXIMUM_WORK_PARTITIONS = 128;
            private const int DEPTH_BUCKETS = 8;

            public NativeArray<CullingWorker> Workers;
            public NativeParallelHashMap<DrawBinKey, byte> KeySet;
            public NativeList<DrawBinKey> Keys;
            public NativeList<DrawWorkItem> WorkItems;
            public NativeArray<DrawBinOutput> Bins;
            public NativeArray<byte> Fades;
            public NativeArray<PlanePacket4> Planes;
            public NativeArray<RenderSplit> Splits;
            public NativeArray<ImpostorCullingStatistics> Statistics;
            public NativeArray<IndirectDrawTarget> Indirect;
            public int Growths;

            public CullingViewScratch(int blockCount)
            {
                // Logical work partitions own persistent streams. Thread stealing cannot move a
                // partition to different scratch or cause fresh allocations on a new physical worker.
                int partitions = math.clamp(blockCount, 1, MAXIMUM_WORK_PARTITIONS);
                Workers = new NativeArray<CullingWorker>(partitions, Allocator.Persistent);
                Statistics = new NativeArray<ImpostorCullingStatistics>(1, Allocator.Persistent);
                Indirect = new NativeArray<IndirectDrawTarget>(1, Allocator.Persistent);
            }

            public void Prepare(int instances, int levels, bool fades, int splitCount, int planePackets)
            {
                Growths = 0;
                long maximumKeys = (long)levels * DEPTH_BUCKETS * (fades ? 3 : 1) * ((1L << splitCount) - 1);
                int keyCapacity = (int)Math.Max(1, Math.Min(instances * 2L, maximumKeys));
                PrepareKeys(keyCapacity);
                Ensure(ref Bins, keyCapacity);

                long maximumMasks = instances * (fades ? 2L : 1L);
                long workItems = (maximumMasks + MASKS_PER_WORK_ITEM - 1) / MASKS_PER_WORK_ITEM
                    + (long)keyCapacity * Workers.Length;
                PrepareWorkItems((int)Math.Max(1, Math.Min(maximumMasks, workItems)));
                Ensure(ref Fades, fades ? instances : 1);
                Ensure(ref Planes, math.max(1, planePackets));
                Ensure(ref Splits, splitCount);
            }

            public void Dispose()
            {
                if (Workers.IsCreated)
                {
                    DisposeWorkers();
                }

                if (KeySet.IsCreated)
                {
                    KeySet.Dispose();
                }

                if (Keys.IsCreated)
                {
                    Keys.Dispose();
                }

                if (WorkItems.IsCreated)
                {
                    WorkItems.Dispose();
                }

                DisposeIfCreated(ref Bins);
                DisposeIfCreated(ref Fades);
                DisposeIfCreated(ref Planes);
                DisposeIfCreated(ref Splits);
                DisposeIfCreated(ref Statistics);
                DisposeIfCreated(ref Indirect);
            }

            private void PrepareKeys(int capacity)
            {
                if (!KeySet.IsCreated)
                {
                    KeySet = new NativeParallelHashMap<DrawBinKey, byte>(capacity, Allocator.Persistent);
                    Keys = new NativeList<DrawBinKey>(capacity, Allocator.Persistent);
                    Growths++;
                }
                else if (KeySet.Capacity < capacity)
                {
                    KeySet.Capacity = capacity;
                    Keys.Capacity = capacity;
                    Growths++;
                }

                KeySet.Clear();
                Keys.Clear();
            }

            private void PrepareWorkItems(int capacity)
            {
                if (!WorkItems.IsCreated)
                {
                    WorkItems = new NativeList<DrawWorkItem>(capacity, Allocator.Persistent);
                    Growths++;
                }
                else if (WorkItems.Capacity < capacity)
                {
                    WorkItems.Capacity = capacity;
                    Growths++;
                }

                WorkItems.Clear();
            }

            private void Ensure<T>(ref NativeArray<T> array, int count) where T : unmanaged
            {
                if (array.IsCreated && array.Length >= count)
                {
                    return;
                }

                DisposeIfCreated(ref array);
                array = new NativeArray<T>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Growths++;
            }

            private void DisposeWorkers()
            {
                var workers = (CullingWorker*)Workers.GetUnsafePtr();
                for (int i = 0; i < Workers.Length; i++)
                {
                    ref CullingWorker worker = ref workers[i];
                    if (!worker.Bins.IsCreated)
                    {
                        continue;
                    }

                    for (int bin = 0; bin < worker.Bins.Length; bin++)
                    {
                        worker.Bins.ElementAt(bin).Masks.Dispose();
                    }

                    worker.Bins.Dispose();
                    worker.BinLookup.Dispose();
                }

                Workers.Dispose();
            }

            private static void DisposeIfCreated<T>(ref NativeArray<T> array) where T : unmanaged
            {
                if (array.IsCreated)
                {
                    array.Dispose();
                }
            }
        }
    }
}
