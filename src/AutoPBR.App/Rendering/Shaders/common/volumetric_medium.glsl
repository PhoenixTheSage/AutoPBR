// Unified participating-medium density (P3 foundation).
// Dual-lobe atmospheric fill (soft valley mist + tall column haze) + analytic cloud fallback;
// shared by froxel inject and screen-space gates.

#ifndef GENESIS_VOLUMETRIC_MEDIUM_GLSL
#define GENESIS_VOLUMETRIC_MEDIUM_GLSL

//!include "volumetric_clouds_density.glsl"
//!include "volumetric_segment.glsl"

// Near-ground density boost. Soft exponential — no hard cutoff above fogSlabTopY.
float vmValleyMistDensity(vec3 worldPos, float groundWorldY, float fogSlabTopY, float strength)
{
    if (strength <= 0.0)
    {
        return 0.0;
    }

    float heightAboveGround = worldPos.y - groundWorldY;
    if (heightAboveGround < 0.0)
    {
        return 0.0;
    }

    float scaleH = max(fogSlabTopY * 0.55, 8.0);
    return exp(-heightAboveGround / scaleH) * strength * 0.30;
}

// Full-column atmospheric haze with a tall scale height for open-air shafts.
float vmAtmosphericColumnDensity(vec3 worldPos, float groundWorldY, float strength)
{
    if (strength <= 0.0)
    {
        return 0.0;
    }

    float heightAboveGround = worldPos.y - groundWorldY;
    if (heightAboveGround < 0.0)
    {
        return 0.0;
    }

    return exp(-heightAboveGround / 64.0) * strength * 0.34;
}

// World-anchored atmospheric fill (not camera-relative - avoids orbit grey dome).
float vmHeightFogDensity(vec3 worldPos, float groundWorldY, float fogSlabTopY, float strength)
{
    return vmValleyMistDensity(worldPos, groundWorldY, fogSlabTopY, strength) +
        vmAtmosphericColumnDensity(worldPos, groundWorldY, strength);
}

float vmMediumDensity(vec3 worldPos, float groundWorldY, float fogSlabTopY, float layerBase, float layerTop,
    float cloudDensityMul, float volumeSize, float heightFogStrength)
{
    float cloud = vcCloudDensityRaw(worldPos, layerBase, layerTop, cloudDensityMul, volumeSize);
    float fog = vmHeightFogDensity(worldPos, groundWorldY, fogSlabTopY, heightFogStrength);
    return cloud + fog;
}

float vmMediumTransmittance(float density, float scale)
{
    return exp(-density * scale);
}

#endif // GENESIS_VOLUMETRIC_MEDIUM_GLSL
