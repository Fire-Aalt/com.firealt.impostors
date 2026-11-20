using Unity.Entities;
using UnityEngine;

namespace Impostors.Tests.Authoring
{
    /// <summary>References an impostor prefab the way gameplay spawners do.</summary>
    public sealed class ImpostorBakingSpawner : MonoBehaviour
    {
        public GameObject Prefab;

        private sealed class Baker : Baker<ImpostorBakingSpawner>
        {
            public override void Bake(ImpostorBakingSpawner authoring)
            {
                AddComponent(GetEntity(TransformUsageFlags.None), new ImpostorBakingSpawnerData
                {
                    Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic),
                });
            }
        }
    }
}
