#version 330 core
// CQ3.1 desktop GL 3.3 reference generator for one cloud-light cache slice.

//!include "common/common.glsl"
//!include "common/volumetric_clouds_density_maps.glsl"

in vec2 vUv;

uniform sampler3D uCloudNoise;
uniform sampler3D uDetailNoise;
uniform sampler2D uCoverageMap;
uniform sampler2D uPreviousPrefix;

uniform vec3 uBasisRight;
uniform vec3 uBasisUp;
uniform vec3 uBasisForward;
uniform vec2 uPlaneCenter;
uniform float uWorldSpan;
uniform float uLightDepthMin;
uniform float uLightDepthSpan;
uniform float uSliceLength;
uniform float uFroxelFootprint;
uniform int uLayerIndex;
uniform int uLayerCount;
uniform int uHasPrevious;

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

// Test-only deterministic fixture. A positive value bypasses texture density while retaining
// the production prefix accumulation and half-float storage path.
uniform float uReferenceDensity;

layout(location = 0) out vec2 FragCache;
layout(location = 1) out vec2 FragPrefix;

vec3 cq3LightToWorld(vec3 light)
{
    return uBasisRight * light.x +
        uBasisUp * light.y +
        uBasisForward * light.z;
}

vec3 cq3WorldPosition(float unitDepth)
{
    vec2 plane = uPlaneCenter +
        (vUv - vec2(0.5)) * uWorldSpan;
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

void main()
{
    float layerCount = float(max(uLayerCount, 1));
    float unitStart = float(uLayerIndex) / layerCount;
    float unitCenter = (float(uLayerIndex) + 0.5) / layerCount;
    float unitEnd = (float(uLayerIndex) + 1.0) / layerCount;
    vec3 worldStart = cq3WorldPosition(unitStart);
    vec3 worldCenter = cq3WorldPosition(unitCenter);
    vec3 worldEnd = cq3WorldPosition(unitEnd);

    vec2 previous = uHasPrevious > 0
        ? texture(uPreviousPrefix, vUv).rg
        : vec2(0.0, 1.0);
    float sampleFootprint = max(uSliceLength, uFroxelFootprint);
    float deltaOpticalDepth;
    if (uReferenceDensity >= 0.0)
    {
        deltaOpticalDepth = uReferenceDensity * uSliceLength * 0.18;
    }
    else
    {
        float cumulusDensity = cq3CumulusDensity(worldCenter, sampleFootprint);
        deltaOpticalDepth =
            cumulusDensity * uSliceLength * 0.18 +
            cq3CirrusOpticalDepth(
                worldStart,
                worldCenter,
                worldEnd,
                sampleFootprint);
    }

    float opticalDepth = max(previous.r, 0.0) + max(deltaOpticalDepth, 0.0);
    float localSkyVisibility = exp(-max(deltaOpticalDepth, 0.0) * 0.35);
    float skyVisibility = clamp(previous.g * localSkyVisibility, 0.0, 1.0);
    vec2 cacheValue = vec2(opticalDepth, skyVisibility);
    FragCache = cacheValue;
    FragPrefix = cacheValue;
}
