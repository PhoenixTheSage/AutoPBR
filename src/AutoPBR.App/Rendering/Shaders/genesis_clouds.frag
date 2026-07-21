#version 330 core
// Curved-shell volumetric clouds with conservative empty-space marching.

//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/sky_dome.glsl"
//!include "common/cloud_shell.glsl"
//!include "common/volumetric_clouds.glsl"
//!include "common/volumetric_medium.glsl"
//!include "common/volumetric_clouds_density_maps.glsl"
//!include "common/cloud_temporal.glsl"
//!include "common/ray_reconstruct.glsl"
//!include "common/cloud_scene_depth.glsl"

in vec2 vUv;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform vec3 uSunDir;
uniform float uSunIntensity;
uniform float uSkyExposure;
uniform int uHdrPresent;
uniform sampler2D uSkyViewLut;
uniform sampler3D uCloudNoise;
uniform sampler3D uDetailNoise;
uniform sampler2D uCoverageMap;
uniform sampler2D uSceneDepth;
uniform float uGroundWorldY;
uniform float uPlanetRadius;
uniform float uLayerHeight;
uniform float uVolumeHeight;
uniform float uDensity;
uniform float uCoverageScale;
uniform float uVolumeSize;
uniform vec3 uWindOffset;
uniform float uCirrusStrength;
uniform vec2 uCirrusWindOffset;
uniform vec2 uCirrusWindDir;
uniform int uQuality;
uniform int uMarchSteps;
uniform int uDebugView;
uniform int uHasSceneDepth;
uniform int uHasCloudNoise;
uniform int uHasDetailNoise;
uniform int uHasCoverageMap;
uniform int uHasSkyLut;
uniform float uFramePhase;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 FragCloudData;

const int CLOUD_MAX_STEPS = 64;
const float CLOUD_HORIZON_FEATHER = 0.0025;

vec3 sampleSkyAmbient(vec3 rd, sampler2D skyLut, int hasSkyLut, float dayAmt)
{
    vec3 night = skyNightZenith(rd) * 2.0;
    if (hasSkyLut < 1)
    {
        return mix(night, vec3(0.42, 0.50, 0.63), dayAmt);
    }

    vec3 ambientDir = normalize(vec3(rd.x * 0.35, max(rd.y, 0.45), rd.z * 0.35));
    vec3 lut = srgbToLinear(sampleSkyViewLutSrgb(skyLut, ambientDir));
    return mix(night, lut, dayAmt);
}

float cloudSceneDistance(vec3 rd)
{
    if (uHasSceneDepth < 1)
    {
        return CSD_NO_SCENE_HIT;
    }

    // The trace target is half resolution. Choose the farthest depth in its full-resolution
    // footprint so one foreground terrain sample cannot erase the neighboring sky sample.
    // The upsample pass performs the authoritative per-display-pixel distance rejection.
    vec2 sceneTexel = 1.0 / vec2(textureSize(uSceneDepth, 0));
    vec2 footprintOffset = max(sceneTexel * 0.5, fwidth(vUv) * 0.25);
    float conservativeDepth = max(
        max(texture(uSceneDepth, vUv + vec2(-footprintOffset.x, -footprintOffset.y)).r,
            texture(uSceneDepth, vUv + vec2( footprintOffset.x, -footprintOffset.y)).r),
        max(texture(uSceneDepth, vUv + vec2(-footprintOffset.x,  footprintOffset.y)).r,
            texture(uSceneDepth, vUv + vec2( footprintOffset.x,  footprintOffset.y)).r));
    return csdSceneRayDistanceFromDepth(
        conservativeDepth, vUv, uInvViewProj, uCameraPos, rd, uHasSceneDepth);
}

