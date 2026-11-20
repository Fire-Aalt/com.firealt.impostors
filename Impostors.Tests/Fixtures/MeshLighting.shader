Shader "Hidden/Impostors/Tests/MeshLighting"
{
    Properties
    {
        _BaseColor("Color", Color) = (0.35,0.55,0.25,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass { Name "ForwardLit" Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma multi_compile_instancing
            #pragma editor_sync_compilation
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma fragment Fragment
            #include "MeshLightingInput.hlsl"
            ENDHLSL
        }
        Pass { Name "SceneSelectionPass" Tags { "LightMode"="SceneSelectionPass" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma multi_compile_instancing
            #pragma editor_sync_compilation
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma fragment Selection
            #define SCENESELECTIONPASS
            int _ObjectId, _PassValue;
            #include "MeshLightingInput.hlsl"
            half4 Selection(Varyings input) : SV_Target { UNITY_SETUP_INSTANCE_ID(input); return half4(_ObjectId, _PassValue, 1, 1); }
            ENDHLSL
        }
        Pass { Name "ScenePickingPass" Tags { "LightMode"="Picking" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma multi_compile_instancing
            #pragma editor_sync_compilation
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma fragment Picking
            #define SCENEPICKINGPASS
            float4 _SelectionID;
            #include "MeshLightingInput.hlsl"
            half4 Picking(Varyings input) : SV_Target { UNITY_SETUP_INSTANCE_ID(input); return unity_SelectionID; }
            ENDHLSL
        }
    }
}
