#version 330 core

// Additive screen-space god rays: depth-occlusion march toward the sun/moon disc.
// Optional cloud opacity supplies occlusion where detailed clouds do not write scene depth,
// so shafts continue across sky/cloud instead of stopping at the terrain silhouette.
//
// Energy model: strength scales *structured* shafts. Open-sky wash is TOD-gated
// (crushed by day, allowed at night with weaker strength + cool tint).

//!include "common/common.glsl"
//!include "common/cloud_present.glsl"

in vec2 vUv;

uniform sampler2D uSceneDepth;
uniform sampler2D uCloudOpacity;
uniform vec2 uSunUv;
uniform float uSunDiscRadius;
uniform float uSunConeRadius;
uniform float uAspect;
uniform float uStrength;
uniform vec3 uScatterTint;
uniform float uSkyWashFloor;
uniform float uTerrainShaftScale;
uniform float uEnergyKnee;
uniform int uHasCloudOpacity;
uniform int uHdrPresent;
uniform float uHdrPaperWhiteNits;
uniform float uHdrPeakNits;

out vec4 FragColor;

const int GR_SAMPLES = 80;
const float SKY_DEPTH_EPS = 0.9992;

float grDistToViewportBorder(vec2 p, vec2 dir)
{
    float tMin = 1e6;
    if (abs(dir.x) > 1e-6)
    {
        float tx0 = (0.0 - p.x) / dir.x;
        float tx1 = (1.0 - p.x) / dir.x;
        if (tx0 > 1e-5) tMin = min(tMin, tx0);
        if (tx1 > 1e-5) tMin = min(tMin, tx1);
    }

    if (abs(dir.y) > 1e-6)
    {
        float ty0 = (0.0 - p.y) / dir.y;
        float ty1 = (1.0 - p.y) / dir.y;
        if (ty0 > 1e-5) tMin = min(tMin, ty0);
        if (ty1 > 1e-5) tMin = min(tMin, ty1);
    }

    return tMin;
}

void main()
{
    if (uStrength <= 0.0)
    {
        discard;
    }

    float receiverDepth = textureLod(uSceneDepth, vUv, 0.0).r;
    bool isSkyReceiver = receiverDepth >= SKY_DEPTH_EPS;

    float aspect = max(uAspect, 1e-4);
    vec2 toSun = uSunUv - vUv;
    float distFromSun = length(vec2(toSun.x * aspect, toSun.y));
    if (distFromSun > uSunConeRadius)
    {
        discard;
    }

    float marchLenRaw = length(toSun);
    if (marchLenRaw < 1e-5)
    {
        discard;
    }

    float disc = max(uSunDiscRadius, 1e-4);
    float cone = max(uSunConeRadius, disc + 1e-4);

    // Carve only the disc itself — must be disc-relative, NOT a fraction of cone width
    // (coneT-based onset grew a vacancy ring as God-Ray Cone Size increased).
    float discKeep = smoothstep(disc * 1.05, disc * 1.85, distFromSun);
    if (discKeep <= 1e-4)
    {
        discard;
    }

    vec2 marchDir = toSun / marchLenRaw;
    float borderT = grDistToViewportBorder(vUv, marchDir);
    float onScreenDist = min(marchLenRaw, max(borderT - 1e-4, 1e-4));

    float shaft = 0.0;
    float visibility = 1.0;
    float occlusionScore = 0.0;
    const float decay = 0.94;
    const float weight = 1.0 / float(GR_SAMPLES);

    for (int i = 0; i < GR_SAMPLES; ++i)
    {
        float t = float(i) / max(float(GR_SAMPLES - 1), 1.0);
        vec2 marchUv = vUv + marchDir * (onScreenDist * t);
        if (marchUv.x < 0.0 || marchUv.x > 1.0 || marchUv.y < 0.0 || marchUv.y > 1.0)
        {
            break;
        }

        float along = onScreenDist * t;
        float beamDist = max(distFromSun - along * (distFromSun / max(marchLenRaw, 1e-5)), 0.0);
        float beamFalloff = 1.0 - smoothstep(disc, cone, beamDist);
        if (beamFalloff <= 0.01)
        {
            visibility *= decay;
            continue;
        }

        float sampleDepth = textureLod(uSceneDepth, marchUv, 0.0).r;
        float pathT = along / max(marchLenRaw, 1e-5);
        float expectedDepth = isSkyReceiver
            ? mix(0.0, SKY_DEPTH_EPS, pathT)
            : mix(receiverDepth, SKY_DEPTH_EPS, pathT);

        // Detailed clouds usually do not write scene depth — use published opacity as blockers.
        float cloudOp = 0.0;
        if (uHasCloudOpacity > 0)
        {
            cloudOp = saturate1(textureLod(uCloudOpacity, marchUv, 0.0).a);
        }

        if (sampleDepth < expectedDepth - 0.0006)
        {
            occlusionScore += weight * visibility;
            visibility *= 0.20;
            if (visibility < 0.03)
            {
                break;
            }

            continue;
        }

        if (cloudOp > 0.04)
        {
            occlusionScore += weight * visibility * cloudOp;
            visibility *= mix(1.0, 0.28, cloudOp);
            if (visibility < 0.03)
            {
                break;
            }
        }

        float skyMask = smoothstep(SKY_DEPTH_EPS - 0.004, SKY_DEPTH_EPS, sampleDepth);
        // Thin cloud still contributes a soft sky tap so shafts read through breaks.
        float gapMask = max(skyMask, (1.0 - cloudOp) * step(0.04, cloudOp) * 0.35);
        if (gapMask > 1e-4)
        {
            float sampleCoreFade = smoothstep(disc * 1.1, disc * 2.1, beamDist);
            shaft += visibility * weight * beamFalloff * gapMask * sampleCoreFade;
        }
        else if (isSkyReceiver)
        {
            visibility *= 0.60;
        }

        visibility *= decay;
    }

    float structure = smoothstep(0.012, 0.095, occlusionScore);
    float skyFloor = saturate1(uSkyWashFloor);
    // Sky/clouds: TOD floor lets night shafts continue off-terrain. Terrain: structure-gated
    // and separately scaled — night must not pile thick additive white onto dark ground.
    float washGate = isSkyReceiver
        ? mix(skyFloor, 1.0, structure)
        : mix(0.10, 0.82, structure);
    float surfaceAtten = isSkyReceiver ? 1.0 : saturate1(uTerrainShaftScale);

    float coneT = saturate1((distFromSun - disc) / (cone - disc));
    // Soft outer rim only — onset is disc-relative via discKeep (independent of cone size).
    float rimSoft = 1.0 - smoothstep(0.82, 1.0, coneT);

    vec3 tint = max(uScatterTint, vec3(0.0));
    vec3 rays = tint * shaft * washGate * surfaceAtten * discKeep * rimSoft * uStrength * 9.0;
    rays = softKnee(rays, max(uEnergyKnee, 0.35));
    rays = cpEncodeShaftRadiance(rays, uHdrPresent, uHdrPaperWhiteNits, uHdrPeakNits);

    float luma = max(max(rays.r, rays.g), rays.b);
    if (luma <= 1e-5)
    {
        discard;
    }

    FragColor = vec4(rays, 1.0);
}
