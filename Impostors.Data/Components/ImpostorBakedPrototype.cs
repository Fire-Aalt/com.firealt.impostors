using Unity.Entities;

namespace Impostors.Data
{
    /// <summary>
    /// Written by SubScene and GetEntity() baking. Adoption resolves it to a renderer's <see cref="ImpostorPrototype"/>.
    /// </summary>
    public struct ImpostorBakedPrototype : ISharedComponentData
    {
        public UnityObjectRef<ImpostorDefinition> Definition;
        public int Layer;
        public bool RealtimeShadows;
    }
}
