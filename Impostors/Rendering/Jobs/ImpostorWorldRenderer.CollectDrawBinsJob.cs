using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Sums one draw key across partitions and gives each partition's bin its offset inside the draw.</summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct CollectDrawBinsJob : IJobParallelForDefer
        {
            private static readonly ProfilerMarker Marker = new("Impostors.CollectBins");

            [ReadOnly] public NativeArray<DrawBinKey> Keys;

            [NativeDisableContainerSafetyRestriction] public NativeArray<CullingWorker> Workers;
            [WriteOnly] public NativeArray<DrawBinOutput> Output;

            public void Execute(int index)
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                DrawBinKey key = Keys[index];
                var workers = (CullingWorker*)Workers.GetUnsafePtr();
                int count = 0;
                for (int i = 0; i < Workers.Length; i++)
                {
                    ref CullingWorker worker = ref workers[i];
                    if (!worker.BinLookup.IsCreated || !worker.BinLookup.TryGetValue(key, out int binIndex))
                    {
                        continue;
                    }

                    ref WorkerDrawBin bin = ref worker.Bins.ElementAt(binIndex);
                    bin.WorkerOffset = count;
                    bin.OutputBinIndex = index;
                    count += bin.Count;
                }

                Output[index] = new DrawBinOutput { Count = count };
            }
        }
    }
}
