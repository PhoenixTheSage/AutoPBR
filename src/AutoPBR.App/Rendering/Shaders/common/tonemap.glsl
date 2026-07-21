// Genesis preview shader - tonemapping.
// ACES Filmic (Narkowicz fit). Cheap and widely accepted as the preview standard.
// Reference: https://knarkowicz.wordpress.com/2016/01/06/aces-filmic-tone-mapping-curve/

#ifndef GENESIS_TONEMAP_GLSL
#define GENESIS_TONEMAP_GLSL

//!include "common.glsl"

vec3 tonemapAcesNarkowicz(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate3((x * (a * x + b)) / (x * (c * x + d) + e));
}

// Inverse of tonemapAcesNarkowicz (per channel). Used so HDR can fog in the same
// post-ACES space as SDR, then recover linear values for presentEncodeScRgb.
vec3 inverseTonemapAcesNarkowicz(vec3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    // Keep clear of the curve asymptote so the quadratic denominator stays stable.
    vec3 y = min(saturate3(color), vec3(0.98));
    vec3 A = y * c - a;
    vec3 B = y * d - b;
    vec3 C = y * e;
    vec3 disc = max(B * B - 4.0 * A * C, vec3(0.0));
    return max((-B - sqrt(disc)) / (2.0 * A), vec3(0.0));
}

#endif // GENESIS_TONEMAP_GLSL
