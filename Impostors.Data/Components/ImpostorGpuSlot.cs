using Unity.Entities;

namespace Impostors.Data
{
    public struct ImpostorGpuSlot : ICleanupComponentData
    {
        public int Value;
    }
}
