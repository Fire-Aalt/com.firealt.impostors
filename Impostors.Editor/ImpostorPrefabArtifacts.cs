using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Impostors.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Packages generated native data inside the prefab. Materials retain their original authored shader assets.</summary>
    public static class ImpostorPrefabArtifacts
    {
        private const string PREFAB_EXTENSION = ".prefab";
        private const string SCENE_EXTENSION = ".unity";
        private const string ASSET_EXTENSION = ".asset";
        private const string ASSETS_ROOT = "Assets/";
        private const string YAML_HEADER = "%YAML";
        private const string DEFINITION_NAME = "Impostor Definition";
        private const string ARTIFACTS_PATH = "BakeData.Artifacts";
        private const string NOT_EMBEDDED = "Rebake from the target prefab before deriving embedded self-shadow data.";
        private const string INVALID_STORE = "A target prefab, successful definition and bake metadata are required.";
        private const string MISSING_FRAME_RECTANGLES = "A complete bake must include its frame-rectangle lookup.";
        private const string SAVE_FAILED = "Could not save the embedded impostor prefab.";
        private const string UNREADABLE_TEXTURE = "Generated Texture2D data must be readable before prefab packaging: ";
        private const string MISSING_EMBEDDED = "An embedded impostor definition is required.";

        public static Texture3D StoreSelfShadow(ImpostorDefinition definition, Texture3D volume)
        {
            string path = AssetDatabase.GetAssetPath(definition);
            if (!path.EndsWith(PREFAB_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(NOT_EMBEDDED);
            }

            ImpostorPackageAssets.RequireWritableAssetPath(path, PREFAB_EXTENSION);
            Object[] previous = OwnedAssets(definition);
            Texture3D embedded = Object.Instantiate(volume);
            embedded.name = volume.name;
            AssetDatabase.AddObjectToAsset(embedded, definition);
            definition.SelfShadowVolume = embedded;
            definition.Material.SetTexture(ImpostorShaderProperties.SelfShadowVolume, embedded);
            definition.SelfShadowLayout.Configure(definition.Material);
            EditorUtility.SetDirty(definition);
            EditorUtility.SetDirty(definition.Material);
            AssetDatabase.SaveAssetIfDirty(definition);
            PruneUnused(definition, previous);
            return embedded;
        }

        public static void ReplaceReferences(ImpostorDefinition previous, ImpostorDefinition replacement)
        {
            if (!previous || !replacement || previous == replacement)
            {
                return;
            }

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(previous, out string oldGuid, out long oldId);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(replacement, out string newGuid, out long newId);
            string oldPath = AssetDatabase.GetAssetPath(previous);
            string[] candidates = AssetDatabase.GetAllAssetPaths()
                .Where(path => IsReferencingAsset(path, oldPath))
                .ToArray();
            // Keep live inspector/scene state aligned with the regenerated references as well as saved assets.
            ReplaceLiveReferences(previous, replacement);
            var pattern = new Regex(@"\{fileID:\s*" + oldId + @", guid:\s*" + oldGuid + @", type:\s*\d+\}");
            string reference = $"{{fileID: {newId}, guid: {newGuid}, type: 3}}";
            foreach (string path in candidates)
            {
                ReplaceSerializedReferences(path, pattern, reference);
            }
        }

        public static ImpostorDefinition Store(GameObject source, ImpostorDefinition baked)
        {
            string path = AssetDatabase.GetAssetPath(source);
            ImpostorPackageAssets.RequireWritableAssetPath(path, PREFAB_EXTENSION);
            if (!source || !baked || baked.BakeData == null || !path.EndsWith(PREFAB_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(INVALID_STORE);
            }

            if (!baked.FrameRectangles || baked.FrameRectangles.width != baked.Frames * baked.Frames)
            {
                throw new ArgumentException(MISSING_FRAME_RECTANGLES);
            }

            ImpostorDefinition current = source.GetComponent<Impostor>().Definition;
            ImpostorDefinition existing = current && AssetDatabase.GetAssetPath(current) == path ? current : null;
            // Capture the live references before CopySerialized replaces them. The ownership list alone
            // can be incomplete after an interrupted/older bake or a manual edit.
            Object[] oldOwned = existing ? OwnedAssets(existing) : Array.Empty<Object>();
            var packager = new Packager(source, path);
            ImpostorDefinition backup = existing ? Object.Instantiate(existing) : null;
            bool committed = false;
            try
            {
                var packaged = (ImpostorDefinition)packager.Clone(baked, existing);
                ImpostorDefinition result = existing ? existing : packaged;
                packaged.name = DEFINITION_NAME;
                if (existing)
                {
                    EditorUtility.CopySerialized(packaged, existing);
                }

                LinkPrefab(path, result, baked.BakeData.Settings);
                EditorUtility.SetDirty(result);
                AssetDatabase.SaveAssetIfDirty(source);
                committed = true;
                if (existing)
                {
                    Remove(packaged);
                }

                // Prune only our own obsolete embedded data, after the complete replacement has been saved.
                PruneUnused(result, oldOwned);
                return result;
            }
            catch
            {
                if (!committed)
                {
                    Rollback(existing, backup, packager);
                    AssetDatabase.SaveAssetIfDirty(source);
                }

                throw;
            }
            finally
            {
                if (backup)
                {
                    Object.DestroyImmediate(backup);
                }
            }
        }

        /// <summary>Remove obsolete bake-owned data, including untracked duplicates left by older bakes.</summary>
        public static int PruneUnused(ImpostorDefinition definition, Object[] previous = null)
        {
            string path = AssetDatabase.GetAssetPath(definition);
            if (!definition || definition.BakeData == null || !path.EndsWith(PREFAB_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(MISSING_EMBEDDED);
            }

            ImpostorPackageAssets.RequireWritableAssetPath(path, PREFAB_EXTENSION);
            Object[] owned = (previous ?? Array.Empty<Object>()).Concat(OwnedAssets(definition)).Distinct().ToArray();
            Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
            HashSet<Object> candidates = PruneCandidates(owned, all);
            HashSet<Object> generated = References(definition);
            var reachable = new HashSet<Object>(generated);
            // Preserve references held by the prefab hierarchy and unrelated user-authored subassets.
            VisitReferences(AssetDatabase.LoadMainAssetAtPath(path), path, reachable);
            foreach (Object value in all)
            {
                if (value && !candidates.Contains(value) && value != definition && value is not Texture and not Mesh)
                {
                    VisitReferences(value, path, reachable);
                }
            }

            Object[] obsolete = candidates
                .Where(value => value && AssetDatabase.GetAssetPath(value) == path && !reachable.Contains(value))
                .ToArray();
            definition.BakeData.Artifacts = generated.Where(IsArtifact).ToArray();
            EditorUtility.SetDirty(definition);
            foreach (Object value in obsolete)
            {
                Remove(value);
            }

            AssetDatabase.SaveAssetIfDirty(definition);
            return obsolete.Length;
        }

        private static bool IsReferencingAsset(string path, string oldPath)
        {
            bool serializedAsset = path.EndsWith(SCENE_EXTENSION) || path.EndsWith(PREFAB_EXTENSION)
                || path.EndsWith(ASSET_EXTENSION);
            return path.StartsWith(ASSETS_ROOT, StringComparison.Ordinal) && path != oldPath && serializedAsset
                && AssetDatabase.GetDependencies(path, false).Contains(oldPath);
        }

        private static void ReplaceLiveReferences(Object previous, Object replacement)
        {
            foreach (MonoBehaviour behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (!behaviour || !ReplaceObjectReferences(behaviour, previous, replacement))
                {
                    continue;
                }

                if (!EditorUtility.IsPersistent(behaviour) && behaviour.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
                }
            }
        }

        private static bool ReplaceObjectReferences(Object owner, Object previous, Object replacement)
        {
            using var serialized = new SerializedObject(owner);
            SerializedProperty property = serialized.GetIterator();
            bool changed = false;
            while (property.Next(true))
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue == previous)
                {
                    property.objectReferenceValue = replacement;
                    changed = true;
                }
            }

            if (changed)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            return changed;
        }

        /// <summary>Rewrites references in a saved asset file. A loaded scene is saved from its live state instead.</summary>
        private static void ReplaceSerializedReferences(string path, Regex pattern, string reference)
        {
            if (path.EndsWith(SCENE_EXTENSION))
            {
                Scene scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.SaveScene(scene);
                    return;
                }
            }

            string text = File.ReadAllText(path);
            if (!text.StartsWith(YAML_HEADER, StringComparison.Ordinal))
            {
                return;
            }

            string updated = pattern.Replace(text, reference);
            if (updated == text)
            {
                return;
            }

            File.WriteAllText(path, updated, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        /// <summary>Saves the component through PrefabUtility so Prefab Mode and project selection see the same link.</summary>
        private static void LinkPrefab(string path, ImpostorDefinition definition, ScriptableObject settings)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var authoring = contents.GetComponent<Impostor>();
                authoring.Definition = definition;
                authoring.BakeSettings = settings;
                PrefabUtility.SaveAsPrefabAsset(contents, path, out bool saved);
                if (!saved)
                {
                    throw new InvalidOperationException(SAVE_FAILED);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static void Rollback(ImpostorDefinition existing, ImpostorDefinition backup, Packager packager)
        {
            if (existing && backup)
            {
                EditorUtility.CopySerialized(backup, existing);
                EditorUtility.SetDirty(existing);
            }

            foreach (Object value in packager.Created)
            {
                if (value)
                {
                    Remove(value);
                }
            }
        }

        /// <summary>Owned artifacts plus orphaned copies recovered by their generated artifact roles, never by name alone.</summary>
        private static HashSet<Object> PruneCandidates(Object[] owned, Object[] all)
        {
            var roles = new HashSet<(Type, string)>(owned.Where(IsArtifact).Select(value => (value.GetType(), value.name)));
            var candidates = new HashSet<Object>(owned.Where(IsArtifact));
            foreach (Object value in all)
            {
                if (IsArtifact(value) && roles.Contains((value.GetType(), value.name)))
                {
                    candidates.Add(value);
                }
            }

            return candidates;
        }

        private static Object[] OwnedAssets(ImpostorDefinition definition)
        {
            return References(definition).Where(IsArtifact)
                .Concat(definition.BakeData?.Artifacts ?? Array.Empty<Object>())
                .Where(value => value)
                .Distinct()
                .ToArray();
        }

        private static HashSet<Object> References(ImpostorDefinition definition)
        {
            var result = new HashSet<Object>();
            VisitReferences(definition, AssetDatabase.GetAssetPath(definition), result);
            return result;
        }

        private static void VisitReferences(Object value, string path, HashSet<Object> result)
        {
            if (!value || AssetDatabase.GetAssetPath(value) != path || !result.Add(value) || value is Texture or Mesh)
            {
                return;
            }

            using var serialized = new SerializedObject(value);
            SerializedProperty property = serialized.GetIterator();
            bool enter = true;
            while (property.Next(enter))
            {
                // Bookkeeping must never make an otherwise unused artifact appear live.
                enter = value is not ImpostorDefinition
                    || (property.propertyPath != ARTIFACTS_PATH && property.propertyPath != nameof(ImpostorDefinition.SourcePrefab));
                if (enter && property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    VisitReferences(property.objectReferenceValue, path, result);
                }
            }
        }

        private static bool IsArtifact(Object value) => value && value is Material or Mesh or Texture;

        private static bool IsData(Object value) => value is ImpostorDefinition or Material or Mesh or Texture;

        private static void Remove(Object value)
        {
            AssetDatabase.RemoveObjectFromAsset(value);
            Object.DestroyImmediate(value, true);
        }

        /// <summary>Deep-copies transient bake output into the prefab, leaving persistent authoring assets referenced.</summary>
        private sealed class Packager
        {
            private readonly Dictionary<Object, Object> _map = new();
            private readonly GameObject _source;
            private readonly string _path;

            public Packager(GameObject source, string path)
            {
                _source = source;
                _path = path;
            }

            public List<Object> Created { get; } = new();

            /// <summary>
            /// Copies <paramref name="baked"/>. Data cloned afterwards that refers to it resolves to
            /// <paramref name="target"/>, the definition that will hold the copy's contents.
            /// </summary>
            public Object Clone(ImpostorDefinition baked, ImpostorDefinition target)
            {
                Object packaged = Clone((Object)baked);
                _map[baked] = target ? target : packaged;
                return packaged;
            }

            private Object Clone(Object value)
            {
                if (!value || !IsData(value))
                {
                    return value;
                }

                if (_map.TryGetValue(value, out Object mapped))
                {
                    return mapped;
                }

                // Authoring meshes/textures/materials remain source dependencies. Generated data is embedded.
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(value)))
                {
                    return value;
                }

                Object copy = Copy(value);
                _map[value] = copy;
                RemapReferences(copy);
                return copy;
            }

            private Object Copy(Object value)
            {
                if (value is Texture2D { isReadable: false })
                {
                    throw new InvalidOperationException(UNREADABLE_TEXTURE + value.name);
                }

                Object copy = Object.Instantiate(value);
                copy.name = value.name;
                copy.hideFlags = HideFlags.None;
                Created.Add(copy);
                return copy;
            }

            private void RemapReferences(Object copy)
            {
                if (copy is not Texture and not Mesh)
                {
                    using var serialized = new SerializedObject(copy);
                    SerializedProperty property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            property.objectReferenceValue = Clone(property.objectReferenceValue);
                        }
                    }

                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }

                AssetDatabase.AddObjectToAsset(copy, _source);
            }
        }
    }
}
