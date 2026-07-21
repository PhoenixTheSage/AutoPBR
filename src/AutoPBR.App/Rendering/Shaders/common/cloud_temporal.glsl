#ifndef GENESIS_CLOUD_TEMPORAL_GLSL
#define GENESIS_CLOUD_TEMPORAL_GLSL

// Perspective-like packing retains sub-unit precision near the camera while still covering
// horizon-scale shell distances. Two RGBA8 channels provide approximately 16 bits.
const float CLOUD_DISTANCE_PACK_SCALE = 256.0;

vec2 ctEncodeDistance(float distanceToCloud)
{
    float normalizedDistance = max(distanceToCloud, 0.0) /
        (max(distanceToCloud, 0.0) + CLOUD_DISTANCE_PACK_SCALE);
    vec2 encoded = fract(normalizedDistance * vec2(1.0, 255.0));
    encoded.x -= encoded.y / 255.0;
    return encoded;
}

float ctDecodeDistance(vec2 encoded)
{
    float normalizedDistance = clamp(dot(encoded, vec2(1.0, 1.0 / 255.0)), 0.0, 0.99998);
    return CLOUD_DISTANCE_PACK_SCALE * normalizedDistance / max(1.0 - normalizedDistance, 1e-5);
}

#endif // GENESIS_CLOUD_TEMPORAL_GLSL
