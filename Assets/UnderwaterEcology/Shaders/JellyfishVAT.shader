Shader "FishDemo/Underwater/JellyfishVAT"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _OpacityMap ("Opacity Map", 2D) = "white" {}
        _EmissionMap ("Emission Map", 2D) = "black" {}
        _EmissionColor ("Emission Color", Color) = (0.25,0.55,1,1)
        _EmissionStrength ("Emission Strength", Float) = 1
        _Color ("Color", Color) = (1,1,1,1)
        _Alpha ("Alpha", Range(0,1)) = 0.5
        _VAT ("VAT", 2D) = "black" {}
        _VATFrames ("VAT Frames", Float) = 51
        _VATSpeed ("VAT Speed", Float) = 1
        _VATAmount ("VAT Amount", Float) = 0.1
        _OffsetScale ("VAT Offset Scale", Float) = 0.1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "UniversalMaterialType" = "Unlit"
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _Color;
                half _Alpha;
                half4 _EmissionColor;
                float _EmissionStrength;
                float _VATFrames;
                float _VATSpeed;
                float _VATAmount;
                float _OffsetScale;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_OpacityMap);
            SAMPLER(sampler_OpacityMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);
            TEXTURE2D(_VAT);
            SAMPLER(sampler_VAT);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 uvVAT : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float3 SampleVAT(float2 uv, float phase)
            {
                float frame = frac(phase) * _VATFrames;
                float f0 = floor(frame);
                float f1 = fmod(f0 + 1.0, _VATFrames);
                float blend = frame - f0;
                float v0 = (f0 + 0.5) / _VATFrames;
                float v1 = (f1 + 0.5) / _VATFrames;
                float3 a = SAMPLE_TEXTURE2D_LOD(_VAT, sampler_VAT, float2(uv.x, v0), 0).xyz;
                float3 b = SAMPLE_TEXTURE2D_LOD(_VAT, sampler_VAT, float2(uv.x, v1), 0).xyz;
                return lerp(a, b, blend);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float phase = _Time.y * _VATSpeed + floor(positionWS.x + positionWS.z) * _OffsetScale;
                float3 offset = SampleVAT(input.uvVAT, phase);
                input.positionOS.xyz += float3(-offset.x, offset.z, offset.y) * (0.05 * _VATAmount);
                positionWS = TransformObjectToWorld(input.positionOS.xyz);

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half opacity = SAMPLE_TEXTURE2D(_OpacityMap, sampler_OpacityMap, input.uv).r;
                half alpha = baseSample.a * opacity * _Color.a * _Alpha;

                Light mainLight = GetMainLight();
                half ndl = saturate(dot(normalize(input.normalWS), mainLight.direction));
                half3 ambient = SampleSH(normalize(input.normalWS));
                half3 color = baseSample.rgb * _Color.rgb * (mainLight.color * ndl * 0.5 + ambient);
                color += SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb
                         * _EmissionColor.rgb * _EmissionStrength;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
