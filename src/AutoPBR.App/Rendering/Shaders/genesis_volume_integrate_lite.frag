#version 330 core
// GENESIS_GLES_PACK rev29
// Lite froxel integrate: view-ray Mie + ambient fill with shared detailed-cloud transmittance; no temporal reuse.
// RGB = in-scatter, A = remaining transmittance for scene*T + inscatter composite.
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
uniform vec3 uLightColor;
uniform vec3 uHalfExtent;
uniform int uSliceCount;
uniform vec2 uFroxelTexelSize;
uniform float uStrength;
uniform float uJitter;
uniform float uDepthDistribution;
uniform float uScatterGain;
uniform float uExtinction;
uniform float uAmbientFillGain;
uniform float uPhaseDirectivity;
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

    float receiverDepth = texture(uSceneDepth, vUv).r;
    bool isSky = receiverDepth >= SKY_DEPTH_EPS;
    vec3 rd = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    vec3 sunToward = normalize(-uLightDir);
    float cosSun = clamp(dot(rd, sunToward), -1.0, 1.0);
    float miePhase = atmosphereMiePhase(cosSun);
    // Keep isotropic floor low so open-sky air does not read as a grey film after HDR encode.
    float isoPhase = 0.06;
    float phase = mix(isoPhase, miePhase, saturate1(uPhaseDirectivity));
    vec3 ambientTint = mix(vec3(0.36, 0.44, 0.58), uLightColor, 0.52);

    float froxelFar = uHalfExtent.z * 2.0;
    float maxT = froxelFar;
    if (!isSky)
    {
        vec3 receiverPos = grWorldPosFromUvDepth(vUv, receiverDepth, uInvViewProj);
        maxT = min(froxelFar, max(length(receiverPos - uCameraPos), 1e-3));
    }

    float stepLen = froxelFar / float(VM_STEPS);
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
    float kneeWidth = max(stepLenFine * 2.5, 0.35);

    for (int i = 0; i < VM_STEPS; ++i)
    {
        if (viSparseMarchSkipOddStep(i, jitter, stepLenCoarse, rd, uCameraPos, uCamRight, uCamUp, uCamForward,
            uHalfExtent, uSliceCount, uDepthDistribution, uFroxelOccupancy))
        {
            continue;
        }

        float t = viSparseMarchT(i, jitter, stepLenCoarse, stepLenFine);
        if (t >= maxT)
        {
            continue;
        }

        float receiverW = isSky ? 1.0 : (1.0 - smoothstep(maxT - kneeWidth, maxT, t));
        if (receiverW <= 1e-4)
        {
            continue;
        }

        vec3 worldPos = uCameraPos + rd * t;
        vec3 froxelUv = vfWorldToFroxelUv(worldPos, uCameraPos, uCamRight, uCamUp, uCamForward,
            uHalfExtent, uSliceCount, uDepthDistribution);
        float edgeW = vfFroxelEdgeWeight(froxelUv);
        float forward01 = dot(worldPos - uCameraPos, uCamForward) / max(uHalfExtent.z * 2.0, 1e-3);
        edgeW *= vfFroxelFarWeight(forward01);
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

        vec3 sunScatter = uLightColor * voxel.g * phase * uScatterGain;
        float litApprox = saturate1(voxel.g / max(density * 1.15, 1e-4));
        vec3 ambientScatter = ambientTint * uAmbientFillGain * (1.0 - litApprox);
        float inscatterW = vmSegmentInscatterWeight(density, stepLen, uExtinction);
        float cloudViewT = cstViewTransmittance(t, sharedCloudDistance, sharedCloudOpacity, stepLenFine);
        float weight = inscatterW * edgeW * receiverW;
        accum += transmittance * cloudViewT * (sunScatter + ambientScatter) * weight;
        float extStep = mix(1.0, vmSegmentTransmittance(density, stepLen, uExtinction * 0.72), edgeW * receiverW);
        transmittance *= extStep;
        if (transmittance < 0.04)
        {
            break;
        }
    }

    // Keep scene-referred linear inscatter; present encode happens in god-ray composite.
    vec3 vol = max(accum * uStrength, vec3(0.0));
    float outT = mix(1.0, max(transmittance, 0.05), saturate1(uStrength));
    if (max(max(vol.r, vol.g), vol.b) <= 1e-6 && outT >= 0.999)
    {
        discard;
    }

    FragColor = vec4(vol, outT);
}
