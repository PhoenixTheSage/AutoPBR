// Shared sky-view LUT UV mapping and sampling (used by IBL and sky dome).
// Cylindrical azimuth wraps at the -Z meridian; bake and sample must agree on
// texel-center UVs and explicit wrap blending or a world-space vertical seam appears
// through cloud ambient / IBL even when the display sky pass is procedural.

#ifndef GENESIS_SKY_VIEW_LUT_GLSL
#define GENESIS_SKY_VIEW_LUT_GLSL

const float SKY_VIEW_LUT_WIDTH = 192.0;
const float SKY_VIEW_LUT_HEIGHT = 108.0;

// Map unit UV in [0,1] onto texel centers of a res-wide texture (Hillaire).
vec2 skyViewLutUnitToTexelUv(vec2 unitUv, vec2 resolution)
{
    vec2 safeRes = max(resolution, vec2(2.0));
    return unitUv * ((safeRes - 1.0) / safeRes) + (0.5 / safeRes);
}

// Inverse: texel-center UV from a full-screen LUT bake to unit parameter in [0,1].
vec2 skyViewLutTexelToUnitUv(vec2 texelUv, vec2 resolution)
{
    vec2 safeRes = max(resolution, vec2(2.0));
    return (texelUv - (0.5 / safeRes)) * (safeRes / (safeRes - 1.0));
}

vec2 skyViewLutUv(vec3 viewDir)
{
    vec3 d = normalize(viewDir);
    float viewZenith = acos(clamp(d.y, -1.0, 1.0)) / GEN_PI;
    // atan(x, z) in (-pi, pi]; map to [0, 1). Keep u=1 aliased to u=0 for Repeat.
    float u = atan(d.x, d.z) * (0.5 / GEN_PI) + 0.5;
    if (u >= 1.0)
    {
        u = 0.0;
    }

    return vec2(u, clamp(viewZenith, 0.0, 1.0));
}

vec3 sampleSkyViewLutSrgb(sampler2D lut, vec3 viewDir)
{
    vec2 unitUv = skyViewLutUv(viewDir);
    vec2 res = vec2(SKY_VIEW_LUT_WIDTH, SKY_VIEW_LUT_HEIGHT);
    // Prefer the actual texture size when available (desktop / ES3).
    vec2 texRes = vec2(textureSize(lut, 0));
    if (texRes.x >= 2.0 && texRes.y >= 2.0)
    {
        res = texRes;
    }

    float v = skyViewLutUnitToTexelUv(vec2(unitUv.x, unitUv.y), res).y;
    float texelU = 1.0 / res.x;
    float u = unitUv.x;

    // Explicit circular blend across the -Z meridian. Relying on Repeat alone still
    // shows a crease when edge columns disagree (e.g. transmittance sampled by azimuth)
    // or when linear filtering straddles a sharp Mie gradient at the wrap.
    float seamWidth = texelU * 2.0;
    float seamDist = min(u, 1.0 - u);
    vec3 cCenter = texture(lut, skyViewLutUnitToTexelUv(vec2(u, unitUv.y), res)).rgb;
    if (seamDist >= seamWidth)
    {
        return cCenter;
    }

    // Samples just inside each side of the wrap, then mix by distance to the seam.
    vec3 cLo = texture(lut, vec2(0.5 * texelU, v)).rgb;
    vec3 cHi = texture(lut, vec2(1.0 - 0.5 * texelU, v)).rgb;
    vec3 cSeam = 0.5 * (cLo + cHi);
    float seamW = 1.0 - smoothstep(0.0, seamWidth, seamDist);
    return mix(cCenter, cSeam, seamW);
}

#endif // GENESIS_SKY_VIEW_LUT_GLSL
