using Unity.Entities;
using Unity.Mathematics;

namespace Impostors.Data
{
    public struct ImpostorLodMetrics : IComponentData
    {
        public float3 ReferencePoint;
        public float Height;
    }
}
