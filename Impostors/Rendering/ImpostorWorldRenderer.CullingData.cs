using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Four culling planes in structure-of-arrays form, tested against one box at a time.</summary>
        private struct PlanePacket4
        {
            public float4 X;
            public float4 Y;
            public float4 Z;
            public float4 Distance;
            public float4 AbsX;
            public float4 AbsY;
            public float4 AbsZ;
        }

        private struct RenderSplit
        {
            public int FirstPacket;
            public int PacketCount;
        }

        /// <summary>Groups visible rows that share a LOD, depth bucket, split mask and fade role into one draw.</summary>
        private struct DrawBinKey : IEquatable<DrawBinKey>
        {
            private const int BUCKET_SHIFT = 32;
            private const int SPLITS_SHIFT = 35;
            private const int FADE_SHIFT = 51;

            public ulong Value;

            public readonly int Lod => (int)(Value & uint.MaxValue);
            public readonly int Bucket => (int)((Value >> BUCKET_SHIFT) & 7);
            public readonly ushort Splits => (ushort)(Value >> SPLITS_SHIFT);

            /// <summary>0 for an opaque draw, 1 for the high-detail and 2 for the low-detail side of a crossfade.</summary>
            public readonly int Fade => (int)((Value >> FADE_SHIFT) & 3);

            public static DrawBinKey Create(int lod, int bucket, ushort splits, int fade)
            {
                return new DrawBinKey
                {
                    Value = (uint)lod | ((ulong)(uint)bucket << BUCKET_SHIFT) | ((ulong)splits << SPLITS_SHIFT)
                        | ((ulong)(uint)fade << FADE_SHIFT),
                };
            }

            public readonly bool Equals(DrawBinKey other) => Value == other.Value;

            public readonly override int GetHashCode() => (int)math.hash(new uint2((uint)Value, (uint)(Value >> 32)));
        }

        private struct DrawMask
        {
            public ChunkVisibilityMask Visible;
            public int ChunkIndex;
        }

        /// <summary>A run of one worker bin's masks that a parallel expansion job writes as one contiguous range.</summary>
        private struct DrawWorkItem
        {
            public int Worker;
            public int Bin;
            public int FirstMask;
            public int MaskCount;
            public int LocalVisibleOffset;
        }

        private struct WorkerDrawBin
        {
            public DrawBinKey Key;
            public UnsafeList<DrawMask> Masks;
            public int Count;
            public int PublishedMasks;
            public int PublishedCount;
            public int WorkerOffset;
            public int OutputBinIndex;
        }

        /// <summary>Persistent per-partition culling state. Padded so partitions never share a cache line.</summary>
        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct CullingWorker
        {
            public UnsafeHashMap<DrawBinKey, int> BinLookup;
            public UnsafeList<WorkerDrawBin> Bins;
            public int Visible;
            public int RejectedChunks;
            public int InsideChunks;
            public int PartialChunks;
            public int UniformChunks;
            public int MixedChunks;
            public int FadingInstances;
            public int Growths;

            public void ResetCounters()
            {
                Visible = 0;
                RejectedChunks = 0;
                InsideChunks = 0;
                PartialChunks = 0;
                UniformChunks = 0;
                MixedChunks = 0;
                FadingInstances = 0;
                Growths = 0;
            }
        }

        private struct DrawBinOutput
        {
            public int Count;
            public int VisibleOffset;
            public int CommandOffset;
        }

        /// <summary>
        /// Where one view's culling jobs send indirect draws. The prefix job decides whether this frame's counts fit.
        /// </summary>
        private struct IndirectDrawTarget
        {
            public ThreadedSparseUploader VisibleUploader;
            public ThreadedSparseUploader ArgumentUploader;
            public GraphicsBufferHandle VisibleBuffer;
            public GraphicsBufferHandle ArgumentBuffer;
            public int VisibleCapacity;
            public int ArgumentCapacity;
            public int OperationCapacity;
            public bool Available;
            public int* VisibleOutput;
            public int VisibleCount;
            public int CommandCount;
            public bool Indirect;
        }
    }
}
