Shader "FishDemo/Underwater/FishVAT"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _VAT ("VAT", 2D) = "black" {}
        _VATFrames ("VAT Frames", Float) = 51
        _VATSpeed ("VAT Speed", Float) = 1
        _VATAmount ("VAT Amount", Float) = 0.1
        _OffsetScale ("VAT Offset Scale", Float) = 0.1
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _AmbientBoost ("Ambient Boost", Range(0,2)) = 0.65
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "UniversalMaterialType" = "Lit"
        }

        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _BumpScale;
                float _VATFrames;
                float _VATSpeed;
                float _VATAmount;
                float _OffsetScale;
                half _Cutoff;
                half _AmbientBoost;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_VAT);
            SAMPLER(sampler_VAT);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                float2 uvVAT : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
                float3 positionWS : TEXCOORD4;
                float fogCoord : TEXCOORD5;
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
                float3 offsetOS = float3(-offset.x, offset.z, offset.y) * (0.05 * _VATAmount);

                input.positionOS.xyz += offsetOS;
                positionWS = TransformObjectToWorld(input.positionOS.xyz);

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                output.normalWS = normalWS;
                output.tangentWS = tangentWS;
                output.bitangentWS = cross(normalWS, tangentWS) * input.tangentOS.w;
                output.fogCoord = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half alpha = baseSample.a * _BaseColor.a;
                clip(alpha - _Cutoff);

                half3 albedo = baseSample.rgb * _BaseColor.rgb;

                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                float3x3 tbn = float3x3(normalize(input.tangentWS), normalize(input.bitangentWS), normalize(input.normalWS));
                half3 normalWS = normalize(TransformTangentToWorld(normalTS, tbn));

                Light mainLight = GetMainLight();
                half ndl = saturate(dot(normalWS, mainLight.direction));
                half ndlBack = saturate(dot(-normalWS, mainLight.direction));
                half3 ambient = SampleSH(normalWS) * _AmbientBoost;
                half3 color = albedo * (mainLight.color * ndl + ambient + mainLight.color * ndlBack * 0.12);
                color = MixFog(color, input.fogCoord);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
