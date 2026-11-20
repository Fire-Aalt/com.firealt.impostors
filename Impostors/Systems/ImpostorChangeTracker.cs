using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Impostors.Data;
using Unity.Entities;
using Unity.Transforms;

namespace Impostors
{
    /// <summary>Detects transform or membership changes of one renderer's prototypes since the last accepted upload.</summary>
    [DisableAutoCreation]
    public partial class ImpostorChangeTracker : SystemBase
    {
        private EntityQuery _query;
        private List<BlobAssetReference<ImpostorPrototypeData>> _prototypes;

        public bool Changed { get; private set; } = true;

        public void Configure(List<BlobAssetReference<ImpostorPrototypeData>> prototypes) => _prototypes = prototypes;

        public void RequireSynchronization() => Changed = true;

        public void AcceptChanges() => Changed = false;

        protected override void OnCreate()
        {
            _query = SystemAPI.QueryBuilder().WithAll<ImpostorPrototype, LocalToWorld>().Build();
        }

        protected override void OnUpdate()
        {
            if (Changed)
            {
                return;
            }

            foreach (BlobAssetReference<ImpostorPrototypeData> prototype in _prototypes)
            {
                _query.SetSharedComponentFilter(new ImpostorPrototype { Data = prototype });
                _query.AddChangedVersionFilter(ComponentType.ReadOnly<LocalToWorld>());
                _query.AddOrderVersionFilter();
                if (!_query.IsEmpty)
                {
                    Changed = true;
                    return;
                }
            }
        }

        [SuppressMessage("FireAlt.Analyzers", "FA1001", Justification = "The owning renderer is disposed with its world "
            + "and unbinds the entities it adopted.")]
        [SuppressMessage("FireAlt.Analyzers", "FA1002", Justification = "The owning renderer waits for its culling jobs "
            + "before it releases the buffers they read.")]
        protected override void OnDestroy() => ImpostorWorldRenderer.ReleaseTracker(this);
    }
}
