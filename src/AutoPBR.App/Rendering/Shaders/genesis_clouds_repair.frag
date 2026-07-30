#version 330 core
// CQ1.8 full-resolution cloud reconstruction and bounded edge repair.

//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/cloud_shell.glsl"
//!include "common/volumetric_clouds.glsl"
//!include "common/volumetric_segment.glsl"
//!include "common/cloud_temporal.glsl"
//!include "common/ray_reconstruct.glsl"
//!include "common/cloud_scene_depth.glsl"

in vec2 vUv;

uniform sampler2D uClouds;
uniform sampler2D uCloudData;
uniform sampler2D uSceneDepth;
uniform sampler3D uCloudNoise;
uniform sampler3D uDetailNoise;
uniform sampler3D uCloudStbn;
uniform sampler2D uCoverageMap;
uniform sampler2D uSkyViewLut;

uniform vec2 uCloudTexelSize;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform vec3 uSunDir;
uniform float uSunIntensity;
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
uniform int uMarchSteps;
uniform int uHasSceneDepth;
uniform int uHasCloudNoise;
uniform int uHasDetailNoise;
uniform int uHasCloudStbn;
uniform int uHasCoverageMap;
uniform int uHasSkyLut;
uniform int uSourceCloudDataDirect;
uniform int uDensityAssetVersion;
uniform int uCloudFrameIndex;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 FragCloudData;

const int CLOUD_REPAIR_STEPS = 8;
const float CLOUD_REPAIR_ALPHA_THRESHOLD = 0.08;
const float CLOUD_REPAIR_DISTANCE_MIN = 0.75;
const float CLOUD_REPAIR_DISTANCE_SCALE = 0.01;
const float CLOUD_REPAIR_VALID_WEIGHT_MIN = 0.75;
const float CLOUD_REPAIR_KIND_THRESHOLD = 0.24;
const float CLOUD_MAX_TRACE_DISTANCE = 4096.0;
const float CLOUD_DISTANCE_FADE_FRACTION = 0.20;
const float CLOUD_STBN_WIDTH = 128.0;
const float CLOUD_STBN_HEIGHT = 128.0;
const float CLOUD_STBN_FRAMES = 64.0;
const float SKY_VIEW_LUT_WIDTH = 192.0;
const float SKY_VIEW_LUT_HEIGHT = 108.0;

float repairSceneDepthWeight(float centerDepth, vec2 tapUv)
{
    if (uHasSceneDepth < 1)
    {
        return 1.0;
    }

    float tapDepth = texture(uSceneDepth, tapUv).r;
    bool centerSky = !csdHasOpaqueDepth(centerDepth, uHasSceneDepth);
    bool tapSky = !csdHasOpaqueDepth(tapDepth, uHasSceneDepth);
    if (centerSky != tapSky)
    {
        return 0.0;
    }

    return centerSky ? 1.0 : exp(-abs(centerDepth - tapDepth) * 420.0);
}

float repairTapSceneVisibility(float sceneDistance, vec4 cloudData)
{
    if (!ctMetadataValid(cloudData, uSourceCloudDataDirect))
    {
        return 0.0;
    }

    return csdCloudInFrontOfScene(
        ctMetadataDistance(cloudData, uSourceCloudDataDirect), sceneDistance);
}

float repairJitter()
{
    if (uHasCloudStbn > 0)
    {
        vec2 pixel = mod(
            floor(gl_FragCoord.xy),
            vec2(CLOUD_STBN_WIDTH, CLOUD_STBN_HEIGHT));
        float frameSlice = mod(float(uCloudFrameIndex), CLOUD_STBN_FRAMES);
        float value = texture(uCloudStbn, vec3(
            (pixel + vec2(0.5)) / vec2(CLOUD_STBN_WIDTH, CLOUD_STBN_HEIGHT),
            (frameSlice + 0.5) / CLOUD_STBN_FRAMES)).r;
        return (value * 255.0 + 0.5) / 256.0;
    }

    return fract(52.9829189 * fract(dot(
        gl_FragCoord.xy + float(uCloudFrameIndex) * vec2(47.0, 17.0),
        vec2(0.06711056, 0.00583715))));
}

