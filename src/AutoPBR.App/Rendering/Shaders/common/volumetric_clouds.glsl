// Shared cloud lighting: dual-lobe Henyey-Greenstein, multi-scatter octaves, sun light march.

#ifndef GENESIS_VOLUMETRIC_CLOUDS_GLSL
#define GENESIS_VOLUMETRIC_CLOUDS_GLSL

//!include "atmosphere.glsl"
//!include "volumetric_clouds_density_maps.glsl"

float vcHGPhase(float cosTheta, float g)
{
    float gg = g * g;
    float base = max(1.0 + gg - 2.0 * g * cosTheta, 1e-4);
    return (1.0 - gg) / (4.0 * 3.14159265 * base * sqrt(base));
}

// Forward lobe plus a soft back lobe so clouds opposite the sun keep silver rims.
float vcDualLobePhase(float cosTheta, float g)
{
    return mix(vcHGPhase(cosTheta, -0.35 * g), vcHGPhase(cosTheta, g), 0.7);
}

// Multi-scattering approximation: each octave re-runs the single-scatter estimate with
// extinction, phase eccentricity, and intensity scaled down, so optically thick cores
// glow instead of clamping to black under pure Beer-Lambert.
vec3 vcSunScatter(vec3 sunColor, float cosTheta, float lightOd)
{
    vec3 sum = vec3(0.0);
    float extScale = 1.0;
    float phaseG = 0.72;
    float intensity = 1.0;
    for (int o = 0; o < 3; ++o)
    {
        float phase = vcDualLobePhase(cosTheta, phaseG);
        sum += sunColor * (intensity * phase * exp(-lightOd * extScale));
        extScale *= 0.5;
        phaseG *= 0.5;
        intensity *= 0.55;
    }

    // Restrained powder shaping: retain the bright-edge cue without whitening thick
    // interiors into the flat, cartoon-like appearance of an aggressive powder term.
    float powder = 1.0 - exp(-lightOd * 2.0);
    return sum * mix(0.72, 0.90, powder);
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
    sampler3D cloudNoise, int hasCloudNoise, sampler2D coverageMap, int hasCoverageMap, vec3 windOffset)
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
        float sampleAltitude = length(samplePos - planetCenter) - planetRadius;
        if (sampleAltitude < layerBase || sampleAltitude > layerTop)
        {
            break;
        }

        float stepBase = vcCloudBaseDensity(samplePos, planetCenter, planetRadius, layerBase, layerTop,
            coverageScale, volumeSize, cloudNoise, hasCloudNoise, coverageMap, hasCoverageMap, windOffset, 2.0);
        if (stepBase <= 1e-5)
        {
            continue;
        }

        od += stepBase * densityMul * dt * 0.18;
    }

    vec3 farPos = worldPos + sunToward * (range * 2.2);
    float farAltitude = length(farPos - planetCenter) - planetRadius;
    if (farAltitude >= layerBase && farAltitude <= layerTop)
    {
        float farBase = vcCloudBaseDensity(farPos, planetCenter, planetRadius, layerBase, layerTop,
            coverageScale, volumeSize, cloudNoise, hasCloudNoise, coverageMap, hasCoverageMap, windOffset, 3.0);
        if (farBase > 1e-5)
        {
            od += farBase * densityMul * range * 0.5 * 0.18;
        }
    }

    return od;
}

float vcLightOpticalDepth(vec3 worldPos, vec3 sunToward, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float densityMul, float coverageScale, float volumeSize, int lightSteps,
    sampler3D cloudNoise, int hasCloudNoise, sampler2D coverageMap, int hasCoverageMap, vec3 windOffset)
{
    float baseAtOrigin = vcCloudBaseDensity(worldPos, planetCenter, planetRadius, layerBase, layerTop,
        coverageScale, volumeSize, cloudNoise, hasCloudNoise, coverageMap, hasCoverageMap, windOffset, 2.0);
    return vcLightOpticalDepthFromBase(baseAtOrigin, worldPos, sunToward, planetCenter, planetRadius,
        layerBase, layerTop, densityMul, coverageScale, volumeSize, lightSteps,
        cloudNoise, hasCloudNoise, coverageMap, hasCoverageMap, windOffset);
}

#endif // GENESIS_VOLUMETRIC_CLOUDS_GLSL
