using Impostors.Data;
using UnityEngine;

namespace Impostors.Editor
{
    [CreateAssetMenu(menuName = MENU_NAME)]
    public sealed class ImpostorBakeSettings : ScriptableObject
    {
        private const string MENU_NAME = "Impostors/Bake Settings";

        public int Frames = 12;

        // Keep newly created settings inside the 4096-pixel atlas-equivalent budget.
        public int Resolution = 256;

        [Tooltip("Padding in final pixels on every edge of each captured view. More padding leaves less room for the "
            + "object. It must be less than half the pixels per view.")]
        public int Border = 8;

        [Min(1)]
        [Tooltip("Linear capture-resolution multiplier before downsampling. At 2, each view is captured at twice the "
            + "width and height (four times the pixels). Improves edge quality at increased bake cost. Final texture "
            + "dimensions stay the same.")]
        public int Supersampling = 2;

        [Tooltip("LODGroup level used as capture geometry. Choose LOD0 for the most detail, or the final mesh LOD for "
            + "the closest match at the impostor transition.")]
        public int SourceLod;

        // Kept for existing settings/API compatibility. Lit capture stores authored
        // material occlusion without multiplying in a separate geometric sky bake.
        [HideInInspector]
        public int AmbientSamples = 16;

        [Tooltip("Bake a compressed visibility volume for main-light self-shadowing. Disable to skip the volume bake, "
            + "storage and runtime self-shadow samples. Ambient occlusion and ordinary light shadows remain available.")]
        public bool SelfShadowing = true;

        [Tooltip("Target self-shadow voxel spacing in prefab-local units: Low = 1, Medium = 0.5, High = 0.25. Volume "
            + "dimensions follow the object's bounds, with a small minimum grid and compression alignment. Higher "
            + "quality uses more bake time and memory. Applies on rebake.")]
        public ImpostorSelfShadowQuality SelfShadowQuality = ImpostorSelfShadowQuality.Medium;

        [Tooltip("Store normals, occlusion and smoothness at half the view resolution. Uses a quarter of that array's "
            + "memory.")]
        public bool HalfResolutionSurface = true;

        [Tooltip("Store depth at half the view resolution in BC4 instead of full-resolution R16. Uses a sixteenth of "
            + "that array's memory. Parallax precision is lower on very large close-up impostors.")]
        public bool CompactDepth = true;

        [Tooltip("Keep the baked geometric normal and metalness that Normal Strength and Metallic Influence need. "
            + "Disable to save a full array when both stay at 1.")]
        public bool KeepAdjustmentData;
    }
}
