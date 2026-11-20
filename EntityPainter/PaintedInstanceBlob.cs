using Unity.Entities;

namespace EntityPainter
{
    /// <summary>
    /// The baked records of one PaintedEntities component, grouped by prototype: <see cref="Counts"/>[i] records of
    /// prototype i follow those of prototype i - 1.
    /// </summary>
    public struct PaintedInstanceBlob
    {
        public BlobArray<PaintedRecord> Records;
        public BlobArray<int> Counts;
    }
}
