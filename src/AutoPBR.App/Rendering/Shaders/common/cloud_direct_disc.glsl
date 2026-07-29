// Full-resolution direct-disc extinction derived from reconstructed cloud opacity.
// This stays after temporal reconstruction so a moving sun cannot leave an alpha trail.

#ifndef GENESIS_CLOUD_DIRECT_DISC_GLSL
#define GENESIS_CLOUD_DIRECT_DISC_GLSL

float cdoDirectDiscOcclusionAlpha(
    float cloudOpacity,
    float cosTheta,
    float cosDiscEdge)
{
    float opacity = clamp(cloudOpacity, 0.0, 1.0);
    float discAngle = max(
        acos(clamp(cosDiscEdge, -1.0, 1.0)),
        1e-3);
    float discRadius = acos(clamp(cosTheta, -1.0, 1.0)) / discAngle;
    float discMask = 1.0 - smoothstep(0.88, 1.08, discRadius);

    // Direct solar radiance is far brighter than diffuse sky radiance. A stronger
    // direct-beam response lets thin cloud soften the disc and seals its core once
    // integrated cloud opacity reaches 0.6, without changing cloud opacity elsewhere.
    float directOpacity = 1.0 - pow(max(1.0 - opacity, 0.0), 5.0);
    float denseSeal = smoothstep(0.45, 0.60, opacity);
    directOpacity = mix(directOpacity, 1.0, denseSeal);
    return mix(opacity, max(opacity, directOpacity), discMask);
}

#endif // GENESIS_CLOUD_DIRECT_DISC_GLSL
