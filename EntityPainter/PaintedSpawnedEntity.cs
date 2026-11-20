using Unity.Entities;

namespace EntityPainter
{
    /// <summary>
    /// The root entity spawned for each record, in record order. Entity.Null marks records whose prefab is missing.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PaintedSpawnedEntity : ICleanupBufferElementData
    {
        public Entity Value;
    }
}
