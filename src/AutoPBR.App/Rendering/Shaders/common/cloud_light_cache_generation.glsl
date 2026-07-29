#ifndef GENESIS_CLOUD_LIGHT_CACHE_GENERATION_GLSL
#define GENESIS_CLOUD_LIGHT_CACHE_GENERATION_GLSL

// Shared CQ3 cloud-light cache density and optical-depth integration. Fragment-slice and
// compute generators intentionally call this exact implementation so their RG16F results
// differ only by the storage path and half-float rounding.

uniform sampler3D uCloudNoise;
uniform sampler3D uDetailNoise;
uniform sampler2D uCoverageMap;

uniform vec3 uBasisRight;
uniform vec3 uBasisUp;
uniform vec3 uBasisForward;
uniform vec2 uPlaneCenter;
uniform float uWorldSpan;
uniform float uLightDepthMin;
uniform float uLightDepthSpan;
uniform float uSliceLength;
uniform float uFroxelFootprint;
uniform int uLayerCount;

uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uCumulusBaseAltitude;
uniform float uCumulusTopAltitude;
uniform float uCirrusBaseAltitude;
uniform float uCirrusTopAltitude;
uniform float uDensity;
uniform float uCoverageScale;
uniform float uVolumeSize;
uniform vec3 uWindOffset;
uniform float uCirrusStrength;
uniform vec2 uCirrusWindOffset;
uniform vec2 uCirrusWindDir;
uniform int uQuality;
uniform int uHasCloudNoise;
uniform int uHasDetailNoise;
uniform int uHasCoverageMap;
uniform int uDensityAssetVersion;

// Test-only deterministic fixture. A non-negative value bypasses texture density while
// retaining production integration, dispatch/draw ordering, barriers, and RG16F storage.
uniform float uReferenceDensity;

vec3 cq3LightToWorld(vec3 light)
{
    return uBasisRight * light.x +
        uBasisUp * light.y +
        uBasisForward * light.z;
}

vec3 cq3WorldPosition(vec2 unitPlane, float unitDepth)
{
    vec2 plane = uPlaneCenter +
        (unitPlane - vec2(0.5)) * uWorldSpan;
    float lightDepth = uLightDepthMin + unitDepth * uLightDepthSpan;
    return cq3LightToWorld(vec3(plane, lightDepth));
}

float cq3CumulusDensity(vec3 worldPos, float sampleFootprint)
{
    float conservative = vcCloudConservativeDensity(
        worldPos,
        uPlanetCenter,
        uPlanetRadius,
        uCumulusBaseAltitude,
        uCumulusTopAltitude,
        uCoverageScale,
        uVolumeSize,
        uCoverageMap,
        uHasCoverageMap,
        uWindOffset,
        sampleFootprint,
        uDensityAssetVersion);
    if (conservative <= 1e-4)
    {
        return 0.0;
    }

    vec4 weather = vcSampleWeather(
        uCoverageMap,
        uHasCoverageMap,
        worldPos,
        uVolumeSize,
        uWindOffset.xz,
        sampleFootprint,
        uDensityAssetVersion);
    float base = vcCloudBaseDensityFromWeather(
        worldPos,
        uPlanetCenter,
        uPlanetRadius,
        uCumulusBaseAltitude,
        uCumulusTopAltitude,
        uCoverageScale,
        uVolumeSize,
        uCloudNoise,
        uHasCloudNoise,
        uWindOffset,
        sampleFootprint,
        weather,
        uDensityAssetVersion);
    return vcCloudDensityFromBase(
        base,
        worldPos,
        uPlanetCenter,
        uPlanetRadius,
        uCumulusBaseAltitude,
        uCumulusTopAltitude,
        uDensity,
        uVolumeSize,
        uDetailNoise,
        uHasDetailNoise,
        uWindOffset,
        sampleFootprint,
        0.0,
        uQuality,
        weather.z,
        weather.w,
        uDensityAssetVersion);
}

float cq34SkyVisibility(
    vec3 worldCenter,
    float localDensity,
    float localCirrusOpticalDepth,
    float sampleFootprint)
{
    vec3 radialUp = normalize(worldCenter - uPlanetCenter);
    float probeDistance = max(
        max(uFroxelFootprint, uSliceLength) * 1.35,
        0.5);
    float probeFootprint = max(sampleFootprint, probeDistance * 1.5);
    vec3 probeDirection0 = normalize(
        radialUp + uBasisRight * 0.46 + uBasisUp * 0.14);
    vec3 probeDirection1 = normalize(
        radialUp - uBasisRight * 0.31 - uBasisUp * 0.22);
    float probeDensity0 = cq3CumulusDensity(
        worldCenter + probeDirection0 * (probeDistance * 0.58),
        probeFootprint);
    float probeDensity1 = cq3CumulusDensity(
        worldCenter + probeDirection1 * probeDistance,
        probeFootprint);

    // Two coarse upward-cone probes make G a local hemispherical visibility estimate rather
    // than a differently scaled copy of cumulative sun optical depth. The fixed weighting
    // favors the nearer probe while retaining a longer-range canopy response.
    float skyOpticalDepth = (
        localDensity * 0.30 +
        probeDensity0 * 0.45 +
        probeDensity1 * 0.25) * probeDistance * 0.18;
    skyOpticalDepth += max(localCirrusOpticalDepth, 0.0) * 0.35;
    return clamp(exp(-skyOpticalDepth * 0.72), 0.0, 1.0);
}

