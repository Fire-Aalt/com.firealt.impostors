using System;
using UnityEngine;

namespace Impostors.Data
{
    [Serializable]
    public struct ImpostorSelfShadowLayout
    {
        private const int MINIMUM_FRAMES = 2;
        private const int MAXIMUM_FRAMES = 128;
        private const int MINIMUM_SAMPLES = 4;
        private const int BLOCK_SIZE = 4;
        private const int BRICKS_PER_ATLAS_ROW = 4;
        private const int BRICKS_PER_ATLAS_SLICE = BRICKS_PER_ATLAS_ROW * BRICKS_PER_ATLAS_ROW;
        private const int BC4_BLOCK_BYTES = 8;
        private const string INVALID_BOUNDS = "Self-shadow bounds must be finite and nonempty.";
        private const string BOUNDS_TOO_LARGE = "Self-shadow dimensions are too large.";

        public Vector3Int Interior;
        public Vector3Int Padding;

        public readonly Vector3Int BrickSize => Interior + Padding * 2;

        public static float VoxelSize(ImpostorSelfShadowQuality quality)
        {
            return quality switch
            {
                ImpostorSelfShadowQuality.Low => 1f,
                ImpostorSelfShadowQuality.Medium => .5f,
                ImpostorSelfShadowQuality.High => .25f,
                _ => throw new ArgumentOutOfRangeException(nameof(quality)),
            };
        }

        public static ImpostorSelfShadowLayout ForBounds(Bounds bounds, ImpostorSelfShadowQuality quality)
        {
            float spacing = VoxelSize(quality);
            Vector3 size = bounds.size;
            return new ImpostorSelfShadowLayout
            {
                Interior = new Vector3Int(Samples(size.x, spacing, true), Samples(size.y, spacing, true),
                    Samples(size.z, spacing, false)),
                Padding = new Vector3Int(2, 2, 1),
            };
        }

        public readonly Vector3Int AtlasSize(int frames)
        {
            if (!IsSupportedFrameCount(frames))
            {
                throw new ArgumentOutOfRangeException(nameof(frames));
            }

            Vector3Int brick = BrickSize;
            return new Vector3Int(checked(brick.x * BRICKS_PER_ATLAS_ROW), checked(brick.y * BRICKS_PER_ATLAS_ROW),
                checked(brick.z * AtlasSlices(frames)));
        }

        public readonly long Bytes(int frames, bool compressed = true)
        {
            Vector3Int size = AtlasSize(frames);
            if (!compressed)
            {
                return checked((long)size.x * size.y * size.z);
            }

            long blocksX = (size.x + BLOCK_SIZE - 1) / BLOCK_SIZE;
            long blocksY = (size.y + BLOCK_SIZE - 1) / BLOCK_SIZE;
            return checked(blocksX * blocksY * size.z * BC4_BLOCK_BYTES);
        }

        public readonly bool IsValid(Texture3D texture, int frames)
        {
            if (!texture || !IsSupportedFrameCount(frames) || !HasValidDimensions())
            {
                return false;
            }

            long x = (long)Interior.x + 2L * Padding.x;
            long y = (long)Interior.y + 2L * Padding.y;
            long z = (long)Interior.z + 2L * Padding.z;
            return texture.width == x * BRICKS_PER_ATLAS_ROW && texture.height == y * BRICKS_PER_ATLAS_ROW
                && texture.depth == z * AtlasSlices(frames) && texture.mipmapCount == 1
                && texture.format is TextureFormat.BC4 or TextureFormat.R8;
        }

        public readonly void Configure(Material material)
        {
            material.SetVector(ImpostorShaderProperties.SelfShadowInterior, (Vector3)Interior);
            material.SetVector(ImpostorShaderProperties.SelfShadowPadding, (Vector3)Padding);
        }

        private static int Samples(float extent, float spacing, bool alignBlocks)
        {
            if (!float.IsFinite(extent) || extent <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(extent), INVALID_BOUNDS);
            }

            // Both endpoints are sampled. Round up to preserve the requested spacing.
            // X/Y interiors plus replicated padding must occupy whole BC4 blocks.
            double samples = Math.Max(MINIMUM_SAMPLES, Math.Ceiling((double)extent / spacing) + 1);
            if (samples > int.MaxValue - 7)
            {
                throw new ArgumentOutOfRangeException(nameof(extent), BOUNDS_TOO_LARGE);
            }

            int count = (int)samples;
            return alignBlocks ? (count + BLOCK_SIZE - 1) / BLOCK_SIZE * BLOCK_SIZE : count;
        }

        private static bool IsSupportedFrameCount(int frames) => frames is >= MINIMUM_FRAMES and <= MAXIMUM_FRAMES;

        private static int AtlasSlices(int frames) => (frames * frames + BRICKS_PER_ATLAS_SLICE - 1) / BRICKS_PER_ATLAS_SLICE;

        private readonly bool HasValidDimensions()
        {
            return Interior is { x: >= 2, y: >= 2, z: >= 2 } && Padding is { x: >= 1, y: >= 1, z: >= 1 };
        }
    }
}
