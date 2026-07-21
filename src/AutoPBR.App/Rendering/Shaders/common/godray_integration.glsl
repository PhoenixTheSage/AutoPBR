// God-ray integration helpers: world reconstruction, cloud attenuation, shadow gates.

#ifndef GENESIS_GODRAY_INTEGRATION_GLSL
#define GENESIS_GODRAY_INTEGRATION_GLSL

//!include "shadow.glsl"
//!include "volumetric_medium.glsl"

vec3 grWorldPosFromUvDepth(vec2 uv, float depth, mat4 invViewProj)
{
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, uv.y * 2.0 - 1.0);
    float z = depth * 2.0 - 1.0;
    vec4 worldH = invViewProj * vec4(ndc, z, 1.0);
    return worldH.xyz / max(worldH.w, 1e-6);
}

vec3 grWorldRayDir(vec2 uv, mat4 invViewProj, vec3 cameraPos)
{
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, uv.y * 2.0 - 1.0);
    vec4 worldH = invViewProj * vec4(ndc, 1.0, 1.0);
    vec3 farPt = worldH.xyz / max(worldH.w, 1e-6);
    vec3 rd = farPt - cameraPos;
    float len2 = dot(rd, rd);
    if (len2 < 1e-12)
    {
        return vec3(0.0, 1.0, 0.0);
    }
    return rd * inversesqrt(len2);
}

vec3 grMarchWorldPos(vec2 uv, float sampleDepth, mat4 invViewProj, vec3 cameraPos, float layerBase, float layerTop)
{
    if (sampleDepth > 0.9995 || sampleDepth < 1e-5)
    {
        vec3 rd = grWorldRayDir(uv, invViewProj, cameraPos);
        float midY = (layerBase + layerTop) * 0.5;
        float t = 40.0;
        if (abs(rd.y) > 1e-4)
        {
            t = (midY - cameraPos.y) / rd.y;
        }
        if (t < 1.0)
        {
            t = 40.0;
        }
        return cameraPos + rd * t;
    }

    return grWorldPosFromUvDepth(uv, sampleDepth, invViewProj);
}

float grCloudAttenuation(vec3 worldPos, float groundWorldY, float fogSlabTopY, float layerBase, float layerTop,
    float cloudDensityMul, float volumeSize, float heightFogStrength, int enableClouds)
{
    if (enableClouds < 1)
    {
        return 1.0;
    }

    float density = vmMediumDensity(worldPos, groundWorldY, fogSlabTopY, layerBase, layerTop,
        cloudDensityMul, volumeSize, heightFogStrength);
    return vmMediumTransmittance(density, 3.4);
}

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
    float bias = max(shadowMinBias, texel * 1.75);
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
        float midVis = grShadowGate(worldPos, lightViewProjMid, shadowMid, texelSizeMid, shadowMinBias, enableShadowMap);
        vis = min(min(nearVis, midVis), farVis);
    }
    else if (nearMidT < 1.0)
    {
        float nearVis = grShadowGate(worldPos, lightViewProjNear, shadowNear, texelSizeNear, shadowMinBias, enableShadowMap);
        float midVis = grShadowGate(worldPos, lightViewProjMid, shadowMid, texelSizeMid, shadowMinBias, enableShadowMap);
        vis = min(min(mix(nearVis, midVis, nearMidT), midVis), farVis);
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

#endif // GENESIS_GODRAY_INTEGRATION_GLSL
