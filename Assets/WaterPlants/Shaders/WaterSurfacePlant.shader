Shader "FishDemo/Water/SurfacePlant"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _WaveStrength ("Wave Strength", Float) = 0.07
        _WaveSpeed ("Wave Speed", Float) = 0.85
        _DriftStrength ("Drift Strength", Float) = 0.06
        _TiltStrength ("Tilt Strength", Float) = 3
        _YawStrength ("Yaw Strength", Float) = 2
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
                half _Cutoff;
                float _WaveStrength;
                float _WaveSpeed;
                float _DriftStrength;
                float _TiltStrength;
                float _YawStrength;
                half _AmbientBoost;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

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
                float fogCoord : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 pivotWS = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float phase = _Time.y * _WaveSpeed + pivotWS.x * 0.37 + pivotWS.z * 0.29;

                float yaw = radians(sin(phase * 0.43 + 0.7) * _YawStrength);
                float cy = cos(yaw);
                float sy = sin(yaw);
                float3 offsetOS = input.positionOS.xyz;
                float2 xz = float2(offsetOS.x * cy - offsetOS.z * sy, offsetOS.x * sy + offsetOS.z * cy);
                offsetOS.x = xz.x;
                offsetOS.z = xz.y;

                float tiltX = radians(sin(phase * 0.81 + 0.3) * _TiltStrength);
                float tiltZ = radians(cos(phase * 0.67 + 1.7) * _TiltStrength);
                offsetOS.x += offsetOS.y * tiltZ;
                offsetOS.z -= offsetOS.y * tiltX;

                float3 positionWS = TransformObjectToWorld(offsetOS);
                positionWS.y += (sin(phase) * 0.65 + sin(phase * 1.73 + 1.1) * 0.35) * _WaveStrength;
                positionWS.x += sin(phase * 0.57 + 2.0) * _DriftStrength;
                positionWS.z += cos(phase * 0.49 + 0.9) * _DriftStrength;

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
                half3 color = albedo * (mainLight.color * ndl + ambient + mainLight.color * ndlBack * 0.35);
                color = MixFog(color, input.fogCoord);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
