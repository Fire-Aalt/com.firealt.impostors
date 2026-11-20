using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Impostors.Data
{
    /// <summary>Root-space render assets only. No prefab, Renderer, entity, or per-placement state.</summary>
    [Serializable]
    [MovedFrom(true, sourceNamespace: "Impostors", sourceAssembly: "Impostors.Runtime")]
    public sealed class ImpostorLodData
    {
        private const string INVALID_PROFILE = "LOD data requires a finite reference point, positive size, and mesh levels.";
        private const string INVALID_LEVELS =
            "LOD thresholds must decrease with nonoverlapping fade bands; each level needs render parts.";
        private const string INVALID_PART = "A LOD part requires a mesh, material, and valid submesh index.";
        private const string UNSUPPORTED_SHADER =
            "A LOD material requires a supported DOTS shader and LOD_FADE_CROSSFADE variants for either adjacent fade band.";

        public Vector3 ReferencePoint;
        public float Size = 1;

#if UNITY_EDITOR
        [HideInInspector]
        public string SourceGeometryHash;

        [HideInInspector]
        public string SourceLodHash;
#endif

        public ImpostorMeshLod[] MeshLevels = Array.Empty<ImpostorMeshLod>();

        public ImpostorLodData Copy()
        {
            var copy = (ImpostorLodData)MemberwiseClone();
            copy.MeshLevels = Array.ConvertAll(MeshLevels, level => level?.Copy());
            return copy;
        }

        public bool TryValidate(out string error)
        {
            error = ValidateProfile() ?? ValidateLevels();
            return error == null;
        }

        private string ValidateProfile()
        {
            bool valid = float.IsFinite(Size) && Size > 0 && float.IsFinite(ReferencePoint.sqrMagnitude)
                && MeshLevels is { Length: > 0 };
            return valid ? null : INVALID_PROFILE;
        }

        private string ValidateLevels()
        {
            float previousEnd = float.PositiveInfinity;
            for (int i = 0; i < MeshLevels.Length; i++)
            {
                ImpostorMeshLod level = MeshLevels[i];
                if (!IsValidLevel(level, previousEnd))
                {
                    return INVALID_LEVELS;
                }

                previousEnd = level.TransitionScreenHeight;
                bool canFade = level.FadeWidth > 0 || (i > 0 && MeshLevels[i - 1].FadeWidth > 0);
                string partError = ValidateParts(level.Parts, canFade);
                if (partError != null)
                {
                    return $"LOD{i}: {partError}";
                }
            }

            return null;
        }

        private static bool IsValidLevel(ImpostorMeshLod level, float previousEnd)
        {
            return level is { Parts: { Length: > 0 } } && float.IsFinite(level.TransitionScreenHeight)
                && level.TransitionScreenHeight > 0 && level.TransitionScreenHeight < previousEnd
                && float.IsFinite(level.FadeWidth) && level.FadeWidth is >= 0 and <= 1;
        }

        private static string ValidateParts(ImpostorLodPart[] parts, bool canFade)
        {
            foreach (ImpostorLodPart part in parts)
            {
                if (!part.Mesh || !part.Material || part.SubmeshIndex < 0 || part.SubmeshIndex >= part.Mesh.subMeshCount)
                {
                    return INVALID_PART;
                }

                if (!IsSupportedShader(part.Material.shader, canFade))
                {
                    return $"{UNSUPPORTED_SHADER} Material: {part.Material.name}.";
                }
            }

            return null;
        }

        private static bool IsSupportedShader(Shader shader, bool canFade)
        {
            return shader && shader.isSupported
                && shader.keywordSpace.FindKeyword(ImpostorShaderKeywords.DOTS_INSTANCING).isValid
                && (!canFade || shader.keywordSpace.FindKeyword(ImpostorShaderKeywords.LOD_CROSSFADE).isValid);
        }
    }
}
