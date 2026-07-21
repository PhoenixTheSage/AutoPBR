// Genesis preview shader - generated world-space environment probe.
// Diffuse: six-direction irradiance probe so side faces are not tied to screen-space sun placement.
// Specular: generated sky/ground/sun "virtual cubemap" prefiltered by roughness.

#ifndef GENESIS_IBL_GLSL
#define GENESIS_IBL_GLSL

//!include "common.glsl"
//!include "brdf.glsl"
//!include "sky_view_lut.glsl"

struct PreviewEnvCtx
{
    vec3 towardSun;
    float dayAmt;
    vec3 skyTintLin;
    vec3 groundTintLin;
    vec3 sunWarmColor;
    float sunIntensity;
};

vec3 fakeIblSky(vec3 dir, vec3 skyTint, vec3 groundTint)
{
    float t = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(groundTint, skyTint, smoothstep(0.0, 1.0, t));
}

vec3 previewEnvTowardSun(vec3 lightPropagationDir)
{
    float l2 = dot(lightPropagationDir, lightPropagationDir);
    if (l2 <= GEN_EPS)
    {
        return normalize(vec3(0.35, 0.85, 0.4));
    }

    return -lightPropagationDir * inversesqrt(l2);
}

float previewEnvDayFactorFromTowardSun(vec3 towardSun, float sunIntensity)
{
    float dayFromSun = smoothstep(-0.04, 0.22, towardSun.y);
    float dayFromIntensity = smoothstep(0.08, 2.0, sunIntensity);
    return saturate1(dayFromSun * dayFromIntensity);
}

vec3 previewEnvSunWarmColorFromTowardSun(vec3 towardSun, vec3 lightColor, float sunIntensity)
{
    vec3 horizonWarm = vec3(1.0, 0.62, 0.28);
    vec3 zenithWarm = vec3(1.0, 0.96, 0.86);
    float boundedIntensity = 0.65 + 0.35 * smoothstep(1.0, 12.0, sunIntensity);
    return lightColor * mix(horizonWarm, zenithWarm, smoothstep(0.0, 0.42, max(towardSun.y, 0.0))) * boundedIntensity;
}

PreviewEnvCtx buildPreviewEnvCtx(vec3 lightPropagationDir, vec3 lightColor, float sunIntensity,
    vec3 skyTint, vec3 groundTint)
{
    PreviewEnvCtx ctx;
    ctx.towardSun = previewEnvTowardSun(lightPropagationDir);
    ctx.dayAmt = previewEnvDayFactorFromTowardSun(ctx.towardSun, sunIntensity);
    ctx.skyTintLin = srgbToLinear(skyTint);
    ctx.groundTintLin = srgbToLinear(groundTint);
    ctx.sunWarmColor = previewEnvSunWarmColorFromTowardSun(ctx.towardSun, lightColor, sunIntensity);
    ctx.sunIntensity = sunIntensity;
    return ctx;
}

vec3 previewEnvSkyGroundRadianceCtx(PreviewEnvCtx ctx, vec3 dir, int enableAtmoSky, sampler2D atmoSkyViewLut)
{
    vec3 d = normalize(dir);
    vec3 sky = enableAtmoSky > 0
        ? srgbToLinear(sampleSkyViewLutSrgb(atmoSkyViewLut, d))
        : fakeIblSky(d, ctx.skyTintLin, ctx.groundTintLin);

    float below = smoothstep(0.08, -0.24, d.y);
    vec3 groundReflection = ctx.groundTintLin * 0.28;
    return mix(sky, groundReflection, below);
}

vec3 previewEnvSunRadianceCtx(PreviewEnvCtx ctx, vec3 dir, float roughness)
{
    float cosSun = max(dot(normalize(dir), ctx.towardSun), 0.0);
    float r = saturate1(roughness);

    float tightExp = mix(1600.0, 18.0, r);
    float broadExp = mix(96.0, 4.0, r);
    float tight = pow(cosSun, tightExp) * mix(7.5, 1.1, r);
    float broad = pow(cosSun, broadExp) * mix(0.25, 0.85, r);
    float sunScale = ctx.dayAmt * mix(0.35, 1.4, smoothstep(0.4, 10.0, ctx.sunIntensity));
    // Preserve original sun-intensity weighting via ctx.sunWarmColor path in legacy function.
    return ctx.sunWarmColor * (tight + broad) * sunScale;
}

