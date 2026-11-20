using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;

namespace Impostors.Data
{
    /// <summary>Blob root that binds entities to one registered prototype of a renderer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImpostorPrototypeData
    {
        public int Index;
        public RenderBounds Bounds;
        public float3 ReferencePoint;
        public float Size;
        public int FirstLod;
        public int LodCount;

#if UNITY_EDITOR
        public FixedString32Bytes SourcePrefabName;
#endif
    }
}
