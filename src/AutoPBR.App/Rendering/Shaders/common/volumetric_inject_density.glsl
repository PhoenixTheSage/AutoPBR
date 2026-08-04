// GLES-safe inject density: dual-lobe atmospheric fill + analytic slab cloud (no FBM loops).
// Continuous height falloff (no hard fog-top cliff). Column haze must remain meaningful up through
// mountain/camera altitudes so Mie shafts can form toward the sun, not only when looking downward.

#ifndef GENESIS_VOLUMETRIC_INJECT_DENSITY_GLSL
#define GENESIS_VOLUMETRIC_INJECT_DENSITY_GLSL

// Near-ground density boost. Soft exponential — no hard cutoff above fogSlabTopY.
float viValleyMistDensity(vec3 worldPos, float groundWorldY, float fogSlabTopY, float strength)
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

    // Scale height ~0.55 of the valley envelope so mist thins smoothly into the column.
    float scaleH = max(fogSlabTopY * 0.55, 8.0);
    return exp(-heightAboveGround / scaleH) * strength * 0.30;
}

// Full-column atmospheric haze. Large scale height so open-air shafts still have a medium
// when the camera is above the valley floor (Minecraft-style crepuscular fill).
float viAtmosphericColumnDensity(vec3 worldPos, float groundWorldY, float strength)
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

    // ~64 world-unit scale height: still ~25% density at h=88.
    return exp(-heightAboveGround / 64.0) * strength * 0.34;
}

float viHeightFogDensity(vec3 worldPos, float groundWorldY, float fogSlabTopY, float strength)
{
    return viValleyMistDensity(worldPos, groundWorldY, fogSlabTopY, strength) +
        viAtmosphericColumnDensity(worldPos, groundWorldY, strength);
}

float viSlabCloudDensity(vec3 worldPos, float layerBase, float layerTop, float densityMul)
{
    if (worldPos.y < layerBase || worldPos.y > layerTop)
    {
        return 0.0;
    }

    float layerH = max(layerTop - layerBase, 0.001);
    float h = (worldPos.y - layerBase) / layerH;
    float heightFade = smoothstep(0.0, 0.12, h) * smoothstep(1.0, 0.58, h);
    return heightFade * densityMul * 0.35;
}

float viInjectMediumDensity(vec3 worldPos, float groundWorldY, float fogSlabTopY, float layerBase, float layerTop,
    float cloudDensityMul, float heightFogStrength)
{
    float fog = viHeightFogDensity(worldPos, groundWorldY, fogSlabTopY, heightFogStrength);
    float cloud = viSlabCloudDensity(worldPos, layerBase, layerTop, cloudDensityMul);
    return fog + cloud;
}

#endif // GENESIS_VOLUMETRIC_INJECT_DENSITY_GLSL
