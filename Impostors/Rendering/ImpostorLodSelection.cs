using Impostors.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Impostors
{
    public static class ImpostorLodSelection
    {
        public const float DISTANCE_EPSILON = 0.000001f;
        private const int MAXIMUM_OPACITY = 127;
        private const int LOCAL_INDEX_MASK = 0x00ffffff;
        private const int FADE_SHIFT = 24;
        public const int DEPTH_BUCKET_COUNT = 8;
        /// <summary>Camera draw ranges carry their depth bucket as this rendering layer bit plus the bucket index.</summary>
        public const int DEPTH_BUCKET_LAYER_SHIFT = 24;

        public static float CameraFactor(LODParameters parameters, float bias)
        {
            if (!math.isfinite(bias) || bias <= 0)
            {
                return 0;
            }

            if (parameters.isOrthographic)
            {
                bool validSize = parameters.orthoSize > 0 && math.isfinite(parameters.orthoSize);
                return validSize ? bias / (2 * parameters.orthoSize) : 0;
            }

            float fieldOfView = parameters.fieldOfView;
            bool validFieldOfView = fieldOfView is > 0 and < 180 && math.isfinite(fieldOfView);
            return validFieldOfView ? bias / (2 * math.tan(math.radians(fieldOfView) * 0.5f)) : 0;
        }

        public static LodSelection Select(NativeArray<RenderLod> levels, int first, int count, float height,
            float distanceSquared, float cameraFactor, bool orthographic)
        {
            if (count <= 1 || cameraFactor <= 0)
            {
                return new LodSelection { Level = first, HighDetailOpacity = 1 };
            }

            float numerator = height * cameraFactor;
            float screenHeight = orthographic
                ? numerator
                : numerator * math.rsqrt(math.max(distanceSquared, DISTANCE_EPSILON));
            for (int level = first; level < first + count - 1; level++)
            {
                RenderLod transition = levels[level];
                float relativeHeight = screenHeight / transition.TransitionScreenHeight;
                if (relativeHeight >= 1)
                {
                    return new LodSelection { Level = level, HighDetailOpacity = 1 };
                }

                float fadeEnd = 1 - transition.FadeWidth;
                if (transition.FadeWidth == 0 || relativeHeight <= fadeEnd)
                {
                    continue;
                }

                float opacity = math.saturate((relativeHeight - fadeEnd) / transition.FadeWidth);
                if (opacity > 0)
                {
                    return new LodSelection { Level = level, HighDetailOpacity = opacity };
                }
            }

            return new LodSelection { Level = first + count - 1, HighDetailOpacity = 1 };
        }

        // At 127 the fade equals the dither texture's 255 texels exactly, so URP's crossfade test sits on its boundary and
        // FMA contraction decides it, differently in depth and forward passes. Under depth priming that leaves unshaded
        // pixels. Up to 126 every fade stays at least 1/(127*255) away from all dither values.
        public static byte QuantizeOpacity(float highOpacity)
        {
            return (byte)math.clamp((int)math.ceil(highOpacity * MAXIMUM_OPACITY), 1, MAXIMUM_OPACITY - 1);
        }

        /// <summary>Packs a visible-instance index with the signed crossfade value BRG reads from its top byte.</summary>
        public static int PackIndex(int localIndex, byte magnitude, bool lowDetail)
        {
            int fade = lowDetail ? -magnitude : magnitude;
            return (localIndex & LOCAL_INDEX_MASK) | ((fade & 255) << FADE_SHIFT);
        }

        /// <summary>
        /// Buckets by the float exponent of the squared distance, so each bucket spans half an octave of distance:
        /// below 32 m, then 45, 64, 90, 128, 181, 256 m and beyond.
        /// </summary>
        public static int DepthBucket(float distanceSquared)
        {
            int exponent = ((math.asint(distanceSquared) >> 23) & 255) - 127;
            return math.clamp(exponent - 9, 0, DEPTH_BUCKET_COUNT - 1);
        }

        public static uint DepthBucketLayers(int firstBucket, int endBucket)
        {
            uint bits = 0;
            for (int bucket = firstBucket; bucket < endBucket; bucket++)
            {
                bits |= 1u << (DEPTH_BUCKET_LAYER_SHIFT + bucket);
            }

            return bits;
        }

        public static void DistanceRange(RenderBounds bounds, float3 origin, out float minimumSquared,
            out float maximumSquared)
        {
            float3 offset = math.abs(bounds.Center - origin);
            minimumSquared = math.lengthsq(math.max(offset - bounds.Extents, 0));
            maximumSquared = math.lengthsq(offset + bounds.Extents);
        }
    }
}
