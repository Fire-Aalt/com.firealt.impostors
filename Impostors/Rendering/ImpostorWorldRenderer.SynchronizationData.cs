using System;
using Impostors.Data;
using Unity.Entities;
using Unity.Mathematics;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>An entity whose position left its chunk's spatial cell. A null entity marks a row that stays.</summary>
        private struct CellMove : IComparable<CellMove>
        {
            public int2 Cell;
            public Entity Entity;

            public readonly int CompareTo(CellMove other)
            {
                int order = Cell.x.CompareTo(other.Cell.x);
                return order != 0 ? order : Cell.y.CompareTo(other.Cell.y);
            }
        }

        /// <summary>One changed chunk and the prototype data its GPU records are packed from.</summary>
        private struct ChunkUpload
        {
            public ArchetypeChunk Chunk;
            public int FirstRow;
            public int Prototype;
            public RenderBounds Bounds;
            public float3 ReferencePoint;
            public float Size;
        }

        private struct ChunkUploadResult
        {
            public RenderBounds Render;
            public RenderBounds References;
            public float MinimumHeight;
            public float MaximumHeight;
        }
    }
}