float cq3CirrusOpticalDepth(
    vec3 worldStart,
    vec3 worldCenter,
    vec3 worldEnd,
    float sampleFootprint)
{
    if (uCirrusStrength <= 0.0 ||
        uCirrusTopAltitude <= uCirrusBaseAltitude)
    {
        return 0.0;
    }

    float altitudeStart = length(worldStart - uPlanetCenter) - uPlanetRadius;
    float altitudeEnd = length(worldEnd - uPlanetCenter) - uPlanetRadius;
    float lowAltitude = min(altitudeStart, altitudeEnd);
    float highAltitude = max(altitudeStart, altitudeEnd);
    float overlapAltitude = max(
        0.0,
        min(highAltitude, uCirrusTopAltitude) -
        max(lowAltitude, uCirrusBaseAltitude));
    float altitudeDelta = highAltitude - lowAltitude;
    float centerAltitude = length(worldCenter - uPlanetCenter) - uPlanetRadius;
    float overlapFraction = altitudeDelta > 1e-4
        ? overlapAltitude / altitudeDelta
        : (centerAltitude >= uCirrusBaseAltitude &&
            centerAltitude <= uCirrusTopAltitude ? 1.0 : 0.0);
    if (overlapFraction <= 0.0)
    {
        return 0.0;
    }

    float cirrusDensity = vcCirrusDensityWithDetail(
        worldCenter.xz,
        uCirrusWindOffset,
        uCirrusWindDir,
        uVolumeSize,
        uDetailNoise,
        uHasDetailNoise,
        sampleFootprint,
        uQuality >= 3 ? -0.35 : 0.0,
        uQuality,
        uDensityAssetVersion);
    float thickness = max(uCirrusTopAltitude - uCirrusBaseAltitude, 0.001);
    float slant = clamp(uSliceLength * overlapFraction / thickness, 0.0, 3.0);
    return cirrusDensity * uCirrusStrength * 0.27 * slant;
}

vec2 cq3EvaluateLayer(vec2 unitPlane, int layerIndex)
{
    float layerCount = float(max(uLayerCount, 1));
    float unitStart = float(layerIndex) / layerCount;
    float unitCenter = (float(layerIndex) + 0.5) / layerCount;
    float unitEnd = (float(layerIndex) + 1.0) / layerCount;
    vec3 worldStart = cq3WorldPosition(unitPlane, unitStart);
    vec3 worldCenter = cq3WorldPosition(unitPlane, unitCenter);
    vec3 worldEnd = cq3WorldPosition(unitPlane, unitEnd);

    float sampleFootprint = max(uSliceLength, uFroxelFootprint);
    float deltaOpticalDepth;
    float localSkyVisibility;
    if (uReferenceDensity >= 0.0)
    {
        deltaOpticalDepth = uReferenceDensity * uSliceLength * 0.18;
        localSkyVisibility = exp(-max(deltaOpticalDepth, 0.0) * 0.35);
    }
    else
    {
        float cumulusDensity = cq3CumulusDensity(worldCenter, sampleFootprint);
        float cirrusOpticalDepth = cq3CirrusOpticalDepth(
            worldStart,
            worldCenter,
            worldEnd,
            sampleFootprint);
        deltaOpticalDepth =
            cumulusDensity * uSliceLength * 0.18 +
            cirrusOpticalDepth;
        localSkyVisibility = cq34SkyVisibility(
            worldCenter,
            cumulusDensity,
            cirrusOpticalDepth,
            sampleFootprint);
    }

    float opticalDepth = max(deltaOpticalDepth, 0.0);
    return vec2(opticalDepth, clamp(localSkyVisibility, 0.0, 1.0));
}

vec2 cq3CombinePrefix(vec2 previous, vec2 localValue)
{
    return vec2(
        max(previous.r, 0.0) + max(localValue.r, 0.0),
        clamp(localValue.g, 0.0, 1.0));
}

vec2 cq3IntegrateLayer(vec2 unitPlane, int layerIndex, vec2 previous)
{
    return cq3CombinePrefix(
        previous,
        cq3EvaluateLayer(unitPlane, layerIndex));
}

#endif // GENESIS_CLOUD_LIGHT_CACHE_GENERATION_GLSL
