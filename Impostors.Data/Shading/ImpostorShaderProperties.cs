using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Impostors.Data
{
    [NoAutoStaticsCleanup]
    public static class ImpostorShaderProperties
    {
        public const string MATERIAL_DATA_ARRAY_NAME = "_MaterialDataArray";
        public static readonly int MaterialDataArray = Shader.PropertyToID(MATERIAL_DATA_ARRAY_NAME);

        public const string NORMAL_STRENGTH_NAME = "_NormalStrength";
        public static readonly int NormalStrength = Shader.PropertyToID(NORMAL_STRENGTH_NAME);

        public const string METALLIC_INFLUENCE_NAME = "_MetallicInfluence";
        public static readonly int MetallicInfluence = Shader.PropertyToID(METALLIC_INFLUENCE_NAME);

        public const string ALBEDO_CONTRAST_NAME = "_AlbedoContrast";
        public static readonly int AlbedoContrast = Shader.PropertyToID(ALBEDO_CONTRAST_NAME);

        public const string ALBEDO_SATURATION_NAME = "_AlbedoSaturation";
        public static readonly int AlbedoSaturation = Shader.PropertyToID(ALBEDO_SATURATION_NAME);

        public const string ALBEDO_LIFT_NAME = "_AlbedoLift";
        public static readonly int AlbedoLift = Shader.PropertyToID(ALBEDO_LIFT_NAME);

        public const string HUE_VARIATION_NAME = "_HueVariation";
        public static readonly int HueVariation = Shader.PropertyToID(HUE_VARIATION_NAME);

        public const string SPECULAR_TINT_NAME = "_SpecularTint";
        public static readonly int SpecularTint = Shader.PropertyToID(SPECULAR_TINT_NAME);

        public const string SPECULAR_STRENGTH_NAME = "_SpecularStrength";
        public static readonly int SpecularStrength = Shader.PropertyToID(SPECULAR_STRENGTH_NAME);

        public const string SPECULAR_HIGHLIGHTS_NAME = "_SpecularHighlights";
        public static readonly int SpecularHighlights = Shader.PropertyToID(SPECULAR_HIGHLIGHTS_NAME);

        public const string ENVIRONMENT_REFLECTIONS_NAME = "_EnvironmentReflections";
        public static readonly int EnvironmentReflections = Shader.PropertyToID(ENVIRONMENT_REFLECTIONS_NAME);

        public const string SMOOTHNESS_OFFSET_NAME = "_SmoothnessOffset";
        public static readonly int SmoothnessOffset = Shader.PropertyToID(SMOOTHNESS_OFFSET_NAME);

        public const string OCCLUSION_CONTRAST_NAME = "_OcclusionContrast";
        public static readonly int OcclusionContrast = Shader.PropertyToID(OCCLUSION_CONTRAST_NAME);

        public const string OCCLUSION_OFFSET_NAME = "_OcclusionOffset";
        public static readonly int OcclusionOffset = Shader.PropertyToID(OCCLUSION_OFFSET_NAME);

        public const string EMISSION_TINT_NAME = "_EmissionTint";
        public static readonly int EmissionTint = Shader.PropertyToID(EMISSION_TINT_NAME);

        public const string EMISSION_STRENGTH_NAME = "_EmissionStrength";
        public static readonly int EmissionStrength = Shader.PropertyToID(EMISSION_STRENGTH_NAME);

        public const string BASE_COLOR_GAIN_NAME = "_BaseColorGain";
        public static readonly int BaseColorGain = Shader.PropertyToID(BASE_COLOR_GAIN_NAME);

        public const string OCCLUSION_STRENGTH_NAME = "_OcclusionStrength";
        public static readonly int OcclusionStrength = Shader.PropertyToID(OCCLUSION_STRENGTH_NAME);

        public const string SMOOTHNESS_SCALE_NAME = "_SmoothnessScale";
        public static readonly int SmoothnessScale = Shader.PropertyToID(SMOOTHNESS_SCALE_NAME);

        public const string SELF_SHADOW_NORMAL_BIAS_NAME = "_SelfShadowNormalBias";
        public static readonly int SelfShadowNormalBias = Shader.PropertyToID(SELF_SHADOW_NORMAL_BIAS_NAME);

        public const string SELF_SHADOW_POWER_NAME = "_SelfShadowPower";
        public static readonly int SelfShadowPower = Shader.PropertyToID(SELF_SHADOW_POWER_NAME);

        public const string SUBSURFACE_SCATTERING_NAME = "_SubsurfaceScattering";
        public static readonly int SubsurfaceScattering = Shader.PropertyToID(SUBSURFACE_SCATTERING_NAME);

        public const string SUBSURFACE_SCATTERING_TINT_NAME = "_SubsurfaceScatteringTint";
        public static readonly int SubsurfaceScatteringTint = Shader.PropertyToID(SUBSURFACE_SCATTERING_TINT_NAME);

        public const string SUBSURFACE_SCATTERING_STRENGTH_NAME = "_SubsurfaceScatteringStrength";
        public static readonly int SubsurfaceScatteringStrength = Shader.PropertyToID(SUBSURFACE_SCATTERING_STRENGTH_NAME);

        public const string SUBSURFACE_SCATTERING_ROUGHNESS_NAME = "_SubsurfaceScatteringRoughness";
        public static readonly int SubsurfaceScatteringRoughness = Shader.PropertyToID(SUBSURFACE_SCATTERING_ROUGHNESS_NAME);

        public const string SUBSURFACE_SCATTERING_AMBIENT_NAME = "_SubsurfaceScatteringAmbient";
        public static readonly int SubsurfaceScatteringAmbient = Shader.PropertyToID(SUBSURFACE_SCATTERING_AMBIENT_NAME);

        public const string SHADOW_RECEIVER_BIAS_NAME = "_ShadowReceiverBias";
        public static readonly int ShadowReceiverBias = Shader.PropertyToID(SHADOW_RECEIVER_BIAS_NAME);

        public const string DETAIL_FULL_PIXELS_NAME = "_DetailFullPixels";
        public static readonly int DetailFullPixels = Shader.PropertyToID(DETAIL_FULL_PIXELS_NAME);

        public const string DETAIL_REDUCED_PIXELS_NAME = "_DetailReducedPixels";
        public static readonly int DetailReducedPixels = Shader.PropertyToID(DETAIL_REDUCED_PIXELS_NAME);

        public const string METALLIC_NAME = "_Metallic";
        public static readonly int Metallic = Shader.PropertyToID(METALLIC_NAME);

        public const string SMOOTHNESS_NAME = "_Smoothness";
        public static readonly int Smoothness = Shader.PropertyToID(SMOOTHNESS_NAME);

        public const string REFLECTANCE_ARRAY_NAME = "_ReflectanceArray";
        public static readonly int ReflectanceArray = Shader.PropertyToID(REFLECTANCE_ARRAY_NAME);

        public const string EMISSION_ARRAY_NAME = "_EmissionArray";
        public static readonly int EmissionArray = Shader.PropertyToID(EMISSION_ARRAY_NAME);

        public const string HAS_EMISSION_NAME = "_HasEmission";
        public static readonly int HasEmission = Shader.PropertyToID(HAS_EMISSION_NAME);

        public const string HAS_UNIFORM_REFLECTANCE_NAME = "_HasUniformReflectance";
        public static readonly int HasUniformReflectance = Shader.PropertyToID(HAS_UNIFORM_REFLECTANCE_NAME);

        public const string UNIFORM_REFLECTANCE_NAME = "_UniformReflectance";
        public static readonly int UniformReflectance = Shader.PropertyToID(UNIFORM_REFLECTANCE_NAME);

        public const string IMPOSTOR_CAPTURE_MODE_NAME = "_ImpostorCaptureMode";
        public static readonly int ImpostorCaptureMode = Shader.PropertyToID(IMPOSTOR_CAPTURE_MODE_NAME);

        public const string TIME_PARAMETERS_NAME = "_TimeParameters";
        public static readonly int TimeParameters = Shader.PropertyToID(TIME_PARAMETERS_NAME);

        public const string TIME_NAME = "_Time";
        public static readonly int Time = Shader.PropertyToID(TIME_NAME);

        public const string PROXY_BOUNDS_MIN_NAME = "_ProxyBoundsMin";
        public static readonly int ProxyBoundsMin = Shader.PropertyToID(PROXY_BOUNDS_MIN_NAME);

        public const string PROXY_BOUNDS_MAX_NAME = "_ProxyBoundsMax";
        public static readonly int ProxyBoundsMax = Shader.PropertyToID(PROXY_BOUNDS_MAX_NAME);

        public const string PROXY_DIAGONAL_MIN_NAME = "_ProxyDiagonalMin";
        public static readonly int ProxyDiagonalMin = Shader.PropertyToID(PROXY_DIAGONAL_MIN_NAME);

        public const string PROXY_DIAGONAL_MAX_NAME = "_ProxyDiagonalMax";
        public static readonly int ProxyDiagonalMax = Shader.PropertyToID(PROXY_DIAGONAL_MAX_NAME);

        /// <summary>Set by the renderer: 1 when its impostors render into the main light's shadow map.</summary>
        public const string LIVE_SHADOWS_NAME = "_ImpostorLiveShadows";
        public static readonly int LiveShadows = Shader.PropertyToID(LIVE_SHADOWS_NAME);

        public const string STENCIL_REF_NAME = "_ImpostorStencilRef";
        public static readonly int StencilRef = Shader.PropertyToID(STENCIL_REF_NAME);

        public const string STENCIL_WRITE_MASK_NAME = "_ImpostorStencilWriteMask";
        public static readonly int StencilWriteMask = Shader.PropertyToID(STENCIL_WRITE_MASK_NAME);

        public const string ADDITIONAL_LIGHTS_NAME = "_ImpostorAdditionalLights";
        public static readonly int AdditionalLights = Shader.PropertyToID(ADDITIONAL_LIGHTS_NAME);

        public const string SELF_SHADOW_STRENGTH_NAME = "_SelfShadowStrength";
        public static readonly int SelfShadowStrength = Shader.PropertyToID(SELF_SHADOW_STRENGTH_NAME);

        public const string BEND_POWER_NAME = "_Bend_Powerness";
        public static readonly int BendPower = Shader.PropertyToID(BEND_POWER_NAME);

        public const string WIND_SPEED_NAME = "_Wind_Speed";
        public static readonly int WindSpeed = Shader.PropertyToID(WIND_SPEED_NAME);

        public const string WIND_STRENGTH_NAME = "_WindStrength";
        public static readonly int WindStrength = Shader.PropertyToID(WIND_STRENGTH_NAME);

        public const string ALPHA_CLIP_NAME = "_AlphaClip";
        public static readonly int AlphaClip = Shader.PropertyToID(ALPHA_CLIP_NAME);

        public const string AMBIENT_DIRECTIONS_NAME = "_AmbientDirections";
        public static readonly int AmbientDirections = Shader.PropertyToID(AMBIENT_DIRECTIONS_NAME);

        public const string AMBIENT_SAMPLES_NAME = "_AmbientSamples";
        public static readonly int AmbientSamples = Shader.PropertyToID(AMBIENT_SAMPLES_NAME);

        public const string BAKE_DIRECTION_WS_NAME = "_BakeDirectionWS";
        public static readonly int BakeDirectionWS = Shader.PropertyToID(BAKE_DIRECTION_WS_NAME);

        public const string BASE_ARRAY_NAME = "_BaseArray";
        public static readonly int BaseArray = Shader.PropertyToID(BASE_ARRAY_NAME);

        public const string BASE_COLOR_NAME = "_BaseColor";
        public static readonly int BaseColor = Shader.PropertyToID(BASE_COLOR_NAME);

        public const string BORDER_NAME = "_Border";
        public static readonly int Border = Shader.PropertyToID(BORDER_NAME);

        public const string BOUNDS_MAX_NAME = "_BoundsMax";
        public static readonly int BoundsMax = Shader.PropertyToID(BOUNDS_MAX_NAME);

        public const string BOUNDS_MIN_NAME = "_BoundsMin";
        public static readonly int BoundsMin = Shader.PropertyToID(BOUNDS_MIN_NAME);

        public const string BOUNDS_SIZE_NAME = "_BoundsSize";
        public static readonly int BoundsSize = Shader.PropertyToID(BOUNDS_SIZE_NAME);

        public const string BRICK_SIZE_NAME = "_BrickSize";
        public static readonly int BrickSize = Shader.PropertyToID(BRICK_SIZE_NAME);

        public const string CAPTURE_CENTER_NAME = "_CaptureCenter";
        public static readonly int CaptureCenter = Shader.PropertyToID(CAPTURE_CENTER_NAME);

        public const string CAPTURE_DIRECTION_NAME = "_CaptureDirection";
        public static readonly int CaptureDirection = Shader.PropertyToID(CAPTURE_DIRECTION_NAME);

        public const string CAPTURE_RADIUS_NAME = "_CaptureRadius";
        public static readonly int CaptureRadius = Shader.PropertyToID(CAPTURE_RADIUS_NAME);

        public const string CAPTURE_RESOLUTION_NAME = "_CaptureResolution";
        public static readonly int CaptureResolution = Shader.PropertyToID(CAPTURE_RESOLUTION_NAME);

        public const string CUTOFF_NAME = "_Cutoff";
        public static readonly int Cutoff = Shader.PropertyToID(CUTOFF_NAME);

        public const string DEPTH_ARRAY_NAME = "_DepthArray";
        public static readonly int DepthArray = Shader.PropertyToID(DEPTH_ARRAY_NAME);

        public const string FRAME_NAME = "_Frame";
        public static readonly int Frame = Shader.PropertyToID(FRAME_NAME);

        public const string FRAME_RECTANGLES_NAME = "_FrameRectangles";
        public static readonly int FrameRectangles = Shader.PropertyToID(FRAME_RECTANGLES_NAME);

        public const string FRAMES_NAME = "_Frames";
        public static readonly int Frames = Shader.PropertyToID(FRAMES_NAME);

        public const string HAS_FRAME_RECTANGLES_NAME = "_HasFrameRectangles";
        public static readonly int HasFrameRectangles = Shader.PropertyToID(HAS_FRAME_RECTANGLES_NAME);

        public const string ENTITY_ID_NAME = "unity_EntityId";
        public static readonly int EntityId = Shader.PropertyToID(ENTITY_ID_NAME);

        public const string INTERIOR_SIZE_NAME = "_InteriorSize";
        public static readonly int InteriorSize = Shader.PropertyToID(INTERIOR_SIZE_NAME);

        public const string OCCLUSION_MAP_NAME = "_OcclusionMap";
        public static readonly int OcclusionMap = Shader.PropertyToID(OCCLUSION_MAP_NAME);

        public const string SELF_SHADOW_VOLUME_NAME = "_SelfShadowVolume";
        public static readonly int SelfShadowVolume = Shader.PropertyToID(SELF_SHADOW_VOLUME_NAME);

        public const string SELF_SHADOW_INTERIOR_NAME = "_SelfShadowInterior";
        public static readonly int SelfShadowInterior = Shader.PropertyToID(SELF_SHADOW_INTERIOR_NAME);

        public const string SELF_SHADOW_PADDING_NAME = "_SelfShadowPadding";
        public static readonly int SelfShadowPadding = Shader.PropertyToID(SELF_SHADOW_PADDING_NAME);

        public const string SUBSURFACE_COLOR_NAME = "_SubsurfaceColor";
        public static readonly int SubsurfaceColor = Shader.PropertyToID(SUBSURFACE_COLOR_NAME);

        public const string SUBSURFACE_INDIRECT_NAME = "_SubsurfaceIndirect";
        public static readonly int SubsurfaceIndirect = Shader.PropertyToID(SUBSURFACE_INDIRECT_NAME);

        public const string SUBSURFACE_KW_TOGGLE_NAME = "_SubsurfaceKwToggle";
        public static readonly int SubsurfaceKwToggle = Shader.PropertyToID(SUBSURFACE_KW_TOGGLE_NAME);

        public const string SUBSURFACE_TEX_NAME = "_SubsurfaceTex";
        public static readonly int SubsurfaceTex = Shader.PropertyToID(SUBSURFACE_TEX_NAME);

        public const string SURFACE_ARRAY_NAME = "_SurfaceArray";
        public static readonly int SurfaceArray = Shader.PropertyToID(SURFACE_ARRAY_NAME);

        public const string VISIBILITY_VOLUME_NAME = "_VisibilityVolume";
        public static readonly int VisibilityVolume = Shader.PropertyToID(VISIBILITY_VOLUME_NAME);

        public const string VOXEL_WORLD_SIZE_NAME = "_VoxelWorldSize";
        public static readonly int VoxelWorldSize = Shader.PropertyToID(VOXEL_WORLD_SIZE_NAME);

        public const string OBJECT_TO_WORLD_NAME = "unity_ObjectToWorld";
        public static readonly int ObjectToWorld = Shader.PropertyToID(OBJECT_TO_WORLD_NAME);

        public const string WORLD_TO_OBJECT_NAME = "unity_WorldToObject";
        public static readonly int WorldToObject = Shader.PropertyToID(WORLD_TO_OBJECT_NAME);
    }
}