vec3 previewEnvCubemapRadianceCtx(PreviewEnvCtx ctx, vec3 dir, float roughness, int enableAtmoSky,
    sampler2D atmoSkyViewLut)
{
    vec3 skyGround = previewEnvSkyGroundRadianceCtx(ctx, dir, enableAtmoSky, atmoSkyViewLut);
    vec3 sun = previewEnvSunRadianceCtx(ctx, dir, roughness);
    return max(skyGround + sun, vec3(0.0));
}

vec3 previewAmbientProbeIrradianceCtx(PreviewEnvCtx ctx, vec3 N, int enableAtmoSky, sampler2D atmoSkyViewLut)
{
    vec3 n = normalize(N);
    vec3 px = vec3(1.0, 0.0, 0.0);
    vec3 nx = vec3(-1.0, 0.0, 0.0);
    vec3 py = vec3(0.0, 1.0, 0.0);
    vec3 ny = vec3(0.0, -1.0, 0.0);
    vec3 pz = vec3(0.0, 0.0, 1.0);
    vec3 nz = vec3(0.0, 0.0, -1.0);

    float roughDiffuse = 1.0;
    vec3 radPx = previewEnvCubemapRadianceCtx(ctx, px, roughDiffuse, enableAtmoSky, atmoSkyViewLut);
    vec3 radNx = previewEnvCubemapRadianceCtx(ctx, nx, roughDiffuse, enableAtmoSky, atmoSkyViewLut);
    vec3 radPy = previewEnvCubemapRadianceCtx(ctx, py, roughDiffuse, enableAtmoSky, atmoSkyViewLut);
    vec3 radNy = previewEnvCubemapRadianceCtx(ctx, ny, roughDiffuse, enableAtmoSky, atmoSkyViewLut);
    vec3 radPz = previewEnvCubemapRadianceCtx(ctx, pz, roughDiffuse, enableAtmoSky, atmoSkyViewLut);
    vec3 radNz = previewEnvCubemapRadianceCtx(ctx, nz, roughDiffuse, enableAtmoSky, atmoSkyViewLut);

    float wPx = max(dot(n, px), 0.0);
    float wNx = max(dot(n, nx), 0.0);
    float wPy = max(dot(n, py), 0.0);
    float wNy = max(dot(n, ny), 0.0);
    float wPz = max(dot(n, pz), 0.0);
    float wNz = max(dot(n, nz), 0.0);

    vec3 weighted = radPx * wPx + radNx * wNx + radPy * wPy + radNy * wNy + radPz * wPz + radNz * wNz;
    float wSum = max(wPx + wNx + wPy + wNy + wPz + wNz, GEN_EPS);
    vec3 probe = weighted / wSum;

    vec3 sideAverage = (radPx + radNx + radPz + radNz) * 0.25;
    vec3 floorFill = radPy * 0.10 + sideAverage * 0.18 + radNy * 0.08;
    probe += floorFill;

    float lum = dot(probe, vec3(0.2126, 0.7152, 0.0722));
    return mix(vec3(lum), probe, 0.35);
}

