#version 330 core
// GENESIS_GLES_PACK rev29
// Froxel inject: density + sun-lit scatter into a 2D array (P3.2). uDebugDensity adds a uniform
// participating-medium floor so god rays are visible/tunable without fog. Analytic cloud density is
// a fallback only when the detailed cloud opacity/depth signal is unavailable.



//!include "common/common.glsl"

//!include "common/shadow_gates.glsl"

//!include "common/volumetric_inject_density.glsl"

//!include "common/volume_froxel_math.glsl"

//!include "common/volume_inject_pack.glsl"



in vec2 vUv;

uniform sampler2DShadow uShadowMap;

uniform sampler2DShadow uShadowMapNear;

uniform mat4 uLightViewProj;

uniform mat4 uLightViewProjNear;

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

uniform float uShadowMinBias;

uniform int uEnableShadowMap;

uniform int uEnableShadowCascades;

uniform float uCascadeSplitDistance;
uniform float uCascadeBlendWidth;



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

    float shadowGate = grShadowGateCascaded(worldPos, uCameraPos, uLightViewProjNear, uLightViewProj,

        uShadowMapNear, uShadowMap, uShadowTexelSize, uShadowMinBias, uEnableShadowMap,

        uEnableShadowCascades, uCascadeSplitDistance, uCascadeBlendWidth);

    FragColor = viPackFroxelInject(mediumRho, uLightColor, shadowGate);
    FragOccupancy = mediumRho;

}

