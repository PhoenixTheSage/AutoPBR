#version 330 core
// Flat continuous-world volumetric clouds with conservative empty-space marching.

//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/cloud_shell.glsl"
//!include "common/volumetric_clouds.glsl"
//!include "common/sparse_cloud_traversal.glsl"
//!include "common/cloud_light_cache.glsl"
//!include "common/volumetric_segment.glsl"
//!include "common/cloud_temporal.glsl"
//!include "common/ray_reconstruct.glsl"
//!include "common/cloud_scene_depth.glsl"

in vec2 vUv;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform vec3 uSunDir;
uniform float uSunIntensity;
uniform sampler2D uSkyViewLut;
uniform sampler3D uCloudNoise;
uniform sampler3D uDetailNoise;
uniform sampler3D uCloudStbn;
uniform sampler2D uCoverageMap;
uniform sampler2D uSceneDepth;
uniform sampler2DArray uCloudLightNear;
uniform sampler2DArray uCloudLightFar;
uniform float uGroundWorldY;
uniform float uPlanetRadius;
uniform float uLayerHeight;
uniform float uVolumeHeight;
uniform float uDensity;
uniform float uCoverageScale;
uniform float uVolumeSize;
uniform float uPixelAngularSize;
uniform vec3 uWindOffset;
uniform float uCirrusStrength;
uniform vec2 uCirrusWindOffset;
uniform vec2 uCirrusWindDir;
uniform int uQuality;
uniform int uMarchSteps;
uniform int uDebugView;
// Multi-deck / cirrus / style uniforms are declared in cloud_layer_envelope.glsl.

#ifdef GENESIS_CLOUD_QUALITY
#define CLOUD_QUALITY GENESIS_CLOUD_QUALITY
#else
#define CLOUD_QUALITY uQuality
#endif
uniform int uHasSceneDepth;
uniform int uHasCloudNoise;
uniform int uHasDetailNoise;
uniform int uHasCloudStbn;
uniform int uHasCoverageMap;
uniform int uHasSkyLut;
uniform int uCloudDataDirect;
uniform int uDensityAssetVersion;
uniform int uDensityAssetProfileCode;
uniform vec3 uCloudLightBasisRight;
uniform vec3 uCloudLightBasisUp;
uniform vec3 uCloudLightBasisForward;
uniform vec2 uCloudLightNearPlaneCenter;
uniform vec2 uCloudLightFarPlaneCenter;
uniform float uCloudLightNearWorldSpan;
uniform float uCloudLightFarWorldSpan;
uniform float uCloudLightNearDepthMin;
uniform float uCloudLightFarDepthMin;
uniform float uCloudLightNearDepthSpan;
uniform float uCloudLightFarDepthSpan;
uniform int uCloudLightNearDepth;
uniform int uCloudLightFarDepth;
uniform float uCloudLightNearOverlap;
uniform int uHasCloudLightNear;
uniform int uHasCloudLightFar;
uniform vec3 uCloudScatterOctave1;
uniform vec3 uCloudScatterOctave2;
uniform float uCloudScatterEnergyClamp;
uniform float uCloudCachedSkyVisibilityFloor;
uniform vec3 uCloudGroundBounceColor;
uniform float uCloudGroundBounceStrength;
uniform int uCloudLocalConeTapCount;
uniform float uCloudLocalConeRange;
uniform float uCloudLocalConeOpticalDepthScale;
uniform float uFramePhase;
uniform int uCloudFrameIndex;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 FragCloudData;

const int CLOUD_MAX_STEPS = 64;
const float CLOUD_MAX_TRACE_DISTANCE = 4096.0;
const float CLOUD_DISTANCE_FADE_FRACTION = 0.20;
const float CLOUD_STBN_WIDTH = 128.0;
const float CLOUD_STBN_HEIGHT = 128.0;
const float CLOUD_STBN_FRAMES = 64.0;
const float SKY_VIEW_LUT_WIDTH = 192.0;
const float SKY_VIEW_LUT_HEIGHT = 108.0;
const int CLOUD_DEBUG_WEATHER_COVERAGE = 1;
const int CLOUD_DEBUG_FINAL_DENSITY = 2;
const int CLOUD_DEBUG_WEATHER_TYPE = 3;
const int CLOUD_DEBUG_WEATHER_DENSITY = 4;
const int CLOUD_DEBUG_WEATHER_CONVECTION = 5;
const int CLOUD_DEBUG_SHAPE_R = 6;
const int CLOUD_DEBUG_SHAPE_A = 9;
const int CLOUD_DEBUG_DETAIL_R = 10;
const int CLOUD_DEBUG_DETAIL_A = 13;
const int CLOUD_DEBUG_SELECTED_LOD = 14;
const int CLOUD_DEBUG_BASE_DENSITY = 15;
const int CLOUD_DEBUG_ASSET_PROFILE = 16;
const int CLOUD_DEBUG_SPARSE_CLIPMAP_LEVEL = 17;
const int CLOUD_DEBUG_SPARSE_PAGE_STATE = 18;
const int CLOUD_DEBUG_SPARSE_PHYSICAL_BRICK = 19;
const int CLOUD_DEBUG_SPARSE_BASE_DENSITY = 20;
const int CLOUD_DEBUG_SPARSE_CONSERVATIVE_DISTANCE = 21;
const int CLOUD_DEBUG_SPARSE_TRAVERSAL_STEPS = 22;
const int CLOUD_DEBUG_SPARSE_FALLBACK = 23;
const int CLOUD_DEBUG_SPARSE_TEMPLATE_FAMILY = 24;
const int CLOUD_DEBUG_SPARSE_CASCADE_BLEND = 25;

float cloudDayFactor(vec3 lightPropagationDir, float sunIntensity)
{
    vec3 towardLight = normalize(-lightPropagationDir);
    float dayFromSun = smoothstep(-0.04, 0.22, towardLight.y);
    float dayFromIntensity = smoothstep(0.08, 2.0, sunIntensity);
    return clamp(dayFromSun * dayFromIntensity, 0.0, 1.0);
}

vec3 cloudNightZenith(vec3 viewDir)
{
    float gradient = clamp(viewDir.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.01, 0.012, 0.02), vec3(0.02, 0.035, 0.07), gradient);
}

