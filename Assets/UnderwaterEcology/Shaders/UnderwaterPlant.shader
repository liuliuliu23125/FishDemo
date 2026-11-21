Shader "FishDemo/Underwater/Plant"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _EmissionMap ("Emission Map", 2D) = "black" {}
        _EmissionColor ("Emission Color", Color) = (1,1,1,1)
        _EmissionStrength ("Emission Strength", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _WindStrength ("Wind Strength", Float) = 0
        _WindSpeed ("Wind Speed", Float) = 1
        _AmbientBoost ("Ambient Boost", Range(0,2)) = 0.7
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
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
            #pragma shader_feature_local _NORMALMAP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _BumpScale;
                half4 _EmissionColor;
                float _EmissionStrength;
                half _Cutoff;
                float _WindStrength;
                float _WindSpeed;
                half _AmbientBoost;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
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

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float height = saturate(input.uv.y);
                float phase = _Time.y * _WindSpeed + positionWS.x * 0.21 + positionWS.z * 0.17;
                float sway = sin(phase) * 0.7 + sin(phase * 1.73 + 1.3) * 0.3;
                input.positionOS.x += sway * _WindStrength * height * height;
                input.positionOS.z += cos(phase * 0.83) * _WindStrength * height * height * 0.6;

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

            half4 Frag(Varyings input, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half alpha = baseSample.a * _BaseColor.a;
                clip(alpha - _Cutoff);

                half3 albedo = baseSample.rgb * _BaseColor.rgb;

                half3 normalWS = normalize(input.normalWS);
                normalWS *= IS_FRONT_VFACE(facing, 1.0, -1.0);
                #ifdef _NORMALMAP
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                float3x3 tbn = float3x3(normalize(input.tangentWS), normalize(input.bitangentWS), normalWS);
                normalWS = normalize(TransformTangentToWorld(normalTS, tbn));
                #endif

                Light mainLight = GetMainLight();
                half ndl = saturate(dot(normalWS, mainLight.direction));
                half ndlBack = saturate(dot(-normalWS, mainLight.direction));
                half3 ambient = SampleSH(normalWS) * _AmbientBoost;
                half3 color = albedo * (mainLight.color * ndl + ambient + mainLight.color * ndlBack * 0.2);
                color += SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb
                         * _EmissionColor.rgb * _EmissionStrength;
                color = MixFog(color, input.fogCoord);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
