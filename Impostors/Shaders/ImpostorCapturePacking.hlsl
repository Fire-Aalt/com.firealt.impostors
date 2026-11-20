#ifndef IMPOSTOR_CAPTURE_PACKING
#define IMPOSTOR_CAPTURE_PACKING
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
int _ImpostorCaptureMode;
float4 _CaptureCenter;
float3 _CaptureDirection;
float _CaptureRadius;

// Seven editor-only draws capture raw surface values without scene lighting.
float3 ImpostorCapturePayload(float3 baseColor, float3 normalWS, float metallic,
    float3 specular, float smoothness, float occlusion, float3 emission,
    float3 positionWS, bool specularWorkflow, float3 geometricNormalWS)
{
    float3 f0 = specularWorkflow ? specular : lerp(kDielectricSpec.rgb, baseColor, metallic);
    float diffuseWeight = specularWorkflow ? 1-ReflectivitySpecular(specular) : OneMinusReflectivityMetallic(metallic);
    if (_ImpostorCaptureMode == 1) return float3(PackNormalOctQuadEncode(normalize(normalWS))*.5+.5, 0);
    if (_ImpostorCaptureMode == 2) return float3(occlusion, smoothness, diffuseWeight);
    if (_ImpostorCaptureMode == 3) return f0;
    if (_ImpostorCaptureMode == 4) return emission;
    if (_ImpostorCaptureMode == 5) return (dot(positionWS-_CaptureCenter.xyz,_CaptureDirection)/_CaptureRadius*.5+.5).xxx;
    if (_ImpostorCaptureMode == 6) return float3(PackNormalOctQuadEncode(normalize(geometricNormalWS))*.5+.5, specularWorkflow ? -1 : saturate(metallic));
    return baseColor;
}
#endif
