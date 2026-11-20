using System;
using System.Linq;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Impostors.Editor
{
    /// <summary>Chooses capture resolutions and estimates the GPU memory of the baked arrays.</summary>
    [NoAutoStaticsCleanup]
    public static class ImpostorCaptureLayout
    {
        public const int MINIMUM_VIEW_RESOLUTION = 32;
        public const int MAXIMUM_ATLAS_RESOLUTION = 4096;
        private const int MINIMUM_FRAMES = 2;
        private const int BLOCK_SIZE = 4;
        private const int COMPRESSED_BYTES_PER_BLOCK = 16;
        private const int COMPRESSED_DEPTH_BYTES_PER_BLOCK = 8;
        private const int COLOR_BYTES_PER_PIXEL = 4;
        private const int DEPTH_BYTES_PER_PIXEL = 2;
        private const string INVALID_LAYOUT =
            "Capture layout requires at least two views per axis and a positive atlas-equivalent resolution.";

        private static readonly int[] ViewResolutions = { 32, 64, 128, 256, 512, 1024, 2048 };

        public static int ResolveViewResolution(int frames, int atlasResolution)
        {
            if (!IsSupportedFrameCount(frames) || atlasResolution < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(atlasResolution), INVALID_LAYOUT);
            }

            int available = Mathf.Max(MINIMUM_VIEW_RESOLUTION, Mathf.Min(atlasResolution, MAXIMUM_ATLAS_RESOLUTION) / frames);
            return Mathf.Max(MINIMUM_VIEW_RESOLUTION, Mathf.NextPowerOfTwo(available + 1) / 2);
        }

        public static int[] ViewResolutionOptions(int frames)
        {
            if (!IsSupportedFrameCount(frames))
            {
                throw new ArgumentOutOfRangeException(nameof(frames), INVALID_LAYOUT);
            }

            return ViewResolutions.Where(value => (long)value * frames <= MAXIMUM_ATLAS_RESOLUTION).ToArray();
        }

        public static ArrayMemory EstimateArrays(int frames, int resolution, bool compressed = true,
            bool halfResolutionSurface = false, bool compactDepth = false, bool adjustmentData = true)
        {
            long slices = (long)frames * frames;
            int surfaceSize = halfResolutionSurface ? resolution / 2 : resolution;
            long depth = compactDepth ? CompactDepthChainBytes(resolution / 2, compressed) : RawDepthChainBytes(resolution);
            long full = ColorChainBytes(resolution, compressed) * slices;
            long surface = ColorChainBytes(surfaceSize, compressed) * slices;
            return new ArrayMemory(full, surface, depth * slices, full, adjustmentData ? full : 0);
        }

        /// <summary>Counts the reflectance array, which a bake drops when its reflectance proves uniform.</summary>
        public static long EstimateArrayBytes(int frames, int resolution, bool compressed = true,
            bool halfResolutionSurface = false, bool compactDepth = false, bool adjustmentData = true)
        {
            return EstimateArrays(frames, resolution, compressed, halfResolutionSurface, compactDepth, adjustmentData).Total;
        }

        private static bool IsSupportedFrameCount(int frames)
        {
            return frames >= MINIMUM_FRAMES && frames <= MAXIMUM_ATLAS_RESOLUTION / MINIMUM_VIEW_RESOLUTION;
        }

        private static long ColorChainBytes(int resolution, bool compressed)
        {
            long bytes = 0;
            for (int size = resolution; size > 0; size >>= 1)
            {
                long blocks = (size + BLOCK_SIZE - 1) / BLOCK_SIZE;
                bytes += compressed ? blocks * blocks * COMPRESSED_BYTES_PER_BLOCK : (long)size * size * COLOR_BYTES_PER_PIXEL;
            }

            return bytes;
        }

        private static long RawDepthChainBytes(int resolution)
        {
            long bytes = 0;
            for (int size = resolution; size > 0; size >>= 1)
            {
                bytes += (long)size * size * DEPTH_BYTES_PER_PIXEL;
            }

            return bytes;
        }

        private static long CompactDepthChainBytes(int resolution, bool compressed)
        {
            long bytes = 0;
            for (int size = resolution; size > 0; size >>= 1)
            {
                long blocks = (size + BLOCK_SIZE - 1) / BLOCK_SIZE;
                bytes += compressed ? blocks * blocks * COMPRESSED_DEPTH_BYTES_PER_BLOCK : (long)size * size;
            }

            return bytes;
        }

        /// <summary>GPU bytes of each baked array, including its mip chain.</summary>
        public readonly struct ArrayMemory
        {
            public readonly long BaseColor;
            public readonly long Surface;
            public readonly long Depth;
            public readonly long Reflectance;
            public readonly long AdjustmentData;

            public ArrayMemory(long baseColor, long surface, long depth, long reflectance, long adjustmentData)
            {
                BaseColor = baseColor;
                Surface = surface;
                Depth = depth;
                Reflectance = reflectance;
                AdjustmentData = adjustmentData;
            }

            public long Total => BaseColor + Surface + Depth + Reflectance + AdjustmentData;
        }
    }
}
