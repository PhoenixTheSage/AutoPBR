#version 330 core
// Curved-shell volumetric clouds with conservative empty-space marching.

//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/cloud_shell.glsl"
//!include "common/volumetric_clouds.glsl"
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
const float CLOUD_HORIZON_FEATHER = 0.0025;
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
    if (uHasCloudStbn > 0 && uQuality >= 2)
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

vec3 sampleSkyAmbient(vec3 rd, sampler2D skyLut, int hasSkyLut, float dayAmt)
{
    vec3 night = cloudNightZenith(rd) * 2.0;
    if (hasSkyLut < 1)
    {
        return mix(night, vec3(0.42, 0.50, 0.63), dayAmt);
    }

    vec3 ambientDir = normalize(vec3(rd.x * 0.35, max(rd.y, 0.45), rd.z * 0.35));
    vec3 lut = srgbToLinear(cloudSampleSkyViewLutSrgb(skyLut, ambientDir));
    return mix(night, lut, dayAmt);
}

vec3 cloudResolveCachedLighting(vec3 worldPosition, out vec3 weights)
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
    return vcCloudDensityEx(
        worldPosition,
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
        sampleFootprint,
        -0.35,
        3,
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
    float altitude = length(worldPos - planetCenter) - planetRadius;
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
                : (uQuality <= 0
                    ? 16
                    : (uQuality >= 3
                        ? 48
                        : (uQuality >= 2 ? 32 : 24)));
            float marchStep = max((tExit - tEnter) / float(steps), 0.01);
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
                float detailBias = uQuality >= 3 ? -0.35 : 0.0;
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
                0.0,
                0.0,
                uQuality,
                uDensityAssetVersion);
        }
        else if (uDebugView == CLOUD_DEBUG_ASSET_PROFILE)
        {
            scalarView = false;
            cloudCol = cloudDebugAssetProfileColor(
                uDensityAssetProfileCode);
        }

        if (scalarView)
        {
            cloudCol = vec3(saturate1(debugValue));
        }

        alpha = 0.95;
        cloudCol *= slabHorizonVisibility;
        alpha *= slabHorizonVisibility;
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
                float coverageFootprint = max(
                    (tExit - tEnter) / 4.0,
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
                int steps = uMarchSteps > 0
                    ? clamp(uMarchSteps, 1, CLOUD_MAX_STEPS)
                    : (uQuality <= 0 ? 16 : (uQuality >= 3 ? 48 : (uQuality >= 2 ? 32 : 24)));
                float fineStep = max((tExit - tEnter) / float(steps), 0.01);
                float coarseStep = fineStep * (uQuality <= 0 ? 4.0 : 3.0);
                int lightSteps = uQuality >= 2 ? 4 : (uQuality <= 0 ? 2 : 3);
                float detailLodBias = uQuality >= 3 ? -0.35 : 0.0;
                float jitter01 = cloudPrimaryMarchJitter();
                float t = tEnter + jitter01 * fineStep;

                for (int i = 0; i < CLOUD_MAX_STEPS; ++i)
                {
                    if (i >= steps || t >= tExit)
                    {
                        break;
                    }

                    float sampleT = min(t + fineStep * 0.5, tExit);
                    vec3 worldPos = uCameraPos + rd * sampleT;
                    float pixelFootprint = sampleT * uPixelAngularSize;
                    float sampleFootprint = max(fineStep, pixelFootprint);
                    float conservativeFootprint = max(coarseStep, pixelFootprint);
                    float conservative = vcCloudConservativeDensity(worldPos, planetCenter, planetRadius,
                        layerBaseAltitude, layerTopAltitude, uCoverageScale, uVolumeSize,
                        uCoverageMap, uHasCoverageMap, uWindOffset, conservativeFootprint,
                        uDensityAssetVersion);
                    if (conservative <= 1e-4)
                    {
                        t += coarseStep;
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
                    if (baseShape <= 1e-5)
                    {
                        t += fineStep;
                        continue;
                    }

                    float density = vcCloudDensityFromBase(baseShape, worldPos, planetCenter, planetRadius,
                        layerBaseAltitude, layerTopAltitude, uDensity, uVolumeSize,
                        uDetailNoise, uHasDetailNoise, uWindOffset,
                        sampleFootprint, detailLodBias, uQuality,
                        weather.z, weather.w, uDensityAssetVersion);
                    if (density > 1e-5)
                    {
                        float segmentLength = min(fineStep, tExit - t);
                        vec3 cloudLightWeights;
                        vec3 cachedLighting = uQuality >= 2
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
                        float boundaryWeight =
                            1.0 - smoothstep(0.18, 0.62, baseShape);
                        if (useCachedLighting &&
                            uQuality >= 3 &&
                            boundaryWeight > 1e-3)
                        {
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
                                uCloudLocalConeOpticalDepthScale;
                        }
                        float altitude = vcsAltitude(worldPos, planetCenter, planetRadius);
                        float hSample = saturate1((altitude - layerBaseAltitude) / max(uVolumeHeight, 0.001));
                        float skyVisibility = useCachedLighting
                            ? cachedLighting.y
                            : exp(-lightOd * 0.32);
                        vec3 radiance = useCachedLighting
                            ? vcSunScatterCq34(
                                sunColor,
                                cosTheta,
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
                                0.38,
                                1.0,
                                skyVisibility);
                        // The shared condensation level stays comparatively shaded while
                        // the cauliflower tops receive progressively more skylight.
                        radiance += skyAmbient * mix(0.22, 0.82, hSample) * 0.62 * ambientVisibility;
                        if (useCachedLighting)
                        {
                            vec3 radialUp = normalize(worldPos - planetCenter);
                            float upwardHemisphereWeight = smoothstep(
                                -0.15,
                                0.65,
                                dot(rd, radialUp));
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

                    t += fineStep;
                }
            }

            // Fade the integrated premultiplied layer, not its input density. Scaling density
            // before Beer-Lambert integration leaves thick clouds opaque through most of the
            // transition and then collapses them into a visible horizontal cutoff.
            if (slabHorizonVisibility < 1.0)
            {
                float cumulusAlpha = saturate1(1.0 - transmittance);
                accum *= slabHorizonVisibility;
                transmittance = 1.0 - cumulusAlpha * slabHorizonVisibility;
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
                    uQuality >= 3 ? -0.35 : 0.0,
                    uQuality,
                    uDensityAssetVersion);
                cirrusDensity += sampleDensity / float(cirrusSamples);
                if (sampleDensity > 1e-3)
                {
                    tCirrus = min(tCirrus, sampleT);
                }
            }
            if (cirrusDensity > 1e-3)
            {
                float slant = clamp((cirrusSeg.y - cirrusSeg.x) / cirrusThickness, 1.0, 3.0);
                float cirrusOd = cirrusDensity * uCirrusStrength * 0.27 * slant;
                float cirrusAlpha = (1.0 - exp(-cirrusOd)) * cirrusHorizonVisibility;
                vec3 cirrusLightWeights;
                vec3 cirrusCachedLighting = uQuality >= 2
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
                        cosTheta,
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
                    skyAmbient * 0.54 * cirrusAmbientVisibility;
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
        // CQ1.4: retain scene-referred linear premultiplied radiance through trace,
        // history and reconstruction. Exposure/knee/display encoding happens once
        // in the final cloud composite.
        cloudCol = max(accum, vec3(0.0));
    }

    FragColor = vec4(cloudCol, alpha);
    FragCloudData = ctEncodeMetadata(representativeT, representativeKind, true, uCloudDataDirect);
}
