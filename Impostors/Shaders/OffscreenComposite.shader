Shader "Hidden/Impostors/OffscreenComposite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Composite"
            ZTest LEqual
            ZWrite On
            Cull Off
            ColorMask RGB
            AlphaToMask On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _IMPOSTOR_BUFFER_MSAA
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            Texture2D<float4> _ImpostorColor;
            #if defined(_IMPOSTOR_BUFFER_MSAA)
            Texture2DMS<float, 4> _ImpostorDepth;
            #define IMPOSTOR_BUFFER_SAMPLES 4
            #define LOAD_IMPOSTOR_DEPTH(texel, index) _ImpostorDepth.Load(texel, index)
            #else
            Texture2D<float> _ImpostorDepth;
            #define IMPOSTOR_BUFFER_SAMPLES 1
            #define LOAD_IMPOSTOR_DEPTH(texel, index) _ImpostorDepth.Load(int3(texel, 0))
            #endif
            float4 _ImpostorSize;
            float _ImpostorSharpness;

            // Texels further than this behind the nearest one belong to another tree and must not bleed into it.
            // Grows with distance, where neighbouring texels are usually separate trees that should still blend.
            #define IMPOSTOR_COMPOSITE_DEPTH_TOLERANCE (2.0 + nearestEye * .04)

            static const int2 Offsets[4] = { int2(0, 0), int2(1, 0), int2(0, 1), int2(1, 1) };

            bool IsCovered(float depth)
            {
            #if UNITY_REVERSED_Z
                return depth > 0;
            #else
                return depth < 1;
            #endif
            }

            bool IsNearer(float depth, float other)
            {
            #if UNITY_REVERSED_Z
                return depth > other;
            #else
                return depth < other;
            #endif
            }

            float EyeDepth(float depth)
            {
                if (unity_OrthoParams.w > .5)
                {
                #if UNITY_REVERSED_Z
                    depth = 1 - depth;
                #endif
                    return lerp(_ProjectionParams.y, _ProjectionParams.z, depth);
                }

                return LinearEyeDepth(depth, _ZBufferParams);
            }

            // Nearest covered depth in a texel, plus how much of the texel is covered at all.
            float NearestSample(int2 texel, out float covered)
            {
                float result = 0;
                bool found = false;
                covered = 0;
                for (int s = 0; s < IMPOSTOR_BUFFER_SAMPLES; s++)
                {
                    float sampleDepth = LOAD_IMPOSTOR_DEPTH(texel, s);
                    if (IsCovered(sampleDepth))
                    {
                        covered += 1.0 / IMPOSTOR_BUFFER_SAMPLES;
                        if (!found || IsNearer(sampleDepth, result))
                        {
                            result = sampleDepth;
                            found = true;
                        }
                    }
                }

            #if UNITY_REVERSED_Z
                return found ? result : 0;
            #else
                return found ? result : 1;
            #endif
            }

            float NearestSample(int2 texel, int ignored)
            {
                float covered;
                return NearestSample(texel, covered);
            }

            // A neighbour on another tree or on empty space stands in as the unsharpened colour,
            // so sharpening cannot ring across silhouettes.
            float3 SameSurface(int2 texel, int2 limit, float nearestEye, float3 fallback)
            {
                texel = clamp(texel, int2(0, 0), limit);
                float neighbour = NearestSample(texel, 0);
                if (!IsCovered(neighbour) || abs(EyeDepth(neighbour) - nearestEye) > IMPOSTOR_COMPOSITE_DEPTH_TOLERANCE)
                {
                    return fallback;
                }

                return _ImpostorColor.Load(int3(texel, 0)).rgb;
            }

            // Robust contrast-adaptive sharpening: the lobe is limited so no channel can overshoot its neighbours.
            float3 Sharpen(float3 color, int2 center, int2 limit, float nearestEye)
            {
                float3 north = SameSurface(center + int2(0, -1), limit, nearestEye, color);
                float3 south = SameSurface(center + int2(0, 1), limit, nearestEye, color);
                float3 west = SameSurface(center + int2(-1, 0), limit, nearestEye, color);
                float3 east = SameSurface(center + int2(1, 0), limit, nearestEye, color);
                float3 low = min(min(north, south), min(west, east));
                float3 high = max(max(north, south), max(west, east));
                float3 hitLow = min(low, color) / max(4 * max(high, color), .0001);
                float3 hitHigh = (1 - max(high, color)) / min(4 * min(low, color) - 4, -.0001);
                float3 lobes = max(-hitLow, hitHigh);
                float lobe = max(-.1875, min(max(lobes.r, max(lobes.g, lobes.b)), 0)) * _ImpostorSharpness;
                return (lobe * (north + south + west + east) + color) / (4 * lobe + 1);
            }

            float4 Frag(Varyings input, out float depth : SV_Depth) : SV_Target
            {
                float2 position = input.texcoord * _ImpostorSize.xy - .5;
                float2 base = floor(position);
                float2 f = position - base;
                float weights[4] = { (1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y };
                int2 limit = (int2)_ImpostorSize.xy - 1;
                float coverage = 0;
                float nearest = 0;
                bool any = false;
                float depths[4];
                int2 texels[4];
                for (int i = 0; i < 4; i++)
                {
                    texels[i] = clamp((int2)base + Offsets[i], int2(0, 0), limit);
                    float texelCoverage;
                    depths[i] = NearestSample(texels[i], texelCoverage);
                    if (IsCovered(depths[i]))
                    {
                        coverage += weights[i] * texelCoverage;
                        if (!any || IsNearer(depths[i], nearest))
                        {
                            nearest = depths[i];
                            any = true;
                        }
                    }
                }

                if (!any)
                {
                    discard;
                }

                float nearestEye = EyeDepth(nearest);
                float3 color = 0;
                float total = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (IsCovered(depths[i]) && EyeDepth(depths[i]) - nearestEye <= IMPOSTOR_COMPOSITE_DEPTH_TOLERANCE)
                    {
                        color += _ImpostorColor.Load(int3(texels[i], 0)).rgb * weights[i];
                        total += weights[i];
                    }
                }

                depth = nearest;
                color /= max(total, .0001);
                if (_ImpostorSharpness > 0)
                {
                    color = Sharpen(color, (int2)floor(position + .5), limit, nearestEye);
                }

                return float4(color, saturate(coverage));
            }
            ENDHLSL
        }
        Pass
        {
            Name "CopyOpaqueDepth"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragCopy
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float FragCopy(Varyings input) : SV_Target
            {
                return LOAD_TEXTURE2D_X(_BlitTexture, uint2(input.positionCS.xy)).r;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
