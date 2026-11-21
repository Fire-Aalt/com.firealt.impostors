#ifndef IMPOSTOR_CORE
#define IMPOSTOR_CORE
#define _ALPHATEST_ON 1
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
// Forward+ defines _ADDITIONAL_LIGHTS even when the camera sees only the sun.
// Keep its main-light and reflection-probe paths, but omit the empty light loop.
#if defined(_IMPOSTOR_NO_ADDITIONAL_LIGHTS)
#undef _ADDITIONAL_LIGHTS
#undef _ADDITIONAL_LIGHTS_VERTEX
#endif
#define _SPECULAR_SETUP 1
#include "ImpostorSunShadows.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "ImpostorMainLight.hlsl"
#if defined(LOD_FADE_CROSSFADE)
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif
CBUFFER_START(UnityPerMaterial)
float4 _CaptureCenter, _BoundsMin, _BoundsMax;
float4 _ProxyBoundsMin, _ProxyBoundsMax, _ProxyDiagonalMin, _ProxyDiagonalMax;
float _CaptureRadius,_Frames,_Border,_Cutoff;
float _HasEmission;
float _HasUniformReflectance;
float4 _UniformReflectance;
float _HasFrameRectangles;
float _SelfShadowStrength;
float4 _BaseColor;
float _BaseColorGain, _OcclusionStrength, _SmoothnessScale;
float _AlbedoContrast, _AlbedoSaturation, _AlbedoLift;
float4 _HueVariation, _SpecularTint, _EmissionTint;
float _SpecularStrength, _SmoothnessOffset, _OcclusionContrast, _OcclusionOffset, _EmissionStrength;
float _NormalStrength, _MetallicInfluence;
float _SelfShadowNormalBias, _SelfShadowPower;
float _DetailFullPixels, _DetailReducedPixels;
float4 _SelfShadowInterior, _SelfShadowPadding;
float4 _SubsurfaceScatteringTint;
float _SubsurfaceScatteringStrength, _SubsurfaceScatteringRoughness, _SubsurfaceScatteringAmbient;
float _ShadowReceiverBias;
float _ImpostorLiveShadows;
CBUFFER_END
bool ImpostorCastsLiveShadows() { return _ImpostorLiveShadows > .5; }
TEXTURE2D(_FrameRectangles);SAMPLER(sampler_FrameRectangles);
TEXTURE2D_ARRAY(_BaseArray);SAMPLER(sampler_BaseArray);
TEXTURE2D_ARRAY(_SurfaceArray);SAMPLER(sampler_SurfaceArray);
TEXTURE2D_ARRAY(_ReflectanceArray);SAMPLER(sampler_ReflectanceArray);
#if defined(_IMPOSTOR_MATERIAL_ADJUSTMENTS)
TEXTURE2D_ARRAY(_MaterialDataArray);SAMPLER(sampler_MaterialDataArray);
#endif
TEXTURE2D_ARRAY(_EmissionArray);SAMPLER(sampler_EmissionArray);
TEXTURE2D_ARRAY(_DepthArray);SAMPLER(sampler_DepthArray);

