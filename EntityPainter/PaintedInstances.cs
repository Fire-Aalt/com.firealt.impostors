using Unity.Entities;

namespace EntityPainter
{
    /// <summary>Placement data of a spawner entity. A new Hash makes the spawn system respawn its instances.</summary>
    public struct PaintedInstances : IComponentData
    {
        public BlobAssetReference<PaintedInstanceBlob> Value;
        public UnityEngine.Hash128 Hash;
    }
}
