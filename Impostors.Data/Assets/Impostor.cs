using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors.Data
{
    /// <summary>Authoring link between a source prefab and its last successful bake.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Impostors/Impostor")]
    public sealed class Impostor : MonoBehaviour
    {
        [Tooltip("The last successfully baked data. Editing the prefab never starts a bake.")]
        public ImpostorDefinition Definition;

        /// <summary>
        /// Whether baked entities cast realtime shadows: only when every renderer of every LOD casts shadows.
        /// </summary>
        public bool RealtimeShadows => CastsRealtimeShadows(GetComponentInChildren<LODGroup>(true),
            GetComponentsInChildren<Renderer>(true));

        /// <summary>
        /// True when every renderer of every level of <paramref name="group"/> casts shadows. Without a LODGroup,
        /// <paramref name="renderers"/> decide. Nothing to render casts no shadows.
        /// </summary>
        public static bool CastsRealtimeShadows(LODGroup group, IReadOnlyList<Renderer> renderers)
        {
            bool any = false;
            if (group)
            {
                foreach (LOD lod in group.GetLODs())
                {
                    foreach (Renderer renderer in lod.renderers)
                    {
                        if (renderer && !CastsShadows(renderer, ref any))
                        {
                            return false;
                        }
                    }
                }

                return any;
            }

            foreach (Renderer renderer in renderers)
            {
                if (renderer && !CastsShadows(renderer, ref any))
                {
                    return false;
                }
            }

            return any;
        }

        private static bool CastsShadows(Renderer renderer, ref bool any)
        {
            any = true;
            return renderer.shadowCastingMode != ShadowCastingMode.Off;
        }

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
