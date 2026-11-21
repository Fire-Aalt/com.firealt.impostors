using Unity.Entities;
using Unity.Mathematics;

namespace Impostors.Data
{
    public struct ImpostorSpatialCell : ISharedComponentData
    {
        public const float DEFAULT_TILE_SIZE = 128;
        public const float MINIMUM_TILE_SIZE = 8;

        /// <summary>
        /// X/Z cell edge in meters, set by the Impostors Renderer Feature. Renderers regroup every instance when it
        /// changes, so entities baked with another size are corrected on their first synchronization.
        /// </summary>
        public static float TileSize { get; set; } = DEFAULT_TILE_SIZE;

        public int2 Value;

        public static ImpostorSpatialCell FromPosition(float3 position)
        {
            return FromPosition(position, TileSize);
        }

        public static ImpostorSpatialCell FromPosition(float3 position, float tileSize)
        {
            return new ImpostorSpatialCell { Value = (int2)math.floor(position.xz / tileSize) };
        }
    }
}
