#version 330 core
// GENESIS_GLES_PACK rev29
// Froxel inject: density + sun-lit scatter into a 2D array (P3.2). Dual-lobe atmospheric fill
// (valley mist + column haze) is the production medium; uDebugDensity remains a uniform floor.
// Analytic cloud density is a fallback only when the detailed cloud opacity/depth signal is unavailable.



//!include "common/common.glsl"

//!include "common/shadow_gates.glsl"

//!include "common/volumetric_inject_density.glsl"

//!include "common/volume_froxel_math.glsl"

//!include "common/volume_inject_pack.glsl"
//!include "common/cloud_ground_transmittance.glsl"



in vec2 vUv;

uniform sampler2DShadow uShadowMap;

uniform sampler2DShadow uShadowMapNear;

uniform sampler2DShadow uShadowMapMid;
uniform sampler2D uCloudGroundTransmittance;

uniform mat4 uLightViewProj;

uniform mat4 uLightViewProjNear;

uniform mat4 uLightViewProjMid;

uniform vec3 uCameraPos;

uniform vec3 uCamRight;

uniform vec3 uCamUp;

uniform vec3 uCamForward;

uniform vec3 uLightDir;

uniform vec3 uLightColor;

uniform vec3 uHalfExtent;

uniform int uSliceIndex;

uniform int uSliceCount;

uniform float uDepthDistribution;

uniform float uLayerHeight;

uniform float uVolumeHeight;

uniform float uCloudDensity;

uniform float uVolumeSize;

uniform float uGroundWorldY;

uniform float uFogSlabHeight;

uniform float uHeightFogStrength;

uniform float uDebugDensity;

uniform vec2 uShadowTexelSize;

uniform vec2 uShadowTexelSizeNear;

uniform vec2 uShadowTexelSizeMid;

uniform float uShadowMinBias;

uniform int uEnableShadowMap;

uniform int uEnableShadowCascades;

uniform float uCascadeSplitDistance;
uniform float uCascadeMidSplitDistance;
uniform float uCascadeBlendWidth;
uniform float uShadowDistance;
uniform float uShadowFadeStart;
uniform int uHasCloudGroundTransmittance;
uniform vec3 uCloudGroundBasisRight;
uniform vec3 uCloudGroundBasisUp;
uniform vec2 uCloudGroundPlaneCenter;
uniform float uCloudGroundWorldSpan;
uniform vec2 uCloudGroundTexelSize;



layout(location = 0) out vec4 FragColor;
layout(location = 1) out float FragOccupancy;



void main()

{

    vec3 worldPos = vfFroxelWorldPos(vUv, uSliceIndex, uSliceCount, uCameraPos, uCamRight, uCamUp, uCamForward,

        uHalfExtent, uDepthDistribution);

    float layerBase = uLayerHeight;

    float layerTop = layerBase + uVolumeHeight;

    float mediumRho = viInjectMediumDensity(worldPos, uGroundWorldY, uFogSlabHeight, layerBase, layerTop,

        uCloudDensity, uHeightFogStrength) + max(uDebugDensity, 0.0);

    // Empty cells pack to zero lit scatter; skip expensive cascade PCF.
    float shadowGate = 1.0;
    if (mediumRho > 1e-4)
    {
        shadowGate = grShadowGateCascaded(worldPos, uCameraPos,
            uLightViewProjNear, uLightViewProjMid, uLightViewProj,
            uShadowMapNear, uShadowMapMid, uShadowMap,
            uShadowTexelSizeNear, uShadowTexelSizeMid, uShadowTexelSize, uShadowMinBias, uEnableShadowMap,
            uEnableShadowCascades, uCascadeSplitDistance, uCascadeMidSplitDistance, uCascadeBlendWidth,
            uShadowDistance, uShadowFadeStart);
        // CQ3.5 modifies sun-lit in-scatter only. viPackFroxelInject keeps density
        // and occupancy unchanged, preserving view-ray depth/transmittance behavior.
        float cloudGroundTransmittance = cgtSampleGroundTransmittance(
            uCloudGroundTransmittance,
            worldPos,
            uCloudGroundBasisRight,
            uCloudGroundBasisUp,
            uCloudGroundPlaneCenter,
            uCloudGroundWorldSpan,
            uCloudGroundTexelSize,
            uHasCloudGroundTransmittance);
        shadowGate *= cloudGroundTransmittance;
        shadowGate =
            isnan(shadowGate) || isinf(shadowGate)
                ? 1.0
                : clamp(shadowGate, 0.0, 1.0);
    }

    FragColor = viPackFroxelInject(mediumRho, uLightColor, shadowGate);
    FragOccupancy = mediumRho;

}

