// Final present encode helpers (SDR vs scRGB HDR).
// No #version - included from entry fragments only.

#ifndef GENESIS_PRESENT_ENCODE_GLSL
#define GENESIS_PRESENT_ENCODE_GLSL

//!include "tonemap.glsl"
//!include "common.glsl"

// scRGB: 1.0 = 80 nits.
// Match SDR midtone appearance via ACES, then place reference white at paper-white nits
// and restore crushed highlight energy into scRGB headroom (stops "very dark" + flashy raw HDR).
vec3 presentEncodeScRgb(vec3 linearScene, float paperWhiteNits, float peakNits)
{
    vec3 linear = max(linearScene, vec3(0.0));
    vec3 aces = tonemapAcesNarkowicz(linear);
    float scale = max(paperWhiteNits, 80.0) / 80.0;
    vec3 base = aces * scale;

    // Excess above ~display-referred white rides in HDR headroom.
    vec3 excess = max(linear - vec3(1.0), vec3(0.0)) * scale;
    float peakSc = peakNits > 80.0 ? (peakNits / 80.0) : (scale * 4.0);
    float headroom = max(peakSc - scale, 0.5);
    vec3 hi = softKnee(excess, headroom) * headroom * 0.35;
    return base + hi;
}

vec3 presentEncodeSdr(vec3 linearScene, vec2 fragCoord)
{
    vec3 mapped = tonemapAcesNarkowicz(linearScene);
    return ditherSrgb8(linearToSrgb(mapped), fragCoord);
}

#endif
