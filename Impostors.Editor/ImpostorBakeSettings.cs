using System;
using Impostors.Data;
using UnityEngine;
using UnityEngine.Serialization;

namespace Impostors.Editor
{
    /// <summary>The LODGroup level a bake captures, whatever the group's level count.</summary>
    public enum ImpostorSourceLod
    {
        [InspectorName("LOD0 (Best Quality)")]
        Lod0 = 0,

        [InspectorName("Last LOD (Best Transition)")]
        LastLod = 1,
    }

    /// <summary>Memory and shading savings applied to the bake.</summary>
    [Flags]
    public enum ImpostorOptimizationFlags
    {
        None = 0,

        /// <summary>Store normals, occlusion and smoothness at half the view resolution.</summary>
        HalfResolutionSurface = 1 << 0,

        /// <summary>Store depth at half the view resolution in BC4 instead of full-resolution R16.</summary>
        CompactDepth = 1 << 1,

        /// <summary>Drop the geometric normal and metalness that Normal Strength and Metallic Influence need.</summary>
        DiscardAdjustmentData = 1 << 2,

        /// <summary>Shade impostors with the main light only, skipping point and spot lights.</summary>
        DisableImpostorAdditionalLights = 1 << 3,
    }

    [CreateAssetMenu(menuName = MENU_NAME)]
    public sealed class ImpostorBakeSettings : ScriptableObject, ISerializationCallbackReceiver
    {
        public const ImpostorOptimizationFlags DEFAULT_OPTIMIZATIONS = ImpostorOptimizationFlags.HalfResolutionSurface
            | ImpostorOptimizationFlags.CompactDepth | ImpostorOptimizationFlags.DiscardAdjustmentData;

        private const string MENU_NAME = "Impostors/Bake Settings";
        private const int ABSENT = -1;

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

        [Tooltip("LODGroup level used as capture geometry. LOD0 has the most detail; the last LOD matches what is on "
            + "screen when the impostor takes over.")]
        public ImpostorSourceLod SourceLod = ImpostorSourceLod.LastLod;

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

        [Tooltip("Half Resolution Surface: normals, occlusion and smoothness at half the view resolution, a quarter of "
            + "that array's memory.\nCompact Depth: depth at half resolution in BC4 instead of full-resolution R16, a "
            + "sixteenth of that array's memory; parallax is less precise on very large close-up impostors.\nDiscard "
            + "Adjustment Data: drop the geometric normal and metalness that Normal Strength and Metallic Influence "
            + "need, saving a full array while both stay at 1.\nDisable Impostor Additional Lights: impostors ignore point "
            + "and spot lights and skip their per-pixel light loop. Mesh LODs stay lit by them.")]
        public ImpostorOptimizationFlags OptimizationFlags = DEFAULT_OPTIMIZATIONS;

        // Settings saved before Optimization Flags stored three toggles. A missing toggle keeps ABSENT.
        [SerializeField, HideInInspector, FormerlySerializedAs("HalfResolutionSurface")]
        private int _legacyHalfResolutionSurface = ABSENT;

        [SerializeField, HideInInspector, FormerlySerializedAs("CompactDepth")]
        private int _legacyCompactDepth = ABSENT;

        [SerializeField, HideInInspector, FormerlySerializedAs("KeepAdjustmentData")]
        private int _legacyKeepAdjustmentData = ABSENT;

        public bool HalfResolutionSurface
        {
            get => Has(ImpostorOptimizationFlags.HalfResolutionSurface);
            set => Set(ImpostorOptimizationFlags.HalfResolutionSurface, value);
        }

        public bool CompactDepth
        {
            get => Has(ImpostorOptimizationFlags.CompactDepth);
            set => Set(ImpostorOptimizationFlags.CompactDepth, value);
        }

        public bool DiscardAdjustmentData
        {
            get => Has(ImpostorOptimizationFlags.DiscardAdjustmentData);
            set => Set(ImpostorOptimizationFlags.DiscardAdjustmentData, value);
        }

        public bool DisableImpostorAdditionalLights
        {
            get => Has(ImpostorOptimizationFlags.DisableImpostorAdditionalLights);
            set => Set(ImpostorOptimizationFlags.DisableImpostorAdditionalLights, value);
        }

        /// <summary>The level of <paramref name="group"/> that <see cref="SourceLod"/> selects.</summary>
        public static int ResolveSourceLod(ImpostorSourceLod sourceLod, LODGroup group)
        {
            return sourceLod == ImpostorSourceLod.Lod0 || !group ? 0 : Mathf.Max(group.lodCount - 1, 0);
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            // Source LOD used to be a level index. Anything past LOD0 now means the last level.
            if ((int)SourceLod > (int)ImpostorSourceLod.LastLod)
            {
                SourceLod = ImpostorSourceLod.LastLod;
            }

            if (_legacyHalfResolutionSurface == ABSENT && _legacyCompactDepth == ABSENT && _legacyKeepAdjustmentData == ABSENT)
            {
                return;
            }

            // Absent toggles take their old defaults: half surface and compact depth on, adjustment data discarded.
            HalfResolutionSurface = _legacyHalfResolutionSurface != 0;
            CompactDepth = _legacyCompactDepth != 0;
            DiscardAdjustmentData = _legacyKeepAdjustmentData != 1;
            _legacyHalfResolutionSurface = _legacyCompactDepth = _legacyKeepAdjustmentData = ABSENT;
        }

        private bool Has(ImpostorOptimizationFlags flag) => (OptimizationFlags & flag) != 0;

        private void Set(ImpostorOptimizationFlags flag, bool value)
        {
            OptimizationFlags = value ? OptimizationFlags | flag : OptimizationFlags & ~flag;
        }
    }
}
