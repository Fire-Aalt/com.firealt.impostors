using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>
        /// Assigns each draw bin its visible and command ranges, allocates the BRG output and publishes the view's
        /// statistics. Chooses indirect output only when this frame's counts fit the buffers prepared from earlier frames.
        /// </summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct PrefixDrawBinsJob : IJob
        {
            private const int ALIGNMENT = 16;
            private static readonly ProfilerMarker Marker = new("Impostors.BuildCommands");

            [ReadOnly] public NativeList<DrawBinKey> Keys;
            [ReadOnly] public NativeArray<RenderLod> Lods;
            [ReadOnly] public NativeList<DrawWorkItem> WorkItems;

            [NativeDisableContainerSafetyRestriction] public NativeArray<CullingWorker> Workers;
            [NativeDisableUnsafePtrRestriction] public BatchCullingOutputDrawCommands* Output;
            [NativeDisableUnsafePtrRestriction] public IndirectDrawTarget* Target;
            public NativeArray<DrawBinOutput> Bins;
            public NativeArray<ImpostorCullingStatistics> Statistics;
            public BatchFilterSettings Filter;
            public int MainThreadGrowths;
            /// <summary>Emits one draw range per depth bucket so a pass can draw the buckets near to far.</summary>
            public bool BandedRanges;
            public int ViewType;
#if UNITY_EDITOR
            public bool Picking;
#endif

            public void Execute()
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                AssignRanges(out int visible, out int commands);
                bool indirect = Target->Available && visible <= Target->VisibleCapacity
                    && commands <= Target->ArgumentCapacity && WorkItems.Length <= Target->OperationCapacity;
                Target->Indirect = indirect;
                Target->VisibleCount = visible;
                Target->CommandCount = commands;
                Target->VisibleOutput = visible > 0 ? Allocate<int>(visible) : null;
                AllocateOutput(indirect, visible, commands);
                Statistics[0] = SumStatistics(visible, commands);
            }

            private void AssignRanges(out int visible, out int commands)
            {
                visible = 0;
                commands = 0;
                for (int i = 0; i < Keys.Length; i++)
                {
                    DrawBinOutput bin = Bins[i];
                    bin.VisibleOffset = visible;
                    bin.CommandOffset = commands;
                    visible += bin.Count;
                    int parts = Lods[Keys[i].Lod].PartCount;
#if UNITY_EDITOR
                    // Picking IDs belong to draw commands, so every picked instance gets its own commands.
                    commands += Picking ? bin.Count * parts : parts;
#else
                    commands += parts;
#endif
                    Bins[i] = bin;
                }
            }

            private void AllocateOutput(bool indirect, int visible, int commands)
            {
                // Capacity comes from earlier frames, so a view that outgrows it draws directly for one frame.
                if (indirect)
                {
                    Output->indirectDrawCommands = commands > 0 ? Allocate<BatchDrawCommandIndirect>(commands) : null;
                    Output->indirectDrawCommandCount = commands;
                }
                else
                {
                    Output->visibleInstances = Target->VisibleOutput;
                    Output->visibleInstanceCount = visible;
                    Output->drawCommands = commands > 0 ? Allocate<BatchDrawCommand>(commands) : null;
                    Output->drawCommandCount = commands;
                }

                int ranges = commands > 0 ? (BandedRanges ? CountBuckets() : 1) : 0;
                Output->drawRanges = ranges > 0 ? Allocate<BatchDrawRange>(ranges) : null;
                Output->drawRangeCount = ranges;
#if UNITY_EDITOR
                Output->drawCommandPickingEntityIds = Picking && commands > 0 ? Allocate<EntityId>(commands) : null;
#endif
                Output->instanceSortingPositions = null;
                Output->instanceSortingPositionFloatCount = 0;
                if (commands > 0 && BandedRanges)
                {
                    WriteBucketRanges(indirect, commands);
                }
                else if (commands > 0)
                {
                    Output->drawRanges[0] = new BatchDrawRange
                    {
                        drawCommandsType = indirect ? BatchDrawCommandType.Indirect : BatchDrawCommandType.Direct,
                        drawCommandsBegin = 0,
                        drawCommandsCount = (uint)commands,
                        filterSettings = Filter,
                    };
                }
            }

            private int CountBuckets()
            {
                int count = 0;
                for (int i = 0; i < Keys.Length; i++)
                {
                    if (i == 0 || Keys[i].Bucket != Keys[i - 1].Bucket)
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>Keys are sorted nearest bucket first, so each bucket's commands are contiguous.</summary>
            private void WriteBucketRanges(bool indirect, int commands)
            {
                int range = 0;
                for (int i = 0; i < Keys.Length; i++)
                {
                    if (i > 0 && Keys[i].Bucket == Keys[i - 1].Bucket)
                    {
                        continue;
                    }

                    int bucket = Keys[i].Bucket;
                    int end = i + 1;
                    while (end < Keys.Length && Keys[end].Bucket == bucket)
                    {
                        end++;
                    }

                    int begin = Bins[i].CommandOffset;
                    int last = end < Keys.Length ? Bins[end].CommandOffset : commands;
                    BatchFilterSettings filter = Filter;
                    filter.renderingLayerMask = (Filter.renderingLayerMask & ((1u << ImpostorLodSelection.DEPTH_BUCKET_LAYER_SHIFT) - 1))
                        | ImpostorLodSelection.DepthBucketLayers(bucket, bucket + 1);
                    Output->drawRanges[range++] = new BatchDrawRange
                    {
                        drawCommandsType = indirect ? BatchDrawCommandType.Indirect : BatchDrawCommandType.Direct,
                        drawCommandsBegin = (uint)begin,
                        drawCommandsCount = (uint)(last - begin),
                        filterSettings = filter,
                    };
                }
            }

            private ImpostorCullingStatistics SumStatistics(int visible, int commands)
            {
                var statistics = new ImpostorCullingStatistics
                {
                    VisibleEntries = visible,
                    DrawCommands = commands,
                    DrawBins = Keys.Length,
                    ScratchGrowths = MainThreadGrowths,
                    ViewType = ViewType,
                };
                var workers = (CullingWorker*)Workers.GetUnsafePtr();
                for (int i = 0; i < Workers.Length; i++)
                {
                    CullingWorker worker = workers[i];
                    statistics.VisibleInstances += worker.Visible;
                    statistics.InsideChunks += worker.InsideChunks;
                    statistics.PartialChunks += worker.PartialChunks;
                    statistics.RejectedChunks += worker.RejectedChunks;
                    statistics.UniformChunks += worker.UniformChunks;
                    statistics.MixedChunks += worker.MixedChunks;
                    statistics.FadingInstances += worker.FadingInstances;
                    statistics.ScratchGrowths += worker.Growths;
                }

                return statistics;
            }

            /// <summary>
            /// Allocates TempJob memory. Unity frees BRG output after drawing. An indirect view's visible list is not
            /// BRG output, so <see cref="CommitIndirectView"/> frees it.
            /// </summary>
            private static T* Allocate<T>(int count) where T : unmanaged
            {
                return (T*)UnsafeUtility.Malloc((long)count * sizeof(T), ALIGNMENT, Allocator.TempJob);
            }
        }
    }
}
