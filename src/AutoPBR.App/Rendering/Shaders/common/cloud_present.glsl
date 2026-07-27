// Final cloud-radiance shaping. Intermediate trace/history/upsample inputs remain
// scene-referred linear and premultiplied; this helper is used only while writing
// the cloud contribution into the already-presented destination framebuffer.

#ifndef GENESIS_CLOUD_PRESENT_GLSL
#define GENESIS_CLOUD_PRESENT_GLSL

//!include "common.glsl"

vec3 cpEncodeCloudRadiance(
    vec3 linearPremultipliedRadiance,
    float opacity,
    float exposure,
    int hdrPresent,
    int applyEncoding)
{
    if (applyEncoding < 1)
    {
        return linearPremultipliedRadiance;
    }

    float safeOpacity = max(opacity, 1e-5);
    vec3 straightRadiance = max(linearPremultipliedRadiance, vec3(0.0)) / safeOpacity;
    vec3 shaped = softKnee(
        straightRadiance * max(exposure, 0.0),
        0.08);
    vec3 presented = hdrPresent > 0 ? shaped : linearToSrgb(shaped);
    return presented * opacity;
}

#endif // GENESIS_CLOUD_PRESENT_GLSL