float3 _LightDirection,_LightPosition;
float4x4 _BakeViewProjection;
float3 _BakeDirectionWS;
float2 HemiEncode(float3 d){d/=dot(abs(d),1);return float2(d.x+d.z,d.x-d.z)*.5+.5;}
float3 HemiDecode(float2 uv){float2 p=uv*2-1;float x=(p.x+p.y)*.5,z=(p.x-p.y)*.5;return normalize(float3(x,1-abs(x)-abs(z),z));}
void FrameBasis(float3 d,out float3 r,out float3 u){r=normalize(cross(d,abs(d.y)>.999?float3(0,0,1):float3(0,1,0)));u=cross(r,d);}
#include "ImpostorProxyVolume.hlsl"
float3 DecodeNormal(float2 uv){float2 p=uv*2-1;float3 n=float3(p,1-abs(p.x)-abs(p.y));float t=saturate(-n.z);n.xy+=float2(n.x>=0?-t:t,n.y>=0?-t:t);return normalize(n);}
#if !defined(IMPOSTOR_DEBUG_CAPTURE) && !defined(IMPOSTOR_SHADOW) && !defined(IMPOSTOR_DEPTH_ONLY) && !defined(IMPOSTOR_NORMALS)
#define IMPOSTOR_FORWARD_SELF_SHADOW
#include "ImpostorSelfShadowProjection.hlsl"
#endif
// Picking and selection render into tiny targets, where a size-based detail level would be meaningless.
#if !defined(IMPOSTOR_SHADOW) && !defined(IMPOSTOR_DEBUG_CAPTURE) && !defined(SCENEPICKINGPASS) && !defined(SCENESELECTIONPASS)
#define IMPOSTOR_CAMERA_DETAIL
#endif
struct Attributes{float3 positionOS:POSITION;UNITY_VERTEX_INPUT_INSTANCE_ID};
// Use pixel-center rays in every pass. Centroid interpolation moves the ray
// between MSAA samples on internal proxy edges, changing distant coverage when
// the same volume is rasterized using different triangles.
// Conservative depth requires centroid SV_Position on D3D. The independent
// object-space varying remains at pixel center for projection and texture reads.
struct Varyings{linear noperspective centroid float4 positionCS:SV_POSITION;float3 positionOS:TEXCOORD0;
nointerpolation float3 originOS:TEXCOORD1;nointerpolation float3 directionOS:TEXCOORD2;nointerpolation float3 frames:TEXCOORD3;nointerpolation float3 weights:TEXCOORD4;nointerpolation float4 rect0:TEXCOORD5;nointerpolation float4 rect1:TEXCOORD6;nointerpolation float4 rect2:TEXCOORD7;
#if defined(_IMPOSTOR_COLOR_ADJUSTMENTS)
nointerpolation float hueVariation:TEXCOORD10;
#endif
nointerpolation float detail:TEXCOORD11;
#ifdef IMPOSTOR_FORWARD_SELF_SHADOW
ImpostorSelfShadowVaryings selfShadow;
#endif
UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO};

void SelectViewFrames(float3 direction, out float3 frames, out float3 weights)
{
 direction.y=max(direction.y,0.00001);
 float2 grid=saturate(HemiEncode(direction))*(_Frames-1);float2 cell=min(floor(grid),_Frames-2),f=grid-cell;float a=cell.y*_Frames+cell.x;
 if(f.x>=f.y){frames=float3(a,a+1,a+_Frames+1);weights=float3(1-f.x,f.x-f.y,f.y);}else{frames=float3(a,a+_Frames+1,a+_Frames);weights=float3(1-f.y,f.x,f.y-f.x);}
}

#if defined(IMPOSTOR_OFFSCREEN_SHADE)
// _ScaledScreenParams describes the smaller buffer during this pass.
float _ImpostorNativeScreenHeight;
#define IMPOSTOR_DETAIL_SCREEN_HEIGHT _ImpostorNativeScreenHeight
#else
#define IMPOSTOR_DETAIL_SCREEN_HEIGHT _ScaledScreenParams.y
#endif

// 1 keeps full parallax and frame blending, 0 is the cheapest representation.
// Driven by the tree's projected diameter at the camera's resolution, so the
// feature's Render Scale does not change the view at the end of a LOD fade.
float ImpostorDetail(float3 cameraOS)
{
 float projected=_CaptureRadius*abs(UNITY_MATRIX_P[1][1])*IMPOSTOR_DETAIL_SCREEN_HEIGHT;
 float pixels=unity_OrthoParams.w>.5?projected*length(GetObjectToWorldMatrix()[0].xyz):projected/max(length(cameraOS-_CaptureCenter.xyz),.001);
 return saturate((pixels-_DetailReducedPixels)/max(_DetailFullPixels-_DetailReducedPixels,.001));
}

