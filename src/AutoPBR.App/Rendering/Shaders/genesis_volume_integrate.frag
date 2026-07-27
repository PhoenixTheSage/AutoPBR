#version 330 core
// GENESIS_GLES_PACK rev29
// Full froxel integrate: view-ray Mie in-scatter march with froxel-space + screen-space temporal reuse.
// Fog/shaft medium with detailed cloud opacity/depth supplied by genesis_clouds.frag.
// ANGLE-safe: texture()-based froxel sampling (no texelFetch), ASCII-only sources, single FragColor write.

//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/volumetric_segment.glsl"
//!include "common/ray_reconstruct.glsl"
//!include "common/temporal_reproject.glsl"
//!include "common/volume_froxel_math.glsl"
//!include "common/volume_integrate_sample.glsl"
//!include "common/volume_integrate_sparse.glsl"
//!include "common/cloud_shared_transmittance.glsl"

in vec2 vUv;
uniform sampler2DArray uFroxelVolume;
uniform sampler2DArray uFroxelOccupancy;
uniform sampler2DArray uPrevFroxelVolume;
uniform sampler2D uSceneDepth;
uniform sampler2D uPrevIntegrate;
uniform sampler2D uCloudTransmittance;
uniform sampler2D uCloudData;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;
uniform vec3 uCameraPos;
uniform vec3 uPrevCameraPos;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec3 uCamForward;
uniform vec3 uPrevCamRight;
uniform vec3 uPrevCamUp;
uniform vec3 uPrevCamForward;
uniform vec3 uLightDir;
uniform vec3 uHalfExtent;
uniform vec3 uPrevHalfExtent;
uniform int uSliceCount;
uniform vec2 uFroxelTexelSize;
uniform float uStrength;
uniform float uJitter;
uniform float uTemporalWeight;
uniform float uFroxelTemporalWeight;
uniform float uDepthDistribution;
uniform float uScatterGain;
uniform float uExtinction;
uniform int uHasPrevIntegrate;
uniform int uHasPrevFroxel;
uniform int uHasCloudTransmittance;
uniform int uCloudDataDirect;

out vec4 FragColor;

const int VM_STEPS = 36;
const float SKY_DEPTH_EPS = 0.9992;

void main()
{
    if (uStrength <= 0.0)
    {
        discard;
    }

    vec3 rd = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    float receiverDepth = texture(uSceneDepth, vUv).r;

    if (receiverDepth >= SKY_DEPTH_EPS)
    {
        discard;
    }

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
#ifdef GENESIS_VOLUME_TEMPORAL
    float froxelTemporal = (uHasPrevFroxel > 0) ? uFroxelTemporalWeight : 0.0;
#else
    float froxelTemporal = 0.0;
#endif

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
#ifdef GENESIS_VOLUME_TEMPORAL
        if (froxelTemporal > 0.0)
        {
            vec3 prevUv = vfWorldToFroxelUv(worldPos, uPrevCameraPos, uPrevCamRight, uPrevCamUp, uPrevCamForward,
                uPrevHalfExtent, uSliceCount, uDepthDistribution);
            if (prevUv.x > 0.01 && prevUv.x < 0.99 && prevUv.y > 0.01 && prevUv.y < 0.99 && prevUv.z >= 0.0)
            {
                vec4 prevVoxel = viSampleFroxel(uPrevFroxelVolume, prevUv, uSliceCount);
                voxel = mix(voxel, prevVoxel, froxelTemporal);
            }
        }
#endif

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

#ifdef GENESIS_VOLUME_TEMPORAL
    if (uHasPrevIntegrate > 0 && uTemporalWeight > 0.0)
    {
        vec2 prevUv = trReprojectUvFromDepth(vUv, receiverDepth, uInvViewProj, uPrevViewProj);
        if (trPrevUvOnScreen(prevUv))
        {
            vec3 history = texture(uPrevIntegrate, prevUv).rgb;
            float histDepth = texture(uSceneDepth, prevUv).r;
            float depthValid = trDepthDisocclusionWeight(receiverDepth, histDepth, 0.002, 0.02);
            float reactive = trLuminanceReactiveWeight(vol, history);
            vol = mix(vol, history, uTemporalWeight * depthValid * reactive);
        }
    }
#endif

    float luma = max(max(vol.r, vol.g), vol.b);
    if (luma <= 1e-6)
    {
        discard;
    }

    FragColor = vec4(vol, 1.0);
}
