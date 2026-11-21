using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    /// <summary>
    /// Removes the generated meshes a saved generator prefab no longer uses, for example after its LOD hierarchy was
    /// deleted in Prefab Mode.
    /// </summary>
    internal sealed class MeshLodGeneratorPostprocessor : AssetPostprocessor
    {
        private const string PREFAB_EXTENSION = ".prefab";

        private static readonly HashSet<string> Pending = new();

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string path in imported)
            {
                if (path.EndsWith(PREFAB_EXTENSION, StringComparison.OrdinalIgnoreCase) && Pending.Add(path)
                    && Pending.Count == 1)
                {
                    // Assets must not be edited during an import, and a running operation may still be adding meshes.
                    EditorApplication.delayCall += Flush;
                }
            }
        }

        private static void Flush()
        {
            string[] paths = Pending.ToArray();
            Pending.Clear();
            foreach (string path in paths)
            {
                try
                {
                    MeshLodGeneratorAuthoring.RemoveUnusedMeshes(path);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, AssetDatabase.LoadMainAssetAtPath(path));
                }
            }
        }
    }
}
