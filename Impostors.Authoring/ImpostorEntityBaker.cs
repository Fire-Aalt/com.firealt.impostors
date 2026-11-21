using System.Collections.Generic;
using Impostors.Data;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Impostors.Authoring
{
    /// <summary>
    /// Bakes an <see cref="Impostor"/> prefab root, placed in a SubScene or reached through GetEntity(), into one entity.
    /// </summary>
    internal sealed class ImpostorEntityBaker : Baker<Impostor>
    {
        private const string MISSING_DEFINITION =
            "Impostor '{0}' has no valid baked definition. Its entity is baked without render data.";

        public override void Bake(Impostor authoring)
        {
            // The override wins over every other request, including the Dynamic usage Unity adds to prefab roots,
            // so transform baking never adds LocalTransform, Parent or PostTransformMatrix.
            Entity entity = GetEntity(TransformUsageFlags.ManualOverride);
            AddDescendants(entity, authoring.transform);

            // Register the dependency before reading the definition, including when it is missing.
            ImpostorDefinition definition = authoring.Definition;
            DependsOn(definition);

            // LOD data is validated when a renderer adopts the entity. Import workers may lack the shader support it checks.
            if (!definition || !definition.IsValid)
            {
                Debug.LogError(string.Format(MISSING_DEFINITION, authoring.name), authoring);
                return;
            }

            AddComponent(entity, new LocalToWorld { Value = float4x4.identity });
            AddComponent<RenderBounds>(entity);
            AddComponent<ImpostorLodMetrics>(entity);
            AddSharedComponent(entity, default(ImpostorSpatialCell));
            AddSharedComponent(entity, new ImpostorBakedPrototype
            {
                Definition = definition,
                Layer = GetLayer(),
                RealtimeShadows = CastsRealtimeShadows(),
            });
        }

        /// <summary>Reads the LODs through the baker so changing a renderer's shadow mode rebakes the entity.</summary>
        private bool CastsRealtimeShadows()
        {
            return Impostor.CastsRealtimeShadows(GetComponentInChildren<LODGroup>(), GetComponentsInChildren<Renderer>());
        }

        /// <summary>Lists every child entity so the strip systems can discard the authoring hierarchy.</summary>
        private void AddDescendants(Entity entity, Transform root)
        {
            var descendants = new List<Entity>();
            foreach (Transform child in GetComponentsInChildren<Transform>())
            {
                if (child != root)
                {
                    descendants.Add(GetEntity(child, TransformUsageFlags.None));
                }
            }

            DynamicBuffer<ImpostorBakedDescendant> buffer = AddBuffer<ImpostorBakedDescendant>(entity);
            foreach (Entity descendant in descendants)
            {
                buffer.Add(new ImpostorBakedDescendant { Value = descendant });
            }
        }
    }
}
