using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace EntityPainter.Editor
{
    /// <summary>
    /// A hash grid of instance positions for the brush's minimum distance. Cells are as wide as the distance, so a
    /// neighbour check visits only the 27 cells around a point instead of every instance. Each cell keeps a linked list
    /// of indices, so adding a point allocates nothing beyond list growth.
    /// </summary>
    internal sealed class PaintedSpatialGrid
    {
        private readonly Dictionary<int3, int> _heads = new();
        private readonly List<float3> _positions = new();
        private readonly List<int> _next = new();
        private readonly float _inverseCellSize;

        private PaintedEntities _target;
        private byte[] _records;

        public PaintedSpatialGrid(float cellSize)
        {
            CellSize = cellSize;
            _inverseCellSize = 1f / cellSize;
        }

        public float CellSize { get; }

        public int Count => _positions.Count;

        /// <summary>
        /// Returns <paramref name="cached"/> while it still describes <paramref name="target"/>'s current records and
        /// distance, otherwise a grid rebuilt from every record of the target.
        /// </summary>
        public static PaintedSpatialGrid GetOrBuild(PaintedSpatialGrid cached, PaintedEntities target, float minDistance)
        {
            if (cached != null && cached._target == target && ReferenceEquals(cached._records, target.Records)
                && cached.CellSize == minDistance)
            {
                return cached;
            }

            var grid = new PaintedSpatialGrid(minDistance);
            foreach (PaintedRecord record in PaintedRecordsEditing.AsRecords(target.Records))
            {
                grid.Add(record.Position);
            }

            grid.Bind(target, target.Records);
            return grid;
        }

        /// <summary>Marks the grid as matching <paramref name="records"/>, after a commit added its new points.</summary>
        public void Bind(PaintedEntities target, byte[] records)
        {
            _target = target;
            _records = records;
        }

        /// <summary>Forces the next <see cref="GetOrBuild"/> to rebuild, after points were added but not committed.</summary>
        public void Invalidate()
        {
            _records = null;
        }

        public void Add(float3 position)
        {
            int3 cell = GetCell(position);
            _positions.Add(position);
            _next.Add(_heads.TryGetValue(cell, out int head) ? head : -1);
            _heads[cell] = _positions.Count - 1;
        }

        /// <summary>True when any point lies closer than <paramref name="distance"/>, which must not exceed the cell size.</summary>
        public bool HasNeighbour(float3 position, float distance)
        {
            if (distance > CellSize)
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }

            float distanceSquared = distance * distance;
            int3 center = GetCell(position);
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (!_heads.TryGetValue(center + new int3(x, y, z), out int index))
                        {
                            continue;
                        }

                        for (; index >= 0; index = _next[index])
                        {
                            if (math.distancesq(_positions[index], position) < distanceSquared)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private int3 GetCell(float3 position)
        {
            return (int3)math.floor(position * _inverseCellSize);
        }
    }
}
