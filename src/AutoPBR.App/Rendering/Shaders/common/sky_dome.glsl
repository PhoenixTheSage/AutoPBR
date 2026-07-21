// Procedural sky dome: day/night cycle, stars, horizon, below-horizon fog.

#ifndef GENESIS_SKY_DOME_GLSL
#define GENESIS_SKY_DOME_GLSL

//!include "common.glsl"
//!include "sky_view_lut.glsl"
//!include "atmosphere.glsl"

const float SKY_PI = 3.14159265358979323846;

float skyHash31(vec3 p)
{
    p = fract(p * 0.3183099 + vec3(0.17, 0.31, 0.47));
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
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
    float mieAmt = atmosphereMiePhase(cosSun) * mix(0.05, 0.4, turbidityT);
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

    // Disc amplitude is HDR (tone-mapped to near-white); aureole stays in sky range.
    vec3 glow = glowCol * (circumsolar * 1.85 + skirt * 0.4) * glowCut;
    float discBright = max(discBrightness, 0.0);
    float bloom = max(bloomStrength, 0.0);
    return (discCol * disc * 34.0 * discBright + glow) * bloom;
}

vec3 skyNightZenith(vec3 viewDir)
{
    float t = clamp(viewDir.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.01, 0.012, 0.02), vec3(0.02, 0.035, 0.07), t);
}

vec3 skyStars(vec3 viewDir, float timeSec)
{
    if (viewDir.y <= 0.01)
    {
        return vec3(0.0);
    }

    vec3 p = normalize(viewDir) * 140.0;
    vec3 cell = floor(p * 6.5);
    float h = skyHash31(cell);
    float twinkle = 0.55 + 0.45 * sin(timeSec * 1.8 + h * 52.0);
    float star = step(0.9935, h) * twinkle;
    star += step(0.9985, skyHash31(cell + vec3(17.0, 3.0, 11.0))) * twinkle * 0.65;
    return vec3(star * 0.95);
}

vec3 skyHorizonGlow(vec3 viewDir, float dayAmt, vec3 sunTint, float horizonBandScale)
{
    float band = exp(-abs(viewDir.y) * 9.0);
    vec3 nightGlow = vec3(0.04, 0.05, 0.08);
    vec3 dayGlow = sunTint * 0.28 + vec3(0.28, 0.42, 0.72);
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

// Reconstruct a unit view direction from sky-view LUT UV (matches atmo_skyview.frag).
// Edge columns share azimuth = π so texel 0 and texel W-1 bake identical radiance for Repeat.
vec3 skyViewDirFromLutUv(vec2 uv)
{
    float viewZenith = uv.y;
    float u = uv.x;
    float azimuth = ATM_PI;
    if (u > 1.0 / SKY_VIEW_LUT_WIDTH && u < 1.0 - 1.0 / SKY_VIEW_LUT_WIDTH)
    {
        azimuth = (u - 0.5) * 2.0 * ATM_PI;
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
