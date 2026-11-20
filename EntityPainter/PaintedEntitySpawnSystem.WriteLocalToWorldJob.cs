using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;

namespace EntityPainter
{
    public partial class PaintedEntitySpawnSystem
    {
        [BurstCompile]
        private struct WriteLocalToWorldJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            public BlobAssetReference<PaintedInstanceBlob> Instances;
            public int Start;
            public PaintedTemplate Template;

            [NativeDisableParallelForRestriction] public ComponentLookup<LocalToWorld> Transforms;

            public void Execute(int index)
            {
                ref PaintedRecord record = ref Instances.Value.Records[Start + index];
                Transforms[Entities[index]] = new LocalToWorld { Value = Template.ComposeWorld(record) };
            }
        }
    }
}
