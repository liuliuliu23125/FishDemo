// [FishDemo] Rain ripple normal layer. Re-apply on Crest upgrade.

#ifndef CREST_RAIN_RIPPLES_INCLUDED
#define CREST_RAIN_RIPPLES_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "Packages/com.waveharmonic.crest/Runtime/Shaders/Library/Texture.hlsl"

m_CrestNameSpace

TEXTURE2D(_Crest_RainRippleNormal);
float _Crest_RainRippleIntensity;
float _Crest_RainRippleScale;
float _Crest_RainRippleSpeed;

half2 RainRippleNormal(float2 worldXZ, bool underwater)
{
    if (underwater || _Crest_RainRippleIntensity <= 0.0)
    {
        return 0.0;
    }

    const float scale = max(_Crest_RainRippleScale, 0.001);
    float2 uv = (worldXZ - g_Crest_DataOffset.xz) / scale;
    uv += float2(0.37, 0.21) * (g_Crest_Time * _Crest_RainRippleSpeed);
    return UnpackNormal(SAMPLE_TEXTURE2D(_Crest_RainRippleNormal, sampler_LinearRepeat, uv)).xy * _Crest_RainRippleIntensity;
}

m_CrestNameSpaceEnd

#endif
