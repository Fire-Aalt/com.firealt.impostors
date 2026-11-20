using Unity.Entities;

namespace Impostors.Authoring
{
    /// <summary>Marks an impostor root during baking and lists the authoring hierarchy that baking discards.</summary>
    [BakingType]
    public struct ImpostorBakedDescendant : IBufferElementData
    {
        public Entity Value;
    }
}
