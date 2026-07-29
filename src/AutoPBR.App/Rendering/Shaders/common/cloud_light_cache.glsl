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
    vec2 uv = clamp(unitPosition.xy, 0.0, 1.0);
    vec2 value0 = texture(cacheTexture, vec3(uv, slice0)).rg;
    vec2 value1 = texture(cacheTexture, vec3(uv, slice1)).rg;
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

// Returns cumulative sun optical depth, sky visibility, and cache-use weight.
// Missing cascades are treated independently: a valid far cascade can cover a missing/outside
// near cascade, while a valid near cascade remains useful if the far cascade failed. Any point
// outside all generated coverage returns z=0 so the caller can run the compatibility light march.
vec3 cqlResolveLighting(
    sampler2DArray nearCache,
    sampler2DArray farCache,
    vec3 worldPosition,
    vec3 basisRight,
    vec3 basisUp,
    vec3 basisForward,
    vec2 nearPlaneCenter,
    vec2 farPlaneCenter,
    float nearWorldSpan,
    float farWorldSpan,
    float nearLightDepthMin,
    float farLightDepthMin,
    float nearLightDepthSpan,
    float farLightDepthSpan,
    int nearDepth,
    int farDepth,
    float nearOverlapFraction,
    int hasNear,
    int hasFar,
    out vec3 resolvedWeights)
{
    vec3 nearUnit = cqlWorldToUnit(
        worldPosition,
        basisRight,
        basisUp,
        basisForward,
        nearPlaneCenter,
        nearWorldSpan,
        nearLightDepthMin,
        nearLightDepthSpan);
    bool nearAvailable = hasNear > 0 && cqlUnitInside(nearUnit);
    if (nearAvailable)
    {
        float overlap = clamp(nearOverlapFraction, 0.001, 0.999);
        float nearEdge = max(
            abs(nearUnit.x - 0.5),
            abs(nearUnit.y - 0.5)) * 2.0;
        // Most occupied samples are comfortably inside the near cascade. Return before
        // constructing far coordinates so High does three light-space dot products and
        // two array reads instead of six dot products plus a dormant far lookup.
        if (hasFar <= 0 || nearEdge < 1.0 - overlap)
        {
            vec2 nearValue = cqlSampleCascadeExplicitDepth(
                nearCache,
                nearUnit,
                nearDepth);
            resolvedWeights = vec3(1.0, 0.0, 0.0);
            return vec3(
                max(nearValue.x, 0.0),
                clamp(nearValue.y, 0.0, 1.0),
                1.0);
        }
    }

    vec3 farUnit = cqlWorldToUnit(
        worldPosition,
        basisRight,
        basisUp,
        basisForward,
        farPlaneCenter,
        farWorldSpan,
        farLightDepthMin,
        farLightDepthSpan);
    bool farAvailable = hasFar > 0 && cqlUnitInside(farUnit);
    if (!nearAvailable && !farAvailable)
    {
        resolvedWeights = vec3(0.0, 0.0, 1.0);
        return vec3(0.0, 1.0, 0.0);
    }

    vec3 weights;
    if (nearAvailable && farAvailable)
    {
        weights = cqlCascadeWeights(
            nearUnit,
            farUnit,
            nearOverlapFraction);
    }
    else
    {
        weights = nearAvailable
            ? vec3(1.0, 0.0, 0.0)
            : vec3(0.0, 1.0, 0.0);
    }

    vec2 nearValue = weights.x > 0.0
        ? cqlSampleCascadeExplicitDepth(
            nearCache,
            nearUnit,
            nearDepth)
        : vec2(0.0, 1.0);
    vec2 farValue = weights.y > 0.0
        ? cqlSampleCascadeExplicitDepth(
            farCache,
            farUnit,
            farDepth)
        : vec2(0.0, 1.0);
    vec2 cacheValue = nearValue * weights.x + farValue * weights.y;
    resolvedWeights = weights;
    return vec3(
        max(cacheValue.x, 0.0),
        clamp(cacheValue.y, 0.0, 1.0),
        clamp(weights.x + weights.y, 0.0, 1.0));
}

#endif // GENESIS_CLOUD_LIGHT_CACHE_GLSL
