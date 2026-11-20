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
        /// <summary>Publishes each bin's masks that culling left below a full work item.</summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct FlushCullingWorkersJob : IJobParallelFor
        {
            private static readonly ProfilerMarker Marker = new("Impostors.FlushWorkers");

            [NativeDisableContainerSafetyRestriction] public NativeArray<CullingWorker> Workers;

            public NativeList<DrawWorkItem>.ParallelWriter WorkItems;

            public void Execute(int index)
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                ref CullingWorker worker = ref ((CullingWorker*)Workers.GetUnsafePtr())[index];
                if (!worker.Bins.IsCreated)
                {
                    return;
                }

                for (int i = 0; i < worker.Bins.Length; i++)
                {
                    ref WorkerDrawBin bin = ref worker.Bins.ElementAt(i);
                    if (bin.PublishedMasks == bin.Masks.Length)
                    {
                        continue;
                    }

                    WorkItems.AddNoResize(new DrawWorkItem
                    {
                        Worker = index,
                        Bin = i,
                        FirstMask = bin.PublishedMasks,
                        MaskCount = bin.Masks.Length - bin.PublishedMasks,
                        LocalVisibleOffset = bin.PublishedCount,
                    });
                }
            }
        }
    }
}
