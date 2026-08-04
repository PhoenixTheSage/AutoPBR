// Shared cloud lighting: dual-lobe Henyey-Greenstein, multi-scatter octaves, sun light march.

#ifndef GENESIS_VOLUMETRIC_CLOUDS_GLSL
#define GENESIS_VOLUMETRIC_CLOUDS_GLSL

//!include "atmosphere.glsl"
//!include "volumetric_clouds_density_maps.glsl"

float vcHGPhase(float cosTheta, float g)
{
    float gg = g * g;
    float base = max(1.0 + gg - 2.0 * g * cosTheta, 1e-4);
    float inverseRoot = inversesqrt(base);
    return (1.0 - gg) *
        inverseRoot * inverseRoot * inverseRoot *
        (1.0 / (4.0 * 3.14159265));
}

// Forward lobe plus a soft back lobe so clouds opposite the sun keep silver rims.
float vcDualLobePhase(float cosTheta, float g)
{
    return mix(vcHGPhase(cosTheta, -0.35 * g), vcHGPhase(cosTheta, g), 0.7);
}

// Schlick's rational phase approximation avoids the square roots in HG. Keep the exact
// dual-lobe evaluation for first-order sunlight and use this only for the deliberately
// low-eccentricity multiple-scattering octaves, where the approximation error is small.
float vcSchlickPhase(float cosTheta, float g)
{
    float gg = g * g;
    float k = 1.55 * g - 0.55 * g * gg;
    float denominator = max(1.0 - k * cosTheta, 1e-3);
    return (1.0 - k * k) /
        (4.0 * 3.14159265 * denominator * denominator);
}

float vcDualLobePhaseMultipleScatter(float cosTheta, float g)
{
    return mix(
        vcSchlickPhase(cosTheta, -0.35 * g),
        vcSchlickPhase(cosTheta, g),
        0.7);
}

vec3 vcSunScatterPhaseTerms(
    float cosTheta,
    vec3 octave1,
    vec3 octave2,
    bool exactFirstOrder)
{
    return vec3(
        exactFirstOrder
            ? vcDualLobePhase(cosTheta, 0.72)
            : vcDualLobePhaseMultipleScatter(cosTheta, 0.72),
        vcDualLobePhaseMultipleScatter(
            cosTheta,
            0.72 * max(octave1.y, 0.0)),
        vcDualLobePhaseMultipleScatter(
            cosTheta,
            0.72 * max(octave2.y, 0.0)));
}

// CQ3.4 controlled direct term plus exactly two higher-order approximations. Each octave
// vector stores extinction scale, phase-eccentricity scale, and energy scale. Cached local
// sky visibility suppresses higher-order energy in enclosed interiors, while the short
// Cinematic cone optical depth sharpens only the direct boundary response.
vec3 vcSunScatterCq34(
    vec3 sunColor,
    vec3 phaseTerms,
    float lightOd,
    float skyVisibility,
    float localConeOpticalDepth,
    vec3 octave1,
    vec3 octave2,
    float energyClamp)
{
    float boundedLightOd = max(lightOd, 0.0);
    float boundedSkyVisibility = clamp(skyVisibility, 0.0, 1.0);
    float cacheTransmittance = exp(-boundedLightOd);
    float boundedLocalConeOd = max(localConeOpticalDepth, 0.0);
    float directTransmittance = cacheTransmittance;
    if (boundedLocalConeOd > 1e-5)
    {
        directTransmittance *= exp(-boundedLocalConeOd);
    }
    // CQ3 freezes the two extinction controls at 0.5 and 0.25. Derive their exact
    // Beer-Lambert values from the first-order result instead of issuing two more
    // transcendental operations for every occupied view-march sample.
    float octave1Transmittance = sqrt(cacheTransmittance);
    float octave2Transmittance = sqrt(octave1Transmittance);
    vec3 sum = sunColor * phaseTerms.x *
        directTransmittance;

    float octave1Visibility = mix(0.22, 1.0, boundedSkyVisibility);
    float octave2Visibility = mix(0.10, 1.0, boundedSkyVisibility);
    sum += sunColor *
        (max(octave1.z, 0.0) * octave1Visibility) *
        phaseTerms.y *
        octave1Transmittance;
    sum += sunColor *
        (max(octave2.z, 0.0) * octave2Visibility) *
        phaseTerms.z *
        octave2Transmittance;

    // Restrained powder shaping: retain the bright-edge cue without whitening thick
    // interiors into the flat, cartoon-like appearance of an aggressive powder term.
    // CA3.3 slightly lowers the powder floor/ceiling so silver lining stays local.
    float powder = 1.0 - cacheTransmittance * cacheTransmittance;
    sum *= mix(0.70, 0.88, powder);

    // Clamp once after all orders. Per-octave clamps flatten the structure that cached sky
    // visibility is intended to preserve.
    return min(
        max(sum, vec3(0.0)),
        max(sunColor, vec3(0.0)) * max(energyClamp, 0.0));
}

// Compatibility wrapper for Low/Medium, repair, and cache-fallback paths.
vec3 vcSunScatter(vec3 sunColor, float cosTheta, float lightOd)
{
    return vcSunScatterCq34(
        sunColor,
        vcSunScatterPhaseTerms(
            cosTheta,
            vec3(0.50, 0.50, 0.55),
            vec3(0.25, 0.25, 0.30),
            true),
        lightOd,
        1.0,
        0.0,
        vec3(0.50, 0.50, 0.55),
        vec3(0.25, 0.25, 0.30),
        2.25);
}

