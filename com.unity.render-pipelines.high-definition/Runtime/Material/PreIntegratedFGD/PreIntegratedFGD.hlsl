#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/PreIntegratedFGD/PreIntegratedFGD.cs.hlsl"

TEXTURE2D(_PreIntegratedFGD_GGXDisneyDiffuse);

float fgd_fit_F(float NdotV, float perceptualRoughness)
{
    const float a0 = 8.639877405353824;
    const float b0 = -3.1381963259563754;
    const float c0 = 1.8836892049272869;
    const float a1 = 0.8701752860749652;
    const float b1 = -2.292256919092684;
    const float c1 = 7.033675736966873;
    const float a2 = 1.165252345324149;
    const float b2 = 2.285565280188149;
    const float c2 = 14.584919434419636;
    const float a3 = 1.9870618038751404;
    const float b3 = -1.3549509840179295;

    float roughness = perceptualRoughness * perceptualRoughness;

    float a = a0 + b0 * perceptualRoughness + c0 * roughness;
    float b = a1 + b1 * perceptualRoughness + c1 * roughness;
    float c = a2 + b2 * perceptualRoughness + c2 * roughness;
    float d = a3 + b3 * perceptualRoughness;

    return d / (c + b * exp(NdotV * a));
}

// For image based lighting, a part of the BSDF is pre-integrated.
// This is done both for specular GGX height-correlated and DisneyDiffuse
// reflectivity is  Integral{(BSDF_GGX / F) - use for multiscattering
void GetPreIntegratedFGDGGXAndDisneyDiffuse(float NdotV, float perceptualRoughness, float3 fresnel0, out float3 specularFGD, out float diffuseFGD, out float reflectivity)
{
    // We want the LUT to contain the entire [0, 1] range, without losing half a texel at each side.
    float2 coordLUT = Remap01ToHalfTexelCoord(float2(sqrt(NdotV), perceptualRoughness), FGDTEXTURE_RESOLUTION);

    float3 preFGD = SAMPLE_TEXTURE2D_LOD(_PreIntegratedFGD_GGXDisneyDiffuse, s_linear_clamp_sampler, coordLUT, 0).xyz;

#if HDRP_LITE
    // Function fits for FGD
    preFGD.x = fgd_fit_F(NdotV, perceptualRoughness);
#endif

    // Pre-integrate GGX FGD
    // Integral{BSDF * <N,L> dw} =
    // Integral{(F0 + (1 - F0) * (1 - <V,H>)^5) * (BSDF / F) * <N,L> dw} =
    // (1 - F0) * Integral{(1 - <V,H>)^5 * (BSDF / F) * <N,L> dw} + F0 * Integral{(BSDF / F) * <N,L> dw}=
    // (1 - F0) * x + F0 * y = lerp(x, y, F0)
    specularFGD = lerp(preFGD.xxx, preFGD.yyy, fresnel0);

    // Pre integrate DisneyDiffuse FGD:
    // z = DisneyDiffuse
    // Remap from the [0, 1] to the [0.5, 1.5] range.
    diffuseFGD = preFGD.z + 0.5;

    reflectivity = preFGD.y;
}

void GetPreIntegratedFGDGGXAndLambert(float NdotV, float perceptualRoughness, float3 fresnel0, out float3 specularFGD, out float diffuseFGD, out float reflectivity)
{
    float2 preFGD = SAMPLE_TEXTURE2D_LOD(_PreIntegratedFGD_GGXDisneyDiffuse, s_linear_clamp_sampler, float2(NdotV, perceptualRoughness), 0).xy;

    // Pre-integrate GGX FGD
    // Integral{BSDF * <N,L> dw} =
    // Integral{(F0 + (1 - F0) * (1 - <V,H>)^5) * (BSDF / F) * <N,L> dw} =
    // (1 - F0) * Integral{(1 - <V,H>)^5 * (BSDF / F) * <N,L> dw} + F0 * Integral{(BSDF / F) * <N,L> dw}=
    // (1 - F0) * x + F0 * y = lerp(x, y, F0)
    specularFGD = lerp(preFGD.xxx, preFGD.yyy, fresnel0);
    diffuseFGD = 1.0;

    reflectivity = preFGD.y;
}

TEXTURE2D(_PreIntegratedFGD_CharlieAndFabric);

void GetPreIntegratedFGDCharlieAndFabricLambert(float NdotV, float perceptualRoughness, float3 fresnel0, out float3 specularFGD, out float diffuseFGD, out float reflectivity)
{
    // Read the texture
    float3 preFGD = SAMPLE_TEXTURE2D_LOD(_PreIntegratedFGD_CharlieAndFabric, s_linear_clamp_sampler, float2(NdotV, perceptualRoughness), 0).xyz;

    specularFGD = lerp(preFGD.xxx, preFGD.yyy, fresnel0) * 2.0f * PI;

    // z = FabricLambert
    diffuseFGD = preFGD.z;

    reflectivity = preFGD.y;
}
