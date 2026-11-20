using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Parallel culling publishes keys in scheduling order. Draw them nearest bucket first, every frame.</summary>
        [BurstCompile]
        private struct SortDrawKeysJob : IJob
        {
            public NativeList<DrawBinKey> Keys;

            public void Execute() => Keys.Sort(new NearestBucketFirst());

            private struct NearestBucketFirst : IComparer<DrawBinKey>
            {
                public int Compare(DrawBinKey x, DrawBinKey y)
                {
                    int order = x.Bucket.CompareTo(y.Bucket);
                    return order != 0 ? order : x.Value.CompareTo(y.Value);
                }
            }
        }
    }
}
