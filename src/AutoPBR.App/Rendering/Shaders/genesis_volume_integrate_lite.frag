#version 330 core
// GENESIS_GLES_PACK rev29
// Lite froxel integrate: view-ray Mie march with shared detailed-cloud transmittance; no temporal reuse.
// ANGLE-safe: texture()-based froxel sampling (no texelFetch), ASCII-only sources.

//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/volumetric_segment.glsl"
//!include "common/ray_reconstruct.glsl"
//!include "common/volume_froxel_math.glsl"
//!include "common/volume_integrate_sample.glsl"
//!include "common/volume_integrate_sparse.glsl"
//!include "common/cloud_shared_transmittance.glsl"

in vec2 vUv;
uniform sampler2DArray uFroxelVolume;
uniform sampler2DArray uFroxelOccupancy;
uniform sampler2D uSceneDepth;
uniform sampler2D uCloudTransmittance;
uniform sampler2D uCloudData;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec3 uCamForward;
uniform vec3 uLightDir;
uniform vec3 uHalfExtent;
uniform int uSliceCount;
uniform vec2 uFroxelTexelSize;
uniform float uStrength;
uniform float uJitter;
uniform float uDepthDistribution;
uniform float uScatterGain;
uniform float uExtinction;
uniform int uHasCloudTransmittance;
uniform int uCloudDataDirect;

out vec4 FragColor;

const int VM_STEPS = 24;
const float SKY_DEPTH_EPS = 0.9992;

void main()
{
    if (uStrength <= 0.0)
    {
        discard;
    }

    if (texture(uSceneDepth, vUv).r >= SKY_DEPTH_EPS)
    {
        discard;
    }

    vec3 rd = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    vec3 sunToward = normalize(-uLightDir);
    float miePhase = atmosphereMiePhase(dot(rd, sunToward));

    float stepLen = uHalfExtent.z * 2.0 / float(VM_STEPS);
    float stepLenCoarse = stepLen;
    float stepLenFine = stepLenCoarse * 0.5;
#ifdef GENESIS_VOLUME_MEDIUMP_ACCUM
    mediump vec3 accum = vec3(0.0);
    mediump float transmittance = 1.0;
#else
    vec3 accum = vec3(0.0);
    float transmittance = 1.0;
#endif
    float jitter = uJitter * stepLen;
    vec2 sharedCloudSignal = cstResolveViewSignal(
        uCloudTransmittance, uCloudData, vUv, uHasCloudTransmittance, uCloudDataDirect);
    float sharedCloudOpacity = sharedCloudSignal.x;
    float sharedCloudDistance = sharedCloudSignal.y;

    for (int i = 0; i < VM_STEPS; ++i)
    {
        if (viSparseMarchSkipOddStep(i, jitter, stepLenCoarse, rd, uCameraPos, uCamRight, uCamUp, uCamForward,
            uHalfExtent, uSliceCount, uDepthDistribution, uFroxelOccupancy))
        {
            continue;
        }

        float t = viSparseMarchT(i, jitter, stepLenCoarse, stepLenFine);
        vec3 worldPos = uCameraPos + rd * t;
        vec3 froxelUv = vfWorldToFroxelUv(worldPos, uCameraPos, uCamRight, uCamUp, uCamForward,
            uHalfExtent, uSliceCount, uDepthDistribution);
        float edgeW = vfFroxelEdgeWeight(froxelUv);
        if (edgeW <= 1e-5)
        {
            continue;
        }

        if (viSampleFroxelOccupancy(uFroxelOccupancy, froxelUv, uSliceCount) <= 1e-5)
        {
            continue;
        }

        vec4 voxel = viSampleFroxel(uFroxelVolume, froxelUv, uSliceCount);
        float density = voxel.r;
        if (density <= 1e-5)
        {
            continue;
        }

        vec3 sunScatter = vec3(voxel.g, voxel.b, voxel.b * 0.92) * miePhase;
        float inscatterW = vmSegmentInscatterWeight(density, stepLen, uExtinction);
        float cloudViewT = cstViewTransmittance(t, sharedCloudDistance, sharedCloudOpacity, stepLenFine);
        accum += transmittance * cloudViewT * sunScatter * inscatterW * uScatterGain * edgeW;
        transmittance *= mix(1.0, vmSegmentTransmittance(density, stepLen, uExtinction), edgeW);
        if (transmittance < 0.02)
        {
            break;
        }
    }

    vec3 vol = softKnee(accum * uStrength, 0.2);
    if (max(max(vol.r, vol.g), vol.b) <= 1e-6)
    {
        discard;
    }

    FragColor = vec4(vol, 1.0);
}
