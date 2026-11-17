Shader "FishDemo/Grass/BRGMinimal"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.16, 0.34, 0.09, 1)
        _TipColor ("Tip Color", Color) = (0.42, 0.62, 0.22, 1)
        _AmbientTint ("Ambient Tint", Color) = (0.55, 0.60, 0.45, 1)
        _WindSpeed ("Wind Speed", Float) = 1.2
        _WindStrength ("Wind Strength (m)", Float) = 0.08
        [NoScaleOffset] _MainTex ("Albedo (RGB) A(Clip)", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [Toggle(_USE_ALBEDO_TEX)] _UseAlbedoTex ("Use Albedo Texture", Float) = 0
        _TypeTint ("Type Tint", Color) = (1,1,1,1)
        _TypeScaleBase ("Type Scale Base (xz, y, meshHeight, unused)", Vector) = (1,1,1,0)
        [HideInInspector] _GrassPos ("Grass Pos", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GrassNormalScale ("Grass NormalScale", Vector) = (0, 1, 0, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "BRGForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM

            #pragma target 4.5
            #pragma exclude_renderers gles

            #pragma multi_compile _ DOTS_INSTANCING_ON

            #pragma multi_compile_fog

            #pragma shader_feature_local _USE_ALBEDO_TEX

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TipColor;
                float4 _AmbientTint;
                float4 _TypeTint;
                float4 _TypeScaleBase;
                float  _WindSpeed;
                float  _WindStrength;
                float  _Cutoff;
                float  _UseAlbedoTex;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(UserPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _GrassPos)
                UNITY_DOTS_INSTANCED_PROP(float4, _GrassNormalScale)
            UNITY_DOTS_INSTANCING_END(UserPropertyMetadata)

            static float4 unity_DOTS_Sampled_GrassPos;
            static float4 unity_DOTS_Sampled_GrassNormalScale;

            void SetupDOTSGrassPropertyCaches()
            {
                unity_DOTS_Sampled_GrassPos =
                    UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, _GrassPos);
                unity_DOTS_Sampled_GrassNormalScale =
                    UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, _GrassNormalScale);
            }

            #undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
            #define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSGrassPropertyCaches()

            #define _GrassPos          unity_DOTS_Sampled_GrassPos
            #define _GrassNormalScale  unity_DOTS_Sampled_GrassNormalScale
            #else
            static const float4 _GrassPos = float4(0, 0, 0, 0);
            static const float4 _GrassNormalScale = float4(0, 1, 0, 1);
            #endif

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
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float  bladeT : TEXCOORD3;
                float  fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float4 pos = _GrassPos;
                float4 nscale = _GrassNormalScale;

                float yaw = pos.w * 6.2831853f;
                float s = nscale.w;

                float3 up = normalize(nscale.xyz);
                float3 hint = float3(cos(yaw), 0.0, sin(yaw));
                float3 right = cross(hint, up);
                float rightLen = length(right);
                right = rightLen > 1e-4f ? right / rightLen : float3(1, 0, 0);
                float3 fwd = cross(up, right);

                float scaleXZ = s * max(1e-4f, _TypeScaleBase.x);
                float scaleY  = s * max(1e-4f, _TypeScaleBase.y);
                float meshH   = max(1e-4f, _TypeScaleBase.z);

                float3 local = float3(input.positionOS.x * scaleXZ,
                                      input.positionOS.y * scaleY,
                                      input.positionOS.z * scaleXZ);

                float bladeT = saturate(input.positionOS.y / meshH);
                float phase = (pos.x + pos.z) * 0.35f;
                float t = _Time.y * _WindSpeed + phase;
                float sway = sin(t) * 0.7f + sin(t * 2.3f + 1.7f) * 0.3f;
                float3 windOffset = hint * (sway * _WindStrength * bladeT * bladeT * scaleY);
                local += windOffset;

                float3 world = pos.xyz + right * local.x + up * local.y + fwd * local.z;

                output.positionWS = world;
                output.positionCS = TransformWorldToHClip(world);
                output.normalWS = up;
                output.uv = input.uv;
                output.bladeT = bladeT;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input, bool front_facing : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half3 albedo;
                #ifdef _USE_ALBEDO_TEX
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                clip(tex.a - _Cutoff);
                albedo = tex.rgb * _TypeTint;
                #else
                albedo = lerp(_BaseColor.rgb, _TipColor.rgb, sqrt(input.bladeT)) * _TypeTint;
                #endif

                float3 n = normalize(input.normalWS);
                if (!front_facing) n = -n;

                Light mainLight = GetMainLight();

                float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                float3 lightDir = normalize(mainLight.direction);
                float3 leafNormal = normalize(lerp(n, viewDirWS, 0.6));
                leafNormal = normalize(leafNormal + float3(0, 0.5, 0));
                float ndotl = saturate(dot(leafNormal, lightDir));
                ndotl = ndotl * 0.75f + 0.25f;
                half3 direct = albedo * mainLight.color * ndotl * mainLight.distanceAttenuation;

                half3 ambient = albedo * _AmbientTint.rgb;

                half3 color = direct + ambient;

                #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                color = MixFog(color, input.fogFactor);
                #endif

                return half4(color, 1.0);
            }

            ENDHLSL
        }
    }

    FallBack Off
}