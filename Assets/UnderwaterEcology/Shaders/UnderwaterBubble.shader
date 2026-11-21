Shader "FishDemo/Underwater/Bubble"
{
    Properties
    {
        _Color ("Color", Color) = (0.7,0.88,1,1)
        _Intensity ("Intensity", Float) = 1.2
        _Alpha ("Alpha", Range(0,1)) = 0.8
        _RimPower ("Rim Power", Range(0.5,6)) = 2
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

        Blend SrcAlpha OneMinusSrcAlpha
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
            #pragma multi_compile_particles

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Intensity;
                half _Alpha;
                float _RimPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float2 p = input.uv * 2.0 - 1.0;
                float r = length(p);
                clip(1.0 - r);

                float rim = pow(saturate(r), _RimPower);
                float center = 1.0 - saturate(r);
                float2 highlightP = p - float2(-0.35, 0.42);
                float highlight = saturate(1.0 - length(highlightP) * 3.2);

                half alpha = (rim * 0.7 + center * 0.06 + highlight * 0.25)
                             * _Color.a * _Alpha * input.color.a * _Intensity;
                half3 color = _Color.rgb * (0.35 + rim * 0.85) + highlight * 0.7;
                return half4(color, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
