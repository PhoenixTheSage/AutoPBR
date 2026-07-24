// Shadow gates for froxel inject (no volumetric_medium include).

#ifndef GENESIS_SHADOW_GATES_GLSL
#define GENESIS_SHADOW_GATES_GLSL

//!include "shadow.glsl"

float grShadowGate(vec3 worldPos, mat4 lightViewProj, sampler2DShadow shadowMap, vec2 shadowTexelSize,
    float shadowMinBias, int enableShadowMap)
{
    if (enableShadowMap < 1)
    {
        return 1.0;
    }

    vec4 shadowPack = worldToShadowUv(worldPos, lightViewProj);
    if (shadowPack.w < 0.5)
    {
        return 1.0;
    }

    vec3 shadowUv = shadowPack.xyz;
    float texel = shadowMapTexelDepth(shadowTexelSize);
    float bias = max(shadowMinBias, texel * 0.5);
    shadowUv.z = clamp(shadowUv.z - bias, 0.0, 1.0);
    return sampleShadowPcf3x3(shadowMap, shadowUv, shadowTexelSize);
}

float grShadowGateCascaded(vec3 worldPos, vec3 cameraPos,
    mat4 lightViewProjNear, mat4 lightViewProjMid, mat4 lightViewProjFar,
    sampler2DShadow shadowNear, sampler2DShadow shadowMid, sampler2DShadow shadowFar,
    vec2 texelSizeNear, vec2 texelSizeMid, vec2 texelSizeFar, float shadowMinBias,
    int enableShadowMap, int enableCascades, float cascadeSplitNear, float cascadeSplitMid,
    float cascadeBlendWidth, float shadowDistance, float shadowFadeStart)
{
    if (enableShadowMap < 1)
    {
        return 1.0;
    }

    float dist = length(worldPos - cameraPos);
    float rangeFade = shadowRangeFade(dist, shadowFadeStart, shadowDistance);
    if (rangeFade <= 1e-4)
    {
        return 1.0;
    }

    if (enableCascades < 1)
    {
        float singleVis = grShadowGate(
            worldPos, lightViewProjFar, shadowFar, texelSizeFar, shadowMinBias, enableShadowMap);
        return mix(1.0, singleVis, rangeFade);
    }

    float nearMidT = shadowCascadeBlendT(dist, cascadeSplitNear, cascadeBlendWidth);
    float midFarT = shadowCascadeBlendT(dist, cascadeSplitMid, cascadeBlendWidth);

    float farVis = grShadowGate(worldPos, lightViewProjFar, shadowFar, texelSizeFar, shadowMinBias, enableShadowMap);

    float vis;
    if (nearMidT <= 0.0)
    {
        float nearVis = grShadowGate(worldPos, lightViewProjNear, shadowNear, texelSizeNear, shadowMinBias, enableShadowMap);
        vis = min(nearVis, farVis);
    }
    else if (nearMidT < 1.0)
    {
        float nearVis = grShadowGate(worldPos, lightViewProjNear, shadowNear, texelSizeNear, shadowMinBias, enableShadowMap);
        float midVis = grShadowGate(worldPos, lightViewProjMid, shadowMid, texelSizeMid, shadowMinBias, enableShadowMap);
        vis = min(mix(nearVis, midVis, nearMidT), farVis);
    }
    else if (midFarT <= 0.0)
    {
        float midVis = grShadowGate(worldPos, lightViewProjMid, shadowMid, texelSizeMid, shadowMinBias, enableShadowMap);
        vis = min(midVis, farVis);
    }
    else if (midFarT < 1.0)
    {
        float midVis = grShadowGate(worldPos, lightViewProjMid, shadowMid, texelSizeMid, shadowMinBias, enableShadowMap);
        vis = min(mix(midVis, farVis, midFarT), farVis);
    }
    else
    {
        vis = farVis;
    }

    return mix(1.0, vis, rangeFade);
}

#endif // GENESIS_SHADOW_GATES_GLSL
