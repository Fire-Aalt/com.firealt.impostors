#ifndef IMPOSTOR_SUN_SHADOWS
#define IMPOSTOR_SUN_SHADOWS
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

// Fragment-local context, initialized before UniversalFragmentPBR. The native
// getter still owns cookies, light layers, SSAO and all additional lights.
static half ImpostorBakedSunVisibility = 1;

// Whether this impostor renders into the main light's shadow map. Defined after the material constants.
bool ImpostorCastsLiveShadows();

// Weight of the baked self-shadow volume. An impostor in the shadow map gets its self-shadowing from the cascades
// and only needs the volume where they fade out. One that casts no live shadows always needs it.
float ImpostorSelfShadowWeight(float3 positionWS, bool receiveShadows)
{
    if (!ImpostorCastsLiveShadows()) return 1;
    return MainLightShadowsAvailable() && receiveShadows ? GetMainLightShadowFade(positionWS) : 1;
}

// Matches URP 17.7's MainLightShadow, which RealtimeLights.hlsl calls from GetMainLight.
half ImpostorMainLightShadow(float4 shadowCoord, float3 positionWS, half4 shadowMask, half4 occlusionProbeChannels,
    bool receiveShadows, bool isSurfaceTypeTransparent)
{
#if defined(_IMPOSTOR_SELF_SHADOW_VOLUME)
    // Without the impostor in the shadow map, other casters still shadow it and the volume adds its own shadowing.
    [branch] if (!ImpostorCastsLiveShadows())
        return MainLightShadow(shadowCoord, positionWS, shadowMask, occlusionProbeChannels, receiveShadows,
            isSurfaceTypeTransparent) * ImpostorBakedSunVisibility;
    // The realtime sample includes shadow strength, but not the distance fade.
    float fade = ImpostorSelfShadowWeight(positionWS, receiveShadows);
    [branch] if (fade >= 1) return ImpostorBakedSunVisibility;
    half realtime = SampleMainLightRealtimeShadow(shadowCoord, isSurfaceTypeTransparent);
    return lerp(realtime, ImpostorBakedSunVisibility, fade);
#else
    return MainLightShadow(shadowCoord, positionWS, shadowMask, occlusionProbeChannels, receiveShadows,
        isSurfaceTypeTransparent);
#endif
}

// Shadows.hlsl is already guarded. Only the first RealtimeLights inclusion
// sees the replacement, so there is no recursive macro or forked lighting loop.
#define MainLightShadow ImpostorMainLightShadow
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
#undef MainLightShadow
#endif
