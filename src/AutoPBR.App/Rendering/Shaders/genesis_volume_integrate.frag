#version 330 core
// GENESIS_GLES_PACK rev29
// Full froxel integrate: view-ray Mie + ambient atmospheric fill with froxel-space + screen-space temporal reuse.
// Fog/shaft medium with detailed cloud opacity/depth supplied by genesis_clouds.frag.
// RGB = in-scatter, A = remaining transmittance for scene*T + inscatter composite.
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
uniform vec3 uLightColor;
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
uniform float uAmbientFillGain;
uniform float uPhaseDirectivity;
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
    bool isSky = receiverDepth >= SKY_DEPTH_EPS;

    float froxelFar = uHalfExtent.z * 2.0;
    float maxT = froxelFar;
    if (!isSky)
    {
        vec3 receiverPos = grWorldPosFromUvDepth(vUv, receiverDepth, uInvViewProj);
        maxT = min(froxelFar, max(length(receiverPos - uCameraPos), 1e-3));
    }

    vec3 sunToward = normalize(-uLightDir);
    float cosSun = clamp(dot(rd, sunToward), -1.0, 1.0);
    float miePhase = atmosphereMiePhase(cosSun);
    // Dual-lobe: a small isotropic floor keeps sun-lit fog readable off-axis; Mie carries
    // crepuscular shafts. Keep the floor low — high values read as an open-sky grey film
    // (especially after HDR present encode). Directivity 0 = volume glow only, 1 = shafts only.
    float isoPhase = 0.06;
    float phase = mix(isoPhase, miePhase, saturate1(uPhaseDirectivity));
    // Fill picks up TOD shaft tint (sun warm / dusk / moon cool) so height fog matches shafts.
    vec3 ambientTint = mix(vec3(0.36, 0.44, 0.58), uLightColor, 0.52);

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
#ifdef GENESIS_VOLUME_TEMPORAL
    float froxelTemporal = (uHasPrevFroxel > 0) ? uFroxelTemporalWeight : 0.0;
#else
    float froxelTemporal = 0.0;
#endif
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

        // Sun energy in G (and mirrored B); tint from uLightColor (TOD sun/moon + scene light).
        vec3 sunScatter = uLightColor * voxel.g * phase * uScatterGain;
        // Ambient is for shadowed medium only — adding it in fully lit open air doubles the grey veil.
        float litApprox = saturate1(voxel.g / max(density * 1.15, 1e-4));
        vec3 ambientScatter = ambientTint * uAmbientFillGain * (1.0 - litApprox);
        float inscatterW = vmSegmentInscatterWeight(density, stepLen, uExtinction);
        float cloudViewT = cstViewTransmittance(t, sharedCloudDistance, sharedCloudOpacity, stepLenFine);
        float weight = inscatterW * edgeW * receiverW;
        accum += transmittance * cloudViewT * (sunScatter + ambientScatter) * weight;
        // Softer extinction keeps looking-into-fog translucent so shafts remain readable.
        float extStep = mix(1.0, vmSegmentTransmittance(density, stepLen, uExtinction * 0.72), edgeW * receiverW);
        transmittance *= extStep;
        if (transmittance < 0.04)
        {
            break;
        }
    }

    // Keep scene-referred linear inscatter; present encode happens in god-ray composite.
    vec3 vol = max(accum * uStrength, vec3(0.0));
    // Strength also deepens extinction slightly so fill and shafts stay coupled.
    float outT = mix(1.0, max(transmittance, 0.05), saturate1(uStrength));

#ifdef GENESIS_VOLUME_TEMPORAL
    if (uHasPrevIntegrate > 0 && uTemporalWeight > 0.0)
    {
        vec2 prevUv = trReprojectUvFromDepth(vUv, isSky ? 0.9995 : receiverDepth, uInvViewProj, uPrevViewProj);
        if (trPrevUvOnScreen(prevUv))
        {
            vec4 history = texture(uPrevIntegrate, prevUv);
            float histDepth = texture(uSceneDepth, prevUv).r;
            float depthValid = isSky
                ? step(SKY_DEPTH_EPS, histDepth)
                : trDepthDisocclusionWeight(receiverDepth, histDepth, 0.002, 0.02);
            float reactive = trLuminanceReactiveWeight(vol, history.rgb);
            float blend = uTemporalWeight * depthValid * reactive;
            vol = mix(vol, history.rgb, blend);
            outT = mix(outT, history.a, blend);
        }
    }
#endif

    float luma = max(max(vol.r, vol.g), vol.b);
    if (luma <= 1e-6 && outT >= 0.999)
    {
        discard;
    }

    FragColor = vec4(vol, outT);
}
