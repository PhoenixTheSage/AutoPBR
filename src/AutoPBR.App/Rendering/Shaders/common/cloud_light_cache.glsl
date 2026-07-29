// CQ3 light-aligned cloud-cache coordinates, explicit slice interpolation, and cascade blend.

#ifndef GENESIS_CLOUD_LIGHT_CACHE_GLSL
#define GENESIS_CLOUD_LIGHT_CACHE_GLSL

vec3 cqlWorldToUnit(
    vec3 worldPos,
    vec3 basisRight,
    vec3 basisUp,
    vec3 basisForward,
    vec2 planeCenter,
    float worldSpan,
    float lightDepthMin,
    float lightDepthSpan)
{
    vec3 light = vec3(
        dot(worldPos, basisRight),
        dot(worldPos, basisUp),
        dot(worldPos, basisForward));
    return vec3(
        (light.xy - planeCenter) / max(worldSpan, 1e-4) + vec2(0.5),
        (light.z - lightDepthMin) / max(lightDepthSpan, 1e-4));
}

bool cqlUnitInside(vec3 unitPosition)
{
    return all(greaterThanEqual(unitPosition, vec3(0.0))) &&
        all(lessThanEqual(unitPosition, vec3(1.0)));
}

vec2 cqlSampleCascadeExplicitDepth(
    sampler2DArray cacheTexture,
    vec3 unitPosition,
    int logicalDepth)
{
    float depth = float(max(logicalDepth, 1));
    float slicePosition = clamp(unitPosition.z, 0.0, 1.0) * max(depth - 1.0, 0.0);
    float slice0 = floor(slicePosition);
    float slice1 = min(slice0 + 1.0, depth - 1.0);
    float fraction = slicePosition - slice0;
    vec2 value0 = texture(cacheTexture, vec3(clamp(unitPosition.xy, 0.0, 1.0), slice0)).rg;
    vec2 value1 = texture(cacheTexture, vec3(clamp(unitPosition.xy, 0.0, 1.0), slice1)).rg;
    return mix(value0, value1, fraction);
}

vec3 cqlCascadeWeights(
    vec3 nearUnit,
    vec3 farUnit,
    float nearOverlapFraction)
{
    bool nearInside = cqlUnitInside(nearUnit);
    bool farInside = cqlUnitInside(farUnit);
    if (!nearInside)
    {
        return farInside ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
    }

    if (!farInside)
    {
        return vec3(1.0, 0.0, 0.0);
    }

    float overlap = clamp(nearOverlapFraction, 0.001, 0.999);
    float edge = max(
        abs(nearUnit.x - 0.5),
        abs(nearUnit.y - 0.5)) * 2.0;
    float farWeight = smoothstep(1.0 - overlap, 1.0, edge);
    return vec3(1.0 - farWeight, farWeight, 0.0);
}

#endif // GENESIS_CLOUD_LIGHT_CACHE_GLSL
