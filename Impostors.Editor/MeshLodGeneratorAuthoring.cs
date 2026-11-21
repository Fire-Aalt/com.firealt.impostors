using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Impostors.Data;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Generates a prefab's mesh LODs and LODGroup from its <see cref="MeshLodGenerator"/> source model.</summary>
    public static class MeshLodGeneratorAuthoring
    {
        public const float DEFAULT_FIRST_TRANSITION = .6f;
        public const float DEFAULT_IMPOSTOR_TRANSITION = .1f;

        private const string FORMAT_VERSION = "Mesh LOD generator v1|";
        private const string ROOT_NAME = "Generated LODs";
        private const string LOD_NAME = "LOD";
        private const string MESH_NAME = "{0} LOD{1}";
        private const string PREFAB_NAME = "{0}/{1} Impostor.prefab";
        private const string SETTINGS_NAME = "/Impostor Bake Settings.asset";
        private const string CREATE_MENU = "Assets/Create/Impostors/Impostor Prefab from Model";
        private const int CREATE_MENU_PRIORITY = 300;
        private const string MISSING_GENERATOR = "The source prefab needs a Mesh LOD Generator component.";
        private const string MISSING_MODEL = "Assign a source model to the Mesh LOD Generator.";
        private const string EMPTY_MODEL = "The source model has no mesh renderers: ";
        private const string UNSUPPORTED_RENDERER =
            "Mesh LODs need static MeshRenderers with a mesh and one material per submesh. Unsupported renderer: ";
        private const string UNSUPPORTED_TOPOLOGY = "Mesh LODs need triangle topology. Disable Keep Quads on mesh: ";
        private const string SAVE_FAILED = "Could not save the generated mesh LODs into the prefab.";

        /// <summary>Renderers of <paramref name="model"/> that become LOD0: its LODGroup's LOD0, or all of them.</summary>
        public static MeshRenderer[] SourceRenderers(GameObject model)
        {
            if (!model)
            {
                throw new ArgumentException(MISSING_MODEL);
            }

            LODGroup group = model.GetComponentInChildren<LODGroup>(true);
            Renderer[] renderers = group && group.lodCount > 0
                ? group.GetLODs()[0].renderers
                : model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                throw new ArgumentException(EMPTY_MODEL + model.name);
            }

            return renderers.Select(renderer => RequireSupported(renderer, model.transform)).ToArray();
        }

        public static bool IsOutdated(MeshLodGenerator generator)
        {
            return !generator.GeneratedRoot || generator.GeneratedFingerprint != Fingerprint(generator);
        }

        /// <summary>Hash of every generation input. The model's dependency hash changes when it is reimported.</summary>
        public static string Fingerprint(MeshLodGenerator generator)
        {
            if (!generator.SourceModel)
            {
                return string.Empty;
            }

            var text = new StringBuilder(FORMAT_VERSION);
            text.Append(GlobalObjectId.GetGlobalObjectIdSlow(generator.SourceModel)).Append('|');
            text.Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(generator.SourceModel)));
            foreach (float ratio in TargetRatios(generator))
            {
                text.Append('|').Append(ratio.ToString("R", CultureInfo.InvariantCulture));
            }

            return Hash128.Compute(text.ToString()).ToString();
        }

        public static float[] TargetRatios(MeshLodGenerator generator)
        {
            return MeshLodGeneration.TargetRatios(generator.LodCount, generator.Reduction, generator.Overrides);
        }

        /// <summary>
        /// Rebuilds the generated LOD hierarchy and LODGroup of a saved source prefab. Existing generated meshes are
        /// overwritten in place, so a previous impostor bake keeps rendering until it is rebaked.
        /// </summary>
        public static void Generate(GameObject source)
        {
            string path = ImpostorAuthoring.RequireWritableSource(source);
            if (!source.TryGetComponent(out MeshLodGenerator generator))
            {
                throw new ArgumentException(MISSING_GENERATOR);
            }

            // The hierarchy is rebuilt from the saved prefab, so inspector edits must reach the file first.
            AssetDatabase.SaveAssetIfDirty(source);
            MeshRenderer[] renderers = SourceRenderers(generator.SourceModel);
            Mesh[] meshes = renderers.Select(SharedMesh).Distinct().ToArray();
            float[] ratios = TargetRatios(generator);
            Dictionary<string, Mesh> previous = GeneratedMeshes(generator.GeneratedRoot, path);
            List<Dictionary<Mesh, Mesh>> levels = BuildLevelMeshes(meshes, ratios, out int[] lodTriangles);
            var added = new List<Mesh>();
            try
            {
                StoreMeshes(levels, source, previous, added);
                SaveHierarchy(path, renderers, levels, lodTriangles);
            }
            catch
            {
                added.ForEach(RemoveMesh);
                throw;
            }

            RemoveUnused(previous.Values, AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }

        /// <summary>Creates "{Model} Impostor.prefab" next to <paramref name="model"/> and generates its LODs.</summary>
        public static GameObject CreatePrefabFromModel(GameObject model)
        {
            SourceRenderers(model);
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(model))?.Replace('\\', '/');
            folder = ImpostorPackageAssets.RequireWritableAssetPath(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath(string.Format(PREFAB_NAME, folder, model.name));
            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            GameObject prefab;
            try
            {
                root.AddComponent<Impostor>().BakeSettings = BakeSettingsIn(folder);
                root.AddComponent<MeshLodGenerator>().SourceModel = model;
                prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            Generate(prefab);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        [MenuItem(CREATE_MENU, true)]
        private static bool CanCreateFromSelection()
        {
            return Selection.gameObjects.Length > 0
                && Selection.gameObjects.All(model => EditorUtility.IsPersistent(model) && !model.transform.parent);
        }

        [MenuItem(CREATE_MENU, false, CREATE_MENU_PRIORITY)]
        private static void CreateFromSelection()
        {
            var created = new List<Object>();
            foreach (GameObject model in Selection.gameObjects)
            {
                try
                {
                    created.Add(CreatePrefabFromModel(model));
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, model);
                }
            }

            if (created.Count > 0)
            {
                Selection.objects = created.ToArray();
                EditorGUIUtility.PingObject(created[^1]);
            }
        }

        private static MeshRenderer RequireSupported(Renderer renderer, Transform root)
        {
            if (renderer is not MeshRenderer meshRenderer || !renderer.transform.IsChildOf(root)
                || !renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh
                || renderer.sharedMaterials.Length != filter.sharedMesh.subMeshCount
                || Array.Exists(renderer.sharedMaterials, material => !material))
            {
                throw new ArgumentException(UNSUPPORTED_RENDERER + (renderer ? renderer.name : "missing"));
            }

            Mesh mesh = filter.sharedMesh;
            if (Enumerable.Range(0, mesh.subMeshCount).Any(submesh => mesh.GetTopology(submesh) != MeshTopology.Triangles))
            {
                throw new NotSupportedException(UNSUPPORTED_TOPOLOGY + mesh.name);
            }

            return meshRenderer;
        }

        private static Mesh SharedMesh(Renderer renderer) => renderer.GetComponent<MeshFilter>().sharedMesh;

        /// <summary>Temporary meshes per LOD, keyed by source mesh. LOD0 maps every mesh to itself.</summary>
        private static List<Dictionary<Mesh, Mesh>> BuildLevelMeshes(Mesh[] meshes, float[] ratios, out int[] lodTriangles)
        {
            var levels = ratios.Select(_ => new Dictionary<Mesh, Mesh>()).ToList();
            var triangles = new List<int[]>();
            foreach (Mesh mesh in meshes)
            {
                Mesh chain = MeshLodGeneration.GenerateChain(mesh);
                try
                {
                    int[] counts = MeshLodGeneration.LevelTriangleCounts(chain);
                    triangles.Add(counts);
                    levels[0][mesh] = mesh;
                    for (int lod = 1; lod < ratios.Length; lod++)
                    {
                        levels[lod][mesh] = MeshLodGeneration.ExtractLevel(chain, MeshLodGeneration.PickLevel(counts, ratios[lod]));
                    }
                }
                finally
                {
                    Object.DestroyImmediate(chain);
                }
            }

            lodTriangles = MeshLodGeneration.LodTriangles(triangles, ratios);
            return levels;
        }

        /// <summary>Generated meshes the current hierarchy uses, keyed by name.</summary>
        private static Dictionary<string, Mesh> GeneratedMeshes(GameObject root, string path)
        {
            if (!root)
            {
                return new Dictionary<string, Mesh>();
            }

            return root.GetComponentsInChildren<MeshFilter>(true)
                .Select(filter => filter.sharedMesh)
                .Where(mesh => mesh && AssetDatabase.GetAssetPath(mesh) == path)
                .GroupBy(mesh => mesh.name)
                .ToDictionary(group => group.Key, group => group.First());
        }

        /// <summary>Embeds the temporary LOD meshes. A mesh that already exists keeps its identity and takes the new data.</summary>
        private static void StoreMeshes(List<Dictionary<Mesh, Mesh>> levels, GameObject source,
            Dictionary<string, Mesh> previous, List<Mesh> added)
        {
            Dictionary<Mesh, string> names = UniqueNames(levels[0].Keys);
            for (int lod = 1; lod < levels.Count; lod++)
            {
                foreach (Mesh mesh in levels[lod].Keys.ToArray())
                {
                    Mesh generated = levels[lod][mesh];
                    string name = string.Format(MESH_NAME, names[mesh], lod);
                    if (previous.Remove(name, out Mesh existing))
                    {
                        EditorUtility.CopySerialized(generated, existing);
                        Object.DestroyImmediate(generated);
                        generated = existing;
                    }
                    else
                    {
                        AssetDatabase.AddObjectToAsset(generated, source);
                        added.Add(generated);
                    }

                    generated.name = name;
                    levels[lod][mesh] = generated;
                }
            }
        }

        private static Dictionary<Mesh, string> UniqueNames(IEnumerable<Mesh> meshes)
        {
            var names = new Dictionary<Mesh, string>();
            foreach (IGrouping<string, Mesh> group in meshes.GroupBy(mesh => mesh.name))
            {
                Mesh[] same = group.ToArray();
                for (int i = 0; i < same.Length; i++)
                {
                    names[same[i]] = same.Length == 1 ? group.Key : group.Key + " " + i;
                }
            }

            return names;
        }

        private static void SaveHierarchy(string path, MeshRenderer[] renderers, List<Dictionary<Mesh, Mesh>> levels,
            int[] lodTriangles)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var generator = contents.GetComponent<MeshLodGenerator>();
                if (generator.GeneratedRoot)
                {
                    Object.DestroyImmediate(generator.GeneratedRoot);
                }

                var root = new GameObject(ROOT_NAME);
                root.transform.SetParent(contents.transform, false);
                Renderer[][] lods = levels
                    .Select((meshes, lod) => BuildLodCopy(generator.SourceModel, renderers, root.transform, lod, meshes))
                    .ToArray();
                ConfigureLodGroup(contents, lods, lodTriangles);
                generator.GeneratedRoot = root;
                generator.GeneratedFingerprint = Fingerprint(generator);
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

        /// <summary>Copies the model under "LOD{lod}", keeping only the source renderers, and assigns that LOD's meshes.</summary>
        private static Renderer[] BuildLodCopy(GameObject model, MeshRenderer[] sources, Transform parent, int lod,
            IReadOnlyDictionary<Mesh, Mesh> meshes)
        {
            var lodRoot = new GameObject(LOD_NAME + lod);
            lodRoot.transform.SetParent(parent, false);
            GameObject copy = Object.Instantiate(model, lodRoot.transform, false);
            copy.name = model.name;
            Renderer[] modelRenderers = model.GetComponentsInChildren<Renderer>(true);
            Renderer[] copyRenderers = copy.GetComponentsInChildren<Renderer>(true);
            // Instantiate preserves hierarchy order, so a model renderer's index finds its copy.
            Renderer[] kept = sources.Select(source => copyRenderers[Array.IndexOf(modelRenderers, source)]).ToArray();
            StripToRenderers(copy.transform, new HashSet<Renderer>(kept));
            foreach (Renderer renderer in kept)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                filter.sharedMesh = meshes[filter.sharedMesh];
            }

            return kept;
        }

        /// <summary>Removes every component except the kept renderers and their filters, then any empty branches.</summary>
        private static void StripToRenderers(Transform root, HashSet<Renderer> kept)
        {
            // Children come first, so a branch is empty by the time its parent is visited.
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true).Reverse())
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                bool keep = transform.TryGetComponent(out Renderer renderer) && kept.Contains(renderer);
                foreach (Component component in transform.GetComponents<Component>().Reverse())
                {
                    if (component is not Transform && !(keep && component is MeshFilter or MeshRenderer))
                    {
                        Object.DestroyImmediate(component);
                    }
                }

                if (transform != root && transform.childCount == 0 && !keep)
                {
                    Object.DestroyImmediate(transform.gameObject);
                }
            }
        }

        /// <summary>
        /// Points the prefab's single LODGroup at the generated levels. Transitions are kept when the level count is
        /// unchanged; otherwise LOD0's and the impostor's transitions are kept and the levels between are distributed.
        /// </summary>
        private static void ConfigureLodGroup(GameObject contents, Renderer[][] lods, int[] lodTriangles)
        {
            LODGroup[] groups = contents.GetComponentsInChildren<LODGroup>(true);
            LODGroup group = groups.Length > 0 ? groups[0] : contents.AddComponent<LODGroup>();
            foreach (LODGroup extra in groups.Skip(1))
            {
                Object.DestroyImmediate(extra);
            }

            // A new LODGroup starts with Unity's default levels, which are not the user's transitions.
            LOD[] previous = groups.Length > 0 ? group.GetLODs() : Array.Empty<LOD>();
            float[] heights = previous.Length == lods.Length
                ? previous.Select(lod => lod.screenRelativeTransitionHeight).ToArray()
                : DefaultTransitions(previous, lodTriangles);
            group.SetLODs(lods.Select((renderers, i) => new LOD(heights[i], renderers)
            {
                fadeTransitionWidth = previous.Length == lods.Length ? previous[i].fadeTransitionWidth : 0,
            }).ToArray());
            group.RecalculateBounds();
        }

        private static float[] DefaultTransitions(LOD[] previous, int[] lodTriangles)
        {
            bool anchored = previous.Length >= 2;
            float first = anchored ? previous[0].screenRelativeTransitionHeight : DEFAULT_FIRST_TRANSITION;
            float impostor = anchored ? previous[^1].screenRelativeTransitionHeight : DEFAULT_IMPOSTOR_TRANSITION;
            if (lodTriangles.Length == 1)
            {
                return new[] { impostor };
            }

            float[] ratios = lodTriangles.Select(triangles => (float)triangles / lodTriangles[0]).ToArray();
            return MeshLodGeneration.AutoTransitions(first, impostor, ratios, out _);
        }

        /// <summary>
        /// Deletes meshes the regenerated hierarchy no longer uses. Meshes the current impostor bake still references stay
        /// until the next bake prunes them.
        /// </summary>
        private static void RemoveUnused(IEnumerable<Mesh> leftovers, GameObject source)
        {
            ImpostorDefinition definition = source.TryGetComponent(out Impostor impostor) ? impostor.Definition : null;
            var baked = new HashSet<Mesh>(definition && definition.LodData != null
                ? definition.LodData.MeshLevels.SelectMany(level => level.Parts).Select(part => part.Mesh)
                : Enumerable.Empty<Mesh>());
            Mesh[] unused = leftovers.Where(mesh => !baked.Contains(mesh)).ToArray();
            if (unused.Length == 0)
            {
                return;
            }

            Array.ForEach(unused, RemoveMesh);
            AssetDatabase.SaveAssetIfDirty(source);
        }

        private static void RemoveMesh(Mesh mesh)
        {
            AssetDatabase.RemoveObjectFromAsset(mesh);
            Object.DestroyImmediate(mesh, true);
        }

        private static ImpostorBakeSettings BakeSettingsIn(string folder)
        {
            string path = folder + SETTINGS_NAME;
            var settings = AssetDatabase.LoadAssetAtPath<ImpostorBakeSettings>(path);
            if (!settings)
            {
                settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
                AssetDatabase.CreateAsset(settings, path);
            }

            return settings;
        }
    }
}
