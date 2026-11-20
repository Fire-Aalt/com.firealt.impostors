using Unity.Entities;
using Unity.Properties;

namespace Impostors.Data
{
    /// <summary>
    /// Instantiation strips cleanup components, so the <see cref="ImpostorPrototype"/> prefab binding must remain separate
    /// from this allocation binding, which survives destruction.
    /// </summary>
    public struct ImpostorCleanupPrototype : ICleanupSharedComponentData
    {
        [DontCreateProperty]
        public BlobAssetReference<ImpostorPrototypeData> Data;

#if UNITY_EDITOR
        [CreateProperty]
        public ImpostorPrototypeView Prototype => new(Data.IsCreated ? Data.Value : default);
#endif
    }
}
