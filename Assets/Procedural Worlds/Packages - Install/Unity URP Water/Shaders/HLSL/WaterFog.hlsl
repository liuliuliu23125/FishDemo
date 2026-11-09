#ifndef PW_WATER_PHYSICAL_SKY_FOG_INCLUDED
#define PW_WATER_PHYSICAL_SKY_FOG_INCLUDED

// Transparent water is drawn after the physical sky's opaque fog pass.
// Apply the same fog to its fully lit forward-pass color, not its albedo.
#if !defined(SHADERGRAPH_PREVIEW) && defined(SHADERPASS_FORWARD) && defined(SHADERPASS) && (SHADERPASS == SHADERPASS_FORWARD)
#if defined(PHYSICALLY_BASED_SKY)
#include "../../../Sky & Lighting Presets/URP Physical Sky/URP Physically Based Sky/Shaders/AtmosphericScattering.hlsl"
#else
SAMPLER(s_linear_clamp_sampler);
#endif

TEXTURE2D_X(_VolumetricCloudsLightingTexture);
TEXTURE2D_X_FLOAT(_VolumetricCloudsDepthTexture);
float4 _VolumetricCloudsLightingTexture_TexelSize;
SAMPLER(sampler_PWCloudPointClamp);
// Set by the cloud pass for this camera only: valid depth, fog, bilateral upscale.
float4 _PW_WaterClouds;
#include "../../../Sky & Lighting Presets/URP Physical Sky/URP Volumetric Clouds/VolumetricClouds/VolumetricCloudsUpscale.hlsl"

half3 PW_WaterCloudComposite(half3 water, half3 cloudRadiance, half transmittance, half radianceWeight)
{
    return water * transmittance + cloudRadiance * radianceWeight;
}

half3 PW_WaterCloudOcclusion(half3 water, float3 positionWS, float4 positionCS, half alpha)
{
    if (_PW_WaterClouds.x < 0.5)
        return water;

    float2 uv = GetNormalizedScreenSpaceUV(positionCS);
    float cloudDepth = SAMPLE_TEXTURE2D_X_LOD(_VolumetricCloudsDepthTexture, sampler_PWCloudPointClamp, uv, 0).r;
    // Mean cloud depth is stored in device depth, not linear eye distance.
    if (cloudDepth == UNITY_RAW_FAR_CLIP_VALUE)
        return water;
    // Compare device depths directly, including orthographic Scene cameras.
#if UNITY_REVERSED_Z
    if (cloudDepth <= positionCS.z)
#else
    if (cloudDepth >= positionCS.z)
#endif
        return water;

    half4 cloud = _PW_WaterClouds.z > 0.5 ? BilateralUpscale(uv) :
        SAMPLE_TEXTURE2D_X_LOD(_VolumetricCloudsLightingTexture, s_linear_clamp_sampler, uv, 0);
#if defined(PHYSICALLY_BASED_SKY)
    // Match the cloud combine pass, including independent height fog.
    if (_PW_WaterClouds.y > 0.5 && (_FogEnabled || _PBRFogEnabled))
    {
        PositionInputs cloudPosition = GetPositionInput(positionCS.xy, rcp(_ScaledScreenParams.xy),
            cloudDepth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        half3 fogColor, fogOpacity;
        EvaluateAtmosphericScattering(cloudPosition,
            normalize(GetCameraPositionWS() - cloudPosition.positionWS), fogColor, fogOpacity);
        cloud.rgb = fogColor * (1.0 - cloud.a) + (1.0 - fogOpacity) * cloud.rgb;
    }
#endif
    // The background already contains clouds. With normal alpha blending this
    // produces C + T * (water * alpha + background * (1-alpha)), not double fog.
    half radianceWeight = 1.0;
#if defined(_ALPHAPREMULTIPLY_ON)
    radianceWeight = alpha;
#endif
    return PW_WaterCloudComposite(water, cloud.rgb, cloud.a, radianceWeight);
}

half3 PW_WaterPhysicalSkyFog(half3 litColor, half standardFogFactor,
    float3 positionWS, float4 positionCS, half alpha)
{
    // Match opaque URP surfaces: built-in fog first, then physical-sky fog.
    litColor = MixFog(litColor, standardFogFactor);
#if defined(PHYSICALLY_BASED_SKY)
    if (!_FogEnabled && !_PBRFogEnabled)
        return PW_WaterCloudOcclusion(litColor, positionWS, positionCS, alpha);

    float linearDepth = -TransformWorldToView(positionWS).z;
    PositionInputs positionInput = GetPositionInput(positionCS.xy, rcp(_ScaledScreenParams.xy),
        positionCS.z, linearDepth, positionWS);
    half3 viewDirection = GetWorldSpaceNormalizeViewDir(positionWS);
    half3 fogColor, fogOpacity;
    EvaluateAtmosphericScattering(positionInput, viewDirection, fogColor, fogOpacity);

    // Alpha blending weights fog with the surface automatically. Premultiplied
    // blending needs that weight here, so clear water doesn't add opaque fog.
#if defined(_ALPHAPREMULTIPLY_ON)
    fogColor *= alpha;
#endif
    litColor = litColor * (1.0 - fogOpacity) + fogColor;
#endif
    return PW_WaterCloudOcclusion(litColor, positionWS, positionCS, alpha);
}

// Shader Graph has no post-lighting color block. Its URP forward pass calls
// MixFog after UniversalFragmentPBR (URP 14 through 17). Redirect only that
// call in this graph's forward variant; other passes retain URP's implementation.
// The helper above still calls the original; inactive clouds leave it unchanged.
#define MixFog(lighting, factor) PW_WaterPhysicalSkyFog((lighting), (factor), inputData.positionWS, unpacked.positionCS, color.a)
#endif

#endif
