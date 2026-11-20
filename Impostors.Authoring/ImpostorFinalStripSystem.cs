using Unity.Entities;

namespace Impostors.Authoring
{
    /// <summary>Strips again after LinkedEntityGroup resolution and any post-baking system.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateInGroup(typeof(PostBakingSystemGroup), OrderLast = true)]
    [RequireMatchingQueriesForUpdate]
    internal partial class ImpostorFinalStripSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var roots = SystemAPI.QueryBuilder()
                .WithAll<ImpostorBakedDescendant>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build();

            ImpostorEntityStrip.Run(EntityManager, roots, false);
        }
    }
}
