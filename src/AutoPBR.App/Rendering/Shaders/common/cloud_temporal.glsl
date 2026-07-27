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

// CQ1 metadata ABI. Desktop RG32F stores raw distance/type in RG and reserves a negative
// type for invalid texels. The RGBA8 compatibility path keeps the original RG distance,
// B type and A validity packing byte-for-byte.
vec4 ctEncodeMetadata(float distanceToCloud, float cloudKind, bool valid, int directMetadata)
{
    if (directMetadata > 0)
    {
        return vec4(valid ? max(distanceToCloud, 0.0) : 0.0, valid ? cloudKind : -1.0, 0.0, 0.0);
    }

    return vec4(ctEncodeDistance(distanceToCloud), cloudKind, valid ? 1.0 : 0.0);
}

bool ctMetadataValid(vec4 metadata, int directMetadata)
{
    return directMetadata > 0 ? metadata.g >= 0.0 : metadata.a >= 0.5;
}

float ctMetadataDistance(vec4 metadata, int directMetadata)
{
    return directMetadata > 0 ? max(metadata.r, 0.0) : ctDecodeDistance(metadata.rg);
}

float ctMetadataKind(vec4 metadata, int directMetadata)
{
    return directMetadata > 0 ? metadata.g : metadata.b;
}

#endif // GENESIS_CLOUD_TEMPORAL_GLSL
