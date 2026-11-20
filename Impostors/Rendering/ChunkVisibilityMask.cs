using Unity.Mathematics;

namespace Impostors
{
    /// <summary>One bit per row of a chunk with at most <see cref="CAPACITY"/> rows.</summary>
    public struct ChunkVisibilityMask
    {
        public const int CAPACITY = 128;
        private const int WORD_BITS = 64;

        public ulong Lower;
        public ulong Upper;

        public readonly int Count => math.countbits(Lower) + math.countbits(Upper);

        public static ChunkVisibilityMask Full(int count)
        {
            return new ChunkVisibilityMask
            {
                Lower = count >= WORD_BITS ? ulong.MaxValue : (1ul << count) - 1,
                Upper = count == CAPACITY ? ulong.MaxValue : count > WORD_BITS ? (1ul << (count - WORD_BITS)) - 1 : 0,
            };
        }

        public void Set(int bit)
        {
            if (bit < WORD_BITS)
            {
                Lower |= 1ul << bit;
            }
            else
            {
                Upper |= 1ul << (bit - WORD_BITS);
            }
        }
    }
}
