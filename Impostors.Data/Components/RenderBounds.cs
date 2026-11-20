using Unity.Entities;
using Unity.Mathematics;

namespace Impostors.Data
{
    /// <summary>World-space instance bounds, stored directly in an ECS component column.</summary>
    public struct RenderBounds : IComponentData
    {
        public float3 Center;
        public float3 Extents;
    }
}
