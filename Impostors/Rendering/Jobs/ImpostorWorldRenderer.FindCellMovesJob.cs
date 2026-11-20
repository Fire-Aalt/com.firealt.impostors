using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Finds entities whose position left their chunk's spatial cell.</summary>
        [BurstCompile]
        private struct FindCellMovesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly] public NativeArray<int2> CurrentCells;
            [ReadOnly] public NativeArray<int> FirstRows;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> TransformType;
            [ReadOnly] public EntityTypeHandle EntityType;

            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<CellMove> Moves;

            public void Execute(int index)
            {
                NativeArray<LocalToWorld> transforms = Chunks[index].GetNativeArray(ref TransformType);
                NativeArray<Entity> entities = Chunks[index].GetNativeArray(EntityType);
                for (int row = 0; row < transforms.Length; row++)
                {
                    int2 cell = ImpostorSpatialCell.FromPosition(transforms[row].Value.c3.xyz).Value;
                    if (!cell.Equals(CurrentCells[index]))
                    {
                        Moves[FirstRows[index] + row] = new CellMove { Cell = cell, Entity = entities[row] };
                    }
                }
            }
        }
    }
}