void main()
{
    vec3 rd = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    float planetRadius = max(uPlanetRadius, 1.0);
    vec3 planetCenter = vec3(0.0, uGroundWorldY - planetRadius, 0.0);
    float layerBaseAltitude = max(uLayerHeight - uGroundWorldY, 0.01);
    float layerTopAltitude = layerBaseAltitude + max(uVolumeHeight, 0.01);
    float innerRadius = planetRadius + layerBaseAltitude;
    float outerRadius = planetRadius + layerTopAltitude;

    vec2 slabSeg = vcsIntersectShell(uCameraPos, rd, planetCenter, innerRadius, outerRadius);
    // Opaque scene depth remains hard. The planet receives a very narrow angular feather:
    // far-side clouds may contribute only inside that transition band, avoiding a cutout edge.
    float planetT = vcsPlanetOcclusionDistance(uCameraPos, rd, planetCenter, planetRadius);
    float horizonVisibility = vcsPlanetHorizonVisibility(
        uCameraPos, rd, planetCenter, planetRadius, CLOUD_HORIZON_FEATHER);
    float sceneT = cloudSceneDistance(rd);
    float tEnter = slabSeg.x;
    float tExit = min(slabSeg.y, sceneT);
    float slabHorizonVisibility = planetT < 1e8 && tEnter >= planetT
        ? horizonVisibility
        : 1.0;
    bool slabHit = tExit > tEnter && slabHorizonVisibility > 1e-3;

    float cirrusAltitude = layerTopAltitude + max(uVolumeHeight * 1.5, 18.0);
    float cirrusThickness = max(uVolumeHeight * 0.035, 0.75);
    vec2 cirrusSeg = vcsIntersectShell(uCameraPos, rd, planetCenter,
        planetRadius + cirrusAltitude, planetRadius + cirrusAltitude + cirrusThickness);
    cirrusSeg.y = min(cirrusSeg.y, sceneT);
    float cirrusHorizonVisibility = planetT < 1e8 && cirrusSeg.x >= planetT
        ? horizonVisibility
        : 1.0;
    bool cirrusHit = cirrusSeg.y > cirrusSeg.x && cirrusHorizonVisibility > 1e-3;

    if (!slabHit && (!cirrusHit || uCirrusStrength <= 0.0))
    {
        discard;
    }

    vec3 cloudCol = vec3(0.0);
    float alpha = 0.0;
    bool debugViewActive = false;

    if (uDebugView == 1 && slabHit)
    {
        float tSample = (tEnter + tExit) * 0.5;
        vec3 pos = uCameraPos + rd * tSample;
        vec2 weather = vcSampleWeather(uCoverageMap, uHasCoverageMap, pos, uVolumeSize, uWindOffset.xz);
        float cov = saturate1(weather.x * uCoverageScale);
        cloudCol = vec3(cov, weather.y, 0.35);
        alpha = (cov > 0.02 ? 0.95 : 0.0) * slabHorizonVisibility;
        debugViewActive = true;
    }
    else if (uDebugView == 2 && slabHit)
    {
        float tSlice = (tEnter + tExit) * 0.5;
        vec3 pos = uCameraPos + rd * tSlice;
        float density = vcCloudDensityEx(pos, planetCenter, planetRadius,
            layerBaseAltitude, layerTopAltitude, uDensity, uCoverageScale, uVolumeSize,
            uCloudNoise, uHasCloudNoise, uDetailNoise, uHasDetailNoise,
            uCoverageMap, uHasCoverageMap, uWindOffset) * slabHorizonVisibility;
        cloudCol = vec3(density * 2.8, density * 1.4, density * 0.35);
        alpha = saturate1(density * 3.5);
        debugViewActive = true;
    }

    float representativeT = slabHit ? tExit : max(cirrusSeg.x, 0.0);
    float representativeKind = slabHit ? 0.0 : 1.0;
    bool representativeFound = false;
    if (!debugViewActive)
    {
        vec3 sunToward = normalize(-uSunDir);
        float cosTheta = dot(rd, sunToward);
        float dayAmt = skyDayFactor(uSunDir, uSunIntensity);
        vec3 sunColor = vcCloudSunColor(sunToward, uSunIntensity);
        vec3 skyAmbient = sampleSkyAmbient(rd, uSkyViewLut, uHasSkyLut, dayAmt);
        vec3 accum = vec3(0.0);
        float transmittance = 1.0;

        if (slabHit)
        {
            // A few weather-map taps reject wholly clear rays before any 3D texture access.
            float covMax = 0.0;
            for (int i = 0; i < 4; ++i)
            {
                float tCov = mix(tEnter, tExit, (float(i) + 0.5) / 4.0);
                vec3 covPos = uCameraPos + rd * tCov;
                covMax = max(covMax,
                    vcSampleWeather(uCoverageMap, uHasCoverageMap, covPos, uVolumeSize, uWindOffset.xz).x);
            }

            bool hasCumulus = covMax * uCoverageScale > 1e-3;
            if (hasCumulus)
            {
                int steps = uMarchSteps > 0
                    ? clamp(uMarchSteps, 1, CLOUD_MAX_STEPS)
                    : (uQuality <= 0 ? 16 : (uQuality >= 2 ? 32 : 24));
                float fineStep = max((tExit - tEnter) / float(steps), 0.01);
                float coarseStep = fineStep * (uQuality <= 0 ? 4.0 : 3.0);
                int lightSteps = uQuality >= 2 ? 4 : (uQuality <= 0 ? 2 : 3);
                float weatherLod = uQuality <= 0 ? 3.0 : 2.0;
                vec2 ignCoord = gl_FragCoord.xy + uFramePhase * vec2(47.0, 17.0);
                float jitter01 = fract(52.9829189 * fract(dot(ignCoord, vec2(0.06711056, 0.00583715))));
                float t = tEnter + jitter01 * fineStep;

                for (int i = 0; i < CLOUD_MAX_STEPS; ++i)
                {
                    if (i >= steps || t >= tExit)
                    {
                        break;
                    }

                    float sampleT = min(t + fineStep * 0.5, tExit);
                    vec3 worldPos = uCameraPos + rd * sampleT;
                    float conservative = vcCloudConservativeDensity(worldPos, planetCenter, planetRadius,
                        layerBaseAltitude, layerTopAltitude, uCoverageScale, uVolumeSize,
                        uCoverageMap, uHasCoverageMap, uWindOffset, weatherLod);
                    if (conservative <= 1e-4)
                    {
                        t += coarseStep;
                        continue;
                    }

                    float baseShape = vcCloudBaseDensity(worldPos, planetCenter, planetRadius,
                        layerBaseAltitude, layerTopAltitude, uCoverageScale, uVolumeSize,
                        uCloudNoise, uHasCloudNoise, uCoverageMap, uHasCoverageMap, uWindOffset, 0.0);
                    if (baseShape <= 1e-5)
                    {
                        t += fineStep;
                        continue;
                    }

                    float density = vcCloudDensityFromBase(baseShape, worldPos, planetCenter, planetRadius,
                        layerBaseAltitude, layerTopAltitude, uDensity, uVolumeSize,
                        uDetailNoise, uHasDetailNoise, uWindOffset) * slabHorizonVisibility;
                    if (density > 1e-5)
                    {
                        float segmentLength = min(fineStep, tExit - t);
                        float lightOd = vcLightOpticalDepthFromBase(baseShape, worldPos, sunToward,
                            planetCenter, planetRadius, layerBaseAltitude, layerTopAltitude,
                            uDensity, uCoverageScale, uVolumeSize, lightSteps,
                            uCloudNoise, uHasCloudNoise, uCoverageMap, uHasCoverageMap, uWindOffset);
                        float altitude = vcsAltitude(worldPos, planetCenter, planetRadius);
                        float hSample = saturate1((altitude - layerBaseAltitude) / max(uVolumeHeight, 0.001));
                        vec3 radiance = vcSunScatter(sunColor, cosTheta, lightOd);
                        float ambientVisibility = mix(0.38, 1.0, exp(-lightOd * 0.32));
                        // The shared condensation level stays comparatively shaded while
                        // the cauliflower tops receive progressively more skylight.
                        radiance += skyAmbient * mix(0.22, 0.82, hSample) * 0.62 * ambientVisibility;
                        float inscatterW = vmSegmentInscatterWeight(density, segmentLength, 1.1);
                        accum += transmittance * radiance * inscatterW;
                        transmittance *= vmSegmentTransmittance(density, segmentLength, 1.1);
                        if (!representativeFound || sampleT < representativeT)
                        {
                            representativeT = sampleT;
                            representativeKind = 0.5;
                            representativeFound = true;
                        }
                        if (transmittance < 0.03)
                        {
                            break;
                        }
                    }

                    t += fineStep;
                }
            }
        }

        if (uCirrusStrength > 0.0 && cirrusHit)
        {
            int cirrusSamples = uQuality >= 2 ? 2 : 1;
            float cirrusDensity = 0.0;
            float tCirrus = (cirrusSeg.x + cirrusSeg.y) * 0.5;
            for (int i = 0; i < 2; ++i)
            {
                if (i >= cirrusSamples)
                {
                    break;
                }

                float sampleFrac = cirrusSamples > 1 ? (float(i) + 0.5) / float(cirrusSamples) : 0.5;
                float sampleT = mix(cirrusSeg.x, cirrusSeg.y, sampleFrac);
                vec3 cirrusPos = uCameraPos + rd * sampleT;
                float sampleDensity = vcCirrusDensity(
                    cirrusPos.xz, uCirrusWindOffset, uCirrusWindDir, uVolumeSize);
                cirrusDensity += sampleDensity / float(cirrusSamples);
                if (sampleDensity > 1e-3)
                {
                    tCirrus = min(tCirrus, sampleT);
                }
            }
            if (cirrusDensity > 1e-3)
            {
                float slant = clamp((cirrusSeg.y - cirrusSeg.x) / cirrusThickness, 1.0, 3.0);
                float cirrusOd = cirrusDensity * uCirrusStrength * 0.27 * slant *
                    cirrusHorizonVisibility;
                float cirrusAlpha = 1.0 - exp(-cirrusOd);
                vec3 cirrusRad = vcSunScatter(sunColor, cosTheta, cirrusDensity * 0.62) * 0.42 +
                    skyAmbient * 0.54;
                accum += transmittance * cirrusRad * cirrusAlpha;
                transmittance *= 1.0 - cirrusAlpha;
                if (!representativeFound || tCirrus < representativeT)
                {
                    representativeT = tCirrus;
                    representativeKind = 1.0;
                    representativeFound = true;
                }
            }
        }

        float clearAmt = (1.0 - transmittance) * mix(0.35, 0.55, dayAmt);
        accum += skyAmbient * clearAmt;
        alpha = saturate1(1.0 - transmittance);
        // RGB is premultiplied volume radiance. Composite with ONE, ONE_MINUS_SRC_ALPHA.
        vec3 linearCloud = skySoftKnee(accum * uSkyExposure, 0.08);
        cloudCol = uHdrPresent > 0 ? linearCloud : linearToSrgb(linearCloud);

    }

    FragColor = vec4(cloudCol, alpha);
    FragCloudData = vec4(ctEncodeDistance(representativeT), representativeKind, 1.0);
}