// Sun radiance at cloud altitude: warms and extinguishes as the sun drops to the horizon,
// matching the warm band the sky dome renders at sunrise/sunset.
vec3 vcCloudSunColor(vec3 sunToward, float sunIntensity)
{
    float sunElev = clamp(sunToward.y, -1.0, 1.0);
    float lowSun = 1.0 - smoothstep(0.04, 0.42, max(sunElev, 0.0));
    vec3 col = mix(vec3(1.0, 0.97, 0.92), vec3(1.0, 0.52, 0.24), lowSun);
    float horizonExtinction = smoothstep(-0.08, 0.12, sunElev);
    return col * horizonExtinction * clamp(sunIntensity * 0.12, 0.0, 2.0);
}

// Light march toward the sun for self-shadowing. Exponential step distribution keeps
// resolution near the sample point, and one distant coarse tap catches far occluders.
// Samples the base shape (no detail erosion) so shadows track the rendered clouds cheaply.
float vcLightOpticalDepthFromBase(float baseAtOrigin, vec3 worldPos, vec3 sunToward,
    vec3 planetCenter, float planetRadius, float layerBase, float layerTop,
    float densityMul, float coverageScale, float volumeSize, int lightSteps,
    sampler3D cloudNoise, int hasCloudNoise, sampler2D coverageMap, int hasCoverageMap,
    vec3 windOffset, float viewSampleFootprint, int densityAssetVersion)
{
    if (baseAtOrigin <= 1e-5)
    {
        return 0.0;
    }

    const float range = 22.0;
    float od = 0.0;
    float tPrev = 0.0;
    for (int i = 0; i < 6; ++i)
    {
        if (i >= lightSteps)
        {
            break;
        }

        float frac = (float(i) + 1.0) / float(max(lightSteps, 1));
        float t = frac * frac * range;
        float dt = t - tPrev;
        vec3 samplePos = worldPos + sunToward * (tPrev + dt * 0.5);
        tPrev = t;
        float sampleAltitude = vcFlatAltitude(samplePos, planetCenter, planetRadius);
        if (sampleAltitude < layerBase || sampleAltitude > layerTop)
        {
            break;
        }

        float lightSampleFootprint = max(viewSampleFootprint, dt);
        vec4 stepWeather = vcSampleWeather(
            coverageMap,
            hasCoverageMap,
            samplePos,
            volumeSize,
            windOffset.xz,
            lightSampleFootprint,
            densityAssetVersion);
        float stepBase = vcCloudBaseDensityFromWeather(
            samplePos,
            planetCenter,
            planetRadius,
            layerBase,
            layerTop,
            coverageScale,
            volumeSize,
            cloudNoise,
            hasCloudNoise,
            windOffset,
            lightSampleFootprint,
            stepWeather,
            densityAssetVersion);
        if (stepBase <= 1e-5)
        {
            continue;
        }

        float stepH = saturate1(
            (sampleAltitude - layerBase) / max(layerTop - layerBase, 0.001));
        float stepDensityScale = vcCloudDensityPotentialScale(
            stepH,
            stepWeather.z,
            densityAssetVersion);
        od += stepBase * stepDensityScale * densityMul * dt * 0.18;
    }

    vec3 farPos = worldPos + sunToward * (range * 2.2);
    float farAltitude = vcFlatAltitude(farPos, planetCenter, planetRadius);
    if (farAltitude >= layerBase && farAltitude <= layerTop)
    {
        float farFootprint = max(viewSampleFootprint, range * 0.5);
        vec4 farWeather = vcSampleWeather(
            coverageMap,
            hasCoverageMap,
            farPos,
            volumeSize,
            windOffset.xz,
            farFootprint,
            densityAssetVersion);
        float farBase = vcCloudBaseDensityFromWeather(
            farPos,
            planetCenter,
            planetRadius,
            layerBase,
            layerTop,
            coverageScale,
            volumeSize,
            cloudNoise,
            hasCloudNoise,
            windOffset,
            farFootprint,
            farWeather,
            densityAssetVersion);
        if (farBase > 1e-5)
        {
            float farH = saturate1(
                (farAltitude - layerBase) / max(layerTop - layerBase, 0.001));
            float farDensityScale = vcCloudDensityPotentialScale(
                farH,
                farWeather.z,
                densityAssetVersion);
            od += farBase * farDensityScale * densityMul * range * 0.5 * 0.18;
        }
    }

    return od;
}

float vcLightOpticalDepth(vec3 worldPos, vec3 sunToward, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float densityMul, float coverageScale, float volumeSize, int lightSteps,
    sampler3D cloudNoise, int hasCloudNoise, sampler2D coverageMap, int hasCoverageMap,
    vec3 windOffset, float viewSampleFootprint, int densityAssetVersion)
{
    float baseAtOrigin = vcCloudBaseDensity(worldPos, planetCenter, planetRadius, layerBase, layerTop,
        coverageScale, volumeSize, cloudNoise, hasCloudNoise, coverageMap, hasCoverageMap,
        windOffset, viewSampleFootprint, densityAssetVersion);
    return vcLightOpticalDepthFromBase(baseAtOrigin, worldPos, sunToward, planetCenter, planetRadius,
        layerBase, layerTop, densityMul, coverageScale, volumeSize, lightSteps,
        cloudNoise, hasCloudNoise, coverageMap, hasCoverageMap, windOffset,
        viewSampleFootprint, densityAssetVersion);
}

#endif // GENESIS_VOLUMETRIC_CLOUDS_GLSL
