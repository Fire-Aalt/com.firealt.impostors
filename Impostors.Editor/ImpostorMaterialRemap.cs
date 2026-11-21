using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Impostors.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>
    /// Replaces materials an impostor can't capture with capture-ready copies on a chosen shader. The copies are saved
    /// next to their source materials and recorded on the prefab's Impostor component, which applies them to every
    /// renderer of the prefab, including ones a Mesh LOD Generator builds later. The source model is left untouched.
    /// </summary>
    public static class ImpostorMaterialRemap
    {
        public const string LIT_SHADER_PATH = "Impostors/Shaders/ImpostorLit.shadergraph";
        public const string FOLIAGE_SHADER_PATH = "Impostors/Shaders/ImpostorFoliage.shadergraph";

        private const string SUFFIX = "_Impostor";
        private const string MATERIAL_EXTENSION = ".mat";
        private const string EMISSION_KEYWORD = "_EMISSION";
        private const string ALPHA_TEST_KEYWORD = "_ALPHATEST_ON";
        private const string ALPHA_CLIP = "_AlphaClip";
        private const string CUTOUT = "TransparentCutout";
        private const string INVALID_SHADER = "Can't remap '{0}': {1}";
        private const string SAVE_FAILED = "Could not save the remapped materials into the prefab.";
        private const string REMAPPED = "Remapped '{0}' to '{1}' ({2}): {3}";
        private const string KEPT = " (kept)";
        private const string NOTHING_MAPPED = "no properties matched";
        private const string ENGINE_PREFIX = "unity_";

        /// <summary>
        /// Accepted spellings per property role, normalized to lowercase without underscores. A target property in a
        /// role takes the first source property found in that order; "!" inverts a 0-1 value.
        /// </summary>
        private static readonly (string[] Names, string[] Fallbacks)[] Roles =
        {
            (new[] { "basemap", "maintex", "basecolormap", "albedo", "albedomap", "albedotex", "diffuse", "diffusemap",
                "maintexture", "basetexture", "colormap" }, Array.Empty<string>()),
            (new[] { "basecolor", "color", "maincolor", "tint", "tintcolor", "albedocolor", "diffusecolor" },
                Array.Empty<string>()),
            (new[] { "bumpmap", "normalmap", "normal", "normaltex", "bump" }, Array.Empty<string>()),
            (new[] { "bumpscale", "normalscale", "normalstrength", "normalintensity" }, Array.Empty<string>()),
            (new[] { "smoothness", "glossiness", "gloss" }, new[] { "!roughness" }),
            (new[] { "roughness" }, new[] { "!smoothness", "!glossiness" }),
            (new[] { "metallic", "metalness" }, Array.Empty<string>()),
            // HDRP's mask map keeps metallic in R and smoothness in A, like URP's metallic map.
            (new[] { "metallicglossmap", "metallicmap", "metallicsmoothness" }, new[] { "maskmap" }),
            // A mask map's G is occlusion, which URP's metallic map doesn't have.
            (new[] { "maskmap" }, Array.Empty<string>()),
            (new[] { "occlusionmap", "aomap", "ambientocclusionmap", "occlusion" }, Array.Empty<string>()),
            (new[] { "occlusionstrength", "aostrength", "aointensity" }, Array.Empty<string>()),
            (new[] { "emissionmap", "emissivemap", "emissivecolormap" }, Array.Empty<string>()),
            (new[] { "emissioncolor", "emissivecolor" }, Array.Empty<string>()),
            (new[] { "cutoff", "alphacutoff", "alphaclipthreshold" }, Array.Empty<string>()),
        };

        private static readonly Dictionary<string, string[]> Aliases = BuildAliases();

        private static readonly HashSet<string> EmissionRoles = new() { "emissionmap", "emissivemap", "emissivecolormap",
            "emissioncolor", "emissivecolor" };

        /// <summary>
        /// Materials of the prefab's capture renderers that can't be captured. A generator prefab that isn't generated
        /// yet is checked through its source model and material remaps. Empty when the renderers can't be resolved.
        /// </summary>
        public static Material[] UncapturableMaterials(GameObject source, ImpostorSourceLod sourceLod)
        {
            return CaptureMaterials(source, sourceLod)
                .Where(material => material && !ImpostorCaptureMaterial.IsCaptureReady(material, out _))
                .Distinct()
                .ToArray();
        }

        /// <summary>The capture-ready shader a remap of <paramref name="material"/> starts with.</summary>
        public static Shader DefaultShader(Material material)
        {
            bool clipped = material && (material.IsKeywordEnabled(ALPHA_TEST_KEYWORD)
                || (material.HasProperty(ALPHA_CLIP) && material.GetFloat(ALPHA_CLIP) > .5f)
                || material.GetTag("RenderType", false) == CUTOUT || BaseMapHasAlpha(material));
            return ImpostorPackageAssets.Load<Shader>(clipped ? FOLIAGE_SHADER_PATH : LIT_SHADER_PATH);
        }

        /// <summary>The shader behind a picked object: a shader, or an asset that imports as one such as a ShaderGraph.</summary>
        public static Shader ResolveShader(Object value)
        {
            if (value is Shader shader)
            {
                return shader;
            }

            string path = value ? AssetDatabase.GetAssetPath(value) : null;
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Shader>(path);
        }

        /// <summary>
        /// Writes a capture-ready copy of each material on its chosen shader, records the copies as the Impostor
        /// component's remaps and applies them to the prefab's renderers. Returns the copies keyed by source material.
        /// </summary>
        public static Dictionary<Material, Material> Remap(GameObject source, IReadOnlyDictionary<Material, Shader> shaders)
        {
            string path = ImpostorAuthoring.RequireWritableSource(source);
            foreach ((Material material, Shader shader) in shaders)
            {
                if (!ImpostorCaptureMaterial.IsCaptureReadyShader(shader, out string reason))
                {
                    throw new ArgumentException(string.Format(INVALID_SHADER, material ? material.name : "missing", reason));
                }
            }

            var targets = new Dictionary<Material, Material>();
            var written = new HashSet<string>();
            foreach ((Material material, Shader shader) in shaders)
            {
                targets[material] = WriteMaterial(material, shader, TargetPath(material, path, written));
            }

            Apply(path, targets);
            return targets;
        }

        /// <summary>
        /// Copies every property of <paramref name="to"/>'s shader that <paramref name="from"/> has under the same name,
        /// the same name in another case, or a common alias. Returns "target←source" for each copied property.
        /// </summary>
        /// <summary>The Impostor component's complete remaps, keyed by source material. Later entries win.</summary>
        public static Dictionary<Material, Material> Remaps(Impostor impostor)
        {
            var remaps = new Dictionary<Material, Material>();
            foreach (MaterialRemap remap in impostor ? impostor.MaterialRemaps ?? Array.Empty<MaterialRemap>()
                         : Array.Empty<MaterialRemap>())
            {
                if (remap.Source && remap.Target)
                {
                    remaps[remap.Source] = remap.Target;
                }
            }

            return remaps;
        }

        /// <summary>Replaces remapped materials on every renderer under <paramref name="root"/>. Returns whether any changed.</summary>
        public static bool ApplyRemaps(GameObject root, IReadOnlyDictionary<Material, Material> remaps, bool recordUndo = false)
        {
            bool changed = false;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool replaced = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] && remaps.TryGetValue(materials[i], out Material target) && target != materials[i])
                    {
                        materials[i] = target;
                        replaced = true;
                    }
                }

                if (replaced)
                {
                    if (recordUndo)
                    {
                        Undo.RecordObject(renderer, nameof(ApplyRemaps));
                    }

                    renderer.sharedMaterials = materials;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Brings the renderers under <paramref name="root"/> in line with edited remaps: targets of removed or changed
        /// remaps go back to their source material, then the current remaps are applied.
        /// </summary>
        public static bool SyncRemaps(GameObject root, IReadOnlyDictionary<Material, Material> previous,
            IReadOnlyDictionary<Material, Material> current, bool recordUndo = false)
        {
            var restore = new Dictionary<Material, Material>();
            foreach ((Material source, Material target) in previous)
            {
                if (!current.TryGetValue(source, out Material now) || now != target)
                {
                    restore[target] = source;
                }
            }

            bool restored = ApplyRemaps(root, restore, recordUndo);
            return ApplyRemaps(root, current, recordUndo) | restored;
        }

        public static List<string> Transfer(Material from, Material to)
        {
            var copied = new List<string>();
            Shader target = to.shader;
            Shader origin = from.shader;
            LocalKeyword emission = origin.keywordSpace.FindKeyword(EMISSION_KEYWORD);
            bool emissionOff = emission.isValid && !from.IsKeywordEnabled(emission);
            for (int i = 0; i < target.GetPropertyCount(); i++)
            {
                if ((target.GetPropertyFlags(i) & (ShaderPropertyFlags.PerRendererData
                        | ShaderPropertyFlags.NonModifiableTextureData)) != 0)
                {
                    continue;
                }

                string name = target.GetPropertyName(i);
                ShaderPropertyType type = target.GetPropertyType(i);
                // Lightmaps and shadow masks are bound by the engine, not authored.
                if (name.StartsWith(ENGINE_PREFIX, StringComparison.Ordinal))
                {
                    continue;
                }

                if (emissionOff && EmissionRoles.Contains(Normalize(name)))
                {
                    if (type is ShaderPropertyType.Color)
                    {
                        to.SetColor(name, Color.black);
                    }

                    continue;
                }

                if (TryFind(from, origin, name, target, i, out int match, out bool invert))
                {
                    Copy(from, origin.GetPropertyName(match), origin.GetPropertyType(match), to, name, type, invert);
                    copied.Add(name + "←" + (invert ? "1-" : string.Empty) + origin.GetPropertyName(match));
                }
            }

            return copied;
        }

        private static IEnumerable<Material> CaptureMaterials(GameObject source, ImpostorSourceLod sourceLod)
        {
            if (!source)
            {
                return Array.Empty<Material>();
            }

            try
            {
                return ImpostorBaker.GetCaptureRenderers(source, sourceLod).SelectMany(renderer => renderer.sharedMaterials);
            }
            catch (ArgumentException)
            {
                if (!source.TryGetComponent(out MeshLodGenerator generator) || !generator.SourceModel)
                {
                    return Array.Empty<Material>();
                }
            }

            try
            {
                Dictionary<Material, Material> remaps = Remaps(source.GetComponent<Impostor>());
                return MeshLodGeneratorAuthoring.SourceRenderers(source.GetComponent<MeshLodGenerator>().SourceModel)
                    .SelectMany(renderer => renderer.sharedMaterials)
                    .Select(material => material && remaps.TryGetValue(material, out Material target) ? target : material)
                    .ToArray();
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Array.Empty<Material>();
            }
        }

        private static bool BaseMapHasAlpha(Material material)
        {
            Shader shader = material.shader;
            for (int i = 0; shader && i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture
                    && Aliases.TryGetValue(Normalize(shader.GetPropertyName(i)), out string[] names) && names[0] == "basemap")
                {
                    Texture texture = material.GetTexture(shader.GetPropertyName(i));
                    if (texture && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)
                    {
                        return importer.alphaSource != TextureImporterAlphaSource.None
                            && (importer.alphaSource == TextureImporterAlphaSource.FromGrayScale
                                || importer.DoesSourceTextureHaveAlpha());
                    }
                }
            }

            return false;
        }

        /// <summary>"{source}_Impostor.mat" next to the source material, or next to the prefab when that folder can't be written.</summary>
        private static string TargetPath(Material source, string prefabPath, HashSet<string> written)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string folder = string.IsNullOrEmpty(sourcePath) ? null : Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            try
            {
                folder = ImpostorPackageAssets.RequireWritableAssetPath(folder);
            }
            catch (ArgumentException)
            {
                folder = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
            }

            string path = folder + "/" + source.name + SUFFIX + MATERIAL_EXTENSION;
            // Two sources with one name in one folder, such as materials embedded in two models, must not share a copy.
            if (!written.Add(path))
            {
                path = AssetDatabase.GenerateUniqueAssetPath(path);
                written.Add(path);
            }

            return ImpostorPackageAssets.RequireWritableAssetPath(path, MATERIAL_EXTENSION);
        }

        /// <summary>
        /// Builds the copy from the source, then from an earlier copy at <paramref name="path"/> so its settings win, and
        /// saves it there. An earlier copy is updated in place, so references to it stay valid.
        /// </summary>
        private static Material WriteMaterial(Material source, Shader shader, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            List<string> copied = Transfer(source, material);
            Material settings = source;
            if (existing)
            {
                copied.AddRange(Transfer(existing, material).Select(entry => entry + KEPT));
                settings = existing;
            }

            material.enableInstancing = settings.enableInstancing;
            material.doubleSidedGI = settings.doubleSidedGI;
            material.DisableKeyword(ImpostorShaderKeywords.CAPTURE_ENABLED);
            Material result;
            if (existing)
            {
                EditorUtility.CopySerialized(material, existing);
                Object.DestroyImmediate(material);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssetIfDirty(existing);
                result = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(material, path);
                result = material;
            }

            Debug.Log(string.Format(REMAPPED, source.name, path, shader.name,
                copied.Count > 0 ? string.Join(", ", copied) : NOTHING_MAPPED), result);
            return result;
        }

        private static void Apply(string path, IReadOnlyDictionary<Material, Material> targets)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (!contents.TryGetComponent(out Impostor impostor))
                {
                    impostor = contents.AddComponent<Impostor>();
                }

                impostor.MaterialRemaps = UpdatedRemaps(impostor.MaterialRemaps, targets);
                // The renderers hold the remapped materials, which may be earlier copies rather than sources.
                ApplyRemaps(contents, targets);
                ApplyRemaps(contents, Remaps(impostor));
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

        /// <summary>
        /// Adds a remap per new copy. When the remapped material is itself an earlier remap's target, that remap is pointed
        /// at the new copy instead, so remaps always start from the model's material.
        /// </summary>
        private static MaterialRemap[] UpdatedRemaps(MaterialRemap[] remaps, IReadOnlyDictionary<Material, Material> targets)
        {
            var result = new List<MaterialRemap>();
            var covered = new HashSet<Material>();
            foreach (MaterialRemap remap in remaps ?? Array.Empty<MaterialRemap>())
            {
                if (!remap.Source)
                {
                    continue;
                }

                Material target = remap.Target && targets.TryGetValue(remap.Target, out Material replaced) ? replaced
                    : targets.TryGetValue(remap.Source, out Material direct) ? direct : remap.Target;
                result.Add(new MaterialRemap { Source = remap.Source, Target = target });
                covered.Add(remap.Source);
                if (remap.Target)
                {
                    covered.Add(remap.Target);
                }
            }

            result.AddRange(targets.Where(pair => !covered.Contains(pair.Key))
                .Select(pair => new MaterialRemap { Source = pair.Key, Target = pair.Value }));
            return result.ToArray();
        }

        /// <summary>
        /// Exact name, then name ignoring case and leading underscores, then the property's role aliases. A texture
        /// prefers the first of these with a texture assigned, since shaders often keep a legacy slot empty.
        /// </summary>
        private static bool TryFind(Material from, Shader origin, string name, Shader target, int index, out int match,
            out bool invert)
        {
            ShaderPropertyType type = target.GetPropertyType(index);
            TextureDimension dimension = type == ShaderPropertyType.Texture ? target.GetPropertyTextureDimension(index)
                : TextureDimension.Unknown;
            (int Index, bool Invert)? first = null;
            foreach ((int candidate, bool inverted) in Candidates(origin, name, type, dimension))
            {
                if (type != ShaderPropertyType.Texture || from.GetTexture(origin.GetPropertyName(candidate)))
                {
                    (match, invert) = (candidate, inverted);
                    return true;
                }

                first ??= (candidate, inverted);
            }

            (match, invert) = first ?? (-1, false);
            return first.HasValue;
        }

        private static IEnumerable<(int Index, bool Invert)> Candidates(Shader origin, string name, ShaderPropertyType type,
            TextureDimension dimension)
        {
            int exact = origin.FindPropertyIndex(name);
            if (exact >= 0 && IsCompatible(origin, exact, type, dimension))
            {
                yield return (exact, false);
            }

            string normalized = Normalize(name);
            foreach (int index in WithName(origin, normalized, type, dimension))
            {
                yield return (index, false);
            }

            if (!Aliases.TryGetValue(normalized, out string[] aliases))
            {
                yield break;
            }

            foreach (string alias in aliases)
            {
                bool inverted = alias[0] == '!';
                if (inverted && !IsScalar(type))
                {
                    continue;
                }

                foreach (int index in WithName(origin, inverted ? alias.Substring(1) : alias, type, dimension))
                {
                    yield return (index, inverted);
                }
            }
        }

        private static IEnumerable<int> WithName(Shader origin, string normalized, ShaderPropertyType type,
            TextureDimension dimension)
        {
            for (int i = 0; i < origin.GetPropertyCount(); i++)
            {
                if (Normalize(origin.GetPropertyName(i)) == normalized && IsCompatible(origin, i, type, dimension))
                {
                    yield return i;
                }
            }
        }

        private static bool IsCompatible(Shader origin, int index, ShaderPropertyType type, TextureDimension dimension)
        {
            ShaderPropertyType source = origin.GetPropertyType(index);
            return type switch
            {
                ShaderPropertyType.Texture => source == ShaderPropertyType.Texture
                    && origin.GetPropertyTextureDimension(index) == dimension,
                ShaderPropertyType.Color or ShaderPropertyType.Vector => source is ShaderPropertyType.Color
                    or ShaderPropertyType.Vector,
                _ => IsScalar(type) && IsScalar(source),
            };
        }

        private static bool IsScalar(ShaderPropertyType type)
        {
            return type is ShaderPropertyType.Float or ShaderPropertyType.Range or ShaderPropertyType.Int;
        }

        private static void Copy(Material from, string fromName, ShaderPropertyType fromType, Material to, string name,
            ShaderPropertyType type, bool invert)
        {
            switch (type)
            {
                case ShaderPropertyType.Texture:
                    to.SetTexture(name, from.GetTexture(fromName));
                    to.SetTextureScale(name, from.GetTextureScale(fromName));
                    to.SetTextureOffset(name, from.GetTextureOffset(fromName));
                    break;
                case ShaderPropertyType.Color:
                    to.SetColor(name, from.GetColor(fromName));
                    break;
                case ShaderPropertyType.Vector:
                    to.SetVector(name, from.GetVector(fromName));
                    break;
                default:
                    float value = fromType == ShaderPropertyType.Int ? from.GetInteger(fromName) : from.GetFloat(fromName);
                    value = invert ? 1 - value : value;
                    if (type == ShaderPropertyType.Int)
                    {
                        to.SetInteger(name, Mathf.RoundToInt(value));
                    }
                    else
                    {
                        to.SetFloat(name, value);
                    }

                    break;
            }
        }

        private static string Normalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

        private static Dictionary<string, string[]> BuildAliases()
        {
            var aliases = new Dictionary<string, string[]>();
            foreach ((string[] names, string[] fallbacks) in Roles)
            {
                string[] candidates = names.Concat(fallbacks).ToArray();
                foreach (string name in names)
                {
                    aliases[name] = candidates;
                }
            }

            return aliases;
        }
    }
}
