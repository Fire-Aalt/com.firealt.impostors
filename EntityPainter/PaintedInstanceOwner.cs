using Unity.Entities;

namespace EntityPainter
{
    /// <summary>
    /// Groups the instances and runtime templates of one spawner so they can be destroyed a chunk at a time.
    /// Entity.Null marks temporary editor previews.
    /// </summary>
    public struct PaintedInstanceOwner : ISharedComponentData
    {
        public Entity Spawner;
    }
}
