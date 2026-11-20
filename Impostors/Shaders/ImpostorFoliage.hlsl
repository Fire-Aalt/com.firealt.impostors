#ifndef IMPOSTOR_FOLIAGE_INCLUDED
#define IMPOSTOR_FOLIAGE_INCLUDED

// Custom Function nodes of the ImpostorFoliage graph.
// Capture renders the rest pose without light-dependent terms, so both functions do nothing
// while the static _ImpostorCaptureEnabled keyword is on.

// Bends the mesh along a world-space wind direction.
// The offset grows with object-space height above the pivot, so the base stays in place.
void ImpostorFoliageWind_float(float3 PositionOS, float3 DirectionWS, float Strength, float Speed, float Flutter,
    out float3 Out)
{
    Out = PositionOS;
#if !defined(SHADERGRAPH_PREVIEW) && !defined(_ImpostorCaptureEnabled)
    float3 pivotWS = TransformObjectToWorld(float3(0, 0, 0));
    float time = _TimeParameters.x * Speed + dot(pivotWS.xz, float2(0.13, 0.17));
    float sway = sin(time) * 0.7 + sin(time * 2.31 + 1.7) * 0.3;
    float flutter = sin(time * 7.3 + dot(PositionOS, float3(3.1, 1.7, 2.3))) * Flutter;
    float3 directionOS = TransformWorldToObjectDir(DirectionWS);
    Out += directionOS * (max(PositionOS.y, 0) * Strength * (sway + flutter));
#endif
}

// Sun transmission added through Emission.
// It uses the same lobe as the impostor material's Subsurface scattering controls,
// so meshes and impostors match when both use the same Tint, Strength, Spread and Ambient values.
void ImpostorFoliageSubsurface_float(float3 Albedo, float3 PositionWS, float3 NormalWS, float3 ViewDirectionWS,
    float3 Tint, float Strength, float Spread, float Ambient, out float3 Out)
{
    Out = 0;
#if defined(UNIVERSAL_LIGHTING_INCLUDED) && !defined(SHADERGRAPH_PREVIEW) && !defined(_ImpostorCaptureEnabled)
#if defined(_MAIN_LIGHT_SHADOWS_SCREEN) && !defined(_SURFACE_TYPE_TRANSPARENT)
    float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(PositionWS));
#else
    float4 shadowCoord = TransformWorldToShadowCoord(PositionWS);
#endif
    Light light = GetMainLight(shadowCoord, PositionWS, half4(1, 1, 1, 1));
    float3 transmitted = SampleSH(NormalWS) * Ambient;
#if defined(_LIGHT_LAYERS)
    [branch] if (IsMatchingLightLayer(light.layerMask, GetMeshRenderingLayer()))
#endif
    {
        float cosine = saturate(-dot(light.direction, SafeNormalize(ViewDirectionWS)));
        float a2 = Spread * Spread;
        float d = (cosine * a2 - cosine) * cosine + 1;
        float lobe = INV_PI * a2 / (d * d + 1e-7);
        transmitted += light.color * (light.distanceAttenuation * light.shadowAttenuation * lobe * Strength);
    }
    Out = min(Albedo * Tint * transmitted, HALF_MAX);
#endif
}

#endif
