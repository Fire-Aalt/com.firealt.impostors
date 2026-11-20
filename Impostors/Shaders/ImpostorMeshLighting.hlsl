#ifndef IMPOSTOR_MESH_LIGHTING
#define IMPOSTOR_MESH_LIGHTING
// Compatibility for old generated shader-cache assets. New bakes never include
// this file. Keep old caches importable while all mesh lighting stays native.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
half4 ImpostorMeshFragmentPBR(InputData inputData, SurfaceData surfaceData)
{
    return UniversalFragmentPBR(inputData, surfaceData);
}
#endif
