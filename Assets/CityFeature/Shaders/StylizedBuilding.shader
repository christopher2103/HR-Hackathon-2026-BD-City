// Stylized city buildings: soft per-building colour (palette texture, UV0) with procedural
// windows on the walls. UV1 = (metres along the wall, metres above street, 1 = gets windows).
// Palette alpha picks the window style: 1 = normal grid, 0.5 = tall narrow, 0 = no windows.
Shader "BDCity/StylizedBuilding"
{
    Properties
    {
        [NoScaleOffset] _BaseMap ("Palette", 2D) = "white" {}
        _WindowColor ("Window colour (bottom)", Color) = (0.60, 0.72, 0.82, 1)
        _WindowSky ("Window colour (top, sky reflection)", Color) = (0.86, 0.92, 0.97, 1)
        _FrameDarken ("Frame darkening", Range(0, 1)) = 0.82
        _Cell ("Window cell size in metres (w, h)", Vector) = (3.0, 3.1, 0, 0)
        _Win ("Window size as part of the cell (w, h)", Vector) = (0.5, 0.55, 0, 0)
        _TallCell ("Tall style cell (w, h)", Vector) = (2.4, 4.6, 0, 0)
        _TallWin ("Tall style window (w, h)", Vector) = (0.38, 0.68, 0, 0)
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    CBUFFER_START(UnityPerMaterial)
        half4 _WindowColor;
        half4 _WindowSky;
        half _FrameDarken;
        float4 _Cell;
        float4 _Win;
        float4 _TallCell;
        float4 _TallWin;
    CBUFFER_END
    TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float3 uv1        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0        : TEXCOORD0;
                float3 uv1        : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half3  normalWS   : TEXCOORD3;
                half   fogFactor  : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // 1 inside a window, 0 outside; anti-aliased with screen derivatives.
            // frame = thin band around the window, area = window share of the wall (for distant fade).
            half WindowMask (float2 metres, float2 cell, float2 win, out half frame, out half area, out half heightInWindow)
            {
                float2 c = metres / cell;
                float2 f = frac(c) - 0.5;
                float2 halfW = win * 0.5;
                float2 d = halfW - abs(f);
                float2 aa = max(fwidth(c), 1e-4);
                float2 m = saturate(d / aa + 0.5);
                float2 fr = saturate((d + 0.06) / aa + 0.5);
                half inside = m.x * m.y;
                frame = saturate(fr.x * fr.y - inside);
                area = win.x * win.y;
                heightInWindow = saturate((f.y + halfW.y) / max(win.y, 1e-3));
                // Fade the pattern out where a cell gets smaller than a few pixels (no shimmering in VR).
                half fade = saturate(1.0 - (max(aa.x, aa.y) - 0.12) * 5.0);
                inside *= fade; frame *= fade;
                return inside;
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                half4 pal = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0);
                half3 albedo = pal.rgb;
                half3 n = normalize(i.normalWS);

                half3 windowCol = 0;
                half windowAmount = 0;
                if (i.uv1.z > 0.5 && pal.a > 0.25)
                {
                    bool tall = pal.a < 0.75;
                    float2 cell = tall ? _TallCell.xy : _Cell.xy;
                    float2 win = tall ? _TallWin.xy : _Win.xy;
                    half frame, area, hw;
                    half inside = WindowMask(i.uv1.xy, cell, win, frame, area, hw);
                    // Far away: blend the wall slightly towards the average window colour instead.
                    half2 aa = fwidth(i.uv1.xy / cell);
                    half far = saturate((max(aa.x, aa.y) - 0.12) * 5.0);
                    albedo *= lerp(1.0, _FrameDarken, frame);
                    windowCol = lerp(_WindowColor.rgb, _WindowSky.rgb, hw * hw);
                    windowAmount = max(inside, far * area * 0.7);
                }

                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light light = GetMainLight(shadowCoord);
                half ndl = saturate(dot(n, light.direction));
                half3 direct = light.color * (ndl * light.shadowAttenuation * light.distanceAttenuation);
                half3 ambient = SampleSH(n);

                half3 wall = albedo * (direct + ambient);
                // Windows are glass: they mostly show the reflected sky, only a little of the sun.
                half3 glass = windowCol * (ambient * 0.6 + 0.55) + windowCol * direct * 0.25;
                half3 col = lerp(wall, glass, windowAmount);
                col = MixFog(col, i.fogFactor);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 posWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 nWS = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - posWS);
            #else
                float3 lightDir = _LightDirection;
            #endif
                float4 pos = TransformWorldToHClip(ApplyShadowBias(posWS, nWS, lightDir));
            #if UNITY_REVERSED_Z
                pos.z = min(pos.z, UNITY_NEAR_CLIP_VALUE);
            #else
                pos.z = max(pos.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.positionCS = pos;
                return o;
            }
            half4 frag (Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert (Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }
            half frag (Varyings i) : SV_Target { return i.positionCS.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; half3 normalWS : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert (Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            half4 frag (Varyings i) : SV_Target { return half4(NormalizeNormalPerPixel(i.normalWS), 0); }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
