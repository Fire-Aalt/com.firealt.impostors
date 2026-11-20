using System;
using UnityEngine;

namespace Impostors.Editor
{
    public static partial class ImpostorBaker
    {
        private const string MISSING_SOURCE = "A source prefab with a LODGroup is required.";
        private const string INVALID_LOD_GROUP = "The source prefab must contain exactly one nonempty LODGroup.";
        private const string MISSING_SOURCE_LOD = "The selected capture LOD does not exist in the source LODGroup.";
        private const string EMPTY_SOURCE_LOD = "The selected source LOD must contain static mesh renderers.";
        private const string INVALID_SOURCE_RENDERER = "The selected LOD requires static MeshRenderers under the source "
            + "prefab, with meshes and one valid material per submesh.";

        public static Renderer[] GetCaptureRenderers(GameObject source, int sourceLod = 0)
        {
            if (!source)
            {
                throw new ArgumentException(MISSING_SOURCE);
            }

            LODGroup[] groups = source.GetComponentsInChildren<LODGroup>(true);
            if (groups.Length != 1 || groups[0].lodCount == 0)
            {
                throw new ArgumentException(INVALID_LOD_GROUP);
            }

            if (sourceLod < 0 || sourceLod >= groups[0].lodCount)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceLod), MISSING_SOURCE_LOD);
            }

            Renderer[] renderers = groups[0].GetLODs()[sourceLod].renderers;
            if (renderers == null || renderers.Length == 0)
            {
                throw new ArgumentException(EMPTY_SOURCE_LOD);
            }

            foreach (Renderer renderer in renderers)
            {
                if (!IsCapturable(renderer, source.transform))
                {
                    throw new ArgumentException(INVALID_SOURCE_RENDERER);
                }
            }

            return renderers;
        }

        private static bool IsCapturable(Renderer renderer, Transform root)
        {
            return renderer is MeshRenderer && renderer.transform.IsChildOf(root)
                && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh
                && renderer.sharedMaterials.Length == filter.sharedMesh.subMeshCount
                && !Array.Exists(renderer.sharedMaterials, material => !material);
        }
    }
}
