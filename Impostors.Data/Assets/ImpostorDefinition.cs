using UnityEngine;

namespace Impostors.Data
{
    public sealed class ImpostorDefinition : ScriptableObject
    {
        public const int VERSION = 3;

        public int BakeVersion = VERSION;
        public int Frames = 12;
        public int Resolution = 512;
        public int Border = 8;
        public string ContentHash;
        public Vector3 Center;
        public float Radius;
        public Bounds Bounds;
        public Vector2 CrownHeights;
        public Mesh Proxy;
        public Texture2DArray BaseColor;
        public Texture2DArray Surface;
        public Texture2DArray Depth;
        public Texture2DArray Reflectance;
        public Texture2DArray MaterialData;
        public Texture2DArray Emission;
        public Texture2D FrameRectangles;
        public Texture3D SelfShadowVolume;
        public ImpostorSelfShadowLayout SelfShadowLayout;
        public string SelfShadowSourceHash;
        public int DepthFilterVersion;

        [SerializeReference]
        public ImpostorLodData LodData;

#if UNITY_EDITOR
        [SerializeReference]
        public ImpostorBakeData BakeData;

        [Tooltip("Original authoring prefab. Its LODGroup supplies the mesh levels, screen-height thresholds and fade widths.")]
        public GameObject SourcePrefab;
#endif

        public Material Material;

        [Range(0, 1)]
        public float Cutoff = .5f;

        public bool HasValidSelfShadowVolume => SelfShadowVolume && SelfShadowSourceHash == ContentHash
            && SelfShadowLayout.IsValid(SelfShadowVolume, Frames);

        public bool IsValid => BakeVersion == VERSION && Proxy && Material && BaseColor && Surface && HasReflectance && Depth
            && Radius > 0;

        // Reflectance may be a material constant, and adjustment data is optional.
        private bool HasReflectance => Reflectance || HasUniformReflectance;

        private bool HasUniformReflectance => Material.HasFloat(ImpostorShaderProperties.HasUniformReflectance)
            && Material.GetFloat(ImpostorShaderProperties.HasUniformReflectance) > .5f;
    }
}
