using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Impostors.Data;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Converts authoring prefabs to root-space mesh/material tables. Prefabs stay editor-only.</summary>
    public static class ImpostorLodProfileBuilder
    {
        private const string PART_NAME = "{0} LOD{1} Part{2}.asset";
        private const string MATERIAL_EXTENSION = ".mat";
        private const string GEOMETRY_HASH_VERSION = "All prefab LODs + original shaders v5|";
        private const string MISSING_LODS =
            "A valid bake record with a source LODGroup containing static MeshRenderers is required.";
        private const string EXPECTED_ASSETS_FOLDER = "Expected an Assets folder.";

        public static ImpostorLodData Build(ImpostorDefinition definition, string folder)
        {
            RequireBakedSource(definition);
            return Build(definition.SourcePrefab, folder, null);
        }

        public static ImpostorLodData BuildForBake(ImpostorDefinition definition, string folder, ImpostorLodData previous,
            Action<Object> track = null)
        {
            RequireBakedSource(definition);
            if (previous == null || previous.SourceGeometryHash != GeometryHash(definition.SourcePrefab))
            {
                return Build(definition.SourcePrefab, folder, track);
            }

            // Capture-only changes and LOD thresholds do not require new mesh/material tables.
            ImpostorLodData profile = previous.Copy();
            ApplyPrefabSettings(profile, definition.SourcePrefab);

            if (!profile.TryValidate(out string error)) throw new InvalidOperationException(error);
            return profile;
        }

        public static void ApplyPrefabSettings(ImpostorLodData profile, GameObject source)
        {
            LODGroup group = RequireLodGroup(source);
            LOD[] geometry = group.GetLODs();
            if (geometry.Length != profile.MeshLevels.Length)
            {
                throw new ArgumentException(MISSING_LODS);
            }

            Matrix4x4 matrix = source.transform.worldToLocalMatrix * group.transform.localToWorldMatrix;
            profile.ReferencePoint = matrix.MultiplyPoint3x4(group.localReferencePoint);
            float scale = Mathf.Max(matrix.MultiplyVector(Vector3.right).magnitude,
                Mathf.Max(matrix.MultiplyVector(Vector3.up).magnitude, matrix.MultiplyVector(Vector3.forward).magnitude));
            profile.Size = group.size * scale;
            for (int i = 0; i < geometry.Length; i++)
            {
                profile.MeshLevels[i].TransitionScreenHeight = geometry[i].screenRelativeTransitionHeight;
                profile.MeshLevels[i].FadeWidth = FadeWidth(group, geometry[i]);
            }

            // Unity's final Culled range is the impostor range. No additional size cull follows it.
            profile.SourceGeometryHash = GeometryHash(source);
            profile.SourceLodHash = LodHash(source);
        }

        private static void RequireBakedSource(ImpostorDefinition definition)
        {
            if (!definition || !definition.SourcePrefab || !definition.IsValid)
            {
                throw new ArgumentException(MISSING_LODS);
            }
        }

        private static LODGroup RequireLodGroup(GameObject source)
        {
            LODGroup group = source.GetComponentInChildren<LODGroup>(true);
            return group ? group : throw new ArgumentException(MISSING_LODS);
        }

        private static float FadeWidth(LODGroup group, LOD lod)
        {
            return group.fadeMode == LODFadeMode.None ? 0 : lod.fadeTransitionWidth;
        }

        private static ImpostorLodData Build(GameObject source, string folder, Action<Object> track)
        {
            if (folder != null)
            {
                folder = ImpostorPackageAssets.RequireWritableAssetPath(folder);
                EnsureFolder(folder);
            }

            LODGroup group = source.GetComponentInChildren<LODGroup>();
            if (!group)
            {
                throw new ArgumentException(MISSING_LODS);
            }

            LOD[] sourceLevels = group.GetLODs();
            var materials = new Dictionary<Material, Material>();
            var levels = new List<ImpostorMeshLod>();
            for (int level = 0; level < sourceLevels.Length; level++)
            {
                List<ImpostorLodPart> parts = BuildParts(source, sourceLevels[level], level, folder, materials, track);
                if (parts.Count == 0)
                {
                    continue;
                }

                levels.Add(new ImpostorMeshLod
                {
                    Parts = parts.ToArray(),
                    FadeWidth = FadeWidth(group, sourceLevels[level]),
                    TransitionScreenHeight = sourceLevels[level].screenRelativeTransitionHeight,
                });
            }

            var profile = new ImpostorLodData { MeshLevels = levels.ToArray() };
            ApplyPrefabSettings(profile, source);
            if (!profile.TryValidate(out string error))
            {
                throw new InvalidOperationException(error);
            }

            return profile;
        }

        /// <summary>Converts one source LOD's renderers into root-space parts, sharing converted materials.</summary>
        private static List<ImpostorLodPart> BuildParts(GameObject source, LOD lod, int level, string folder,
            Dictionary<Material, Material> materials, Action<Object> track)
        {
            var parts = new List<ImpostorLodPart>();
            foreach (Renderer renderer in lod.renderers)
            {
                if (renderer is not MeshRenderer || !renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh)
                {
                    throw new ArgumentException(MISSING_LODS);
                }

                Matrix4x4 relative = source.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                Mesh mesh = filter.sharedMesh;
                if (relative != Matrix4x4.identity)
                {
                    mesh = TransformMesh(mesh, relative);
                    track?.Invoke(mesh);
                    if (folder != null)
                    {
                        AssetDatabase.CreateAsset(mesh, folder + "/" + string.Format(PART_NAME, source.name, level, parts.Count));
                    }
                }

                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials.Length != mesh.subMeshCount)
                {
                    throw new ArgumentException(MISSING_LODS);
                }

                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material material = GetMaterial(sourceMaterials[submesh], level, folder, materials, track);
                    parts.Add(new ImpostorLodPart { Mesh = mesh, Material = material, SubmeshIndex = submesh });
                }
            }

            return parts;
        }

        private static Material GetMaterial(Material source, int level, string folder, Dictionary<Material, Material> materials,
            Action<Object> track)
        {
            if (!source)
            {
                throw new ArgumentException($"LOD{level} contains a missing material. Assign a material with a supported DOTS shader.");
            }

            if (!materials.TryGetValue(source, out Material material))
            {
                material = BuildMaterial(source, level, folder);
                track?.Invoke(material);
                materials.Add(source, material);
            }

            return material;
        }

        private static string GeometryHash(GameObject source)
        {
            LODGroup group = RequireLodGroup(source);
            var text = new StringBuilder(GEOMETRY_HASH_VERSION);
            foreach (LOD lod in group.GetLODs())
            {
                text.Append("LOD|");
                foreach (Renderer renderer in lod.renderers)
                {
                    if (!renderer || !renderer.TryGetComponent(out MeshFilter filter))
                    {
                        throw new ArgumentException(MISSING_LODS);
                    }

                    text.Append(GlobalObjectId.GetGlobalObjectIdSlow(filter.sharedMesh));
                    text.Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(filter.sharedMesh)));
                    text.Append((source.transform.worldToLocalMatrix * renderer.localToWorldMatrix).ToString("R"));
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        text.Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(material)));
                    }
                }
            }

            return Hash128.Compute(text.ToString()).ToString();
        }

        private static string LodHash(GameObject source)
        {
            LODGroup group = source.GetComponentInChildren<LODGroup>(true);
            var text = new StringBuilder(group.size.ToString("R", CultureInfo.InvariantCulture));
            text.Append(group.localReferencePoint.ToString("R")).Append(group.fadeMode).Append(group.animateCrossFading);
            text.Append((source.transform.worldToLocalMatrix * group.transform.localToWorldMatrix).ToString("R"));
            foreach (LOD lod in group.GetLODs())
            {
                text.Append('|').Append(lod.screenRelativeTransitionHeight.ToString("R", CultureInfo.InvariantCulture));
                text.Append('|').Append(lod.fadeTransitionWidth.ToString("R", CultureInfo.InvariantCulture));
            }

            return Hash128.Compute(text.ToString()).ToString();
        }

        private static Material BuildMaterial(Material source, int level, string folder)
        {
            if (!source.shader || !source.shader.isSupported || ShaderUtil.ShaderHasError(source.shader)
                || !source.shader.keywordSpace.FindKeyword(ImpostorShaderKeywords.DOTS_INSTANCING).isValid)
            {
                throw new NotSupportedException($"LOD{level} material '{source.name}' ({AssetDatabase.GetAssetPath(source)}) requires a compiled, supported shader with DOTS_INSTANCING_ON variants for BatchRendererGroup. Update its original shader; Impostors does not generate replacements.");
            }

            var result = new Material(source) { enableInstancing = true };
            if (folder != null) AssetDatabase.CreateAsset(result, folder + "/" + source.name + MATERIAL_EXTENSION);
            return result;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent))
            {
                throw new ArgumentException(EXPECTED_ASSETS_FOLDER);
            }

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Bakes a renderer's transform into a mesh copy, flipping winding for mirrored transforms.</summary>
        private static Mesh TransformMesh(Mesh source, Matrix4x4 matrix)
        {
            Mesh mesh = Object.Instantiate(source);
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            bool mirrored = matrix.determinant < 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            }

            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
            }

            for (int i = 0; i < tangents.Length; i++)
            {
                Vector3 direction = matrix.MultiplyVector(tangents[i]).normalized;
                tangents[i] = new Vector4(direction.x, direction.y, direction.z, tangents[i].w * (mirrored ? -1 : 1));
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            if (mirrored)
            {
                FlipWinding(mesh);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        private static void FlipWinding(Mesh mesh)
        {
            for (int part = 0; part < mesh.subMeshCount; part++)
            {
                if (mesh.GetTopology(part) != MeshTopology.Triangles)
                {
                    throw new NotSupportedException(MISSING_LODS);
                }

                int[] indices = mesh.GetIndices(part);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    (indices[i], indices[i + 1]) = (indices[i + 1], indices[i]);
                }

                mesh.SetIndices(indices, MeshTopology.Triangles, part);
            }
        }
    }
}
