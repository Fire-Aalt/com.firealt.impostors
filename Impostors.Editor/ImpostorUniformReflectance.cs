using Impostors.Data;
using UnityEngine;

namespace Impostors.Editor
{
    /// <summary>
    /// Finds bakes whose reflectance is the same on every covered texel, which is typical of
    /// dielectric vegetation, so the impostor shader can use one constant instead of texture reads.
    /// </summary>
    public static class ImpostorUniformReflectance
    {
        // BC7 can move a constant channel by a step or two.
        private const float TOLERANCE = 2f / 255f;

        // BC7 also leaves a faint alpha on empty texels, which hold no captured surface.
        private const float MINIMUM_COVERAGE = 2f / 255f;

        /// <summary>Returns true and the shared value when every covered texel of the bake matches.</summary>
        public static bool TryDetect(ImpostorDefinition definition, out Vector4 value)
        {
            value = Vector4.zero;
            if (!definition || !definition.BaseColor || !definition.Reflectance)
            {
                return false;
            }

            int width = definition.Reflectance.width;
            int height = definition.Reflectance.height;
            RenderTexture previous = RenderTexture.active;
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            try
            {
                Vector4 first = default;
                Vector4 sum = default;
                long covered = 0;
                for (int slice = 0; slice < definition.Reflectance.depth; slice++)
                {
                    Color[] colors = Read(definition.BaseColor, slice, target, readback);
                    Color[] reflectance = Read(definition.Reflectance, slice, target, readback);
                    for (int i = 0; i < colors.Length; i++)
                    {
                        if (colors[i].a < MINIMUM_COVERAGE)
                        {
                            continue;
                        }

                        Vector4 texel = reflectance[i];
                        if (covered == 0)
                        {
                            first = texel;
                        }

                        if (!Matches(texel, first))
                        {
                            return false;
                        }

                        sum += texel;
                        covered++;
                    }
                }

                if (covered == 0)
                {
                    return false;
                }

                value = sum / covered;
                return true;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(readback);
            }
        }

        /// <summary>Records the result on a material. A bake that varies keeps reading its array.</summary>
        public static bool Apply(ImpostorDefinition definition, Material material)
        {
            // A bake that already dropped its array keeps the constant recorded at bake time.
            if (definition && !definition.Reflectance)
            {
                return material.GetFloat(ImpostorShaderProperties.HasUniformReflectance) > .5f;
            }

            bool uniform = TryDetect(definition, out Vector4 value);
            material.SetFloat(ImpostorShaderProperties.HasUniformReflectance, uniform ? 1 : 0);
            if (uniform)
            {
                material.SetVector(ImpostorShaderProperties.UniformReflectance, value);
            }

            return uniform;
        }

        private static Color[] Read(Texture2DArray source, int slice, RenderTexture target, Texture2D readback)
        {
            Graphics.Blit(source, target, slice, 0);
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply();
            return readback.GetPixels();
        }

        private static bool Matches(Vector4 a, Vector4 b)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                if (Mathf.Abs(a[channel] - b[channel]) > TOLERANCE)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