// Drops frames that contribute little, so most small trees read one view instead of three.
// Weights stay continuous as detail changes; the dominant frame always remains above zero.
#define IMPOSTOR_FRAME_FOCUS .7
float3 FocusViewFrames(float3 weights,float detail)
{
 float largest=max(weights.x,max(weights.y,weights.z));
 float3 focused=max(weights-largest*IMPOSTOR_FRAME_FOCUS*(1-detail),0);
 return focused/dot(focused,1);
}

float4 LoadViewRectangle(float frame)
{
 float4 rectangle=float4(1-2*_Border,1-2*_Border,_Border,_Border);
 if(_HasFrameRectangles>.5)rectangle=SAMPLE_TEXTURE2D_LOD(_FrameRectangles,sampler_FrameRectangles,float2((frame+.5)/(_Frames*_Frames),.5),0);
 return rectangle;
}

Varyings Vert(Attributes v){
 Varyings o=(Varyings)0;UNITY_SETUP_INSTANCE_ID(v);UNITY_TRANSFER_INSTANCE_ID(v,o);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
 float3 proxyCenter=(_ProxyBoundsMin.xyz+_ProxyBoundsMax.xyz)*.5;
 float3 proxyDirection=unity_OrthoParams.w>.5?normalize(TransformWorldToObjectDir(UNITY_MATRIX_V[2].xyz)):normalize(TransformWorldToObject(_WorldSpaceCameraPos)-proxyCenter);
 #ifdef IMPOSTOR_SHADOW
 #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
 proxyDirection=normalize(TransformWorldToObject(_LightPosition)-proxyCenter);
 #else
 proxyDirection=normalize(TransformWorldToObjectDir(_LightDirection));
 #endif
 #elif defined(IMPOSTOR_DEBUG_CAPTURE)
 proxyDirection=normalize(TransformWorldToObjectDir(_BakeDirectionWS));
 #endif
 v.positionOS=CameraProxyPosition(v.positionOS.xy,proxyDirection);
 o.positionOS=v.positionOS;o.positionCS=TransformObjectToHClip(v.positionOS);
 #if defined(_IMPOSTOR_COLOR_ADJUSTMENTS)
 float3 hueSeed=frac(TransformObjectToWorld(float3(0,0,0))*.1031);
 hueSeed+=dot(hueSeed,hueSeed.yzx+33.33);
 o.hueVariation=frac((hueSeed.x+hueSeed.y)*hueSeed.z);
 #endif
  float3 cameraOS=TransformWorldToObject(_WorldSpaceCameraPos);
 float3 direction=normalize(cameraOS-_CaptureCenter.xyz);
 o.originOS=cameraOS;o.directionOS=0;
 if(unity_OrthoParams.w>.5){direction=normalize(TransformWorldToObjectDir(UNITY_MATRIX_V[2].xyz));o.directionOS=-direction;}
 #ifdef IMPOSTOR_SHADOW
 #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
 o.originOS=TransformWorldToObject(_LightPosition);o.directionOS=0;
 direction=normalize(o.originOS-_CaptureCenter.xyz);
 #else
 direction=normalize(TransformWorldToObjectDir(_LightDirection));o.directionOS=-direction;
 #endif
 o.positionCS=ApplyShadowClamping(o.positionCS);
 #endif
 #ifdef IMPOSTOR_DEBUG_CAPTURE
 direction=normalize(TransformWorldToObjectDir(_BakeDirectionWS));o.directionOS=-direction;
 o.positionCS=mul(_BakeViewProjection,float4(TransformObjectToWorld(v.positionOS),1));
 #endif
 SelectViewFrames(direction,o.frames,o.weights);
 o.detail=1;
 #ifdef IMPOSTOR_CAMERA_DETAIL
 o.detail=ImpostorDetail(cameraOS);
 o.weights=FocusViewFrames(o.weights,o.detail);
 #endif
 o.rect0=LoadViewRectangle(o.frames.x);o.rect1=LoadViewRectangle(o.frames.y);o.rect2=LoadViewRectangle(o.frames.z);
 #ifdef IMPOSTOR_FORWARD_SELF_SHADOW
 o.selfShadow=BuildSelfShadowVaryings();
 #endif
 return o;
}
struct Hit{float3 positionOS,normalOS;half3 albedo,emission;half4 reflectance;half coverage,outputCoverage,ao,smoothness;
#if defined(_IMPOSTOR_MATERIAL_ADJUSTMENTS)
float3 geometricNormalOS;half metallic,specularWorkflow;
#endif
};
Hit Reconstruct(Varyings v){
 float3 origin,rayDirection,frames,weights;
  origin=v.originOS;rayDirection=v.directionOS;frames=v.frames;weights=v.weights;
 float3 ray=normalize(v.positionOS-origin);
 if(dot(rayDirection,rayDirection)>.5){ray=rayDirection;origin=v.positionOS-ray*(_CaptureRadius*4);}
 float2 proxyInterval=IntersectProxyVolume(origin,ray);
 v.positionOS=origin+ray*max(proxyInterval.x,0);
 Hit h=(Hit)0;float weight=0;float distance=0;
 half4 colors[3];float hitTimes[3],validSamples[3];
 float nearestHit=1e20;
 float2 uvDx[3],uvDy[3],uvs[3],correctedUVs[3];float3 rights[3],ups[3];float4 rectangles[3];float denominators[3],times[3];
 [unroll]for(int j=0;j<3;j++){
  float3 d,r,u;
  float frame=frames[j];float4 rect;
    d=HemiDecode(float2(fmod(frame,_Frames),floor(frame/_Frames))/(_Frames-1));FrameBasis(d,r,u);rect=j==0?v.rect0:(j==1?v.rect1:v.rect2);
  float denominator=dot(ray,d);denominator=abs(denominator)<.0001?-.0001:denominator;
  float t=dot(_CaptureCenter.xyz-origin,d)/denominator;
  float3 p=origin+ray*t-_CaptureCenter.xyz;
  float2 uv=float2(dot(p,r),dot(p,u))/(_CaptureRadius*2)+.5;
  float2 mapped=uv*rect.xy+rect.zw;
  uvs[j]=mapped;uvDx[j]=ddx(mapped);uvDy[j]=ddy(mapped);rights[j]=r;ups[j]=u;rectangles[j]=rect;denominators[j]=denominator;times[j]=t;
 }
 [unroll]for(int i=0;i<3;i++){
  // A frame with exactly zero barycentric weight cannot affect coverage,
  // attributes or depth. Keep derivatives above the branch, but skip its reads.
  [branch]if(weights[i]<=0){colors[i]=0;correctedUVs[i]=0;hitTimes[i]=0;validSamples[i]=0;continue;}
  float frame=frames[i];float depth=SAMPLE_TEXTURE2D_ARRAY_GRAD(_DepthArray,sampler_PointClamp,uvs[i],frame,uvDx[i]*.5,uvDy[i]*.5).r;
  // At zero detail the first depth read doubles as the surface depth and parallax is skipped.
  float2 uv=uvs[i];float surfaceDepth=depth;
  [branch]if(v.detail>0){
   float t=times[i]+((depth*2-1)*_CaptureRadius)/denominators[i];
   float3 p=origin+ray*t-_CaptureCenter.xyz;
   float4 rect=rectangles[i];
   float2 corrected=(float2(dot(p,rights[i]),dot(p,ups[i]))/(_CaptureRadius*2)+.5)*rect.xy+rect.zw;
   uv=lerp(uv,corrected,v.detail);
   // Depth at the corrected colour sample reconstructs that surface, rather than
   // the unrelated texel used to calculate the single UV correction.
   surfaceDepth=SAMPLE_TEXTURE2D_ARRAY_GRAD(_DepthArray,sampler_PointClamp,uv,frame,uvDx[i]*.5,uvDy[i]*.5).r;
  }
  half4 c=SAMPLE_TEXTURE2D_ARRAY_GRAD(_BaseArray,sampler_BaseArray,uv,frame,uvDx[i],uvDy[i]);
  float valid=all(uv>=_Border)&&all(uv<=1-_Border)&&depth>0.00002?1:0;
  valid*=surfaceDepth>0.00002?1:0;
  float hitTime=times[i]+((surfaceDepth*2-1)*_CaptureRadius)/denominators[i];
  colors[i]=c;correctedUVs[i]=uv;hitTimes[i]=hitTime;validSamples[i]=valid;
  h.coverage+=weights[i]*c.a*valid;
  if(weights[i]*c.a*valid>.05)nearestHit=min(nearestHit,hitTime);
 }
 float tolerance=max(_CaptureRadius*.08,(length(ddx(v.positionOS))+length(ddy(v.positionOS)))*2);
 // Resolve coverage before fetching surface attributes. Explicit gradients and
 // the depth tolerance are already available for every quad lane before clip.
 #if defined(IMPOSTOR_DEBUG_CAPTURE) || defined(IMPOSTOR_SHADOW) || defined(SCENEPICKINGPASS) || defined(SCENESELECTIONPASS)
 clip(h.coverage-_Cutoff);h.outputCoverage=1;
 #else
 h.outputCoverage=AlphaClip(h.coverage,_Cutoff);
 #endif
 // Keep helper lanes alive through UV and coverage derivatives. Discarding
 // proxy misses earlier makes alpha-to-coverage unstable at small tree edges.
 clip(proxyInterval.y-max(proxyInterval.x,0));
 // Normal, AO and reflectance change slowly between neighbouring views, so read them only from
 // frames near the strongest one. Weights reach zero continuously, so a frame leaves without a pop.
 float3 frameScores=float3(weights[0]*colors[0].a*validSamples[0],weights[1]*colors[1].a*validSamples[1],weights[2]*colors[2].a*validSamples[2]);
 float3 surfaceFocus=max(frameScores-max(frameScores.x,max(frameScores.y,frameScores.z))*IMPOSTOR_FRAME_FOCUS,0);
 float surfaceWeight=0;
 [unroll]for(int k=0;k<3;k++){
  [branch]if(weights[k]<=0)continue;
  half4 c=colors[k];
  // Preserve barycentric coverage, while avoiding colour/normal averages between
  // foreground needles and unrelated branches behind them.
  float confidence=exp2(-max(0,hitTimes[k]-nearestHit)/max(.001,tolerance));
  float w=weights[k]*c.a*validSamples[k]*confidence;
  h.albedo+=c.rgb*w;
  #if !defined(IMPOSTOR_DEBUG_CAPTURE) && !defined(IMPOSTOR_DEPTH_ONLY)
  // Standard shadow casting also needs the reconstructed normal for URP bias.
  float sw=surfaceFocus[k]*confidence;
  [branch]if(sw>0){
  half4 s=SAMPLE_TEXTURE2D_ARRAY_GRAD(_SurfaceArray,sampler_SurfaceArray,correctedUVs[k],frames[k],uvDx[k],uvDy[k]);
  h.normalOS+=DecodeNormal(s.rg)*sw;h.ao+=s.b*sw;h.smoothness+=s.a*sw;surfaceWeight+=sw;
  #if defined(_IMPOSTOR_MATERIAL_ADJUSTMENTS)
  half4 material=SAMPLE_TEXTURE2D_ARRAY_GRAD(_MaterialDataArray,sampler_MaterialDataArray,correctedUVs[k],frames[k],uvDx[k],uvDy[k]);
  h.geometricNormalOS+=DecodeNormal(material.rg)*sw;h.metallic+=material.b*sw;h.specularWorkflow+=material.a*sw;
  #endif
  #if defined(IMPOSTOR_FORWARD_SELF_SHADOW)
  [branch]if(_HasUniformReflectance<.5)h.reflectance+=SAMPLE_TEXTURE2D_ARRAY_GRAD(_ReflectanceArray,sampler_ReflectanceArray,correctedUVs[k],frames[k],uvDx[k],uvDy[k])*sw;
  #endif
  }
  #if defined(IMPOSTOR_FORWARD_SELF_SHADOW)
  [branch]if(_HasEmission>.5)h.emission+=SAMPLE_TEXTURE2D_ARRAY_GRAD(_EmissionArray,sampler_EmissionArray,correctedUVs[k],frames[k],uvDx[k],uvDy[k]).rgb*w;
  #endif
  #endif
  distance+=hitTimes[k]*w;weight+=w;
 }
 float inv=rcp(max(weight,.00001));h.albedo*=inv;
 #if !defined(IMPOSTOR_DEBUG_CAPTURE) && !defined(IMPOSTOR_DEPTH_ONLY)
 float surfaceInv=rcp(max(surfaceWeight,.00001));
 h.normalOS=normalize(h.normalOS+float3(0,0,.000001));h.ao*=surfaceInv;h.smoothness*=surfaceInv;h.reflectance*=surfaceInv;h.emission*=inv;
 #if defined(IMPOSTOR_FORWARD_SELF_SHADOW)
 if(_HasUniformReflectance>.5)h.reflectance=_UniformReflectance;
 #endif
 #if defined(_IMPOSTOR_MATERIAL_ADJUSTMENTS)
 h.geometricNormalOS=normalize(h.geometricNormalOS+float3(0,0,.000001));h.metallic*=surfaceInv;h.specularWorkflow*=surfaceInv;
 h.normalOS=normalize(lerp(h.geometricNormalOS,h.normalOS,_NormalStrength)+float3(0,0,.000001));
 #endif
 #endif
 // A conservative proxy's front face must remain ahead of the reconstructed hit.
 distance=max(distance*inv,dot(v.positionOS-origin,ray));
 float3 relative=origin-_CaptureCenter.xyz;float b=dot(relative,ray);float exitDistance=-b+sqrt(max(0,b*b-dot(relative,relative)+_CaptureRadius*_CaptureRadius));
 distance=min(distance,max(exitDistance,dot(v.positionOS-origin,ray)));
 float3 safeRay=ray+float3(abs(ray.x)<1e-6?1e-6:0,abs(ray.y)<1e-6?1e-6:0,abs(ray.z)<1e-6?1e-6:0);
 float3 a=(_BoundsMin.xyz-origin)/safeRay,boundsExit=(_BoundsMax.xyz-origin)/safeRay;
 float3 farTimes=max(a,boundsExit);
 if(any(_BoundsMax.xyz>_BoundsMin.xyz))distance=min(distance,min(farTimes.x,min(farTimes.y,farTimes.z)));
 h.positionOS=origin+ray*distance;return h;
}
#if defined(_IMPOSTOR_SUBSURFACE) && defined(IMPOSTOR_FORWARD_SELF_SHADOW)
// Source foliage graphs can add sun transmission through Emission. Capture omits
// light-dependent terms, so evaluate a matching view-dependent lobe here: GGX of
// the angle between the view ray and the direction towards the sun. It uses the
// same main light, cookie, layer and shadow/baked visibility as URP's direct term.
half3 ImpostorSubsurface(InputData data,SurfaceData surface,half3 albedo){
 AmbientOcclusionFactor aoFactor=CreateAmbientOcclusionFactor(data,surface);
 Light light=GetMainLight(data,CalculateShadowMask(data),aoFactor);
 float3 transmitted=data.bakedGI*_SubsurfaceScatteringAmbient;
 // Like UniversalFragmentPBR, test layers only when enabled: otherwise the mask is unset.
 #if defined(_LIGHT_LAYERS)
 [branch]if(IsMatchingLightLayer(light.layerMask,GetMeshRenderingLayer()))
 #endif
 {
  float cosine=saturate(-dot(light.direction,data.viewDirectionWS));
  float a2=_SubsurfaceScatteringRoughness*_SubsurfaceScatteringRoughness;
  float d=(cosine*a2-cosine)*cosine+1;
  float lobe=INV_PI*a2/(d*d+1e-7);
  transmitted+=light.color*(light.distanceAttenuation*light.shadowAttenuation*lobe*_SubsurfaceScatteringStrength);
 }
 return min(albedo*_SubsurfaceScatteringTint.rgb*transmitted,HALF_MAX);
}
#endif
#if UNITY_REVERSED_Z
#define IMPOSTOR_DEPTH SV_DepthLessEqual
#else
#define IMPOSTOR_DEPTH SV_DepthGreaterEqual
#endif
void Frag(Varyings v,out float4 color:SV_Target,out float depth:IMPOSTOR_DEPTH){
 UNITY_SETUP_INSTANCE_ID(v);UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
 Hit h=Reconstruct(v);
 #if defined(LOD_FADE_CROSSFADE)
 LODFadeCrossFade(v.positionCS);
 #endif
 half outputCoverage=h.outputCoverage;
 float3 ws=TransformObjectToWorld(h.positionOS);
 #if !defined(IMPOSTOR_DEBUG_CAPTURE) && !defined(IMPOSTOR_DEPTH_ONLY)
 float3 normal=TransformObjectToWorldNormal(h.normalOS);
 #endif
 float4 cs=TransformWorldToHClip(ws);
 #ifdef IMPOSTOR_SHADOW
 #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
 float3 shadowLightDirection=normalize(_LightPosition-ws);
 #else
 float3 shadowLightDirection=_LightDirection;
 #endif
 cs=ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(ws,normal,shadowLightDirection)));
 #endif
 #ifdef IMPOSTOR_DEBUG_CAPTURE
 cs=mul(_BakeViewProjection,float4(ws,1));
 #endif
 depth=cs.z/cs.w;
 #if UNITY_REVERSED_Z
 depth=min(depth,v.positionCS.z);
 #else
 depth=max(depth,v.positionCS.z);
 #endif
 #if defined(IMPOSTOR_DEBUG_CAPTURE)
 color=float4(h.albedo,h.coverage);
 return;
 #elif defined(SCENEPICKINGPASS)
 color=unity_SelectionID;return;
 #elif defined(SCENESELECTIONPASS)
 color=float4(_ObjectId,_PassValue,1,1);return;
 #elif defined(IMPOSTOR_DEPTH_ONLY)
 color=float4(0,0,0,outputCoverage);return;
 #elif defined(IMPOSTOR_NORMALS)
 #if defined(_GBUFFER_NORMALS_OCT)
 float2 octNormal=PackNormalOctQuadEncode(normal);color=half4(PackFloat2To888(saturate(octNormal*.5+.5)),outputCoverage);
 #else
 color=half4(normal,outputCoverage);
 #endif
 return;
 #elif defined(IMPOSTOR_SHADOW)
 color=float4(0,0,0,outputCoverage);return;
 #else

 InputData data=(InputData)0;
 data.positionWS=ws;data.positionCS=cs;data.normalWS=normal;
 data.viewDirectionWS=GetWorldSpaceNormalizeViewDir(ws);
 data.bakedGI=SampleSH(normal);data.shadowMask=1;
 #if defined(_ADDITIONAL_LIGHTS_VERTEX)
 data.vertexLighting=VertexLighting(ws,normal);
 #endif
 #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
 data.shadowCoord=ComputeScreenPos(cs);
 #else
 // The reconstructed surface is only as precise as the blended, compressed depth views,
 // so it can sit behind the depth the impostor casts. Look up realtime shadows from a
 // point moved towards the light by a fraction of the capture radius to avoid acne.
 float3 shadowOffsetOS=TransformWorldToObjectDir(_MainLightPosition.xyz)*(_ShadowReceiverBias*_CaptureRadius);
 data.shadowCoord=TransformWorldToShadowCoord(TransformObjectToWorld(h.positionOS+shadowOffsetOS));
 #endif
 data.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(v.positionCS);
 SurfaceData surface=(SurfaceData)0;
 // Preserve diffuse energy and F0 for both source workflows using URP's BRDF.
 #if !defined(_IMPOSTOR_COLOR_ADJUSTMENTS) && !defined(_IMPOSTOR_REFLECTANCE_ADJUSTMENTS) && !defined(_IMPOSTOR_MATERIAL_ADJUSTMENTS)
 surface.specular=h.reflectance.rgb;
 surface.albedo=h.albedo*_BaseColor.rgb*_BaseColorGain*h.reflectance.a/max(1-ReflectivitySpecular(surface.specular),HALF_MIN);
 surface.smoothness=saturate(h.smoothness*_SmoothnessScale);surface.occlusion=lerp(1,h.ao,_OcclusionStrength);
 surface.emission=h.emission;
 #else
 half3 albedo=h.albedo;
 #if defined(_IMPOSTOR_COLOR_ADJUSTMENTS)
 albedo=max(0,(albedo-.18h)*_AlbedoContrast+.18h+_AlbedoLift);
 half luminance=dot(albedo,half3(.2126h,.7152h,.0722h));
 albedo=max(0,lerp(luminance.xxx,albedo,_AlbedoSaturation));
 // One stable variation per instance, independent of camera and selected frame.
 albedo=lerp(albedo,albedo*_HueVariation.rgb,v.hueVariation*_HueVariation.a);
 #endif
 half3 capturedF0=h.reflectance.rgb;half diffuseWeight=h.reflectance.a;
 #if defined(_IMPOSTOR_MATERIAL_ADJUSTMENTS)
 half metallicDelta=(saturate(h.metallic*_MetallicInfluence)-h.metallic)*(1-saturate(h.specularWorkflow));
 capturedF0=saturate(capturedF0+(h.albedo-.04h)*metallicDelta);
 diffuseWeight=saturate(diffuseWeight-.96h*metallicDelta);
 #endif
 surface.specular=capturedF0;
 surface.smoothness=saturate(h.smoothness*_SmoothnessScale);
 surface.occlusion=saturate(lerp(1,h.ao,_OcclusionStrength));
 surface.emission=h.emission;
 #if defined(_IMPOSTOR_REFLECTANCE_ADJUSTMENTS)
 surface.specular=saturate(capturedF0*_SpecularTint.rgb*_SpecularStrength);
 // Artistic specular boosts must not drive a nonzero diffuse payload into a
 // zero-energy denominator when URP reconstructs the specular-workflow BRDF.
 if (diffuseWeight>0) surface.specular=min(surface.specular,.999h);
 surface.smoothness=saturate(h.smoothness*_SmoothnessScale+_SmoothnessOffset);
 // Zero reflectance is an explicit artist override. Remove the stock grazing
 // lobe as well by supplying zero smoothness, without replacing URP lighting.
 if (_SpecularStrength<=0 || !any(_SpecularTint.rgb>0)) surface.smoothness=0;
 half occlusion=saturate((h.ao-.5h)*_OcclusionContrast+.5h+_OcclusionOffset);
 surface.occlusion=saturate(lerp(1,occlusion,_OcclusionStrength));
 surface.emission=min(h.emission*_EmissionTint.rgb*_EmissionStrength,HALF_MAX);
 #endif
 surface.albedo=albedo*_BaseColor.rgb*_BaseColorGain*diffuseWeight/max(1-ReflectivitySpecular(surface.specular),HALF_MIN);
 #endif
 surface.alpha=1;surface.normalTS=float3(0,0,1);
 ImpostorBakedSunVisibility=1;
 #if defined(_IMPOSTOR_SELF_SHADOW_VOLUME)
 [branch]if(ImpostorSelfShadowWeight(ws,true)>0)
     ImpostorBakedSunVisibility=lerp(1,ImpostorSelfVisibility(h.positionOS,h.normalOS,v.selfShadow),ImpostorMainLightShadowStrength()*_SelfShadowStrength);
 #endif
 #if defined(_IMPOSTOR_SUBSURFACE)
 // Raw captured albedo, independent of the surface grading and gain above.
 surface.emission+=ImpostorSubsurface(data,surface,h.albedo);
 #endif
 color=UniversalFragmentPBR(data,surface);color.a=outputCoverage;
 color.rgb=MixFog(color.rgb,ComputeFogFactor(cs.z));
 #endif
}
#endif
