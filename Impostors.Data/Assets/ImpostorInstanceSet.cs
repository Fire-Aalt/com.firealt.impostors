using System;
using UnityEngine;

namespace Impostors.Data
{
    [CreateAssetMenu(menuName = MENU_NAME)]
    public sealed class ImpostorInstanceSet : ScriptableObject
    {
        private const string MENU_NAME = "Impostors/Instance Set";
        private const string MISSING_DEFINITION_HASH = "missing";

        public GameObject[] Prefabs = Array.Empty<GameObject>();

        [NonSerialized]
        public ImpostorDefinition[] Definitions = Array.Empty<ImpostorDefinition>();

        public ImpostorInstance[] Instances = Array.Empty<ImpostorInstance>();

        /// <summary>Returns the explicit definitions, or the definitions baked onto <see cref="Prefabs"/>.</summary>
        public ImpostorDefinition[] ResolveDefinitions()
        {
            return Definitions.Length > 0 ? Definitions : Array.ConvertAll(Prefabs, FindDefinition);
        }

        public string ComputeHash()
        {
            var hash = new Hash128();
            foreach (ImpostorDefinition definition in ResolveDefinitions())
            {
                hash.Append(definition ? definition.ContentHash : MISSING_DEFINITION_HASH);
            }

            foreach (ImpostorInstance instance in Instances)
            {
                hash.Append(instance.Prototype);
                hash.Append(instance.Position.x);
                hash.Append(instance.Position.y);
                hash.Append(instance.Position.z);
                hash.Append(instance.Yaw);
                hash.Append(instance.Scale);
            }

            return hash.ToString();
        }

        private static ImpostorDefinition FindDefinition(GameObject prefab)
        {
            return prefab && prefab.TryGetComponent(out Impostor impostor) ? impostor.Definition : null;
        }
    }
}
