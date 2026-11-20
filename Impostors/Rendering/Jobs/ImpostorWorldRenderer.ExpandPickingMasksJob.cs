#if UNITY_EDITOR
using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>
        /// Writes one direct draw command per visible picked instance. Unity's picking IDs belong to draw commands,
        /// not GPU slots or ECS rows.
        /// </summary>
        [BurstCompile]
        private struct ExpandPickingMasksJob : IJobParallelForDefer
        {
            private const int WORD_BITS = 64;

            [ReadOnly] public NativeArray<DrawWorkItem> WorkItems;
            [ReadOnly] public NativeArray<byte> Fades;
            [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly] public ComponentTypeHandle<ImpostorGpuSlot> SlotType;
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public NativeArray<DrawBinOutput> Bins;
            [ReadOnly] public NativeArray<RenderLod> Lods;
            [ReadOnly] public NativeArray<RenderPart> Parts;

            [NativeDisableContainerSafetyRestriction] public NativeArray<CullingWorker> Workers;
            [NativeDisableUnsafePtrRestriction] public BatchCullingOutputDrawCommands* Output;

            public void Execute(int index)
            {
                DrawWorkItem work = WorkItems[index];
                ref CullingWorker worker = ref ((CullingWorker*)Workers.GetUnsafePtr())[work.Worker];
                WorkerDrawBin bin = worker.Bins[work.Bin];
                DrawBinOutput outputBin = Bins[bin.OutputBinIndex];
                int destination = outputBin.VisibleOffset + bin.WorkerOffset + work.LocalVisibleOffset;
                for (int m = work.FirstMask; m < work.FirstMask + work.MaskCount; m++)
                {
                    DrawMask mask = bin.Masks[m];
                    for (int word = 0; word < 2; word++)
                    {
                        ulong remaining = word == 0 ? mask.Visible.Lower : mask.Visible.Upper;
                        while (remaining != 0)
                        {
                            int row = math.tzcnt(remaining) + word * WORD_BITS;
                            remaining &= remaining - 1;
                            EmitInstance(bin.Key, outputBin, mask.ChunkIndex, row, destination++);
                        }
                    }
                }
            }

            private void EmitInstance(DrawBinKey key, DrawBinOutput outputBin, int chunkIndex, int row, int destination)
            {
                ArchetypeChunk chunk = Chunks[chunkIndex];
                int slot = chunk.GetNativeArray(ref SlotType)[row].Value;
                Entity entity = chunk.GetNativeArray(EntityType)[row];
                Output->visibleInstances[destination] = key.Fade == 0
                    ? slot
                    : ImpostorLodSelection.PackIndex(slot, Fades[chunkIndex * ChunkVisibilityMask.CAPACITY + row], key.Fade == 2);

                RenderLod level = Lods[key.Lod];
                int firstCommand = outputBin.CommandOffset + (destination - outputBin.VisibleOffset) * level.PartCount;
                for (int p = 0; p < level.PartCount; p++)
                {
                    RenderPart part = Parts[level.FirstPart + p];
                    Output->drawCommandPickingEntityIds[firstCommand + p] = entity;
                    Output->drawCommands[firstCommand + p] = new BatchDrawCommand
                    {
                        batchID = level.Batch,
                        meshID = part.Mesh,
                        materialID = key.Fade == 0 ? part.Material : part.FadeMaterial,
                        submeshIndex = part.Submesh,
                        visibleOffset = (uint)destination,
                        visibleCount = 1,
                        splitVisibilityMask = key.Splits,
                        flags = DrawFlags(key.Fade),
                        sortingPosition = 0,
                    };
                }
            }
        }
    }
}
#endif
