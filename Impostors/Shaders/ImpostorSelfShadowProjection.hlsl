#ifndef IMPOSTOR_SELF_SHADOW_PROJECTION
#define IMPOSTOR_SELF_SHADOW_PROJECTION

#if defined(_IMPOSTOR_SELF_SHADOW_VOLUME)
TEXTURE3D(_SelfShadowVolume);
#include "ImpostorSelfShadowSampling.hlsl"
#endif

struct ImpostorSelfShadowVaryings
{
    nointerpolation float3 frames : TEXCOORD8;
    nointerpolation float3 weights : TEXCOORD9;
};

ImpostorSelfShadowVaryings BuildSelfShadowVaryings()
{
    ImpostorSelfShadowVaryings result = (ImpostorSelfShadowVaryings)0;
    #if defined(_IMPOSTOR_SELF_SHADOW_VOLUME)
    float3 direction = TransformWorldToObjectDir(_MainLightPosition.xyz, false);
    float lengthSquared = dot(direction, direction);
    direction *= rsqrt(max(lengthSquared, 0.000001));
    float enabled = lengthSquared > 0.000001 && direction.y > 0 ? 1 : 0;
    direction.y = max(direction.y, 0.00001);
    float2 grid = saturate(HemiEncode(direction)) * (_Frames - 1);
    float2 cell = min(floor(grid), _Frames - 2);
    float2 fraction = grid - cell;
    float first = cell.y * _Frames + cell.x;
    if (fraction.x >= fraction.y)
    {
        result.frames = float3(first, first + 1, first + _Frames + 1);
        result.weights = float3(1 - fraction.x, fraction.x - fraction.y, fraction.y) * enabled;
    }
    else
    {
        result.frames = float3(first, first + _Frames + 1, first + _Frames);
        result.weights = float3(1 - fraction.y, fraction.x, fraction.y - fraction.x) * enabled;
    }
    #endif
    return result;
}

float ImpostorSelfVisibility(float3 positionOS, float3 normalOS, ImpostorSelfShadowVaryings projection)
{
    #if defined(_IMPOSTOR_SELF_SHADOW_VOLUME)
    [branch] if (_SelfShadowStrength <= 0 || ImpostorMainLightShadowStrength() <= 0) return 1;
    [branch] if (_SelfShadowNormalBias > 0)
    {
        float3 voxel = (_BoundsMax.xyz - _BoundsMin.xyz) / max(_SelfShadowInterior.xyz - 1, 1);
        positionOS += normalOS * voxel * _SelfShadowNormalBias;
    }
    float visibility = SampleImpostorSelfShadow(TEXTURE3D_ARGS(_SelfShadowVolume, sampler_LinearClamp),
        positionOS, _BoundsMin.xyz, _BoundsMax.xyz, projection.frames, projection.weights, _Frames);
    // A monotonic quadratic response preserves visibility endpoints and is
    // exactly neutral at 1, without a per-fragment logarithm/exponent path.
    visibility += (1 - _SelfShadowPower) * visibility * (1 - visibility);
    return visibility;
    #else
    return 1;
    #endif
}
#endif
