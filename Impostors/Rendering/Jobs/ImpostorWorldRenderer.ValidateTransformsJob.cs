using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Flags each chunk that holds a transform the renderer cannot draw.</summary>
        [BurstCompile]
        private struct ValidateTransformsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> TransformType;

            [WriteOnly] public NativeArray<byte> Invalid;

            public void Execute(int index)
            {
                NativeArray<LocalToWorld> transforms = Chunks[index].GetNativeArray(ref TransformType);
                for (int row = 0; row < transforms.Length; row++)
                {
                    if (!ImpostorTransformValidation.IsValid(transforms[row].Value))
                    {
                        Invalid[index] = 1;
                        return;
                    }
                }
            }
        }
    }
}
