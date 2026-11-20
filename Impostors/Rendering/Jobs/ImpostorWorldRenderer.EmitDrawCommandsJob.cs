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
        /// <summary>Writes one direct or indirect draw command per part of each draw bin's LOD.</summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct EmitDrawCommandsJob : IJobParallelForDefer
        {
            private static readonly ProfilerMarker Marker = new("Impostors.EmitCommands");

            [ReadOnly] public NativeArray<DrawBinKey> Keys;
            [ReadOnly] public NativeArray<DrawBinOutput> Bins;
            [ReadOnly] public NativeArray<RenderLod> Lods;
            [ReadOnly] public NativeArray<RenderPart> Parts;

            [NativeDisableUnsafePtrRestriction] public BatchCullingOutputDrawCommands* Output;
            [NativeDisableUnsafePtrRestriction] public IndirectDrawTarget* Target;

            public void Execute(int index)
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                DrawBinKey key = Keys[index];
                DrawBinOutput bin = Bins[index];
                RenderLod level = Lods[key.Lod];
                BatchDrawCommandFlags flags = DrawFlags(key.Fade);
                for (int p = 0; p < level.PartCount; p++)
                {
                    RenderPart part = Parts[level.FirstPart + p];
                    int command = bin.CommandOffset + p;
                    BatchMaterialID material = key.Fade == 0 ? part.Material : part.FadeMaterial;
                    if (Target->Indirect)
                    {
                        EmitIndirect(command, key, bin, level, part, material, flags);
                        continue;
                    }

                    Output->drawCommands[command] = new BatchDrawCommand
                    {
                        batchID = level.Batch,
                        meshID = part.Mesh,
                        materialID = material,
                        submeshIndex = part.Submesh,
                        visibleOffset = (uint)bin.VisibleOffset,
                        visibleCount = (uint)bin.Count,
                        splitVisibilityMask = key.Splits,
                        flags = flags,
                        sortingPosition = 0,
                    };
                }
            }

            private void EmitIndirect(int command, DrawBinKey key, DrawBinOutput bin, RenderLod level, RenderPart part,
                BatchMaterialID material, BatchDrawCommandFlags flags)
            {
                int argumentOffset = command * GraphicsBuffer.IndirectDrawIndexedArgs.size;
                Target->ArgumentUploader.AddUpload(new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = part.IndexCount,
                    instanceCount = (uint)bin.Count,
                    startIndex = part.IndexStart,
                    baseVertexIndex = part.BaseVertex,
                    startInstance = 0,
                }, argumentOffset);
                Output->indirectDrawCommands[command] = new BatchDrawCommandIndirect
                {
                    batchID = level.Batch,
                    meshID = part.Mesh,
                    materialID = material,
                    topology = part.Topology,
                    visibleOffset = (uint)bin.VisibleOffset,
                    splitVisibilityMask = key.Splits,
                    flags = flags,
                    sortingPosition = 0,
                    visibleInstancesBufferHandle = Target->VisibleBuffer,
                    indirectArgsBufferHandle = Target->ArgumentBuffer,
                    indirectArgsBufferOffset = (uint)argumentOffset,
                };
            }
        }

        private static BatchDrawCommandFlags DrawFlags(int fade)
        {
            return fade == 0
                ? BatchDrawCommandFlags.None
                : BatchDrawCommandFlags.LODCrossFadeKeyword | BatchDrawCommandFlags.LODCrossFadeValuePacked;
        }
    }
}
