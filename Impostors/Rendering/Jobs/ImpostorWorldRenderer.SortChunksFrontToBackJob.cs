using System;
using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>
        /// Orders a camera view's chunks nearest first. Later stages preserve this order within each draw bin,
        /// so early depth rejection no longer depends on the direction the population was authored in.
        /// </summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct SortChunksFrontToBackJob : IJob
        {
            private static readonly ProfilerMarker Marker = new("Impostors.SortChunks");

            [ReadOnly] public ComponentTypeHandle<RenderChunk> ChunkType;

            public NativeArray<ArchetypeChunk> Chunks;
            public float3 Origin;

            public void Execute()
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                var entries = new NativeArray<Entry>(Chunks.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < Chunks.Length; i++)
                {
                    float3 center = Chunks[i].GetChunkComponentData(ref ChunkType).Bounds.Center;
                    entries[i] = new Entry { DistanceSquared = math.distancesq(center, Origin), Chunk = i };
                }

                entries.Sort();
                var sorted = new NativeArray<ArchetypeChunk>(Chunks.Length, Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < Chunks.Length; i++)
                {
                    sorted[i] = Chunks[entries[i].Chunk];
                }

                Chunks.CopyFrom(sorted);
            }

            private struct Entry : IComparable<Entry>
            {
                public float DistanceSquared;
                public int Chunk;

                public readonly int CompareTo(Entry other)
                {
                    int order = DistanceSquared.CompareTo(other.DistanceSquared);
                    return order != 0 ? order : Chunk.CompareTo(other.Chunk);
                }
            }
        }
    }
}