vec3 iblPrefilteredSkyRadianceWithProbe(PreviewEnvCtx ctx, vec3 N, vec3 R, float roughness, vec3 probe,
    int enableAtmoSky, sampler2D atmoSkyViewLut)
{
    float alpha = brdfGgxAlpha(roughness);

    vec3 up = abs(R.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 t = normalize(cross(up, R));
    vec3 b = cross(R, t);
    float spread = 0.035 + alpha * 1.65;
    vec3 center = previewEnvCubemapRadianceCtx(ctx, R, roughness, enableAtmoSky, atmoSkyViewLut);
    vec3 tap0 = previewEnvCubemapRadianceCtx(ctx, normalize(R + t * spread), roughness, enableAtmoSky, atmoSkyViewLut);
    vec3 tap1 = previewEnvCubemapRadianceCtx(ctx, normalize(R - t * spread), roughness, enableAtmoSky, atmoSkyViewLut);
    vec3 tap2 = previewEnvCubemapRadianceCtx(ctx, normalize(R + b * spread), roughness, enableAtmoSky, atmoSkyViewLut);
    vec3 tap3 = previewEnvCubemapRadianceCtx(ctx, normalize(R - b * spread), roughness, enableAtmoSky, atmoSkyViewLut);
    vec3 wide = (tap0 + tap1 + tap2 + tap3) * 0.25;

    vec3 prefiltered = mix(center, wide, smoothstep(0.02, 0.35, roughness));
    return mix(prefiltered, probe, smoothstep(0.35, 1.0, roughness) * 0.55);
}

vec3 fakeIblSpecularWithProbe(PreviewEnvCtx ctx, vec3 probe, vec3 N, vec3 V, vec3 f0, float roughness, float metallic,
    int enableAtmoSky, sampler2D atmoSkyViewLut)
{
    vec3 R = reflect(-V, N);
    float NoV = max(dot(N, V), 0.0);

    float specRough = max(roughness, mix(0.72, 0.04, metallic));
    vec2 envBrdf = iblEnvBrdfFactor(NoV, specRough);

    vec3 prefiltered = iblPrefilteredSkyRadianceWithProbe(ctx, N, R, specRough, probe, enableAtmoSky, atmoSkyViewLut);
    vec3 specular = prefiltered * (f0 * envBrdf.x + envBrdf.y);
    float dielectricWt = (1.0 - metallic) * 0.08;
    float metalWt = metallic;
    return specular * (dielectricWt + metalWt);
}

// LOD-ring aerial fog: sky-lit haze with celestial inscatter so night is not full-bright.
// Intentionally avoids per-fragment sky-view LUT sampling (IBL/specular still uses the LUT). Soft
// haze only needs day/night tint + light inscatter; that cut is the main shadows-off GPU win.
vec3 previewAerialFogRadiance(vec3 worldPos, vec3 cameraPos, vec3 lightPropagationDir, vec3 lightColor,
    float sunIntensity, vec3 skyTint, vec3 groundTint, int enableAtmoSky, sampler2D atmoSkyViewLut)
{
    // enableAtmoSky / atmoSkyViewLut kept for call-site parity with IBL; fog uses tint haze only.
    PreviewEnvCtx ctx = buildPreviewEnvCtx(lightPropagationDir, lightColor, sunIntensity, skyTint, groundTint);
    vec3 toFrag = worldPos - cameraPos;
    float len2 = dot(toFrag, toFrag);
    vec3 viewDir = len2 > GEN_EPS * GEN_EPS ? toFrag * inversesqrt(len2) : vec3(0.0, 0.0, 1.0);
    // Bias toward the low horizon so distant LOD haze matches aerial perspective.
    vec3 fogDir = normalize(vec3(viewDir.x, clamp(viewDir.y * 0.35 + 0.10, 0.06, 0.45), viewDir.z));

    float h = clamp(fogDir.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 nightHaze = mix(vec3(0.01, 0.012, 0.02), vec3(0.02, 0.035, 0.07), h) * 2.0;
    // Tint-space day haze (no LUT). Height blend keeps a little horizon lift without a texture fetch.
    vec3 dayHaze = mix(ctx.groundTintLin, ctx.skyTintLin, mix(0.45, 0.68, h));

    vec3 fog = mix(nightHaze, dayHaze, ctx.dayAmt);
    // Celestial light reception (sun by day, reflected moonlight by night).
    float scatter = mix(0.48, 0.10, ctx.dayAmt);
    fog += lightColor * scatter;
    return max(fog, vec3(0.0));
}

#endif // GENESIS_IBL_GLSL
