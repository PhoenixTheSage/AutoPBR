// Final cloud-radiance shaping. Intermediate trace/history/upsample inputs remain
// scene-referred linear and premultiplied; this helper is used only while writing
// the cloud contribution into the already-presented destination framebuffer.

#ifndef GENESIS_CLOUD_PRESENT_GLSL
#define GENESIS_CLOUD_PRESENT_GLSL

//!include "common.glsl"
//!include "present_encode.glsl"

// Shared final encode for scene-referred linear radiance written onto an already-presented
// destination. SDR: soft-knee + sRGB. HDR: same soft-knee, then the SDR sRGB code lifted to
// paper white — ACES crushed midtones, and shaped*scale alone still read darker grey than
// the SDR preview which writes linearToSrgb(shaped) into the present target.
vec3 cpEncodeLinearRadiance(
    vec3 linearRadiance,
    int hdrPresent,
    float paperWhiteNits,
    float peakNits)
{
    vec3 linear = max(linearRadiance, vec3(0.0));
    vec3 shaped = softKnee(linear, 0.08);
    if (hdrPresent > 0)
    {
        float scale = max(paperWhiteNits, 80.0) / 80.0;
        vec3 base = linearToSrgb(shaped) * scale;
        // Bright lit faces keep a little scRGB headroom above paper white.
        vec3 excess = max(linear - vec3(1.0), vec3(0.0)) * scale;
        float peakSc = peakNits > 80.0 ? (peakNits / 80.0) : (scale * 4.0);
        float headroom = max(peakSc - scale, 0.5);
        vec3 hi = softKnee(excess, headroom) * headroom * 0.35;
        return base + hi;
    }

    return linearToSrgb(shaped);
}

// Froxel / screen-space shafts: Blend One,SrcAlpha (or SrcAlpha,One) onto an already-presented
// framebuffer. Do NOT linearToSrgb in SDR — that double-brightens over presentEncodeSdr.
// HDR follows the cloud soft-knee+paper-white family (not scene ACES midtones), but uses a
// stronger knee and sub-paper-white scale so open-sky atmospheric fill does not veil the view.
vec3 cpEncodeShaftRadiance(
    vec3 linearRadiance,
    int hdrPresent,
    float paperWhiteNits,
    float peakNits)
{
    vec3 linear = max(linearRadiance, vec3(0.0));
    if (hdrPresent > 0)
    {
        float scale = max(paperWhiteNits, 80.0) / 80.0;
        vec3 shaped = softKnee(linear, 0.35);
        vec3 base = shaped * (scale * 0.55);
        vec3 excess = max(linear - vec3(1.0), vec3(0.0)) * scale;
        float peakSc = peakNits > 80.0 ? (peakNits / 80.0) : (scale * 4.0);
        float headroom = max(peakSc - scale, 0.5);
        vec3 hi = softKnee(excess, headroom) * headroom * 0.20;
        return base + hi;
    }

    // SDR destination is already ACES+sRGB. Match the prior integrate soft-knee energy budget.
    return softKnee(linear, 0.35);
}

vec3 cpEncodeCloudRadiance(
    vec3 linearPremultipliedRadiance,
    float opacity,
    float exposure,
    int hdrPresent,
    int applyEncoding,
    float paperWhiteNits,
    float peakNits)
{
    if (applyEncoding < 1)
    {
        return linearPremultipliedRadiance;
    }

    float safeOpacity = max(opacity, 1e-5);
    vec3 straightRadiance = max(linearPremultipliedRadiance, vec3(0.0)) / safeOpacity;
    return cpEncodeLinearRadiance(
        straightRadiance * max(exposure, 0.0),
        hdrPresent,
        paperWhiteNits,
        peakNits) * opacity;
}

#endif // GENESIS_CLOUD_PRESENT_GLSL
