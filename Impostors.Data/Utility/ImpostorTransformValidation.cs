using Unity.Mathematics;

namespace Impostors.Data
{
    public static class ImpostorTransformValidation
    {
        private const float RELATIVE_TOLERANCE = 0.0001f;

        /// <summary>Accepts a finite affine transform with positive uniform scale and no skew.</summary>
        public static bool IsValid(in float4x4 matrix)
        {
            return IsFiniteAffine(matrix) && HasPositiveUniformScale(matrix.c0.xyz, matrix.c1.xyz, matrix.c2.xyz)
                && IsPositive(math.determinant(matrix));
        }

        private static bool IsFiniteAffine(in float4x4 matrix)
        {
            return math.all(math.isfinite(matrix.c0)) && math.all(math.isfinite(matrix.c1))
                && math.all(math.isfinite(matrix.c2)) && math.all(math.isfinite(matrix.c3))
                && matrix.c0.w == 0 && matrix.c1.w == 0 && matrix.c2.w == 0 && matrix.c3.w == 1;
        }

        private static bool HasPositiveUniformScale(float3 x, float3 y, float3 z)
        {
            float scale = math.length(x);
            float orthogonality = scale * scale * RELATIVE_TOLERANCE;
            float lengthTolerance = scale * RELATIVE_TOLERANCE;
            return scale > 0 && math.abs(math.dot(x, y)) < orthogonality && math.abs(math.dot(x, z)) < orthogonality
                && math.abs(math.dot(y, z)) < orthogonality && math.abs(math.length(y) - scale) < lengthTolerance
                && math.abs(math.length(z) - scale) < lengthTolerance;
        }

        private static bool IsPositive(float determinant) => math.isfinite(determinant) && determinant > 0;
    }
}