float repairDayFactor(vec3 lightPropagationDir, float sunIntensity)
{
    vec3 towardLight = normalize(-lightPropagationDir);
    float dayFromSun = smoothstep(-0.04, 0.22, towardLight.y);
    float dayFromIntensity = smoothstep(0.08, 2.0, sunIntensity);
    return clamp(dayFromSun * dayFromIntensity, 0.0, 1.0);
}

vec3 repairNightZenith(vec3 viewDir)
{
    float gradient = clamp(viewDir.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.01, 0.012, 0.02), vec3(0.02, 0.035, 0.07), gradient);
}

vec3 repairSampleSkyViewLutSrgb(vec3 viewDir)
{
    vec3 direction = normalize(viewDir);
    float azimuth = atan(direction.x, direction.z) * (0.5 / GEN_PI) + 0.5;
    float zenith = acos(clamp(direction.y, -1.0, 1.0)) / GEN_PI;
    vec2 lutSize = max(
        vec2(textureSize(uSkyViewLut, 0)),
        vec2(SKY_VIEW_LUT_WIDTH, SKY_VIEW_LUT_HEIGHT));
    vec2 unitUv = vec2(fract(azimuth), clamp(zenith, 0.0, 1.0));
    vec2 texelUv = unitUv * ((lutSize - 1.0) / lutSize) + (0.5 / lutSize);
    return texture(uSkyViewLut, texelUv).rgb;
}

vec3 repairSkyAmbient(vec3 viewDir, float dayAmount)
{
    vec3 night = repairNightZenith(viewDir) * 2.0;
    if (uHasSkyLut < 1)
    {
        return mix(night, vec3(0.42, 0.50, 0.63), dayAmount);
    }

    vec3 ambientDir = normalize(vec3(
        viewDir.x * 0.35,
        max(viewDir.y, 0.45),
        viewDir.z * 0.35));
    vec3 lut = srgbToLinear(repairSampleSkyViewLutSrgb(ambientDir));
    vec3 dayFloor = vec3(0.060, 0.080, 0.120);
    return mix(night, max(lut, dayFloor), dayAmount);
}

void writeEmpty()
{
    FragColor = vec4(0.0);
    FragCloudData = ctEncodeMetadata(0.0, 0.0, false, 1);
}

