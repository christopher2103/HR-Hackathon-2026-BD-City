// Low-poly water: flat-shaded facets that move with a few slow sine waves (vertex shader, cheap on Quest).
Shader "BDCity/LowPolyWater"
{
    Properties
    {
        _Deep ("Deep colour", Color) = (0.20, 0.42, 0.62, 1)
        _Shallow ("Light colour (wave tops)", Color) = (0.42, 0.66, 0.82, 1)
        _Sky ("Reflection colour", Color) = (0.78, 0.88, 0.96, 1)
        _WaveHeight ("Wave height (m)", Float) = 0.3
        _WaveSpeed ("Wave speed", Float) = 0.7
        _Sparkle ("Sun sparkle", Range(0, 1)) = 0.55
        _Facets ("Facet contrast", Float) = 5
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    CBUFFER_START(UnityPerMaterial)
        half4 _Deep;
        half4 _Shallow;
        half4 _Sky;
        float _WaveHeight;
        float _WaveSpeed;
        half _Sparkle;
        half _Facets;
    CBUFFER_END

    float Wave(float3 p)
    {
        float t = _Time.y * _WaveSpeed;
        float h = sin(p.x * 0.21 + t * 1.3) * 0.5
                + sin(p.z * 0.17 - t * 1.0) * 0.5
                + sin((p.x + p.z) * 0.37 + t * 1.9) * 0.3
                + sin((p.x - p.z) * 0.53 - t * 2.3) * 0.2;
        return h * _WaveHeight;
    }
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

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float  height     : TEXCOORD1;
                half   fogFactor  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                float h = Wave(p);
                p.y += h;
                o.positionWS = p;
                o.height = h / max(_WaveHeight, 1e-3);
                o.positionCS = TransformWorldToHClip(p);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Flat facet normal from screen derivatives (gives the low-poly look).
                float3 n = normalize(cross(ddy(i.positionWS), ddx(i.positionWS)));
                if (n.y < 0) n = -n;
                float3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));

                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half ndl = saturate(dot(n, light.direction));
                half3 lightCol = light.color * light.shadowAttenuation;
                // Each facet gets its own shade from its tilt, so the low-poly pattern reads clearly.
                half facet = dot(n.xz, half2(0.6, 0.8)) * _Facets;
                half3 baseCol = lerp(_Deep.rgb, _Shallow.rgb, saturate(0.5 + facet + i.height * 0.25));
                half3 col = baseCol * (SampleSH(n) * 0.8 + lightCol * (0.35 + 0.65 * ndl));
                // Sky reflection at grazing angles, small sparkle where a facet catches the sun.
                half fresnel = pow(1.0 - saturate(dot(n, viewDir)), 4);
                col = lerp(col, _Sky.rgb, fresnel * 0.55);
                half spec = pow(saturate(dot(n, normalize(light.direction + viewDir))), 90);
                col += lightCol * spec * _Sparkle;
                col = MixFog(col, i.fogFactor);
                return half4(col, 1);
            }
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
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                p.y += Wave(p);
                o.positionCS = TransformWorldToHClip(p);
                return o;
            }
            half frag (Varyings i) : SV_Target { return i.positionCS.z; }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
