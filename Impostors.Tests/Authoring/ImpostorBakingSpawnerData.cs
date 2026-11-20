using Unity.Entities;

namespace Impostors.Tests.Authoring
{
    public struct ImpostorBakingSpawnerData : IComponentData
    {
        public Entity Prefab;
    }
}
