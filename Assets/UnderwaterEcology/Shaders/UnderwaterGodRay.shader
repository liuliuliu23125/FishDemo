Shader "FishDemo/Underwater/GodRay"
{
    Properties
    {
        _MainTex ("Gradient", 2D) = "white" {}
        _Color ("Color", Color) = (0.55,0.8,1,1)
        _Intensity ("Intensity", Float) = 0.5
        _EdgeSoftness ("Edge Softness", Range(0.01,1)) = 0.35
        _VerticalFade ("Vertical Fade", Range(0.01,1)) = 0.8
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha One
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float _Intensity;
                float _EdgeSoftness;
                float _VerticalFade;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 sampleColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half edge = smoothstep(0.0, _EdgeSoftness, input.uv.x)
                          * (1.0 - smoothstep(1.0 - _EdgeSoftness, 1.0, input.uv.x));
                half vertical = smoothstep(0.0, _VerticalFade, 1.0 - input.uv.y);
                half alpha = sampleColor.a * edge * vertical * _Color.a * _Intensity;
                half3 color = sampleColor.rgb * _Color.rgb * _Intensity;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
