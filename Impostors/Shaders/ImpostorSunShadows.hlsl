#ifndef IMPOSTOR_SUN_SHADOWS
#define IMPOSTOR_SUN_SHADOWS
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

// Fragment-local context, initialized before UniversalFragmentPBR. The native
// getter still owns cookies, light layers, SSAO and all additional lights.
static half ImpostorBakedSunVisibility = 1;

float ImpostorSunShadowFade(float3 positionWS)
{
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    return GetMainLightShadowFade(positionWS);
#else
    return 1;
#endif
}

half ImpostorMainLightShadow(float4 shadowCoord, float3 positionWS,
    half4 shadowMask, half4 occlusionProbeChannels)
{
#if defined(_IMPOSTOR_SELF_SHADOW_VOLUME)
    // MainLightRealtimeShadow includes strength, but does not include fade.
    float fade = ImpostorSunShadowFade(positionWS);
    [branch] if (fade >= 1) return ImpostorBakedSunVisibility;
    return lerp(MainLightRealtimeShadow(shadowCoord), ImpostorBakedSunVisibility, fade);
#else
    return MainLightShadow(shadowCoord, positionWS, shadowMask, occlusionProbeChannels);
#endif
}

// Shadows.hlsl is already guarded. Only the first RealtimeLights inclusion
// sees the replacement, so there is no recursive macro or forked lighting loop.
#define MainLightShadow ImpostorMainLightShadow
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
#undef MainLightShadow
#endif
