using Impostors.Data;
using UnityEngine;

namespace Impostors
{
    /// <summary>Keeps the owned shader's native Lit switches and optional payload sampling aligned with its properties.</summary>
    public static class ImpostorMaterialControls
    {
        private const float TOGGLE_THRESHOLD = .5f;

        public static void ApplyKeywords(Material material)
        {
            if (!material)
            {
                return;
            }

            SetKeyword(material, ImpostorShaderKeywords.SPECULAR_HIGHLIGHTS_OFF,
                IsToggleOff(material, ImpostorShaderProperties.SpecularHighlights));
            SetKeyword(material, ImpostorShaderKeywords.ENVIRONMENT_REFLECTIONS_OFF,
                IsToggleOff(material, ImpostorShaderProperties.EnvironmentReflections));
            SetKeyword(material, ImpostorShaderKeywords.MATERIAL_ADJUSTMENTS, HasMaterialAdjustments(material));
            SetKeyword(material, ImpostorShaderKeywords.COLOR_ADJUSTMENTS, HasColorAdjustments(material));
            SetKeyword(material, ImpostorShaderKeywords.REFLECTANCE_ADJUSTMENTS, HasReflectanceAdjustments(material));
            SetKeyword(material, ImpostorShaderKeywords.SUBSURFACE,
                material.HasFloat(ImpostorShaderProperties.SubsurfaceScattering) && !IsToggleOff(material, ImpostorShaderProperties.SubsurfaceScattering));
            SetKeyword(material, ImpostorShaderKeywords.NO_ADDITIONAL_LIGHTS,
                IsToggleOff(material, ImpostorShaderProperties.AdditionalLights));
        }

        private static bool IsToggleOff(Material material, int property)
        {
            return material.HasFloat(property) && material.GetFloat(property) <= TOGGLE_THRESHOLD;
        }

        /// <summary>Normal and metallic adjustments sample the optional material data array.</summary>
        private static bool HasMaterialAdjustments(Material material)
        {
            bool hasData = material.HasTexture(ImpostorShaderProperties.MaterialDataArray)
                && material.GetTexture(ImpostorShaderProperties.MaterialDataArray);
            return hasData && (!IsDefault(material, ImpostorShaderProperties.NormalStrength, 1)
                || !IsDefault(material, ImpostorShaderProperties.MetallicInfluence, 1));
        }

        private static bool HasColorAdjustments(Material material)
        {
            return !IsDefault(material, ImpostorShaderProperties.AlbedoContrast, 1)
                || !IsDefault(material, ImpostorShaderProperties.AlbedoSaturation, 1)
                || !IsDefault(material, ImpostorShaderProperties.AlbedoLift, 0)
                || material.GetColor(ImpostorShaderProperties.HueVariation).a > 0;
        }

        private static bool HasReflectanceAdjustments(Material material)
        {
            return !IsDefault(material, ImpostorShaderProperties.SpecularStrength, 1)
                || !IsWhite(material, ImpostorShaderProperties.SpecularTint)
                || !IsDefault(material, ImpostorShaderProperties.SmoothnessOffset, 0)
                || material.GetFloat(ImpostorShaderProperties.OcclusionStrength) > 1
                || !IsDefault(material, ImpostorShaderProperties.OcclusionContrast, 1)
                || !IsDefault(material, ImpostorShaderProperties.OcclusionOffset, 0)
                || !IsDefault(material, ImpostorShaderProperties.EmissionStrength, 1)
                || !IsWhite(material, ImpostorShaderProperties.EmissionTint);
        }

        private static bool IsDefault(Material material, int property, float value) => material.GetFloat(property) == value;

        private static bool IsWhite(Material material, int property)
        {
            Color color = material.GetColor(property);
            return color.r == 1 && color.g == 1 && color.b == 1;
        }

        private static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled)
            {
                material.EnableKeyword(keyword);
            }
            else
            {
                material.DisableKeyword(keyword);
            }
        }
    }
}
