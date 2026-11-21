using System;
using UnityEngine;

namespace Impostors.Data
{
    /// <summary>Authoring settings that generate the prefab's mesh LODs and LODGroup from one source model.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Impostors/Mesh LOD Generator")]
    public sealed class MeshLodGenerator : MonoBehaviour
    {
#if UNITY_EDITOR
        // Authoring data belongs to the Editor. Keep the source model out of player builds.
        public const int MAX_LODS = 8;

        [Tooltip("Model or prefab whose mesh renderers become LOD0. If it has a LODGroup, its LOD0 renderers are used.")]
        public GameObject SourceModel;

        [Range(1, MAX_LODS)]
        [Tooltip("Number of mesh LODs, LOD0 included. The impostor follows the last one.")]
        public int LodCount = 3;

        [Range(0, 1)]
        [Tooltip("Fraction of triangles each LOD removes from the previous one. Unity's simplifier snaps every LOD to "
            + "its nearest generated level.")]
        public float Reduction = .5f;

        [Tooltip("Optional per-LOD triangle targets, indexed by LOD. LOD0 always keeps the source meshes.")]
        public MeshLodOverride[] Overrides = Array.Empty<MeshLodOverride>();

        [HideInInspector]
        public string GeneratedFingerprint;

        [HideInInspector]
        public GameObject GeneratedRoot;
#endif
    }

#if UNITY_EDITOR
    [Serializable]
    public struct MeshLodOverride
    {
        public bool Enabled;

        [Range(0, 1)]
        [Tooltip("Fraction of LOD0's triangles this LOD keeps.")]
        public float Keep;
    }
#endif
}