float cloudPrimaryMarchJitter()
{
    if (uHasCloudStbn > 0 && CLOUD_QUALITY >= 2)
    {
        vec2 stbnPixel = mod(
            floor(gl_FragCoord.xy),
            vec2(CLOUD_STBN_WIDTH, CLOUD_STBN_HEIGHT));
        float frameSlice = mod(float(uCloudFrameIndex), CLOUD_STBN_FRAMES);
        vec3 stbnUv = vec3(
            (stbnPixel + vec2(0.5)) / vec2(CLOUD_STBN_WIDTH, CLOUD_STBN_HEIGHT),
            (frameSlice + 0.5) / CLOUD_STBN_FRAMES);
        float stbnValue = texture(uCloudStbn, stbnUv).r;
        // Sample at the center of the represented R8 rank interval, never exactly 0 or 1.
        return (stbnValue * 255.0 + 0.5) / 256.0;
    }

    vec2 ignCoord = gl_FragCoord.xy + uFramePhase * vec2(47.0, 17.0);
    return fract(52.9829189 * fract(dot(ignCoord, vec2(0.06711056, 0.00583715))));
}

vec3 cloudSampleSkyViewLutSrgb(sampler2D skyLut, vec3 viewDir)
{
    vec3 direction = normalize(viewDir);
    float azimuth = atan(direction.x, direction.z) * (0.5 / GEN_PI) + 0.5;
    float zenith = acos(clamp(direction.y, -1.0, 1.0)) / GEN_PI;
    vec2 lutSize = vec2(textureSize(skyLut, 0));
    lutSize = max(lutSize, vec2(SKY_VIEW_LUT_WIDTH, SKY_VIEW_LUT_HEIGHT));
    vec2 unitUv = vec2(fract(azimuth), clamp(zenith, 0.0, 1.0));
    vec2 texelUv = unitUv * ((lutSize - 1.0) / lutSize) + (0.5 / lutSize);
    return texture(skyLut, texelUv).rgb;
}

// Pull skylight toward luminance so cloud bodies stay neutral white and shaded
// regions read as grey rather than Rayleigh blue.
vec3 cloudNeutralSkyAmbient(vec3 skyAmbient)
{
    float lum = dot(skyAmbient, vec3(0.2126, 0.7152, 0.0722));
    return mix(skyAmbient, vec3(lum), 0.90);
}

vec3 sampleSkyAmbient(vec3 rd, sampler2D skyLut, int hasSkyLut, float dayAmt)
{
    vec3 night = cloudNightZenith(rd) * 2.0;
    if (hasSkyLut < 1)
    {
        return cloudNeutralSkyAmbient(mix(night, vec3(0.52, 0.53, 0.55), dayAmt));
    }

    vec3 ambientDir = normalize(vec3(rd.x * 0.35, max(rd.y, 0.45), rd.z * 0.35));
    vec3 lut = srgbToLinear(cloudSampleSkyViewLutSrgb(skyLut, ambientDir));
    // Flat layers can sustain much longer horizontal optical paths than the former curved
    // shell. Retain a conservative diffuse-sky floor so dense camera-inside views do not
    // collapse to unlit black after direct sunlight is fully extinguished.
    vec3 dayFloor = vec3(0.070, 0.072, 0.074);
    return cloudNeutralSkyAmbient(mix(night, max(lut, dayFloor), dayAmt));
}

vec3 cloudResolveCachedLighting(
    vec3 worldPosition,
    out vec3 weights)
{
    return cqlResolveLighting(
        uCloudLightNear,
        uCloudLightFar,
        worldPosition,
        uCloudLightBasisRight,
        uCloudLightBasisUp,
        uCloudLightBasisForward,
        uCloudLightNearPlaneCenter,
        uCloudLightFarPlaneCenter,
        uCloudLightNearWorldSpan,
        uCloudLightFarWorldSpan,
        uCloudLightNearDepthMin,
        uCloudLightFarDepthMin,
        uCloudLightNearDepthSpan,
        uCloudLightFarDepthSpan,
        uCloudLightNearDepth,
        uCloudLightFarDepth,
        uCloudLightNearOverlap,
        uHasCloudLightNear,
        uHasCloudLightFar,
        weights);
}

float cloudLocalConeDensity(
    vec3 worldPosition,
    vec3 planetCenter,
    float planetRadius,
    float layerBaseAltitude,
    float layerTopAltitude,
    float sampleFootprint)
{
    vec4 weather = vcSampleWeather(
        uCoverageMap,
        uHasCoverageMap,
        worldPosition,
        uVolumeSize,
        uWindOffset.xz,
        sampleFootprint,
        uDensityAssetVersion);
    float baseShape = vcCloudBaseDensityFromWeather(
        worldPosition,
        planetCenter,
        planetRadius,
        layerBaseAltitude,
        layerTopAltitude,
        uCoverageScale,
        uVolumeSize,
        uCloudNoise,
        uHasCloudNoise,
        uWindOffset,
        sampleFootprint,
        weather,
        uDensityAssetVersion);
    if (uHasSparseCloudTraversal > 0)
    {
        Cq45ResolvedBase sparseBase =
            cq45ResolveBaseDensity(worldPosition, baseShape);
        baseShape = sparseBase.density;
    }
    return vcCloudDensityFromBase(
        baseShape,
        worldPosition,
        planetCenter,
        planetRadius,
        layerBaseAltitude,
        layerTopAltitude,
        uDensity,
        uVolumeSize,
        uDetailNoise,
        uHasDetailNoise,
        uWindOffset,
        uCirrusWindDir,
        sampleFootprint,
        -0.35,
        3,
        weather.z,
        weather.w,
        uDensityAssetVersion);
}

