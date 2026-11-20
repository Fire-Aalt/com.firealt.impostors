using Unity.Entities;

namespace Impostors.Data
{
    /// <summary>Chunk component with the aggregate culling and LOD state of the chunk's instances.</summary>
    public struct RenderChunk : IComponentData
    {
        public RenderBounds Bounds;
        public RenderBounds ReferenceBounds;
        public float MinimumHeight;
        public float MaximumHeight;
        public uint TransformVersion;
        public uint OrderVersion;
    }
}
