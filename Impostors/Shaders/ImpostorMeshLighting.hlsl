#ifndef IMPOSTOR_MESH_LIGHTING
#define IMPOSTOR_MESH_LIGHTING
// Compatibility for old generated shader-cache assets. New bakes never include
// this file. Keep old caches importable while all mesh lighting stays native.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/MetallicSpecGloss.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/SpecularHighlights.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/EnvironmentReflections.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/ReceiveShadows.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/SurfaceType.hlsl"
half4 ImpostorMeshFragmentPBR(InputData inputData, SurfaceData surfaceData)
{
    return UniversalFragmentPBR(inputData, surfaceData, IsSpecularSetup(), UseSpecularHighlights(), false, false,
        ReceiveShadows(), IsSurfaceTypeTransparent(), UseEnvironmentReflections());
}
#endif
