using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Computes derived per-instance data and packs GPU records for every changed chunk.</summary>
        [BurstCompile]
        private struct PackChunkUploadsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ChunkUpload> Work;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> TransformType;
            [ReadOnly] public EntityTypeHandle EntityType;

            [NativeDisableParallelForRestriction] public ComponentTypeHandle<RenderBounds> BoundsType;
            [NativeDisableParallelForRestriction] public ComponentTypeHandle<ImpostorLodMetrics> MetricsType;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<uint> Matrices;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<uint> Inverses;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<uint> EntityWords;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<ChunkUploadResult> Results;

            public void Execute(int index)
            {
                ChunkUpload work = Work[index];
                NativeArray<LocalToWorld> transforms = work.Chunk.GetNativeArray(ref TransformType);
                NativeArray<Entity> entities = work.Chunk.GetNativeArray(EntityType);
                NativeArray<RenderBounds> bounds = work.Chunk.GetNativeArray(ref BoundsType);
                NativeArray<ImpostorLodMetrics> metrics = work.Chunk.GetNativeArray(ref MetricsType);
                var aggregate = new ChunkAggregate();
                aggregate.Reset();
                var matrices = (uint*)Matrices.GetUnsafePtr();
                var inverses = (uint*)Inverses.GetUnsafePtr();
                var ids = (uint*)EntityWords.GetUnsafePtr();
                for (int row = 0; row < transforms.Length; row++)
                {
                    float4x4 matrix = transforms[row].Value;
                    RenderBounds instanceBounds = TransformBounds(matrix, work.Bounds);
                    float3 reference = math.transform(matrix, work.ReferencePoint);
                    float height = work.Size * math.length(matrix.c0.xyz);
                    bounds[row] = instanceBounds;
                    metrics[row] = new ImpostorLodMetrics { ReferencePoint = reference, Height = height };
                    aggregate.Add(instanceBounds, reference, height);

                    int flat = work.FirstRow + row;
                    WritePacked(matrices + flat * PACKED_MATRIX_WORDS, matrix);
                    WritePacked(inverses + flat * PACKED_MATRIX_WORDS, math.inverse(matrix));
                    // Native selection loads the ECS entity handle with an 8-byte stride.
                    ids[flat * ENTITY_ID_WORDS] = (uint)entities[row].Index;
                    ids[flat * ENTITY_ID_WORDS + 1] = (uint)entities[row].Version;
                }

                Results[index] = aggregate.ToResult();
            }

            private static RenderBounds TransformBounds(float4x4 matrix, RenderBounds local)
            {
                float3 extents = math.abs(matrix.c0.xyz * local.Extents.x) + math.abs(matrix.c1.xyz * local.Extents.y)
                    + math.abs(matrix.c2.xyz * local.Extents.z);
                return new RenderBounds { Center = math.transform(matrix, local.Center), Extents = extents };
            }

            private static void WritePacked(uint* destination, in float4x4 matrix)
            {
                var columns = (float3*)destination;
                columns[0] = matrix.c0.xyz;
                columns[1] = matrix.c1.xyz;
                columns[2] = matrix.c2.xyz;
                columns[3] = matrix.c3.xyz;
            }

            private struct ChunkAggregate
            {
                private float3 _renderMin;
                private float3 _renderMax;
                private float3 _referenceMin;
                private float3 _referenceMax;
                private float _minimumHeight;
                private float _maximumHeight;

                public void Reset()
                {
                    _renderMin = float.PositiveInfinity;
                    _renderMax = float.NegativeInfinity;
                    _referenceMin = float.PositiveInfinity;
                    _referenceMax = float.NegativeInfinity;
                    _minimumHeight = float.PositiveInfinity;
                    _maximumHeight = 0;
                }

                public void Add(RenderBounds bounds, float3 reference, float height)
                {
                    _renderMin = math.min(_renderMin, bounds.Center - bounds.Extents);
                    _renderMax = math.max(_renderMax, bounds.Center + bounds.Extents);
                    _referenceMin = math.min(_referenceMin, reference);
                    _referenceMax = math.max(_referenceMax, reference);
                    _minimumHeight = math.min(_minimumHeight, height);
                    _maximumHeight = math.max(_maximumHeight, height);
                }

                public readonly ChunkUploadResult ToResult()
                {
                    return new ChunkUploadResult
                    {
                        Render = FromRange(_renderMin, _renderMax),
                        References = FromRange(_referenceMin, _referenceMax),
                        MinimumHeight = _minimumHeight,
                        MaximumHeight = _maximumHeight,
                    };
                }

                private static RenderBounds FromRange(float3 minimum, float3 maximum)
                {
                    return new RenderBounds { Center = (minimum + maximum) * .5f, Extents = (maximum - minimum) * .5f };
                }
            }
        }
    }
}
