#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
CBUFFER_START(UnityPerMaterial)
float4 _BaseColor;
CBUFFER_END
struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
Varyings Vertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    output.positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    return output;
}
half4 Fragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    InputData data = (InputData)0;
    data.positionWS = input.positionWS;
    data.normalWS = normalize(input.normalWS);
    data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    SurfaceData surface = (SurfaceData)0;
    surface.albedo = _BaseColor.rgb;
    surface.alpha = 1;
    surface.occlusion = 1;
    data.bakedGI = SampleSH(data.normalWS);
    data.shadowCoord = TransformWorldToShadowCoord(data.positionWS);
    data.shadowMask = 1;
    return UniversalFragmentPBR(data, surface);
}
