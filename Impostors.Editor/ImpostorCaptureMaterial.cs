using System;
using Impostors.Data;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    /// <summary>Enables the explicitly authored capture variant of the source shader.</summary>
    internal static class ImpostorCaptureMaterial
    {
        private const string MISSING_SHADER = "Capture materials require a shader.";
        private const string UNSUPPORTED_SHADER = "Shader '{0}' needs an opaque/cutout Universal Lit graph with the Impostor "
            + "Capture node, a local Boolean Shader Feature keyword named " + ImpostorShaderKeywords.CAPTURE_ENABLED
            + ", and the ForwardLit pass. Stock Lit and shader-specific capture mappings are not supported.";
        private const string UNSUPPORTED_MATERIAL = "Material '{0}' ({1}): {2}";
        private const string KEYWORD_ENABLED = "Disable the baking-only " + ImpostorShaderKeywords.CAPTURE_ENABLED
            + " keyword on source material '{0}'.";
        private const string NOT_COMPILING = "Capture shader '{0}' must compile on the current graphics device.";

        public static Material Create(Material source)
        {
            if (!IsCaptureReady(source, out string reason, out bool unsupported))
            {
                throw unsupported ? new NotSupportedException(reason) : new ArgumentException(reason);
            }

            var keyword = source.shader.keywordSpace.FindKeyword(ImpostorShaderKeywords.CAPTURE_ENABLED);
            var material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            material.SetKeyword(keyword, true);
            material.DisableKeyword(ImpostorShaderKeywords.LOD_CROSSFADE);
            material.DisableKeyword(ImpostorShaderKeywords.DOTS_INSTANCING);
            return material;
        }

        /// <summary>Whether <see cref="Create"/> accepts <paramref name="source"/>. Otherwise explains why not.</summary>
        public static bool IsCaptureReady(Material source, out string reason)
        {
            return IsCaptureReady(source, out reason, out _);
        }

        /// <summary>Whether materials using <paramref name="shader"/> can be captured.</summary>
        public static bool IsCaptureReadyShader(Shader shader, out string reason)
        {
            if (!shader)
            {
                reason = MISSING_SHADER;
                return false;
            }

            // Tags and passes are only visible through a material.
            var probe = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                reason = ShaderProblem(probe);
                return reason == null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        private static bool IsCaptureReady(Material source, out string reason, out bool unsupported)
        {
            unsupported = false;
            if (!source || !source.shader)
            {
                reason = MISSING_SHADER;
                return false;
            }

            string problem = ShaderProblem(source);
            if (problem != null)
            {
                unsupported = true;
                reason = string.Format(UNSUPPORTED_MATERIAL, source.name, AssetDatabase.GetAssetPath(source), problem);
                return false;
            }

            if (source.IsKeywordEnabled(source.shader.keywordSpace.FindKeyword(ImpostorShaderKeywords.CAPTURE_ENABLED)))
            {
                reason = string.Format(KEYWORD_ENABLED, source.name);
                return false;
            }

            reason = null;
            return true;
        }

        private static string ShaderProblem(Material material)
        {
            Shader shader = material.shader;
            var keyword = shader.keywordSpace.FindKeyword(ImpostorShaderKeywords.CAPTURE_ENABLED);
            if (!keyword.isValid || keyword.isOverridable || keyword.isDynamic
                || material.GetTag("ShaderGraphTargetId", false) != "UniversalLitSubTarget"
                || material.GetTag("RenderType", false) is not ("Opaque" or "TransparentCutout")
                || material.FindPass(ImpostorShaderPasses.FORWARD_LIT) < 0)
            {
                return string.Format(UNSUPPORTED_SHADER, shader.name);
            }

            return !shader.isSupported || ShaderUtil.ShaderHasError(shader) ? string.Format(NOT_COMPILING, shader.name) : null;
        }
    }
}
