using UnityEngine;

namespace Impostors.Editor
{
    public static partial class ImpostorBaker
    {
        private const int COVERAGE_SEARCH_ITERATIONS = 16;
        private const float MAXIMUM_COVERAGE_SCALE = 8;
        private const float ALPHA_TEST_THRESHOLD = 0.5f;

        /// <summary>
        /// Searches the alpha scale that keeps the source alpha-test area at each mip.
        /// Colour, normals and depth are filtered with the original coverage, before scaling.
        /// </summary>
        public static void PreserveCoverage(Color[] colors, float targetCoverage)
        {
            float low = 0;
            float high = MAXIMUM_COVERAGE_SCALE;
            for (int iteration = 0; iteration < COVERAGE_SEARCH_ITERATIONS; iteration++)
            {
                float scale = (low + high) * 0.5f;
                if (Coverage(colors, scale) < targetCoverage)
                {
                    low = scale;
                }
                else
                {
                    high = scale;
                }
            }

            float multiplier = (low + high) * 0.5f;
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i].a = Mathf.Clamp01(colors[i].a * multiplier);
            }
        }

        private static float Coverage(Color[] colors, float alphaScale)
        {
            int covered = 0;
            foreach (Color color in colors)
            {
                covered += color.a * alphaScale >= ALPHA_TEST_THRESHOLD ? 1 : 0;
            }

            return covered / (float)colors.Length;
        }
    }
}