void main()
{
    vec2 o = uCloudTexelSize * 0.5;
    vec2 tapUv[4];
    tapUv[0] = vUv + vec2(-o.x, -o.y);
    tapUv[1] = vUv + vec2( o.x, -o.y);
    tapUv[2] = vUv + vec2(-o.x,  o.y);
    tapUv[3] = vUv + vec2( o.x,  o.y);

    vec4 colors[4];
    vec4 metadata[4];
    float weights[4];
    float centerDepth = uHasSceneDepth > 0 ? texture(uSceneDepth, vUv).r : 1.0;
    vec3 rayDir = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    float sceneDistance = csdSceneRayDistanceFromDepth(
        centerDepth, vUv, uInvViewProj, uCameraPos, rayDir, uHasSceneDepth);

    float alphaMin = 1.0;
    float alphaMax = 0.0;
    float distanceMin = 1e9;
    float distanceMax = 0.0;
    float kindMin = 1e9;
    float kindMax = -1e9;
    float validCount = 0.0;
    float validWeight = 0.0;
    float weightSum = 0.0;
    vec4 reconstructed = vec4(0.0);
    float nearestDistance = 1e9;
    float nearestKind = 0.0;
    float nearestCloudDistance = 1e9;
    float nearestCloudKind = 0.5;

    for (int i = 0; i < 4; ++i)
    {
        colors[i] = texture(uClouds, tapUv[i]);
        metadata[i] = texture(uCloudData, tapUv[i]);
        bool valid = ctMetadataValid(metadata[i], uSourceCloudDataDirect);
        float sceneWeight = valid
            ? repairTapSceneVisibility(sceneDistance, metadata[i])
            : 0.0;
        weights[i] = repairSceneDepthWeight(centerDepth, tapUv[i]) * sceneWeight;
        weightSum += weights[i];
        reconstructed += colors[i] * weights[i];
        alphaMin = min(alphaMin, colors[i].a);
        alphaMax = max(alphaMax, colors[i].a);
        if (valid)
        {
            float distanceToCloud = ctMetadataDistance(
                metadata[i], uSourceCloudDataDirect);
            float kind = ctMetadataKind(metadata[i], uSourceCloudDataDirect);
            validCount += 1.0;
            validWeight += weights[i];
            distanceMin = min(distanceMin, distanceToCloud);
            distanceMax = max(distanceMax, distanceToCloud);
            kindMin = min(kindMin, kind);
            kindMax = max(kindMax, kind);
            if (weights[i] > 0.0 && distanceToCloud < nearestDistance)
            {
                nearestDistance = distanceToCloud;
                nearestKind = kind;
            }
            if (weights[i] > 0.0 && colors[i].a > 0.03 &&
                distanceToCloud < nearestCloudDistance)
            {
                nearestCloudDistance = distanceToCloud;
                nearestCloudKind = kind;
            }
        }
    }

    // No source metadata means there is no representative boundary around which to place
    // the bounded eight-sample retrace. Exit before shell/density work so clear sky and
    // opaque terrain footprints remain a cheap empty write.
    if (validCount <= 0.0 && alphaMax <= 1e-4)
    {
        writeEmpty();
        return;
    }

    if (weightSum > 1e-5)
    {
        reconstructed /= weightSum;
    }

    float planetRadius = max(uPlanetRadius, 1.0);
    vec3 planetCenter = vec3(0.0, uGroundWorldY - planetRadius, 0.0);
    float layerBaseAltitude = max(uLayerHeight - uGroundWorldY, 0.01);
    float layerTopAltitude = layerBaseAltitude + max(uVolumeHeight, 0.01);
    float layerSupportGuard = clamp(uVolumeHeight * 0.015, 0.50, 1.50);
    vec2 slabSegment = vcsIntersectAltitudeSlab(
        uCameraPos,
        rayDir,
        uGroundWorldY,
        max(layerBaseAltitude - layerSupportGuard, 0.001),
        layerTopAltitude + layerSupportGuard,
        CLOUD_MAX_TRACE_DISTANCE);
    slabSegment.y = min(slabSegment.y, sceneDistance);

    float cirrusAltitude = layerTopAltitude + max(uVolumeHeight * 1.5, 18.0);
    float cirrusThickness = max(uVolumeHeight * 0.035, 0.75);
    float cirrusSupportGuard = clamp(cirrusThickness * 0.20, 0.25, 0.75);
    vec2 cirrusSegment = vcsIntersectAltitudeSlab(
        uCameraPos,
        rayDir,
        uGroundWorldY,
        cirrusAltitude - cirrusSupportGuard,
        cirrusAltitude + cirrusThickness + cirrusSupportGuard,
        CLOUD_MAX_TRACE_DISTANCE);
    cirrusSegment.y = min(cirrusSegment.y, sceneDistance);

    float slabDistanceVisibility = vcsDistanceVisibility(
        slabSegment.x, CLOUD_MAX_TRACE_DISTANCE, CLOUD_DISTANCE_FADE_FRACTION);
    float cirrusDistanceVisibility = vcsDistanceVisibility(
        cirrusSegment.x, CLOUD_MAX_TRACE_DISTANCE, CLOUD_DISTANCE_FADE_FRACTION);
    bool slabVisible =
        slabSegment.y > slabSegment.x && slabDistanceVisibility > 1e-4;
    bool cirrusVisible =
        cirrusSegment.y > cirrusSegment.x && cirrusDistanceVisibility > 1e-4;
    bool shellIntersects = slabVisible || (uCirrusStrength > 0.0 && cirrusVisible);

    if (!shellIntersects)
    {
        writeEmpty();
        return;
    }

    bool validityEdge = validCount > 0.0 && validCount < 4.0;
    bool distanceEdge = validCount > 1.0 &&
        distanceMax - distanceMin > max(
            CLOUD_REPAIR_DISTANCE_MIN,
            distanceMin * CLOUD_REPAIR_DISTANCE_SCALE);
    bool kindEdge = validCount > 1.0 &&
        kindMax - kindMin > CLOUD_REPAIR_KIND_THRESHOLD;
    float normalizedValidWeight = clamp(validWeight * 0.25, 0.0, 1.0);
    bool lowValidWeight = validCount > 0.0 &&
        normalizedValidWeight < CLOUD_REPAIR_VALID_WEIGHT_MIN;
    bool alphaEdge = alphaMax - alphaMin > CLOUD_REPAIR_ALPHA_THRESHOLD;
    bool needsRepair = alphaEdge || distanceEdge || validityEdge || kindEdge || lowValidWeight;

    float reconstructedDistance = nearestDistance < 1e8
        ? nearestDistance
        : max(slabSegment.x, 0.0);
    bool reconstructedValid = weightSum > 1e-5 && reconstructed.a > 1e-4;
    if (!needsRepair)
    {
        FragColor = max(reconstructed, vec4(0.0));
        FragCloudData = ctEncodeMetadata(
            reconstructedDistance,
            nearestKind,
            reconstructedValid,
            1);
        return;
    }

    bool repairCirrus = nearestCloudDistance < 1e8
        ? nearestCloudKind >= 0.75
        : (!slabVisible && cirrusVisible);
    vec2 repairSegment = repairCirrus ? cirrusSegment : slabSegment;
    if (repairSegment.y <= repairSegment.x)
    {
        repairSegment = repairCirrus ? slabSegment : cirrusSegment;
        repairCirrus = !repairCirrus;
    }
    if (repairSegment.y <= repairSegment.x)
    {
        FragColor = max(reconstructed, vec4(0.0));
        FragCloudData = ctEncodeMetadata(
            reconstructedDistance,
            nearestKind,
            reconstructedValid,
            1);
        return;
    }

    int primarySteps = repairCirrus
        ? 2
        : (uMarchSteps > 0 ? clamp(uMarchSteps, 1, 64) : 48);
    float primaryFineStep = vcsMarchStepLength(
        repairSegment.x,
        repairSegment.y,
        primarySteps,
        uVolumeSize,
        uVolumeHeight);
    float boundaryCenter = nearestCloudDistance < 1e8
        ? nearestCloudDistance
        : (nearestDistance < 1e8
            ? nearestDistance
            : (repairSegment.x + repairSegment.y) * 0.5);
    float repairStart = max(repairSegment.x, boundaryCenter - primaryFineStep);
    float repairEnd = min(repairSegment.y, boundaryCenter + primaryFineStep);
    if (repairEnd <= repairStart)
    {
        FragColor = max(reconstructed, vec4(0.0));
        FragCloudData = ctEncodeMetadata(
            reconstructedDistance,
            nearestKind,
            reconstructedValid,
            1);
        return;
    }

    vec3 sunToward = normalize(-uSunDir);
    float cosTheta = dot(rayDir, sunToward);
    float dayAmount = repairDayFactor(uSunDir, uSunIntensity);
    vec3 sunColor = vcCloudSunColor(sunToward, uSunIntensity);
    vec3 skyAmbient = repairSkyAmbient(rayDir, dayAmount);
    float segmentLength = (repairEnd - repairStart) / float(CLOUD_REPAIR_STEPS);
    float sampleJitter = repairJitter();
    float transmittance = 1.0;
    vec3 repairRadiance = vec3(0.0);
    float repairDistance = 1e9;
    float densityHitCount = 0.0;

    for (int i = 0; i < CLOUD_REPAIR_STEPS; ++i)
    {
        float sampleFraction = (float(i) + sampleJitter) /
            float(CLOUD_REPAIR_STEPS);
        float sampleDistance = mix(repairStart, repairEnd, sampleFraction);
        vec3 worldPos = uCameraPos + rayDir * sampleDistance;
        float sampleFootprint = max(
            segmentLength,
            sampleDistance * uPixelAngularSize);
        float density = 0.0;
        vec3 radiance = vec3(0.0);

        if (repairCirrus)
        {
            float cirrusDensity = vcCirrusDensityWithDetail(
                worldPos.xz,
                uCirrusWindOffset,
                uCirrusWindDir,
                uVolumeSize,
                uDetailNoise,
                uHasDetailNoise,
                sampleFootprint,
                -0.35,
                3,
                uDensityAssetVersion);
            float cirrusSampleAltitude =
                vcsAltitude(worldPos, uGroundWorldY);
            float cirrusHeight = saturate1(
                (cirrusSampleAltitude - cirrusAltitude) /
                max(cirrusThickness, 0.01));
            float cirrusVerticalProfile =
                smoothstep(0.0, 0.18, cirrusHeight) *
                (1.0 - smoothstep(0.72, 1.0, cirrusHeight));
            density =
                cirrusDensity *
                cirrusVerticalProfile *
                uCirrusStrength *
                0.27 /
                max(cirrusThickness, 0.01);
            radiance = vcSunScatter(
                sunColor,
                cosTheta,
                cirrusDensity * 0.62) * 0.42 + skyAmbient * 0.54;
        }
        else
        {
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
            density = vcCloudDensityFromBase(
                baseShape,
                worldPos,
                planetCenter,
                planetRadius,
                layerBaseAltitude,
                layerTopAltitude,
                uDensity,
                uVolumeSize,
                uDetailNoise,
                uHasDetailNoise,
                uWindOffset,
                sampleFootprint,
                -0.35,
                3,
                weather.z,
                weather.w,
                uDensityAssetVersion);
            float lightOd = vcLightOpticalDepthFromBase(
                baseShape,
                worldPos,
                sunToward,
                planetCenter,
                planetRadius,
                layerBaseAltitude,
                layerTopAltitude,
                uDensity,
                uCoverageScale,
                uVolumeSize,
                4,
                uCloudNoise,
                uHasCloudNoise,
                uCoverageMap,
                uHasCoverageMap,
                uWindOffset,
                sampleFootprint,
                uDensityAssetVersion);
            float altitude = vcsAltitude(worldPos, uGroundWorldY);
            float heightSample = saturate1(
                (altitude - layerBaseAltitude) / max(uVolumeHeight, 0.001));
            radiance = vcSunScatter(sunColor, cosTheta, lightOd);
            float ambientVisibility = mix(0.38, 1.0, exp(-lightOd * 0.32));
            radiance += skyAmbient * mix(0.22, 0.82, heightSample) *
                0.62 * ambientVisibility;
        }

        if (density > 1e-5)
        {
            float inscatterWeight = vmSegmentInscatterWeight(
                density, segmentLength, 1.1);
            repairRadiance += transmittance * radiance * inscatterWeight;
            transmittance *= vmSegmentTransmittance(
                density, segmentLength, 1.1);
            repairDistance = min(repairDistance, sampleDistance);
            densityHitCount += 1.0;
        }
    }

    float repairAlpha = saturate1(1.0 - transmittance);
    repairRadiance += skyAmbient * repairAlpha * mix(0.35, 0.55, dayAmount);
    float repairDistanceVisibility = repairCirrus
        ? cirrusDistanceVisibility
        : slabDistanceVisibility;
    repairRadiance *= repairDistanceVisibility;
    repairAlpha *= repairDistanceVisibility;

    float alphaSeverity = clamp(
        (alphaMax - alphaMin - CLOUD_REPAIR_ALPHA_THRESHOLD) / 0.32,
        0.0,
        1.0);
    float distanceThreshold = max(
        CLOUD_REPAIR_DISTANCE_MIN,
        distanceMin * CLOUD_REPAIR_DISTANCE_SCALE);
    float distanceSeverity = distanceEdge
        ? clamp((distanceMax - distanceMin) / max(distanceThreshold, 1e-4) - 1.0, 0.0, 1.0)
        : 0.0;
    float lowWeightSeverity = clamp(
        (CLOUD_REPAIR_VALID_WEIGHT_MIN - normalizedValidWeight) /
            CLOUD_REPAIR_VALID_WEIGHT_MIN,
        0.0,
        1.0);
    float edgeSeverity = max(
        max(alphaSeverity, distanceSeverity),
        max(validityEdge || kindEdge ? 1.0 : 0.0, lowWeightSeverity));
    float sourceCoverage = clamp(validCount * 0.25, 0.0, 1.0);
    float sampledCoverage = densityHitCount / float(CLOUD_REPAIR_STEPS);
    float repairConfidence = edgeSeverity *
        mix(0.35, 1.0, sourceCoverage) *
        mix(0.78, 1.0, sampledCoverage);

    vec4 repaired = vec4(max(repairRadiance, vec3(0.0)), repairAlpha);
    vec4 outputCloud = mix(max(reconstructed, vec4(0.0)), repaired, repairConfidence);
    bool repairFound = repairDistance < 1e8 && repairAlpha > 1e-4;
    float outputDistance = repairFound ? repairDistance : reconstructedDistance;
    float outputKind = repairFound ? (repairCirrus ? 1.0 : 0.5) : nearestKind;
    bool outputValid = outputCloud.a > 1e-4 &&
        (repairFound || reconstructedValid);

    FragColor = outputCloud;
    FragCloudData = ctEncodeMetadata(
        outputDistance,
        outputKind,
        outputValid,
        1);
}
