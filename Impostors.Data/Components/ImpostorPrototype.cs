using Unity.Entities;
using Unity.Properties;

namespace Impostors.Data
{
    public struct ImpostorPrototype : ISharedComponentData
    {
        [DontCreateProperty]
        public BlobAssetReference<ImpostorPrototypeData> Data;

#if UNITY_EDITOR
        [CreateProperty]
        public ImpostorPrototypeView Prototype => new(Data.IsCreated ? Data.Value : default);
#endif
    }
}
