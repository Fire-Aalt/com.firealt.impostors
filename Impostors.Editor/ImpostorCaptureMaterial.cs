using System;
using Impostors.Data;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    /// <summary>Enables the explicitly authored capture variant of the source shader.</summary>
    internal static class ImpostorCaptureMaterial
    {
        public static Material Create(Material source)
        {
            if (!source || !source.shader)
            {
                throw new ArgumentException("Capture materials require a shader.");
            }

            Shader shader = source.shader;
            var keyword = shader.keywordSpace.FindKeyword(ImpostorShaderKeywords.CAPTURE_ENABLED);
            if (!keyword.isValid || keyword.isOverridable || keyword.isDynamic
                || source.GetTag("ShaderGraphTargetId", false) != "UniversalLitSubTarget"
                || source.GetTag("RenderType", false) is not ("Opaque" or "TransparentCutout")
                || source.FindPass(ImpostorShaderPasses.FORWARD_LIT) < 0)
            {
                throw new NotSupportedException($"Material '{source.name}' ({AssetDatabase.GetAssetPath(source)}) requires an opaque/cutout Universal Lit graph with the Impostor Capture node, a local Boolean Shader Feature keyword named {ImpostorShaderKeywords.CAPTURE_ENABLED}, and the ForwardLit pass. Stock Lit and shader-specific capture mappings are not supported.");
            }

            if (source.IsKeywordEnabled(keyword))
            {
                throw new ArgumentException($"Disable the baking-only {ImpostorShaderKeywords.CAPTURE_ENABLED} keyword on source material '{source.name}'.");
            }

            if (!shader.isSupported || ShaderUtil.ShaderHasError(shader))
            {
                throw new NotSupportedException($"Capture shader '{shader.name}' on material '{source.name}' must compile on the current graphics device.");
            }

            var material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            material.SetKeyword(keyword, true);
            material.DisableKeyword(ImpostorShaderKeywords.LOD_CROSSFADE);
            material.DisableKeyword(ImpostorShaderKeywords.DOTS_INSTANCING);
            return material;
        }
    }
}
