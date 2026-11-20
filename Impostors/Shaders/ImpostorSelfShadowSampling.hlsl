#ifndef IMPOSTOR_SELF_SHADOW_SAMPLING
#define IMPOSTOR_SELF_SHADOW_SAMPLING

// Shared by real meshes and impostors. Dimensions always come from the baked layout.
float SampleImpostorSelfShadow(TEXTURE3D_PARAM(volume, volumeSampler), float3 positionOS,
    float3 boundsMin, float3 boundsMax, float3 frames, float3 weights, float framesPerAxis)
{
    float3 position = (positionOS - boundsMin) / max(boundsMax - boundsMin, 0.0001);
    if (any(position < 0) || any(position > 1)) return 1;
    float3 interior = _SelfShadowInterior.xyz;
    float3 padding = _SelfShadowPadding.xyz;
    float3 brickSize = interior + 2 * padding;
    float3 atlasSize = brickSize * float3(4, 4, ceil(framesPerAxis * framesPerAxis / 16));
    float visibility = 1 - dot(weights, float3(1, 1, 1));
    [unroll] for (int i = 0; i < 3; i++)
    {
        float frame = frames[i];
        float3 brick = float3(fmod(frame, 4), fmod(floor(frame / 4), 4), floor(frame / 16));
        float3 uvw = (brick * brickSize + padding + 0.5 + position * (interior - 1)) / atlasSize;
        visibility += SAMPLE_TEXTURE3D_LOD(volume, volumeSampler, uvw, 0).r * weights[i];
    }
    return saturate(visibility);
}
#endif
