Shader "Impostors/Hemi Octahedral" {
 Properties {
 [HideInInspector] _ProxyBoundsMin("Proxy bounds min",Vector)=(0,0,0,0)
 [HideInInspector] _ProxyBoundsMax("Proxy bounds max",Vector)=(0,0,0,0)
 [HideInInspector] _ProxyDiagonalMin("Proxy diagonal min",Vector)=(0,0,0,0)
 [HideInInspector] _ProxyDiagonalMax("Proxy diagonal max",Vector)=(0,0,0,0)

 [MainColor] _BaseColor("Base Color tint",Color)=(1,1,1,1)
 _BaseColorGain("Base Color gain",Range(0,4))=1
 _AlbedoContrast("Albedo contrast",Range(0,3))=1
 _AlbedoSaturation("Albedo saturation",Range(0,2))=1
 _AlbedoLift("Albedo lift",Range(-0.5,0.5))=0
 _HueVariation("Per-instance hue variation",Color)=(1,0.5,0,0)
 _SpecularTint("Specular tint",Color)=(1,1,1,1)
 _SpecularStrength("Specular strength",Range(0,2))=1
 _MetallicInfluence("Captured metallic influence",Range(0,2))=1
 _NormalStrength("Captured normal strength",Range(0,2))=1
 [ToggleOff(_SPECULARHIGHLIGHTS_OFF)] _SpecularHighlights("Direct specular highlights",Float)=1
 [ToggleOff(_ENVIRONMENTREFLECTIONS_OFF)] _EnvironmentReflections("Environment reflection probes",Float)=1
 _OcclusionStrength("Captured AO strength",Range(0,2))=1
 _OcclusionContrast("AO contrast",Range(0,3))=1
 _OcclusionOffset("AO offset",Range(-1,1))=0
 _SmoothnessScale("Smoothness scale",Range(0,2))=1
 _SmoothnessOffset("Smoothness offset",Range(-1,1))=0
 [HDR] _EmissionTint("Emission tint",Color)=(1,1,1,1)
 _EmissionStrength("Emission strength",Range(0,8))=1
 [Toggle(_IMPOSTOR_SUBSURFACE)] _SubsurfaceScattering("Subsurface scattering",Float)=0
 [HDR] _SubsurfaceScatteringTint("Subsurface tint",Color)=(1,1,1,1)
 _SubsurfaceScatteringStrength("Subsurface strength",Range(0,4))=1
 _SubsurfaceScatteringRoughness("Subsurface spread",Range(0.1,1))=0.7
 _SubsurfaceScatteringAmbient("Subsurface ambient",Range(0,2))=0
 _SelfShadowStrength("Self-shadow strength",Range(0,1))=1
 _SelfShadowNormalBias("Self-shadow normal bias (voxels)",Range(0,2))=0
 _SelfShadowPower("Self-shadow response",Range(0.25,2))=1
 _ShadowReceiverBias("Realtime shadow receiver bias",Range(0,0.2))=0.05
 [HideInInspector] _ImpostorLiveShadows("Casts realtime shadows",Float)=0
 [HideInInspector] _ImpostorStencilRef("Stencil reference",Float)=0
 [HideInInspector] _ImpostorStencilWriteMask("Stencil write mask",Float)=0
 _DetailFullPixels("Full detail above (px)",Range(0,1024))=160
 _DetailReducedPixels("Lowest detail below (px)",Range(0,1024))=64
 [HideInInspector] _SelfShadowVolume("Prefiltered self visibility",3D)="white"{}
 [HideInInspector] _SelfShadowInterior("Self-shadow interior",Vector)=(0,0,0,0)
 [HideInInspector] _SelfShadowPadding("Self-shadow padding",Vector)=(0,0,0,0)
 [HideInInspector] _FrameRectangles("Frame rectangles",2D)="white"{}
 [HideInInspector] _HasFrameRectangles("Has frame rectangles",Float)=0
 [HideInInspector] _BoundsMin("Bounds min",Vector)=(0,0,0,0)
 [HideInInspector] _BoundsMax("Bounds max",Vector)=(0,0,0,0)
 _BaseArray("Base colour",2DArray)=""{} _SurfaceArray("Surface",2DArray)=""{} _DepthArray("Depth",2DArray)=""{}
 _ReflectanceArray("Reflectance",2DArray)=""{} _EmissionArray("Emission",2DArray)=""{}
 [HideInInspector] _MaterialDataArray("Geometric normal, metallic, workflow",2DArray)=""{}
 [HideInInspector] _HasEmission("Has emission",Float)=0
 [HideInInspector] _HasUniformReflectance("Reflectance is uniform",Float)=0
 [HideInInspector] _UniformReflectance("Uniform reflectance",Vector)=(0.04,0.04,0.04,0.96)
 _CaptureCenter("Center",Vector)=(0,0,0,0) _CaptureRadius("Radius",Float)=1 _Frames("Frames",Float)=12 _Border("Border",Float)=0.015625 _Cutoff("Cutoff",Float)=0.5
 }
 SubShader {
 Tags {"RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" "RenderType"="TransparentCutout"}
 Cull Back ZWrite On
 Pass {
 AlphaToMask On
 Name "ForwardLit" Tags{"LightMode"="UniversalForwardOnly"}
 HLSLPROGRAM
 #include_with_pragmas "ImpostorForwardPass.hlsl"
 ENDHLSL
 }
 Pass {
 AlphaToMask On
 Name "ForwardOffscreen" Tags{"LightMode"="ImpostorForwardOffscreen"}
 HLSLPROGRAM
 #define IMPOSTOR_OFFSCREEN_SHADE
 #include_with_pragmas "ImpostorForwardPass.hlsl"
 ENDHLSL
 }
 Pass {AlphaToMask On Name "DepthOnly" Tags{"LightMode"="DepthOnly"} ColorMask R
 Stencil{Ref[_ImpostorStencilRef] WriteMask[_ImpostorStencilWriteMask] Comp Always Pass Replace}
 HLSLPROGRAM
 #pragma target 5.0
 // URP's opaque pass only keeps impostor pixels that this pass wrote, so it must never be skipped while compiling.
 #pragma editor_sync_compilation
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #define IMPOSTOR_URP_PREPASS
 #define IMPOSTOR_DEPTH_ONLY
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 Pass {AlphaToMask On Name "DepthNormals" Tags{"LightMode"="DepthNormalsOnly"}
 Stencil{Ref[_ImpostorStencilRef] WriteMask[_ImpostorStencilWriteMask] Comp Always Pass Replace}
 HLSLPROGRAM
 #pragma target 5.0
 #pragma editor_sync_compilation
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #define IMPOSTOR_URP_PREPASS
 #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
 #define IMPOSTOR_NORMALS
 #pragma shader_feature_local _IMPOSTOR_MATERIAL_ADJUSTMENTS
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 Pass {AlphaToMask On Name "ImpostorDepthOnly" Tags{"LightMode"="ImpostorDepthOnly"} ColorMask R
 Stencil{Ref[_ImpostorStencilRef] WriteMask[_ImpostorStencilWriteMask] Comp Always Pass Replace}
 HLSLPROGRAM
 #pragma target 5.0
 #pragma editor_sync_compilation
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #define IMPOSTOR_PREPASS_TEST
 #define IMPOSTOR_DEPTH_ONLY
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 Pass {AlphaToMask On Name "ImpostorDepthNormals" Tags{"LightMode"="ImpostorDepthNormals"}
 Stencil{Ref[_ImpostorStencilRef] WriteMask[_ImpostorStencilWriteMask] Comp Always Pass Replace}
 HLSLPROGRAM
 #pragma target 5.0
 #pragma editor_sync_compilation
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #define IMPOSTOR_PREPASS_TEST
 #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
 #define IMPOSTOR_NORMALS
 #pragma shader_feature_local _IMPOSTOR_MATERIAL_ADJUSTMENTS
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 Pass {Name "ShadowCaster" Tags{"LightMode"="ShadowCaster"} ColorMask 0
 HLSLPROGRAM
 #pragma target 5.0
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW
 #define IMPOSTOR_SHADOW
 #pragma shader_feature_local _IMPOSTOR_MATERIAL_ADJUSTMENTS
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 Pass {Name "ScenePickingPass" Tags{"LightMode"="Picking"}
 HLSLPROGRAM
 #pragma target 5.0
 #pragma editor_sync_compilation
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #define SCENEPICKINGPASS
 #define IMPOSTOR_DEPTH_ONLY
 float4 _SelectionID;
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 Pass {Name "SceneSelectionPass" Tags{"LightMode"="SceneSelectionPass"}
 HLSLPROGRAM
 #pragma target 5.0
 #pragma editor_sync_compilation
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #define SCENESELECTIONPASS
 #define IMPOSTOR_DEPTH_ONLY
 int _ObjectId;
 int _PassValue;
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 Pass {Name "DebugCapture" Tags{"LightMode"="ImpostorDebugCapture"}
 HLSLPROGRAM
 #pragma target 5.0
 #pragma multi_compile _ LOD_FADE_CROSSFADE
 #pragma vertex Vert
 #pragma fragment Frag
 #pragma multi_compile_instancing
 #define IMPOSTOR_DEBUG_CAPTURE
 #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
 #include "ImpostorCore.hlsl"
 ENDHLSL
 }
 } Fallback Off
 CustomEditor "Impostors.Editor.ImpostorMaterialEditor"
}

