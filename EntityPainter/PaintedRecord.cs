using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace EntityPainter
{
    /// <summary>
    /// One painted instance in its serialized 18-byte form: a full-precision position, a smallest-three rotation and a
    /// half-precision uniform scale. Records are stored back to back in <see cref="PaintedEntities.Records"/> and in the
    /// baked <see cref="PaintedInstanceBlob"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PaintedRecord
    {
        public const int SIZE = 18;

        private const int COMPONENT_BITS = 10;
        private const int LARGEST_SHIFT = 30;
        private const int FIRST_SHIFT = 20;
        private const uint COMPONENT_MASK = (1u << COMPONENT_BITS) - 1u;
        // Every component except the largest lies within plus or minus 1/sqrt(2).
        private const float COMPONENT_RANGE = 0.70710678f;

        public float3 Position;
        public uint Rotation;
        public ushort Scale;

        public static PaintedRecord Encode(float3 position, quaternion rotation, float scale)
        {
            return new PaintedRecord
            {
                Position = position,
                Rotation = PackRotation(rotation),
                Scale = (ushort)math.f32tof16(scale),
            };
        }

        public readonly quaternion DecodeRotation()
        {
            return UnpackRotation(Rotation);
        }

        public readonly float DecodeScale()
        {
            return math.f16tof32(Scale);
        }

        private static uint PackRotation(quaternion rotation)
        {
            float4 value = math.normalizesafe(rotation.value, quaternion.identity.value);
            float4 magnitude = math.abs(value);
            int largest = 0;
            for (int i = 1; i < 4; i++)
            {
                if (magnitude[i] > magnitude[largest])
                {
                    largest = i;
                }
            }

            // q and -q are the same rotation, so the omitted component is always reconstructed as positive.
            if (value[largest] < 0f)
            {
                value = -value;
            }

            uint packed = (uint)largest << LARGEST_SHIFT;
            int shift = FIRST_SHIFT;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest)
                {
                    continue;
                }

                float normalized = math.saturate(value[i] / COMPONENT_RANGE * 0.5f + 0.5f);
                packed |= (uint)math.round(normalized * COMPONENT_MASK) << shift;
                shift -= COMPONENT_BITS;
            }

            return packed;
        }

        private static quaternion UnpackRotation(uint packed)
        {
            int largest = (int)(packed >> LARGEST_SHIFT);
            float4 value = float4.zero;
            float sum = 0f;
            int shift = FIRST_SHIFT;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest)
                {
                    continue;
                }

                float normalized = ((packed >> shift) & COMPONENT_MASK) / (float)COMPONENT_MASK;
                float component = (normalized * 2f - 1f) * COMPONENT_RANGE;
                value[i] = component;
                sum += component * component;
                shift -= COMPONENT_BITS;
            }

            value[largest] = math.sqrt(math.max(0f, 1f - sum));
            return new quaternion(value);
        }
    }
}
