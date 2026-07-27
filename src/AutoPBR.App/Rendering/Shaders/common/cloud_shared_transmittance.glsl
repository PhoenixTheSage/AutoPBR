#ifndef GENESIS_CLOUD_SHARED_TRANSMITTANCE_GLSL
#define GENESIS_CLOUD_SHARED_TRANSMITTANCE_GLSL

//!include "cloud_temporal.glsl"

// Contract published by the detailed cloud trace/temporal resolve:
//   cloudColor.a = integrated view opacity
//   compatibility cloudData = packed RG distance, B layer identity, A validity
//   desktop cloudData = direct R distance, G layer identity; negative G means invalid
vec2 cstResolveViewSignal(
    sampler2D cloudColor,
    sampler2D cloudData,
    vec2 uv,
    int hasSignal,
    int directMetadata)
{
    if (hasSignal < 1)
    {
        return vec2(0.0, 1e9);
    }

    vec4 data = texture(cloudData, uv);
    if (!ctMetadataValid(data, directMetadata))
    {
        return vec2(0.0, 1e9);
    }

    return vec2(saturate1(texture(cloudColor, uv).a), ctMetadataDistance(data, directMetadata));
}

// Treat the integrated detailed cloud as a thin extinction sheet at its representative
// distance. This preserves shaft radiance in front of the cloud and attenuates only samples
// behind it, while the later cloud composite handles the scene/background transmittance.
float cstViewTransmittance(float sampleDistance, float cloudDistance, float cloudOpacity, float featherWidth)
{
    float behindCloud = smoothstep(cloudDistance - featherWidth, cloudDistance + featherWidth, sampleDistance);
    return mix(1.0, 1.0 - cloudOpacity, behindCloud);
}

#endif // GENESIS_CLOUD_SHARED_TRANSMITTANCE_GLSL
