using Unity.Entities;

namespace EntityPainter
{
    /// <summary>The PaintedInstances.Hash that the current instances were spawned from.</summary>
    public struct PaintedSpawnerState : ICleanupComponentData
    {
        public UnityEngine.Hash128 Hash;
    }
}
