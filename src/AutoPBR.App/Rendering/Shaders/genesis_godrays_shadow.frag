#version 330 core
// Shadow-aware screen-space god rays (epipolar-style occlusion via shadow maps).

//!include "common/common.glsl"
//!include "common/cloud_present.glsl"
//!include "common/godray_integration.glsl"
#ifdef GENESIS_GODRAY_SPARSE_MARCH
//!include "common/godray_march_sparse.glsl"
#endif

in vec2 vUv;uniform sampler2D uSceneDepth;
uniform sampler2DShadow uShadowMap;
uniform sampler2DShadow uShadowMapNear;
uniform sampler2DShadow uShadowMapMid;
uniform mat4 uInvViewProj;
uniform mat4 uLightViewProj;
uniform mat4 uLightViewProjNear;
uniform mat4 uLightViewProjMid;
uniform vec3 uCameraPos;
uniform vec2 uSunUv;
uniform float uSunDiscRadius;
uniform float uSunConeRadius;
uniform float uAspect;
uniform float uStrength;
uniform int uHdrPresent;
uniform float uHdrPaperWhiteNits;
uniform float uHdrPeakNits;
uniform float uLayerHeight;
uniform float uVolumeHeight;
uniform float uCloudDensity;
uniform float uVolumeSize;
uniform float uGroundWorldY;
uniform float uFogSlabHeight;
uniform float uHeightFogStrength;
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
uniform int uEnableCloudAttenuation;

out vec4 FragColor;

const int GR_SAMPLES = 48;
const float SKY_DEPTH_EPS = 0.9992;

void main()
{
    if (uStrength <= 0.0)
    {
        discard;
    }

    float receiverDepth = texture(uSceneDepth, vUv).r;
    // Do not discard sky: leaf-gap / open-air shafts must light sky pixels looking toward the sun.
    bool isSkyReceiver = receiverDepth >= SKY_DEPTH_EPS;

    vec2 toSun = uSunUv - vUv;
    float distFromSun = length(vec2(toSun.x * max(uAspect, 1e-4), toSun.y));
    if (distFromSun > uSunConeRadius)
    {
        discard;
    }

    float disc = max(uSunDiscRadius, 1e-4);
    float cone = max(uSunConeRadius, disc + 1e-4);
    float discKeep = smoothstep(disc * 1.05, disc * 1.85, distFromSun);
    if (discKeep <= 1e-4)
    {
        discard;
    }

    float layerTop = uLayerHeight + uVolumeHeight;
    float shaft = 0.0;
    float visibility = 1.0;
    float occlusionScore = 0.0;
    const float decay = 0.90;
    const float weight = 1.0 / float(GR_SAMPLES);

#ifdef GENESIS_GODRAY_SPARSE_MARCH
    bool grCoarseOccluded = false;
    bool grCoarseWasSky = false;
    float grCoarseBeamFalloff = 1.0;
#endif

    for (int i = 0; i < GR_SAMPLES; ++i)
    {
#ifdef GENESIS_GODRAY_SPARSE_MARCH
        if (grSparseMarchSkipOddStepShadow(i, grCoarseBeamFalloff, grCoarseOccluded, grCoarseWasSky))
        {
            visibility *= decay;
            continue;
        }
#endif

        float t;
#ifdef GENESIS_GODRAY_SPARSE_MARCH
        t = grSparseMarchT(i, GR_SAMPLES);
#else
        t = float(i) / max(float(GR_SAMPLES - 1), 1.0);
#endif
        vec2 marchUv = mix(vUv, uSunUv, t);
        if (marchUv.x < 0.002 || marchUv.x > 0.998 || marchUv.y < 0.002 || marchUv.y > 0.998)
        {
            break;
        }

        float beamDist = (1.0 - t) * distFromSun;
        float beamFalloff = 1.0 - smoothstep(uSunDiscRadius, uSunConeRadius, beamDist);
        if (beamFalloff <= 0.01)
        {
            visibility *= decay;
            continue;
        }

        float sampleDepth = texture(uSceneDepth, marchUv).r;
        float expectedDepth = isSkyReceiver
            ? mix(0.0, SKY_DEPTH_EPS, t)
            : mix(receiverDepth, SKY_DEPTH_EPS, t);
        if (sampleDepth < expectedDepth - 0.0006)
        {
            occlusionScore += weight * visibility;
            visibility *= 0.30;
            if (visibility < 0.04)
            {
                break;
            }
            continue;
        }

        // Same core fade as depth-only SS: keep beams, spare the sun disc/aureole.
        float sampleCoreFade = smoothstep(disc * 1.1, disc * 2.1, beamDist);
        if (sampleDepth >= SKY_DEPTH_EPS)
        {
            // Sky taps: depth occlusion already carved leaf gaps. Do NOT sample cascaded
            // shadows at cloud-slab reconstructions — that paints shadow-map texel squares.
            shaft += visibility * weight * beamFalloff * sampleCoreFade;
        }
        else if (isSkyReceiver)
        {
            // Geometry between camera and sun blocks the beam for sky receivers.
            visibility *= 0.55;
        }
        else
        {
            // Lit geometry receivers can still take a cheap shadow gate (near surface).
            vec3 worldPos = grMarchWorldPos(marchUv, sampleDepth, uInvViewProj, uCameraPos, uLayerHeight, layerTop);
            float lightVis = grShadowGateCascaded(worldPos, uCameraPos,
                uLightViewProjNear, uLightViewProjMid, uLightViewProj,
                uShadowMapNear, uShadowMapMid, uShadowMap,
                uShadowTexelSizeNear, uShadowTexelSizeMid, uShadowTexelSize, uShadowMinBias, uEnableShadowMap,
                uEnableShadowCascades, uCascadeSplitDistance, uCascadeMidSplitDistance, uCascadeBlendWidth,
                uShadowDistance, uShadowFadeStart);
            shaft += visibility * weight * beamFalloff * lightVis * sampleCoreFade * 0.35;
        }

#ifdef GENESIS_GODRAY_SPARSE_MARCH
        if (mod(float(i), 2.0) < 0.5)
        {
            grCoarseOccluded = sampleDepth < expectedDepth - 0.0006;
            grCoarseWasSky = sampleDepth >= SKY_DEPTH_EPS;
            grCoarseBeamFalloff = beamFalloff;
        }
#endif

        visibility *= decay;
    }

    float structure = smoothstep(0.015, 0.10, occlusionScore);
    float washGate = isSkyReceiver
        ? mix(0.015, 1.0, structure)
        : mix(0.20, 1.0, structure);
    float coneT = saturate1((distFromSun - disc) / (cone - disc));
    float rimSoft = 1.0 - smoothstep(0.82, 1.0, coneT);

    vec3 warmScatter = vec3(1.0, 0.94, 0.82);
    // Linear scene-referred; encode once for the already-presented destination.
    // Blend is One,One — keep A=1 so energy is not multiplied by luma again.
    vec3 rays = warmScatter * shaft * washGate * discKeep * rimSoft * uStrength * 9.0;
    rays = softKnee(rays, 0.85);
    rays = cpEncodeShaftRadiance(rays, uHdrPresent, uHdrPaperWhiteNits, uHdrPeakNits);

    float luma = max(max(rays.r, rays.g), rays.b);
    if (luma <= 1e-5)
    {
        discard;
    }

    FragColor = vec4(rays, 1.0);
}
