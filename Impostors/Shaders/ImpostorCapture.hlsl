#ifndef IMPOSTOR_CAPTURE_NODE
#define IMPOSTOR_CAPTURE_NODE
#include "ShaderApiReflectionSupport.hlsl"
#if defined(_ImpostorCaptureEnabled) && !defined(SHADERGRAPH_PREVIEW)
#include "ImpostorCapturePacking.hlsl"
#endif

///<funchints>
/// <sg:ProviderKey>Fire.Impostors.Capture.v1</sg:ProviderKey>
/// <sg:DisplayName>Impostor Capture</sg:DisplayName>
/// <sg:SearchCategory>Impostors</sg:SearchCategory>
///</funchints>
///<paramhints name="Normal"><sg:Default>0,0,1</sg:Default></paramhints>
///<paramhints name="BaseColor"><sg:Default>1,1,1</sg:Default></paramhints>
///<paramhints name="Specular"><sg:Default>0.04,0.04,0.04</sg:Default></paramhints>
///<paramhints name="Occlusion"><sg:Default>1</sg:Default></paramhints>
///<paramhints name="Alpha"><sg:Default>1</sg:Default></paramhints>
///<paramhints name="AlphaClipThreshold"><sg:Default>0.5</sg:Default></paramhints>
///<paramhints name="PositionWS"><sg:Referable>Position</sg:Referable><sg:Default>World</sg:Default></paramhints>
///<paramhints name="GeometricNormalWS"><sg:Referable>Normal</sg:Referable><sg:Default>World</sg:Default></paramhints>
///<paramhints name="TangentWS"><sg:Referable>Tangent</sg:Referable><sg:Default>World</sg:Default></paramhints>
///<paramhints name="BitangentWS"><sg:Referable>Bitangent</sg:Referable><sg:Default>World</sg:Default></paramhints>
///<paramhints name="NormalSpace"><sg:Default>0</sg:Default></paramhints>
///<paramhints name="SpecularWorkflow"><sg:Default>false</sg:Default></paramhints>
///<paramhints name="CaptureEnabled"><sg:Default>false</sg:Default></paramhints>
UNITY_EXPORT_REFLECTION
void ImpostorCapture(float3 BaseColor, float3 Normal, float Metallic, float3 Specular,
    float Smoothness, float Occlusion, float3 Emission, float Alpha, float AlphaClipThreshold,
    float3 PositionWS, float3 GeometricNormalWS, float3 TangentWS, float3 BitangentWS,
    out float3 BaseColorOut, out float3 NormalOut, out float MetallicOut, out float3 SpecularOut,
    out float SmoothnessOut, out float OcclusionOut, out float3 EmissionOut,
    out float AlphaOut, out float AlphaClipThresholdOut,
    float NormalSpace, bool SpecularWorkflow, bool CaptureEnabled)
{
    BaseColorOut=BaseColor;NormalOut=Normal;MetallicOut=Metallic;SpecularOut=Specular;
    SmoothnessOut=Smoothness;OcclusionOut=Occlusion;EmissionOut=Emission;
    AlphaOut=Alpha;AlphaClipThresholdOut=AlphaClipThreshold;
#if defined(_ALPHATEST_ON) && !defined(SHADERGRAPH_PREVIEW)
    // URP's forward pass keeps alpha-to-coverage on without MSAA, where it can drop partially transparent pixels
    // that the depth prepass kept. Under depth priming those become holes, so pass a binary alpha unless MSAA is on.
    if (!IsAlphaToMaskAvailable())
        AlphaOut=Alpha>=AlphaClipThreshold?1:0;
#endif
#if defined(_ImpostorCaptureEnabled) && !defined(SHADERGRAPH_PREVIEW)
    if (CaptureEnabled)
    {
        // NormalSpace: 0 = tangent, 1 = object, 2 = world. This is authored
        // alongside the graph's fragment normal space, never inferred by a baker.
        float3 normalWS = NormalSpace < .5
            ? normalize(TangentWS*Normal.x+BitangentWS*Normal.y+GeometricNormalWS*Normal.z)
            : NormalSpace < 1.5 ? TransformObjectToWorldNormal(Normal) : normalize(Normal);
        EmissionOut=ImpostorCapturePayload(BaseColor,normalWS,Metallic,Specular,Smoothness,
            Occlusion,Emission,PositionWS,SpecularWorkflow,GeometricNormalWS);
        // Keep the original Lit pass, with no diffuse, direct specular, or GI.
        // Emission carries raw (including signed/HDR) data to the float target.
        BaseColorOut=0;MetallicOut=1;SpecularOut=0;SmoothnessOut=0;OcclusionOut=0;
    }
#endif
}
#endif
