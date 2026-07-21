#version 330 core
// GENESIS_GLES_PACK rev29
// Lite froxel inject for GLES fallback: no shadow maps. uDebugDensity adds a uniform
// participating-medium floor so god rays are visible/tunable without fog. Analytic cloud density is
// a fallback only when the detailed cloud opacity/depth signal is unavailable.



//!include "common/common.glsl"

//!include "common/volumetric_inject_density.glsl"

//!include "common/volume_froxel_math.glsl"

//!include "common/volume_inject_pack.glsl"



in vec2 vUv;

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

    FragColor = viPackFroxelInject(mediumRho, uLightColor, 1.0);
    FragOccupancy = mediumRho;

}