float cloudCinematicLocalConeOpticalDepth(
    vec3 worldPosition,
    vec3 sunToward,
    vec3 planetCenter,
    float planetRadius,
    float layerBaseAltitude,
    float layerTopAltitude,
    float sampleFootprint)
{
    if (uCloudLocalConeTapCount < 2 ||
        uCloudLocalConeRange <= 1e-4)
    {
        return 0.0;
    }

    float rangeWorld = uCloudLocalConeRange;
    float distance0 = rangeWorld * 0.42;
    float distance1 = rangeWorld * 0.88;
    float coneFootprint = max(sampleFootprint, rangeWorld * 0.5);
    vec3 direction0 = normalize(
        sunToward +
        uCloudLightBasisRight * 0.075 +
        uCloudLightBasisUp * 0.035);
    vec3 direction1 = normalize(
        sunToward -
        uCloudLightBasisRight * 0.065 -
        uCloudLightBasisUp * 0.045);

    // Exactly two CQ2 explicit-LOD density samples. The farthest is below one near-cache
    // XY texel, so these refine local boundaries without recreating a long secondary march.
    float density0 = cloudLocalConeDensity(
        worldPosition + direction0 * distance0,
        planetCenter,
        planetRadius,
        layerBaseAltitude,
        layerTopAltitude,
        coneFootprint);
    float density1 = cloudLocalConeDensity(
        worldPosition + direction1 * distance1,
        planetCenter,
        planetRadius,
        layerBaseAltitude,
        layerTopAltitude,
        coneFootprint);
    return max(
        density0 * distance0 +
        density1 * (distance1 - distance0),
        0.0) * 0.18;
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

float cloudDebugChannel(vec4 value, int channel)
{
    if (channel == 0)
    {
        return value.r;
    }
    if (channel == 1)
    {
        return value.g;
    }
    if (channel == 2)
    {
        return value.b;
    }
    return value.a;
}

vec4 cloudDebugShapeCoordinates(
    vec3 worldPos,
    vec3 planetCenter,
    float planetRadius,
    float layerBase,
    float layerTop,
    vec4 weather)
{
    float altitude = vcsAltitude(worldPos, planetCenter.y + planetRadius);
    float h = saturate1(
        (altitude - layerBase) / max(layerTop - layerBase, 0.001));
    float sizeScale = max(uVolumeSize, 8.0) * 2.0;
    float type = saturate1(weather.y);
    float convection = uDensityAssetVersion >= 2
        ? saturate1(weather.w)
        : 0.0;
    float convectionLift = type * convection;
    float horizontalScale = sizeScale * mix(1.16, 0.78, type);
    if (uDensityAssetVersion >= 2)
    {
        horizontalScale *= mix(1.04, 0.86, convectionLift);
    }

    vec2 upperDrift = vec2(0.19, -0.13) *
        (h * h * type * horizontalScale) *
        mix(1.0, 1.42, convection);
    vec2 shapeXz =
        (worldPos.xz + uWindOffset.xz + upperDrift) /
        horizontalScale;
    float shapeY =
        h * mix(0.34, 0.86, type) +
        uWindOffset.y / sizeScale;
    if (uDensityAssetVersion >= 2)
    {
        shapeY += h * h * convectionLift * 0.08;
    }

    vec3 shapeUvw = fract(vec3(shapeXz.x, shapeY, shapeXz.y));
    return vec4(shapeUvw, horizontalScale);
}

vec4 cloudDebugDetailCoordinates(vec3 worldPos)
{
    float detailRepeatSize = max(uVolumeSize, 8.0) * 0.5;
    vec3 detailWorld = worldPos + uWindOffset * 0.5;
    return vec4(fract(detailWorld / detailRepeatSize), detailRepeatSize);
}

vec3 cloudDebugAssetProfileColor(int profileCode)
{
    // 1=v2 bundled, 2=v1 compatibility policy, 3=v1 fallback,
    // 4=runtime-generated v1, 5=procedural shader fallback.
    if (profileCode == 1)
    {
        return vec3(0.08, 0.92, 0.48);
    }
    if (profileCode == 2)
    {
        return vec3(0.10, 0.48, 1.00);
    }
    if (profileCode == 3)
    {
        return vec3(1.00, 0.55, 0.06);
    }
    if (profileCode == 4)
    {
        return vec3(0.78, 0.20, 0.96);
    }
    if (profileCode == 5)
    {
        return vec3(1.00, 0.08, 0.08);
    }
    return vec3(0.35);
}

void main()
{
    vec3 rd = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    float planetRadius = max(uPlanetRadius, 1.0);
    vec3 planetCenter = vec3(0.0, uGroundWorldY - planetRadius, 0.0);
    float layerBaseAltitude = max(uLayerHeight - uGroundWorldY, 0.01);
    float layerTopAltitude = layerBaseAltitude + max(uVolumeHeight, 0.01);
    VcCloudAltitudeStack altitudeStack = vcBuildCloudAltitudeStack(
        layerBaseAltitude,
        uVolumeHeight,
        uCumulusLayerCount,
        uInterDeckGap,
        uHeightVariance,
        uUpperThicknessScale,
        uCirrusGap,
        uCirrusThickness,
        uCirrusStrength);
    // Opaque scene depth remains hard. March each cumulus deck as its own altitude
    // slab so large inter-deck gaps do not inflate the step lattice.
    float sceneT = cloudSceneDistance(rd);
    vec2 deckSegs[CLOUD_LAYER_ENVELOPE_MAX_DECKS];
    float deckThicknesses[CLOUD_LAYER_ENVELOPE_MAX_DECKS];
    float deckDistanceVisibility[CLOUD_LAYER_ENVELOPE_MAX_DECKS];
    int deckIds[CLOUD_LAYER_ENVELOPE_MAX_DECKS];
    int deckHitCount = 0;
    float tEnter = 0.0;
    float tExit = 0.0;
    float slabDistanceVisibility = 0.0;
    for (int deckIndex = 0; deckIndex < CLOUD_LAYER_ENVELOPE_MAX_DECKS; ++deckIndex)
    {
        float supportBase;
        float supportTop;
        float deckThickness;
        if (!vcTryGetCumulusDeckSupportBand(
                deckIndex,
                layerBaseAltitude,
                uVolumeHeight,
                supportBase,
                supportTop,
                deckThickness))
        {
            continue;
        }

        vec2 deckSeg = vcsIntersectAltitudeSlab(
            uCameraPos,
            rd,
            uGroundWorldY,
            supportBase,
            supportTop,
            CLOUD_MAX_TRACE_DISTANCE);
        deckSeg.y = min(deckSeg.y, sceneT);
        float deckVis = vcsDistanceVisibility(
            deckSeg.x, CLOUD_MAX_TRACE_DISTANCE, CLOUD_DISTANCE_FADE_FRACTION);
        if (deckSeg.y > deckSeg.x && deckVis > 1e-3)
        {
            deckSegs[deckHitCount] = deckSeg;
            deckThicknesses[deckHitCount] = deckThickness;
            deckDistanceVisibility[deckHitCount] = deckVis;
            deckIds[deckHitCount] = deckIndex;
            if (deckHitCount == 0 || deckSeg.x < tEnter)
            {
                tEnter = deckSeg.x;
                slabDistanceVisibility = deckVis;
            }
            if (deckHitCount == 0 || deckSeg.y > tExit)
            {
                tExit = deckSeg.y;
            }
            deckHitCount++;
        }
    }

    // Front-to-back order so transmittance accumulates correctly for any camera height.
    for (int i = 0; i < CLOUD_LAYER_ENVELOPE_MAX_DECKS; ++i)
    {
        if (i >= deckHitCount)
        {
            break;
        }
        for (int j = i + 1; j < CLOUD_LAYER_ENVELOPE_MAX_DECKS; ++j)
        {
            if (j >= deckHitCount)
            {
                break;
            }
            if (deckSegs[j].x < deckSegs[i].x)
            {
                vec2 swapSeg = deckSegs[i];
                deckSegs[i] = deckSegs[j];
                deckSegs[j] = swapSeg;
                float swapThickness = deckThicknesses[i];
                deckThicknesses[i] = deckThicknesses[j];
                deckThicknesses[j] = swapThickness;
                float swapVis = deckDistanceVisibility[i];
                deckDistanceVisibility[i] = deckDistanceVisibility[j];
                deckDistanceVisibility[j] = swapVis;
                int swapId = deckIds[i];
                deckIds[i] = deckIds[j];
                deckIds[j] = swapId;
            }
        }
    }

    bool slabHit = deckHitCount > 0;

    float cirrusAltitude = altitudeStack.cirrusBase;
    float cirrusThickness = max(altitudeStack.cirrusThickness, 0.0);
    float cirrusSupportGuard = clamp(max(cirrusThickness, 0.1) * 0.20, 0.25, 0.75);
    vec2 cirrusSeg = vcsIntersectAltitudeSlab(
        uCameraPos,
        rd,
        uGroundWorldY,
        cirrusAltitude - cirrusSupportGuard,
        cirrusAltitude + cirrusThickness + cirrusSupportGuard,
        CLOUD_MAX_TRACE_DISTANCE);
    cirrusSeg.y = min(cirrusSeg.y, sceneT);
    float cirrusDistanceVisibility = vcsDistanceVisibility(
        cirrusSeg.x, CLOUD_MAX_TRACE_DISTANCE, CLOUD_DISTANCE_FADE_FRACTION);
    bool cirrusHit =
        cirrusThickness > 1e-4 &&
        cirrusSeg.y > cirrusSeg.x &&
        cirrusDistanceVisibility > 1e-3;

    if (!slabHit && (!cirrusHit || uCirrusStrength <= 0.0))
    {
        discard;
    }
    if (uDebugView != 0 && !slabHit)
    {
        // Density-map inspectors describe the cumulus shell only. Do not allow the
        // independently procedural cirrus sheet to leak into a selected debug view.
        discard;
    }

    vec3 cloudCol = vec3(0.0);
    float alpha = 0.0;
    bool debugViewActive = false;

    if (uDebugView != 0 && slabHit)
    {
        float tSample = (tEnter + tExit) * 0.5;
        vec3 pos = uCameraPos + rd * tSample;
        vec4 weather = vcSampleWeather(
            uCoverageMap,
            uHasCoverageMap,
            pos,
            uVolumeSize,
            uWindOffset.xz,
            0.0,
            uDensityAssetVersion);
        float debugValue = 0.0;
        bool scalarView = true;

        if (uDebugView >= CLOUD_DEBUG_WEATHER_COVERAGE &&
            uDebugView <= CLOUD_DEBUG_WEATHER_CONVECTION)
        {
            int weatherChannel = uDebugView == CLOUD_DEBUG_FINAL_DENSITY
                ? -1
                : (uDebugView == CLOUD_DEBUG_WEATHER_COVERAGE
                    ? 0
                    : uDebugView - 2);
            if (weatherChannel >= 0)
            {
                debugValue = cloudDebugChannel(weather, weatherChannel);
            }
        }

        if (uDebugView >= CLOUD_DEBUG_SHAPE_R &&
            uDebugView <= CLOUD_DEBUG_SHAPE_A)
        {
            if (uHasCloudNoise > 0)
            {
                vec4 shapeCoordinates = cloudDebugShapeCoordinates(
                    pos,
                    planetCenter,
                    planetRadius,
                    layerBaseAltitude,
                    layerTopAltitude,
                    weather);
                vec4 shapeChannels = textureLod(
                    uCloudNoise,
                    shapeCoordinates.xyz,
                    0.0);
                debugValue = cloudDebugChannel(
                    shapeChannels,
                    uDebugView - CLOUD_DEBUG_SHAPE_R);
            }
            else
            {
                scalarView = false;
                cloudCol = vec3(1.0, 0.0, 1.0);
            }
        }
        else if (uDebugView >= CLOUD_DEBUG_DETAIL_R &&
            uDebugView <= CLOUD_DEBUG_DETAIL_A)
        {
            if (uHasDetailNoise > 0)
            {
                vec4 detailCoordinates = cloudDebugDetailCoordinates(pos);
                vec4 detailChannels = textureLod(
                    uDetailNoise,
                    detailCoordinates.xyz,
                    0.0);
                debugValue = cloudDebugChannel(
                    detailChannels,
                    uDebugView - CLOUD_DEBUG_DETAIL_R);
            }
            else
            {
                scalarView = false;
                cloudCol = vec3(1.0, 0.0, 1.0);
            }
        }
        else if (uDebugView == CLOUD_DEBUG_SELECTED_LOD)
        {
            scalarView = false;
            int steps = uMarchSteps > 0
                ? clamp(uMarchSteps, 1, CLOUD_MAX_STEPS)
                : (CLOUD_QUALITY <= 0
                    ? 16
                    : (CLOUD_QUALITY >= 3
                        ? 48
                        : (CLOUD_QUALITY >= 2 ? 32 : 24)));
            float marchStep = vcsMarchStepLength(
                tEnter,
                tExit,
                steps,
                uVolumeSize,
                uVolumeHeight);
            float sampleFootprint = max(
                marchStep,
                tSample * uPixelAngularSize);
            vec4 shapeCoordinates = cloudDebugShapeCoordinates(
                pos,
                planetCenter,
                planetRadius,
                layerBaseAltitude,
                layerTopAltitude,
                weather);
            vec4 detailCoordinates = cloudDebugDetailCoordinates(pos);

            float shapeLod = 0.0;
            float shapeMaxMip = 1.0;
            if (uHasCloudNoise > 0)
            {
                ivec3 shapeSize = textureSize(uCloudNoise, 0);
                float shapeDimension = float(max(
                    shapeSize.x,
                    max(shapeSize.y, shapeSize.z)));
                shapeLod = vcCloudRayFootprintLod(
                    sampleFootprint,
                    shapeCoordinates.w,
                    shapeDimension,
                    0.0);
                shapeMaxMip = max(floor(log2(shapeDimension)), 1.0);
            }

            float detailLod = 0.0;
            float detailMaxMip = 1.0;
            if (uHasDetailNoise > 0)
            {
                ivec3 detailSize = textureSize(uDetailNoise, 0);
                float detailDimension = float(max(
                    detailSize.x,
                    max(detailSize.y, detailSize.z)));
                float detailBias = CLOUD_QUALITY >= 3 ? -0.35 : 0.0;
                detailLod = vcCloudRayFootprintLod(
                    sampleFootprint,
                    detailCoordinates.w,
                    detailDimension,
                    detailBias);
                detailMaxMip = max(floor(log2(detailDimension)), 1.0);
            }

            float shapeNormalized = shapeLod / shapeMaxMip;
            float detailNormalized = detailLod / detailMaxMip;
            cloudCol = vec3(
                shapeNormalized,
                detailNormalized,
                max(shapeNormalized, detailNormalized));
        }
        else if (uDebugView == CLOUD_DEBUG_BASE_DENSITY)
        {
            debugValue = vcCloudBaseDensityFromWeather(
                pos,
                planetCenter,
                planetRadius,
                layerBaseAltitude,
                layerTopAltitude,
                uCoverageScale,
                uVolumeSize,
                uCloudNoise,
                uHasCloudNoise,
                uWindOffset,
                0.0,
                weather,
                uDensityAssetVersion);
        }
        else if (uDebugView == CLOUD_DEBUG_FINAL_DENSITY)
        {
            debugValue = vcCloudDensityEx(
                pos,
                planetCenter,
                planetRadius,
                layerBaseAltitude,
                layerTopAltitude,
                uDensity,
                uCoverageScale,
                uVolumeSize,
                uCloudNoise,
                uHasCloudNoise,
                uDetailNoise,
                uHasDetailNoise,
                uCoverageMap,
                uHasCoverageMap,
                uWindOffset,
                uCirrusWindDir,
                0.0,
                0.0,
                CLOUD_QUALITY,
                uDensityAssetVersion);
        }
        else if (uDebugView == CLOUD_DEBUG_ASSET_PROFILE)
        {
            scalarView = false;
            cloudCol = cloudDebugAssetProfileColor(
                uDensityAssetProfileCode);
        }
        else if (uDebugView >= CLOUD_DEBUG_SPARSE_CLIPMAP_LEVEL &&
            uDebugView <= CLOUD_DEBUG_SPARSE_CASCADE_BLEND)
        {
            scalarView = false;
            if (uHasSparseCloudTraversal < 1)
            {
                cloudCol = vec3(0.15, 0.15, 0.18);
            }
            else
            {
                Cq45ResolvedBase sparseBase =
                    cq45ResolveBaseDensity(pos, 0.0);
                Cq45LevelSample level0 = cq45SampleLevel(0, pos);
                Cq45LevelSample level1 = cq45SampleLevel(1, pos);
                Cq45LevelSample level2 = cq45SampleLevel(2, pos);
                Cq45LevelSample finest =
                    level0.resident > 0.5
                        ? level0
                        : (level1.resident > 0.5
                            ? level1
                            : level2);
                if (uDebugView == CLOUD_DEBUG_SPARSE_CLIPMAP_LEVEL)
                {
                    float level = max(sparseBase.selectedLevel, 0.0);
                    cloudCol = level < 0.5
                        ? vec3(0.15, 0.85, 0.35)
                        : (level < 1.5
                            ? vec3(0.95, 0.75, 0.15)
                            : vec3(0.95, 0.35, 0.15));
                    if (sparseBase.shellWeight > 0.5)
                    {
                        cloudCol = mix(cloudCol, vec3(0.35, 0.55, 0.95), 0.65);
                    }
                }
                else if (uDebugView == CLOUD_DEBUG_SPARSE_PAGE_STATE)
                {
                    uint page = finest.pageValue;
                    if (finest.resident > 0.5)
                    {
                        cloudCol = vec3(0.20, 0.85, 0.35);
                    }
                    else if (page == CQ45_REQUESTED_PAGE)
                    {
                        cloudCol = vec3(0.95, 0.75, 0.15);
                    }
                    else
                    {
                        cloudCol = vec3(0.25, 0.30, 0.40);
                    }
                }
                else if (uDebugView == CLOUD_DEBUG_SPARSE_PHYSICAL_BRICK)
                {
                    float util = finest.resident > 0.5
                        ? float((finest.pageValue - 1u) % 64u) / 63.0
                        : 0.0;
                    cloudCol = finest.resident > 0.5
                        ? vec3(util, 1.0 - util, 0.35 + 0.45 * util)
                        : vec3(0.12, 0.12, 0.14);
                }
                else if (uDebugView == CLOUD_DEBUG_SPARSE_BASE_DENSITY)
                {
                    scalarView = true;
                    debugValue = sparseBase.density;
                }
                else if (uDebugView == CLOUD_DEBUG_SPARSE_CONSERVATIVE_DISTANCE)
                {
                    scalarView = true;
                    float voxel = max(finest.voxelWorldSize, 0.001);
                    debugValue = saturate1(
                        finest.distanceWorld / (voxel * 32.0));
                }
                else if (uDebugView == CLOUD_DEBUG_SPARSE_TRAVERSAL_STEPS)
                {
                    Cq45TraversalResult steps =
                        cq45TraverseToCandidate(
                            uCameraPos,
                            rd,
                            tEnter,
                            tExit,
                            max(uVolumeSize, 8.0) * 0.02);
                    float pageN = saturate1(float(steps.pageSteps) / 16.0);
                    float distN = saturate1(float(steps.distanceSteps) / 32.0);
                    float fineN = saturate1(float(steps.fineSteps) / 16.0);
                    cloudCol = vec3(pageN, distN, fineN);
                }
                else if (uDebugView == CLOUD_DEBUG_SPARSE_FALLBACK)
                {
                    float fallback =
                        saturate1(
                            sparseBase.shellWeight +
                            (1.0 - sparseBase.resident));
                    cloudCol = mix(
                        vec3(0.20, 0.85, 0.35),
                        vec3(0.95, 0.35, 0.85),
                        fallback);
                }
                else if (uDebugView == CLOUD_DEBUG_SPARSE_TEMPLATE_FAMILY)
                {
                    // Weather G approximates the CQ4.1 family selector used by brick generation.
                    float family = saturate1(weather.g);
                    cloudCol = family < 0.25
                        ? vec3(0.55, 0.85, 0.35)
                        : (family < 0.50
                            ? vec3(0.35, 0.75, 0.95)
                            : (family < 0.75
                                ? vec3(0.95, 0.55, 0.25)
                                : vec3(0.75, 0.75, 0.80)));
                }
                else
                {
                    cloudCol = vec3(
                        saturate1((sparseBase.selectedLevel + 1.0) / 3.0),
                        saturate1(1.0 - sparseBase.shellWeight),
                        saturate1(sparseBase.shellWeight));
                }
            }
        }

        if (scalarView)
        {
            cloudCol = vec3(saturate1(debugValue));
        }

        alpha = 0.95;
        cloudCol *= slabDistanceVisibility;
        alpha *= slabDistanceVisibility;
        debugViewActive = true;
    }

    float representativeT = slabHit ? tExit : max(cirrusSeg.x, 0.0);
    float representativeKind = slabHit ? 0.0 : 1.0;
    bool representativeFound = false;
    if (!debugViewActive)
    {
        vec3 sunToward = normalize(-uSunDir);
        float cosTheta = dot(rd, sunToward);
        float dayAmt = cloudDayFactor(uSunDir, uSunIntensity);
        vec3 sunColor = vcCloudSunColor(sunToward, uSunIntensity);
        vec3 cq34PhaseTerms = vcSunScatterPhaseTerms(
            cosTheta,
            uCloudScatterOctave1,
            uCloudScatterOctave2,
            true);
        vec3 skyAmbient = sampleSkyAmbient(rd, uSkyViewLut, uHasSkyLut, dayAmt);
        vec3 accum = vec3(0.0);
        float transmittance = 1.0;
        float cacheDepthJitter = cloudPrimaryMarchJitter();

        if (slabHit)
        {
            int totalSteps = uMarchSteps > 0
                ? clamp(uMarchSteps, 1, CLOUD_MAX_STEPS)
                : (CLOUD_QUALITY <= 0
                    ? 16
                    : (CLOUD_QUALITY >= 3
                        ? 48
                        : (CLOUD_QUALITY >= 2 ? 32 : 24)));
            int remainingDeckPasses = deckHitCount;
            for (int deckPass = 0; deckPass < CLOUD_LAYER_ENVELOPE_MAX_DECKS; ++deckPass)
            {
                if (deckPass >= deckHitCount || transmittance < 0.03)
                {
                    break;
                }

                tEnter = deckSegs[deckPass].x;
                tExit = deckSegs[deckPass].y;
                slabDistanceVisibility = deckDistanceVisibility[deckPass];
                float activeDeckHeight = max(deckThicknesses[deckPass], 0.01);
                bool allowSparseDeck = deckIds[deckPass] == 0 && uHasSparseCloudTraversal > 0;
                int steps = clamp(
                    max(totalSteps / max(remainingDeckPasses, 1), CLOUD_QUALITY <= 0 ? 12 : 16),
                    1,
                    CLOUD_MAX_STEPS);
                remainingDeckPasses = max(remainingDeckPasses - 1, 0);
                float deckTransmittanceStart = transmittance;
                vec3 deckAccumStart = accum;


                        // A few weather-map taps reject wholly clear rays before any 3D texture access.
                        // Near taps keep local banks visible across eye-height; full-interval taps keep
                        // distant inside-layer formations from being early-out'd by the march-span cap.
                        float covMax = 0.0;
                        float interval = max(tExit - tEnter, 0.0);
                        float nearSpan = min(
                            interval,
                            vcsMarchSpanLimit(uVolumeSize, activeDeckHeight));
                        for (int i = 0; i < 4; ++i)
                        {
                            float tCov = i < 2
                                ? tEnter + nearSpan * ((float(i) + 0.5) / 2.0)
                                : mix(tEnter, tExit, (float(i - 2) + 0.5) / 2.0);
                            vec3 covPos = uCameraPos + rd * tCov;
                            float coverageFootprint = max(
                                (i < 2 ? nearSpan : interval) / 2.0,
                                tCov * uPixelAngularSize);
                            covMax = max(covMax,
                                vcSampleWeather(
                                    uCoverageMap,
                                    uHasCoverageMap,
                                    covPos,
                                    uVolumeSize,
                                    uWindOffset.xz,
                                    coverageFootprint,
                                    uDensityAssetVersion).x);
                        }

                        bool hasCumulus = covMax * uCoverageScale > 1e-3;
                        if (hasCumulus)
                        {
                            // steps already split across hit decks in the outer pass.
                            // Short and long intervals share one camera-region-independent policy. Long
                            // grazing rays keep a bounded near step, then grow across their actual interval
                            // so crossing into the slab cannot select a different integrator.
                            float marchInterval = max(tExit - tEnter, 0.0);
                            float marchSpanLimit =
                                vcsMarchSpanLimit(uVolumeSize, activeDeckHeight);
                            // Near-horizontal rays spend many samples at nearly constant
                            // altitude; tighten the near lattice so depth banding does not
                            // read as horizontal slices on cloud faces.
                            float grazingTighten = mix(
                                0.48,
                                1.0,
                                smoothstep(0.025, 0.20, abs(rd.y)));
                            float sizedSpan = min(marchInterval, marchSpanLimit) * grazingTighten;
                            float baseStep = max(sizedSpan / float(max(steps, 1)), 0.01);
                            // Cover the full interval with an arithmetic ramp. Cap growth so
                            // far samples stay dense enough to avoid slice banding, and bump
                            // the local step count when the cap would otherwise undersample.
                            float maxFarStep = max(baseStep * 2.75, activeDeckHeight * 0.28);
                            float farStepIdeal = max(
                                baseStep,
                                marchInterval * 2.0 / float(max(steps, 1)) - baseStep);
                            bool longSlabMarch = marchInterval > marchSpanLimit + 1e-3;
                            float farStep = farStepIdeal;
                            if (longSlabMarch && farStepIdeal > maxFarStep + 1e-4)
                            {
                                float avgNeeded = 0.5 * (baseStep + maxFarStep);
                                steps = clamp(
                                    int(ceil(marchInterval / max(avgNeeded, 0.01))),
                                    steps,
                                    CLOUD_MAX_STEPS);
                                farStep = min(
                                    maxFarStep,
                                    max(
                                        baseStep,
                                        marchInterval * 2.0 / float(max(steps, 1)) - baseStep));
                            }
                            int lightSteps = CLOUD_QUALITY >= 2 ? 4 : (CLOUD_QUALITY <= 0 ? 2 : 3);
                            float detailLodBias = CLOUD_QUALITY >= 3 ? -0.35 : 0.0;
                            float jitter01 = cacheDepthJitter;
                            // Anchor the first cell to ray distance instead of shell entry. A grazing
                            // entry can move tens of units while the camera crosses only centimeters.
                            float phaseDistance = jitter01 * baseStep;
                            float t = tEnter + mod(phaseDistance - tEnter, baseStep);

                            for (int i = 0; i < CLOUD_MAX_STEPS; ++i)
                            {
                                if (i >= steps || t >= tExit)
                                {
                                    break;
                                }

                                float stepLen = longSlabMarch
                                    ? mix(
                                        baseStep,
                                        farStep,
                                        float(i) / float(max(steps - 1, 1)))
                                    : baseStep;
                                if (allowSparseDeck)
                                {
                                    Cq45TraversalResult sparseTraversal =
                                        cq45TraverseToCandidate(
                                            uCameraPos,
                                            rd,
                                            t,
                                            tExit,
                                            stepLen);
                                    if (sparseTraversal.found < 0.5)
                                    {
                                        break;
                                    }

                                    // CQ4.6 enables this only after CQ1 history and both CQ3 lighting
                                    // cascades commit the same published sparse-density identity.
                                    t = max(t, sparseTraversal.t);
                                }

                                float emptyLen = max(
                                    stepLen * (CLOUD_QUALITY <= 0 ? 4.0 : 3.0),
                                    CLOUD_MAX_TRACE_DISTANCE / float(CLOUD_MAX_STEPS));
                                float sampleT = min(t + stepLen * 0.5, tExit);
                                vec3 worldPos = uCameraPos + rd * sampleT;
                                float pixelFootprint = sampleT * uPixelAngularSize;
                                float sampleFootprint = max(stepLen, pixelFootprint);
                                float conservativeFootprint = max(emptyLen, pixelFootprint);
                                float conservative = vcCloudConservativeDensity(worldPos, planetCenter, planetRadius,
                                    layerBaseAltitude, layerTopAltitude, uCoverageScale, uVolumeSize,
                                    uCoverageMap, uHasCoverageMap, uWindOffset, conservativeFootprint,
                                    uDensityAssetVersion);
                                if (allowSparseDeck)
                                {
                                    // Sparse page/SDF traversal has already performed the conservative
                                    // rejection. Do not let the procedural weather upper bound erase a
                                    // valid resident envelope before cascade blending.
                                    conservative = 1.0;
                                }
                                if (conservative <= 1e-4)
                                {
                                    t += emptyLen;
                                    continue;
                                }

                                vec4 weather = vcSampleWeather(
                                    uCoverageMap,
                                    uHasCoverageMap,
                                    worldPos,
                                    uVolumeSize,
                                    uWindOffset.xz,
                                    sampleFootprint,
                                    uDensityAssetVersion);
                                float baseShape = vcCloudBaseDensityFromWeather(
                                    worldPos,
                                    planetCenter,
                                    planetRadius,
                                    layerBaseAltitude,
                                    layerTopAltitude,
                                    uCoverageScale,
                                    uVolumeSize,
                                    uCloudNoise,
                                    uHasCloudNoise,
                                    uWindOffset,
                                    sampleFootprint,
                                    weather,
                                    uDensityAssetVersion);
                                if (allowSparseDeck)
                                {
                                    Cq45ResolvedBase sparseBase =
                                        cq45ResolveBaseDensity(worldPos, baseShape);
                                    baseShape = sparseBase.density;
                                }
                                if (baseShape <= 1e-5)
                                {
                                    t += stepLen;
                                    continue;
                                }

                                float density = vcCloudDensityFromBase(baseShape, worldPos, planetCenter, planetRadius,
                                    layerBaseAltitude, layerTopAltitude, uDensity, uVolumeSize,
                                    uDetailNoise, uHasDetailNoise, uWindOffset, uCirrusWindDir,
                                    sampleFootprint, detailLodBias, CLOUD_QUALITY,
                                    weather.z, weather.w, uDensityAssetVersion);
                                if (density > 1e-5)
                                {
                                    float segmentLength = min(stepLen, tExit - t);
                                    vec3 cloudLightWeights;
                                    vec3 cachedLighting = CLOUD_QUALITY >= 2
                                        ? cloudResolveCachedLighting(
                                            worldPos,
                                            cloudLightWeights)
                                        : vec3(0.0, 1.0, 0.0);
                                    bool useCachedLighting = cachedLighting.z > 0.5;
                                    float lightOd = useCachedLighting
                                        ? cachedLighting.x
                                        : vcLightOpticalDepthFromBase(baseShape,
                                            worldPos,
                                            sunToward,
                                            planetCenter,
                                            planetRadius,
                                            layerBaseAltitude,
                                            layerTopAltitude,
                                            uDensity,
                                            uCoverageScale,
                                            uVolumeSize,
                                            lightSteps,
                                            uCloudNoise,
                                            uHasCloudNoise,
                                            uCoverageMap,
                                            uHasCoverageMap,
                                            uWindOffset,
                                            sampleFootprint,
                                            uDensityAssetVersion);
                                    float localConeOpticalDepth = 0.0;
                                    // Sparse CQ4 envelopes are harder isosurfaces than the procedural
                                    // shell. The original 0.18..0.62 cone gate stayed open across a thick
                                    // shell and, with Cinematic local taps, inked every lobe like cell
                                    // shading. Keep a thinner rim while sparse density is active.
                                    float boundaryWeight = allowSparseDeck
                                        ? (1.0 - smoothstep(0.05, 0.26, baseShape))
                                        : (1.0 - smoothstep(0.18, 0.62, baseShape));
                                    if (useCachedLighting &&
                                        CLOUD_QUALITY >= 3 &&
                                        boundaryWeight > 1e-3)
                                    {
                                        float coneScale = uCloudLocalConeOpticalDepthScale;
                                        if (allowSparseDeck)
                                        {
                                            coneScale *= 0.42;
                                        }
                                        localConeOpticalDepth =
                                            cloudCinematicLocalConeOpticalDepth(
                                                worldPos,
                                                sunToward,
                                                planetCenter,
                                                planetRadius,
                                                layerBaseAltitude,
                                                layerTopAltitude,
                                                sampleFootprint) *
                                            boundaryWeight *
                                            coneScale;
                                    }
                                    float altitude = vcsAltitude(worldPos, uGroundWorldY);
                                    float hSample = saturate1(
                                        (altitude - layerBaseAltitude) /
                                        max(uVolumeHeight, 0.001));
                                    float skyVisibility = useCachedLighting
                                        ? cachedLighting.y
                                        : exp(-lightOd * 0.32);
                                    vec3 radiance = useCachedLighting
                                        ? vcSunScatterCq34(
                                            sunColor,
                                            cq34PhaseTerms,
                                            lightOd,
                                            skyVisibility,
                                            localConeOpticalDepth,
                                            uCloudScatterOctave1,
                                            uCloudScatterOctave2,
                                            uCloudScatterEnergyClamp)
                                        : vcSunScatter(
                                            sunColor,
                                            cosTheta,
                                            lightOd);
                                    float ambientVisibility = useCachedLighting
                                        ? mix(
                                            uCloudCachedSkyVisibilityFloor,
                                            1.0,
                                            skyVisibility)
                                        : mix(
                                            0.20,
                                            1.0,
                                            skyVisibility);
                                    // Condensation bases stay darker grey; tops pick up more neutral skylight.
                                    radiance +=
                                        skyAmbient *
                                        mix(0.10, 0.78, hSample) *
                                        0.52 *
                                        ambientVisibility;
                                    if (useCachedLighting)
                                    {
                                        vec3 localUp = vec3(0.0, 1.0, 0.0);
                                        float upwardHemisphereWeight = smoothstep(
                                            -0.15,
                                            0.65,
                                            dot(rd, localUp));
                                        float lowerAltitudeProfile =
                                            1.0 - smoothstep(0.28, 0.67, hSample);
                                        radiance +=
                                            uCloudGroundBounceColor *
                                            uCloudGroundBounceStrength *
                                            upwardHemisphereWeight *
                                            lowerAltitudeProfile *
                                            skyVisibility;
                                    }
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

                                t += stepLen;
                            }
                        }

                        // Fade only this deck's contribution when near the distance cutoff.
                        if (slabDistanceVisibility < 1.0 &&
                            deckTransmittanceStart > transmittance + 1e-5)
                        {
                            vec3 deckDelta = accum - deckAccumStart;
                            accum = deckAccumStart + deckDelta * slabDistanceVisibility;
                            float deckFactor =
                                transmittance / max(deckTransmittanceStart, 1e-4);
                            float fadedFactor = mix(1.0, deckFactor, slabDistanceVisibility);
                            transmittance = deckTransmittanceStart * fadedFactor;
                        }

            }
        }

        if (uCirrusStrength > 0.0 && cirrusHit)
        {
            int cirrusSamples = CLOUD_QUALITY >= 3 ? 4 : 2;
            float cirrusDensity = 0.0;
            float cirrusColumnDensity = 0.0;
            float cirrusProfileWeight = 0.0;
            float tCirrus = (cirrusSeg.x + cirrusSeg.y) * 0.5;
            float cirrusSampleLength =
                (cirrusSeg.y - cirrusSeg.x) / float(cirrusSamples);
            for (int i = 0; i < 4; ++i)
            {
                if (i >= cirrusSamples)
                {
                    break;
                }

                float sampleFrac = cirrusSamples > 1 ? (float(i) + 0.5) / float(cirrusSamples) : 0.5;
                float sampleT = mix(cirrusSeg.x, cirrusSeg.y, sampleFrac);
                vec3 cirrusPos = uCameraPos + rd * sampleT;
                float cirrusSampleFootprint = max(
                    max(
                        (cirrusSeg.y - cirrusSeg.x) / float(cirrusSamples),
                        0.01),
                    sampleT * uPixelAngularSize);
                float sampleDensity = vcCirrusDensityWithDetail(
                    cirrusPos.xz,
                    uCirrusWindOffset,
                    uCirrusWindDir,
                    uVolumeSize,
                    uDetailNoise,
                    uHasDetailNoise,
                    cirrusSampleFootprint,
                    CLOUD_QUALITY >= 3 ? -0.35 : 0.0,
                    CLOUD_QUALITY,
                    uDensityAssetVersion);
                float cirrusSampleAltitude =
                    vcsAltitude(cirrusPos, uGroundWorldY);
                float cirrusHeight = saturate1(
                    (cirrusSampleAltitude - cirrusAltitude) /
                    max(cirrusThickness, 0.01));
                float cirrusVerticalProfile =
                    smoothstep(0.0, 0.18, cirrusHeight) *
                    (1.0 - smoothstep(0.72, 1.0, cirrusHeight));
                float pathWeight =
                    cirrusVerticalProfile *
                    cirrusSampleLength /
                    max(cirrusThickness, 0.01);
                cirrusColumnDensity += sampleDensity * pathWeight;
                cirrusProfileWeight += pathWeight;
                if (sampleDensity * cirrusVerticalProfile > 1e-3)
                {
                    tCirrus = min(tCirrus, sampleT);
                }
            }
            if (cirrusProfileWeight > 1e-4)
            {
                cirrusDensity =
                    cirrusColumnDensity / cirrusProfileWeight;
            }
            if (cirrusColumnDensity > 1e-3)
            {
                // Actual profiled path length makes opacity converge to zero at both
                // boundaries; the previous minimum-one slant clamp caused a hard pop.
                float cirrusOd =
                    min(cirrusColumnDensity, 3.0) *
                    uCirrusStrength *
                    0.27;
                float cirrusAlpha = (1.0 - exp(-cirrusOd)) * cirrusDistanceVisibility;
                vec3 cirrusLightWeights;
                vec3 cirrusCachedLighting = CLOUD_QUALITY >= 2
                    ? cloudResolveCachedLighting(
                        uCameraPos + rd * tCirrus,
                        cirrusLightWeights)
                    : vec3(0.0, 1.0, 0.0);
                bool useCirrusCache = cirrusCachedLighting.z > 0.5;
                float cirrusLightOd = useCirrusCache
                    ? cirrusCachedLighting.x
                    : cirrusDensity * 0.62;
                float cirrusSkyVisibility = useCirrusCache
                    ? cirrusCachedLighting.y
                    : 1.0;
                vec3 cirrusSun = useCirrusCache
                    ? vcSunScatterCq34(
                        sunColor,
                        cq34PhaseTerms,
                        cirrusLightOd,
                        cirrusSkyVisibility,
                        0.0,
                        uCloudScatterOctave1,
                        uCloudScatterOctave2,
                        uCloudScatterEnergyClamp)
                    : vcSunScatter(
                        sunColor,
                        cosTheta,
                        cirrusLightOd);
                float cirrusAmbientVisibility = useCirrusCache
                    ? mix(
                        uCloudCachedSkyVisibilityFloor,
                        1.0,
                        cirrusSkyVisibility)
                    : 1.0;
                vec3 cirrusRad = cirrusSun * 0.42 +
                    skyAmbient * 0.42 * cirrusAmbientVisibility;
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

        float clearAmt = (1.0 - transmittance) * mix(0.22, 0.38, dayAmt);
        accum += skyAmbient * clearAmt;
        alpha = saturate1(1.0 - transmittance);
        // CQ1.4: retain scene-referred linear premultiplied radiance through trace,
        // history and reconstruction. Exposure/knee/display encoding happens once
        // in the final cloud composite.
        cloudCol = max(accum, vec3(0.0));
    }

    FragColor = vec4(cloudCol, alpha);
    // Clear shell rays must not publish the altitude-plane exit as nearest-cloud depth;
    // that floor/ceiling value fights itself across eye-height when the camera is inside.
    FragCloudData = ctEncodeMetadata(
        representativeT,
        representativeKind,
        representativeFound || alpha > 1e-3,
        uCloudDataDirect);
}
