// CQ3.5 shared terrain/fog lookup for the stabilized ground cloud-shadow field.
// Missing and out-of-range samples deliberately resolve to full sunlight.

#ifndef GENESIS_CLOUD_GROUND_TRANSMITTANCE_GLSL
#define GENESIS_CLOUD_GROUND_TRANSMITTANCE_GLSL

vec2 cgtWorldToUv(
    vec3 worldPosition,
    vec3 basisRight,
    vec3 basisUp,
    vec2 planeCenter,
    float worldSpan)
{
    vec2 lightPlane = vec2(
        dot(worldPosition, basisRight),
        dot(worldPosition, basisUp));
    return (lightPlane - planeCenter) / max(worldSpan, 1e-4) + vec2(0.5);
}

float cgtSampleGroundTransmittance(
    sampler2D transmittanceTexture,
    vec3 worldPosition,
    vec3 basisRight,
    vec3 basisUp,
    vec2 planeCenter,
    float worldSpan,
    vec2 texelSize,
    int hasTransmittance)
{
    if (hasTransmittance <= 0)
    {
        return 1.0;
    }

    vec2 uv = cgtWorldToUv(
        worldPosition,
        basisRight,
        basisUp,
        planeCenter,
        worldSpan);
    if (any(isnan(uv)) || any(isinf(uv)))
    {
        return 1.0;
    }

    if (any(lessThan(uv, vec2(0.0))) ||
        any(greaterThan(uv, vec2(1.0))))
    {
        return 1.0;
    }

    // Fade the outer two texels back to full sunlight. This prevents a hard square
    // boundary when camera motion takes a receiver beyond the published far footprint.
    vec2 edgeDistance = min(uv, vec2(1.0) - uv);
    vec2 feather = max(texelSize * 2.0, vec2(1e-6));
    float coverage = min(
        smoothstep(0.0, feather.x, edgeDistance.x),
        smoothstep(0.0, feather.y, edgeDistance.y));
    float sampledRaw = texture(transmittanceTexture, uv).r;
    if (isnan(sampledRaw) || isinf(sampledRaw))
    {
        return 1.0;
    }

    float sampled = clamp(sampledRaw, 0.0, 1.0);
    return mix(1.0, sampled, coverage);
}

#endif // GENESIS_CLOUD_GROUND_TRANSMITTANCE_GLSL
