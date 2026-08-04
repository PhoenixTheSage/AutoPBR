// Procedural sky dome: day/night cycle, stars, horizon, below-horizon fog.

#ifndef GENESIS_SKY_DOME_GLSL
#define GENESIS_SKY_DOME_GLSL

//!include "common.glsl"
//!include "sky_view_lut.glsl"
//!include "atmosphere.glsl"
//!include "sky_radiance.glsl"

const float SKY_PI = 3.14159265358979323846;

float skyHash31(vec3 p)
{
    p = fract(p * 0.3183099 + vec3(0.17, 0.31, 0.47));
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

// Sun disc + aureole in angular space; add to sky radiance before skyTonemapLum.
// Disc: limb-darkened core (I = 1 - u * (1 - sqrt(1 - r^2))) with a thin edge softened
// by atmospheric seeing. Aureole: tight circumsolar glow plus a wide 1/theta^2 glare
// skirt (CIE-like) that fades below visibility instead of hitting a circular boundary.
vec3 skySunDiscAureole(vec3 viewDir, vec3 lightPropagationDir, float cosDiscEdge,
    float bloomRadiusUv, float bloomStrength, float discBrightness, float turbidity)
{
    if (bloomStrength <= 0.0 && discBrightness <= 0.0)
    {
        return vec3(0.0);
    }

    vec3 towardSun = normalize(-lightPropagationDir);
    vec3 vd = normalize(viewDir);
    float cosAngle = clamp(dot(vd, towardSun), -1.0, 1.0);
    float thetaDisc = max(acos(clamp(cosDiscEdge, -1.0, 1.0)), 1e-3);
    float r = acos(cosAngle) / thetaDisc;

    float sunElev = max(towardSun.y, 0.0);
    float lowSun = 1.0 - smoothstep(0.04, 0.42, sunElev);
    float turbidityT = clamp((turbidity - 1.0) / 9.0, 0.0, 1.0);

    // Planet-curvature occlusion: per-pixel slice at the horizon line so the disc
    // visibly sinks behind the planet edge. Edge softness is a fraction of the disc
    // radius (atmospheric refraction smear); the glow fades over a wider band.
    float pixelElev = asin(clamp(vd.y, -1.0, 1.0)) / thetaDisc;
    float discCut = smoothstep(-0.22, 0.1, pixelElev);
    float glowCut = smoothstep(-3.0, 0.5, pixelElev);

    // Limb-darkened disc; edge softened over the last 8 percent of the radius.
    float disc = 0.0;
    if (r < 1.0)
    {
        float limb = 1.0 - 0.6 * (1.0 - sqrt(max(1.0 - r * r, 0.0)));
        disc = limb * (1.0 - smoothstep(0.92, 1.0, r)) * discCut;
    }

    // Aureole width in disc radii; the bloom-radius setting and haze both widen it.
    float spread = mix(2.5, 9.0, clamp(bloomRadiusUv * 36.0, 0.0, 1.0)) * (1.0 + turbidityT * 1.6);
    float circumsolar = exp(-pow(max(r - 1.0, 0.0) / (spread * 0.4), 1.5));
    float skirt = 1.0 / (1.0 + pow(r / spread, 2.0));

    // Vivid warm disc that reddens at the horizon; glow whitens with haze.
    vec3 discCol = mix(vec3(1.0, 0.90, 0.72), vec3(1.0, 0.46, 0.12), lowSun);
    vec3 glowCol = mix(vec3(1.0, 0.80, 0.52), vec3(0.92, 0.93, 1.0), turbidityT * 0.7);
    glowCol = mix(glowCol, vec3(1.0, 0.40, 0.10), lowSun * 0.85);

    // Disc amplitude is HDR (tone-mapped to near-white on SDR; left linear for
    // presentEncodeScRgb so the core rides paper-white headroom). Aureole stays in sky range.
    vec3 glow = glowCol * (circumsolar * 1.85 + skirt * 0.4) * glowCut;
    float discBright = max(discBrightness, 0.0);
    float bloom = max(bloomStrength, 0.0);
    return (discCol * disc * 34.0 * discBright + glow) * bloom;
}

// Atmospheric scintillation: stable per-star magnitude with a small multi-frequency
// wobble. Avoid large single-sine gains - those read as whole-sky exposure pulsing.
float skyStarTwinkle(float timeSec, float seed)
{
    float phase = seed * 6.2831853;
    float rate = mix(1.7, 5.5, fract(seed * 17.13));
    float scint =
        0.50 * sin(timeSec * rate + phase) +
        0.32 * sin(timeSec * rate * 1.73 + phase * 1.9) +
        0.18 * sin(timeSec * rate * 3.11 + phase * 2.7);
    // Keep ~+/-10% of base brightness so individual stars twinkle without sky-wide pulse.
    float twinkle = 1.0 + 0.10 * scint;
    // Occasional brief sparkle (pow keeps duty cycle low).
    float sparkle = pow(max(sin(timeSec * rate * 0.41 + phase * 3.7), 0.0), 28.0);
    return twinkle + sparkle * mix(0.08, 0.22, seed);
}

vec3 skyStars(vec3 viewDir, float timeSec)
{
    if (viewDir.y <= 0.01)
    {
        return vec3(0.0);
    }

    vec3 p = normalize(viewDir) * 140.0;
    float grid = 6.5;
    vec3 cell = floor(p * grid);
    vec3 local = fract(p * grid) - 0.5;
    // Soft disc covering most of the hash cell (old path filled the whole cell).
    // A tight gaussian (~exp(-r^2*72)) crushed stars to subpixel dots under TAA.
    float r = length(local);
    float core = 1.0 - smoothstep(0.18, 0.52, r);
    float spike = exp(-r * r * 14.0);
    float shape = max(core, spike);

    float h = skyHash31(cell);
    float primary = 0.0;
    if (h > 0.9935)
    {
        float mag = (h - 0.9935) / 0.0065;
        float base = mix(0.75, 1.35, pow(clamp(mag, 0.0, 1.0), 0.65));
        primary = base * skyStarTwinkle(timeSec, h) * shape;
    }

    float h2 = skyHash31(cell + vec3(17.0, 3.0, 11.0));
    float secondary = 0.0;
    if (h2 > 0.9985)
    {
        float mag2 = (h2 - 0.9985) / 0.0015;
        float base2 = mix(0.45, 0.95, pow(clamp(mag2, 0.0, 1.0), 0.65));
        secondary = base2 * skyStarTwinkle(timeSec, h2) * shape;
    }

    float star = primary + secondary;
    // Cool/warm tint variation so the field feels less like a flat exposure plane.
    vec3 tint = mix(vec3(0.82, 0.90, 1.0), vec3(1.0, 0.92, 0.82), fract(h * 7.91));
    return tint * star;
}

vec3 skyHorizonGlow(vec3 viewDir, float dayAmt, vec3 sunTint, float horizonBandScale)
{
    float band = exp(-abs(viewDir.y) * 9.0);
    vec3 nightGlow = vec3(0.04, 0.05, 0.08);
    vec3 dayGlow = sunTint * 0.28 + vec3(0.42, 0.66, 0.86);
    return mix(nightGlow, dayGlow, dayAmt) * band * 0.42 * clamp(horizonBandScale, 0.0, 1.0);
}

vec3 skyBelowHorizonFog(vec3 viewDir, float strength, float horizonBandScale)
{
    if (viewDir.y >= 0.0 || strength <= 0.0)
    {
        return vec3(0.0);
    }

    float depth = smoothstep(0.0, -0.55, viewDir.y);
    vec3 fogCol = vec3(0.06, 0.07, 0.09);
    return fogCol * depth * strength * clamp(horizonBandScale, 0.0, 1.0);
}

vec2 skyMoonDiscUv(vec3 viewDir, vec3 towardMoon, float cosDiscEdge)
{
    vec3 vd = normalize(viewDir);
    float cosAngle = clamp(dot(vd, towardMoon), -1.0, 1.0);
    float sinTheta = sqrt(max(1.0 - cosAngle * cosAngle, 0.0));
    vec3 tangent = vd - towardMoon * cosAngle;
    float tLen2 = dot(tangent, tangent);
    if (tLen2 < 1e-10)
    {
        return vec2(0.5);
    }

    tangent *= inversesqrt(tLen2);
    vec3 moonUp = abs(towardMoon.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 moonRight = normalize(cross(moonUp, towardMoon));
    moonUp = cross(towardMoon, moonRight);
    float angularRadius = max(acos(clamp(cosDiscEdge, -1.0, 1.0)), 1e-4);
    vec2 discUv = vec2(dot(tangent, moonRight), dot(tangent, moonUp)) * (sinTheta / angularRadius);
    return discUv * 0.5 + 0.5;
}

// Procedural full moon: tight limb, mare/crater variation, faint outer penumbra only.
vec3 skyMoonDiscShading(vec3 viewDir, vec3 lightPropagationDir, float cosDiscEdge)
{
    vec3 towardMoon = normalize(lightPropagationDir);
    float cosAngle = dot(normalize(viewDir), towardMoon);
    float edge = clamp(cosDiscEdge, 0.94, 0.99998);
    float penumbra = (1.0 - edge) * 2.5;
    float outerCos = clamp(edge - penumbra, -1.0, 1.0);
    float disc = smoothstep(outerCos, edge, cosAngle);
    if (disc <= 1e-4)
    {
        return vec3(0.0);
    }

    vec2 mUv = skyMoonDiscUv(viewDir, towardMoon, edge);
    vec3 samplePos = vec3(mUv * 8.0, 0.0);
    float n0 = skyHash31(samplePos);
    float n1 = skyHash31(samplePos * 2.13 + vec3(1.7, 4.1, 0.0));
    float n2 = skyHash31(samplePos * 4.37 + vec3(9.0, 2.3, 0.0));
    float mare = smoothstep(0.38, 0.62, n0 * 0.55 + n1 * 0.3 + n2 * 0.15);
    float crater = smoothstep(0.82, 0.94, n1) * smoothstep(0.15, 0.45, n2);
    vec3 highland = vec3(0.78, 0.80, 0.84);
    vec3 lowland = vec3(0.58, 0.60, 0.66);
    vec3 moonCol = mix(highland, lowland, mare * 0.9);
    moonCol = mix(moonCol, vec3(0.48, 0.50, 0.55), crater * 0.55);

    float radial = length(mUv - 0.5) * 2.0;
    moonCol *= 1.0 - smoothstep(0.55, 1.0, radial) * 0.35;
    moonCol *= 0.65 + 0.35 * disc;
    return moonCol * disc;
}

// Reconstruct a unit view direction from sky-view LUT texel UV (matches atmo_skyview.frag).
// Fullscreen bake UVs are texel centers; convert to unit [0,1] so edge columns share
// azimuth = pi (identical radiance) and Repeat sampling stays continuous at the -Z meridian.
vec3 skyViewDirFromLutUv(vec2 texelUv)
{
    vec2 unitUv = clamp(
        skyViewLutTexelToUnitUv(texelUv, vec2(SKY_VIEW_LUT_WIDTH, SKY_VIEW_LUT_HEIGHT)),
        vec2(0.0),
        vec2(1.0));
    float viewZenith = unitUv.y;
    float u = unitUv.x;
    // Both u=0 and u=1 map to the -Z meridian so the wrapped edge columns match.
    float azimuth = (u - 0.5) * 2.0 * ATM_PI;
    if (u <= 0.0 || u >= 1.0)
    {
        azimuth = ATM_PI;
    }

    float sinTheta = sin(viewZenith * ATM_PI);
    float cosTheta = cos(viewZenith * ATM_PI);
    return normalize(vec3(sinTheta * sin(azimuth), cosTheta, sinTheta * cos(azimuth)));
}

vec3 skySoftKnee(vec3 x, float knee)
{
    return softKnee(x, knee);
}

#endif // GENESIS_SKY_DOME_GLSL
