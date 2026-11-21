using System;
using UnityEngine;

namespace Impostors.Data
{
    /// <summary>Authoring link between a source prefab and its last successful bake.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Impostors/Impostor")]
    public sealed class Impostor : MonoBehaviour
    {
        [Tooltip("The last successfully baked data. Editing the prefab never starts a bake.")]
        public ImpostorDefinition Definition;

        [Tooltip("Baked entities cast realtime shadows. Applies to SubScene and GetEntity() baking.")]
        public bool RealtimeShadows;

#if UNITY_EDITOR
        // The settings type belongs to the editor assembly. Keep it out of player builds.
        [Tooltip("An Impostor Bake Settings asset used by the Bake / Rebake button.")]
        public ScriptableObject BakeSettings;

        [Tooltip("Capture-ready materials this prefab's renderers use in place of materials the bake can't capture. "
            + "Filled by Remap materials; remove an entry to go back to the source material.")]
        public MaterialRemap[] MaterialRemaps = Array.Empty<MaterialRemap>();
#endif
    }

#if UNITY_EDITOR
    [Serializable]
    public struct MaterialRemap
    {
        public Material Source;
        public Material Target;
    }
#endif
}
