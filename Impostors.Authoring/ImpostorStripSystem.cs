using Unity.Entities;

namespace Impostors.Authoring
{
    /// <summary>Strips before companion GameObjects are created, so stripped managed components never spawn one.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateInGroup(typeof(BakingSystemGroup), OrderLast = true)]
    [RequireMatchingQueriesForUpdate]
    internal partial class ImpostorStripSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var roots = SystemAPI.QueryBuilder()
                .WithAll<ImpostorBakedDescendant>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build();

            ImpostorEntityStrip.Run(EntityManager, roots, true);
        }
    }
}
