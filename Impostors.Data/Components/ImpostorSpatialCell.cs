using Unity.Entities;
using Unity.Mathematics;

namespace Impostors.Data
{
    public struct ImpostorSpatialCell : ISharedComponentData
    {
        /// <summary>X/Z cell edge in meters. Bake and runtime regrouping share this value.</summary>
        public const float TILE_SIZE = 128;

        public int2 Value;

        public static ImpostorSpatialCell FromPosition(float3 position)
        {
            return new ImpostorSpatialCell { Value = (int2)math.floor(position.xz / TILE_SIZE) };
        }
    }
}
