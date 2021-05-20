#ifndef UNIVERSAL_LIGHT_COOKIE_INCLUDED
#define UNIVERSAL_LIGHT_COOKIE_INCLUDED

// Includes
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LightCookie/LightCookieInput.hlsl"

#if defined(_LIGHT_COOKIES)
    #ifndef REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
        #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR 1
    #endif
#endif


float2 URP_LightCookie_ComputeUVDirectional(float4x4 worldToLight, float3 samplePositionWS, float4 atlasUVRect, uint2 uvWrap)
{
    // Translate and rotate 'positionWS' into the light space.
    // Project point to light "view" plane, i.e. discard Z.
    float2 positionLS   = mul(worldToLight, float4(samplePositionWS, 1)).xy;

    // Remap [-1, 1] to [0, 1]
    // (implies the transform has ortho projection mapping world space box to [-1, 1])
    float2 positionUV = positionLS * 0.5 + 0.5;

    // Tile texture for cookie in repeat mode
    positionUV.x = (uvWrap.x == URP_TEXTURE_WRAP_MODE_REPEAT) ? frac(positionUV.x) : positionUV.x;
    positionUV.y = (uvWrap.y == URP_TEXTURE_WRAP_MODE_REPEAT) ? frac(positionUV.y) : positionUV.y;
    positionUV.x = (uvWrap.x == URP_TEXTURE_WRAP_MODE_CLAMP)  ? saturate(positionUV.x) : positionUV.x;
    positionUV.y = (uvWrap.y == URP_TEXTURE_WRAP_MODE_CLAMP)  ? saturate(positionUV.y) : positionUV.y;

    // Remap to atlas texture
    float2 positionAtlasUV = atlasUVRect.xy * float2(positionUV) + atlasUVRect.zw;

    return positionAtlasUV;
}

float2 URP_LightCookie_ComputeUVSpot(float4x4 worldToLightPerspective, float3 samplePositionWS, float4 atlasUVRect)
{
    // Translate, rotate and project 'positionWS' into the light clip space.
    float4 positionCS   = mul(worldToLightPerspective, float4(samplePositionWS, 1));
    float2 positionNDC  = positionCS.xy / positionCS.w;

    // Remap NDC to the texture coordinates, from NDC [-1, 1]^2 to [0, 1]^2.
    float2 positionUV = saturate(positionNDC * 0.5 + 0.5);

    // Remap into rect in the atlas texture
    float2 positionAtlasUV = atlasUVRect.xy * float2(positionUV) + atlasUVRect.zw;

    return positionAtlasUV;
}

float2 URP_LightCookie_ComputeUVPoint(float4x4 worldToLight, float3 samplePositionWS, float4 atlasUVRect)
{
    // Translate and rotate 'positionWS' into the light space.
    float4 positionLS  = mul(worldToLight, float4(samplePositionWS, 1));

    float3 sampleDirLS = normalize(positionLS.xyz / positionLS.w);

    // Project direction to Octahederal quad UV.
    float2 positionUV = saturate(PackNormalOctQuadEncode(sampleDirLS) * 0.5 + 0.5);

    // Remap to atlas texture
    float2 positionAtlasUV = atlasUVRect.xy * float2(positionUV) + atlasUVRect.zw;

    return positionAtlasUV;
}



real3 URP_LightCookie_SampleMainLightCookie(float3 samplePositionWS)
{
    float2 uv = URP_LightCookie_ComputeUVDirectional(_MainLightWorldToLight, samplePositionWS, float4(1, 1, 0, 0), URP_TEXTURE_WRAP_MODE_NONE);
    real4 color = URP_LightCookie_SampleMainLightTexture(uv);

    return URP_LightCookie_MainLightTextureIsRGBFormat() ? color.rgb
             : URP_LightCookie_MainLightTextureIsAlphaFormat() ? color.aaa
             : color.rrr;
}

real3 URP_LightCookie_SampleAdditionalLightCookie(int perObjectLightIndex, float3 samplePositionWS)
{
    float4 uvRect = URP_LightCookie_GetAtlasUVRect(perObjectLightIndex);

    // TODO: pack into bits, read less
    if(URP_LightCookie_IsEnabled(uvRect))
        return real3(1,1,1);

    int lightType     = URP_LightCookie_GetLightType(perObjectLightIndex);
    int isSpot        = lightType == URP_LIGHT_TYPE_SPOT;
    int isDirectional = lightType == URP_LIGHT_TYPE_DIRECTIONAL;

    float4x4 worldToLight = URP_LightCookie_GetWorldToLightMatrix(perObjectLightIndex);

    float2 uv;
    if(isSpot)
    {
        uv = URP_LightCookie_ComputeUVSpot(worldToLight, samplePositionWS, uvRect);
    }
    else if(isDirectional)
    {
        uv = URP_LightCookie_ComputeUVDirectional(worldToLight, samplePositionWS, uvRect, URP_TEXTURE_WRAP_MODE_REPEAT);
    }
    else
    {
        uv = URP_LightCookie_ComputeUVPoint(worldToLight, samplePositionWS, uvRect);
    }

    real4 color = URP_LightCookie_SampleAdditionalLightsTexture(uv);

    return URP_LightCookie_AdditionalLightsTextureIsRGBFormat() ? color.rgb
            : URP_LightCookie_AdditionalLightsTextureIsAlphaFormat() ? color.aaa
            : color.rrr;
}

#endif //UNIVERSAL_LIGHT_COOKIE_INCLUDED
