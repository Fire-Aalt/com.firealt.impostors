using Unity.Entities;
using UnityEngine;

namespace Impostors.Tests.Authoring
{
    /// <summary>Stands in for third-party bakers: adds its own component and asks for a dynamic transform.</summary>
    public sealed class ImpostorBakingProbe : MonoBehaviour
    {
        private sealed class Baker : Baker<ImpostorBakingProbe>
        {
            public override void Bake(ImpostorBakingProbe authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.NonUniformScale);
                AddComponent(entity, new ImpostorBakingJunk { Value = 1 });
            }
        }
    }
}
