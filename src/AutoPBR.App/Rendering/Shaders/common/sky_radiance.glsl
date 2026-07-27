// Shared procedural sky radiance (day/night gradient + luminance tonemap).
// Used by the sky dome pass and by LOD-ring aerial fog so both share one atmosphere.
// Intentionally does not include atmosphere.glsl so genesis IBL stays free of ATM_* symbols;
// Mie phase is inlined here for the day radiance halo only.

#ifndef GENESIS_SKY_RADIANCE_GLSL
#define GENESIS_SKY_RADIANCE_GLSL

//!include "common.glsl"

const float SKY_RAD_PI = 3.14159265358979323846;

float skyRadianceMiePhase(float cosTheta)
{
    const float g = 0.76;
    float gg = g * g;
    float base = max(1.0 + gg - 2.0 * g * cosTheta, 1e-3);
    return (1.0 - gg) / (4.0 * SKY_RAD_PI * base * sqrt(base));
}

// lightPropagationDir: direction the directional light travels (away from the sun/moon).
float skyDayFactor(vec3 lightPropagationDir, float sunIntensity)
{
    vec3 towardLight = normalize(-lightPropagationDir);
    float sunElev = towardLight.y;
    float dayFromSun = smoothstep(-0.04, 0.22, sunElev);
    float dayFromIntensity = smoothstep(0.08, 2.0, sunIntensity);
    return clamp(dayFromSun * dayFromIntensity, 0.0, 1.0);
}

// View-anchored horizon haze is correct at ground level but washes the lower viewport
// when the camera climbs; fade it with altitude above the ground plane.
float skyHorizonAltitudeFade(float camY, float groundY)
{
    float alt = max(camY - groundY, 0.0);
    return 1.0 - smoothstep(8.0, 56.0, alt);
}

// Daytime sky: saturated Rayleigh blue gradient + warm horizon band near the sun.
// Output is normalized linear RGB (~0..1.3); tone-map with skyTonemapLum, never a
// per-channel x/(x+k) knee (that compresses every channel toward 1 = grey/white sky).
// horizonBandScale: 1 at ground level, 0 high above (see skyHorizonAltitudeFade).
vec3 skyDayRadiance(vec3 viewDir, vec3 lightPropagationDir, float sunIntensity, float turbidity, float horizonFalloff,
    float horizonBandScale)
{
    float bandScale = clamp(horizonBandScale, 0.0, 1.0);
    float mu = clamp(viewDir.y, -1.0, 1.0);
    vec3 towardSun = normalize(-lightPropagationDir);
    float cosSun = dot(viewDir, towardSun);
    float sunElev = max(towardSun.y, 0.0);

    // Sky brightness tracks sun intensity only gently (perceptual auto-exposure).
    float illum = 0.8 + 0.2 * smoothstep(1.0, 12.0, max(sunIntensity, 0.0));

    // Rayleigh blue: saturated zenith, paler toward horizon (linear RGB targets).
    vec3 zenithBlue = vec3(0.052, 0.22, 0.74);
    vec3 horizonBlue = vec3(0.38, 0.62, 0.98);
    float gradT = pow(1.0 - max(mu, 0.0), 2.4);
    vec3 sky = mix(zenithBlue, horizonBlue, gradT * mix(0.7, 1.0, bandScale));

    // Haze band hugging the horizon only (high exponent = tight band).
    float bandExp = mix(9.0, 3.5, clamp(horizonFalloff, 0.0, 1.0));
    float horizonBand = pow(1.0 - max(mu, 0.0), bandExp);

    float turbidityT = clamp((turbidity - 1.0) / 9.0, 0.0, 1.0);
    vec3 hazeCol = mix(vec3(0.80, 0.90, 1.0), vec3(0.92, 0.88, 0.82), turbidityT);
    sky = mix(sky, hazeCol, horizonBand * mix(0.25, 0.55, turbidityT) * bandScale);

    // Warm sunrise/sunset band: strongest at low sun, biased toward the sun azimuth.
    // Use a smooth sun-facing weight (never max(cosSun,0)): a hard hemisphere cut leaves a
    // C1 crease on the sky dome that tracks the sun as a visible world-space line.
    float lowSun = 1.0 - smoothstep(0.04, 0.42, sunElev);
    float sunFacing = clamp(cosSun * 0.5 + 0.5, 0.0, 1.0);
    float sunBias = pow(sunFacing, 3.0);
    vec3 warmCol = vec3(1.0, 0.46, 0.18);
    sky = mix(sky, warmCol, horizonBand * lowSun * sunBias * 0.85 * bandScale);

    // Forward Mie halo around the sun (warmer when the sun is low).
    vec3 mieTint = mix(vec3(1.0, 0.95, 0.85), warmCol, lowSun);
    float mieAmt = skyRadianceMiePhase(cosSun) * mix(0.05, 0.4, turbidityT);
    sky += mieTint * mieAmt * 0.4;

    return max(sky * illum, vec3(0.0));
}

// Luminance-preserving Reinhard: compresses brightness while keeping hue ratios,
// so the blue sky stays blue instead of washing out to white.
vec3 skyTonemapLum(vec3 c)
{
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    if (l <= 1e-5)
    {
        return c;
    }

    return c * ((l / (1.0 + l)) / l);
}

vec3 skyNightZenith(vec3 viewDir)
{
    float t = clamp(viewDir.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.01, 0.012, 0.02), vec3(0.02, 0.035, 0.07), t);
}

// Linear procedural sky (day/night mix) used by the dome and LOD aerial fog.
vec3 skyProceduralRadiance(vec3 viewDir, vec3 lightPropagationDir, float sunIntensity, float turbidity,
    float horizonFalloff, float horizonBandScale)
{
    vec3 col = skyDayRadiance(viewDir, lightPropagationDir, sunIntensity, turbidity, horizonFalloff, horizonBandScale);
    float dayAmt = skyDayFactor(lightPropagationDir, sunIntensity);
    vec3 nightSky = skyNightZenith(viewDir);
    return max(mix(nightSky, col, dayAmt), vec3(0.0));
}

// Mild horizon bias for LOD fog / above-horizon haze so the rim matches aerial perspective
// without collapsing fully into washed haze.
vec3 skySeamFogDir(vec3 viewDir)
{
    vec3 d = normalize(viewDir);
    float y = clamp(mix(d.y, 0.12, 0.35), 0.02, 0.55);
    return normalize(vec3(d.x, y, d.z));
}

// Soft aerial band just above the geometric horizon so the sky meets LOD fog.
// Returns a mix weight in [0,1]; caller mixes sky toward horizon-biased radiance.
float skyAboveHorizonHazeWeight(vec3 viewDir, float strength, float horizonBandScale)
{
    if (strength <= 0.0)
    {
        return 0.0;
    }

    float elev = max(normalize(viewDir).y, 0.0);
    // Tight exponential so zenith stays clear; strongest right at the rim.
    float band = exp(-elev * 7.5);
    return band * saturate1(strength) * 0.72 * clamp(horizonBandScale, 0.0, 1.0);
}

#endif // GENESIS_SKY_RADIANCE_GLSL
