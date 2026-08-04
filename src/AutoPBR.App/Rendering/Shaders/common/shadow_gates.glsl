// Shadow gates for froxel inject (no volumetric_medium include).
// Hard-ish compare + reduced bias for sharper shafts, but cascade selection must
// fall back when the preferred map's light frustum does not cover the froxel cell
// (otherwise out-of-frustum → "fully lit" paints axis-aligned slab artifacts).

#ifndef GENESIS_SHADOW_GATES_GLSL
#define GENESIS_SHADOW_GATES_GLSL

//!include "shadow.glsl"

// Visibility in [0,1], or -1 when the world position is outside this cascade's map.
float grShadowGateSample(vec3 worldPos, mat4 lightViewProj, sampler2DShadow shadowMap, vec2 shadowTexelSize,
    float shadowMinBias, int enableShadowMap)
{
    if (enableShadowMap < 1)
    {
        return 1.0;
    }

    vec4 shadowPack = worldToShadowUv(worldPos, lightViewProj);
    if (shadowPack.w < 0.5)
    {
        return -1.0;
    }

    vec3 shadowUv = shadowPack.xyz;
    float texel = shadowMapTexelDepth(shadowTexelSize);
    // Volume receivers have no surface normal; keep bias low so leaf cutouts stay open.
    float bias = max(shadowMinBias, texel * 0.2);
    shadowUv.z = clamp(shadowUv.z - bias, 0.0, 1.0);
    // Compact cross PCF: softer than bare 1-tap (reduces froxel-cell stair-steps) but
    // much sharper than full 3x3 so canopy holes stay readable.
    vec2 t = shadowTexelSize * 0.65;
    float sum =
        sampleShadowBordered(shadowMap, shadowUv) * 2.0 +
        sampleShadowBordered(shadowMap, vec3(shadowUv.xy + vec2(-t.x, 0.0), shadowUv.z)) +
        sampleShadowBordered(shadowMap, vec3(shadowUv.xy + vec2(t.x, 0.0), shadowUv.z)) +
        sampleShadowBordered(shadowMap, vec3(shadowUv.xy + vec2(0.0, -t.y), shadowUv.z)) +
        sampleShadowBordered(shadowMap, vec3(shadowUv.xy + vec2(0.0, t.y), shadowUv.z));
    return sum * (1.0 / 6.0);
}

float grShadowGate(vec3 worldPos, mat4 lightViewProj, sampler2DShadow shadowMap, vec2 shadowTexelSize,
    float shadowMinBias, int enableShadowMap)
{
    float vis = grShadowGateSample(worldPos, lightViewProj, shadowMap, shadowTexelSize, shadowMinBias, enableShadowMap);
    // Legacy callers treat outside-frustum as lit (no occlusion from this map).
    return vis < 0.0 ? 1.0 : vis;
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

    float blend = max(cascadeBlendWidth * 0.55, 0.15);
    float nearMidT = shadowCascadeBlendT(dist, cascadeSplitNear, blend);
    float midFarT = shadowCascadeBlendT(dist, cascadeSplitMid, blend);

    float nearVis = grShadowGateSample(
        worldPos, lightViewProjNear, shadowNear, texelSizeNear, shadowMinBias, enableShadowMap);
    float midVis = grShadowGateSample(
        worldPos, lightViewProjMid, shadowMid, texelSizeMid, shadowMinBias, enableShadowMap);
    float farVis = grShadowGateSample(
        worldPos, lightViewProjFar, shadowFar, texelSizeFar, shadowMinBias, enableShadowMap);

    // Prefer the highest-res cascade that actually covers this froxel. Never treat
    // "outside preferred map" as lit — that produced camera-aligned lit slabs.
    float vis;
    if (nearMidT <= 0.0)
    {
        if (nearVis >= 0.0)
        {
            vis = nearVis;
        }
        else if (midVis >= 0.0)
        {
            vis = midVis;
        }
        else
        {
            vis = farVis < 0.0 ? 1.0 : farVis;
        }
    }
    else if (nearMidT < 1.0)
    {
        float a = nearVis >= 0.0 ? nearVis : (midVis >= 0.0 ? midVis : (farVis < 0.0 ? 1.0 : farVis));
        float b = midVis >= 0.0 ? midVis : (farVis < 0.0 ? 1.0 : farVis);
        vis = mix(a, b, nearMidT);
    }
    else if (midFarT <= 0.0)
    {
        if (midVis >= 0.0)
        {
            vis = midVis;
        }
        else
        {
            vis = farVis < 0.0 ? 1.0 : farVis;
        }
    }
    else if (midFarT < 1.0)
    {
        float a = midVis >= 0.0 ? midVis : (farVis < 0.0 ? 1.0 : farVis);
        float b = farVis < 0.0 ? 1.0 : farVis;
        vis = mix(a, b, midFarT);
    }
    else
    {
        vis = farVis < 0.0 ? 1.0 : farVis;
    }

    return mix(1.0, vis, rangeFade);
}

#endif // GENESIS_SHADOW_GATES_GLSL
