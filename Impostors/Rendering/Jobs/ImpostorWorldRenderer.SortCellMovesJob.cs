using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>Groups moves by destination so each cell becomes one bulk structural change.</summary>
        [BurstCompile]
        private struct SortCellMovesJob : IJob
        {
            [ReadOnly] public NativeArray<CellMove> Moves;

            public NativeList<CellMove> Sorted;
            public NativeList<Entity> Entities;

            public void Execute()
            {
                foreach (CellMove move in Moves)
                {
                    if (move.Entity != Entity.Null)
                    {
                        Sorted.Add(move);
                    }
                }

                Sorted.Sort();
                foreach (CellMove move in Sorted)
                {
                    Entities.Add(move.Entity);
                }
            }
        }
    }
}
