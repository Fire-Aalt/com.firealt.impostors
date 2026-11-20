#if UNITY_EDITOR
using Unity.Collections;
using Unity.Mathematics;

namespace Impostors.Data
{
    /// <summary>Inspector view of <see cref="ImpostorPrototypeData"/>, independent of the blob's hot-data-first layout.</summary>
    public readonly struct ImpostorPrototypeView
    {
        public readonly FixedString32Bytes SourcePrefabName;
        public readonly int Index;
        public readonly RenderBounds Bounds;
        public readonly float3 ReferencePoint;
        public readonly float Size;
        public readonly int FirstLod;
        public readonly int LodCount;

        public ImpostorPrototypeView(ImpostorPrototypeData data)
        {
            SourcePrefabName = data.SourcePrefabName;
            Index = data.Index;
            Bounds = data.Bounds;
            ReferencePoint = data.ReferencePoint;
            Size = data.Size;
            FirstLod = data.FirstLod;
            LodCount = data.LodCount;
        }
    }
}
#endif
