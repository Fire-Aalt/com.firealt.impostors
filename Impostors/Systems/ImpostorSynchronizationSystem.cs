using System.Diagnostics.CodeAnalysis;
using Unity.Entities;

namespace Impostors
{
    /// <summary>Uploads each renderer's changed ECS population before presentation culls it.</summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial class ImpostorSynchronizationSystem : SystemBase
    {
        [SuppressMessage("FireAlt.Analyzers", "FA1001", Justification = "GPU slot assignment and spatial regrouping are "
            + "batched structural changes of the renderer-owned population, applied once per changed set.")]
        [SuppressMessage("FireAlt.Analyzers", "FA1002", Justification = "BRG culling reads the uploaded buffers on the "
            + "main thread, so synchronization must finish its packing jobs before presentation.")]
        protected override void OnUpdate() => ImpostorWorldRenderer.SynchronizeWorld(World);
    }
}
