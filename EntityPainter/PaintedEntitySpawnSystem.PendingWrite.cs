using Unity.Collections;
using Unity.Entities;

namespace EntityPainter
{
    public partial class PaintedEntitySpawnSystem
    {
        /// <summary>One instantiated prototype range whose placements are written once all structural changes are done.</summary>
        private struct PendingWrite
        {
            public NativeArray<Entity> Entities;
            public BlobAssetReference<PaintedInstanceBlob> Instances;
            public int Start;
            public PaintedTemplate Template;
        }
    }
}
