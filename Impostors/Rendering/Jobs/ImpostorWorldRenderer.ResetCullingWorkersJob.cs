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
        /// <summary>Clears each partition's counters and bins while keeping their allocations for this view.</summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct ResetCullingWorkersJob : IJobParallelFor
        {
            private static readonly ProfilerMarker Marker = new("Impostors.ResetWorkers");

            [NativeDisableContainerSafetyRestriction] public NativeArray<CullingWorker> Workers;

            public void Execute(int index)
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                ref CullingWorker worker = ref ((CullingWorker*)Workers.GetUnsafePtr())[index];
                worker.ResetCounters();
                if (!worker.Bins.IsCreated)
                {
                    return;
                }

                for (int i = 0; i < worker.Bins.Length; i++)
                {
                    ref WorkerDrawBin bin = ref worker.Bins.ElementAt(i);
                    bin.Masks.Clear();
                    bin.Count = 0;
                    bin.PublishedMasks = 0;
                    bin.PublishedCount = 0;
                }
            }
        }
    }
}
