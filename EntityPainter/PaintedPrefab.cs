using Unity.Entities;

namespace EntityPainter
{
    /// <summary>
    /// The baked entity prefab of one prototype, in prototype order. Record ranges live in the blob instead: the
    /// live-baking differ in Entities 6.6 compares only the first element of buffers that hold entity references, so
    /// edits to later elements would never reach the Editor world.
    /// </summary>
    public struct PaintedPrefab : IBufferElementData
    {
        public Entity Prefab;
    }
}
