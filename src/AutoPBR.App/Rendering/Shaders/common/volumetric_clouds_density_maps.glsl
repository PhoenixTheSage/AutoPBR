// Cloud density model (not included on GLES froxel lite inject path).
// Remap chain: weather coverage -> Perlin-Worley base shape -> high-frequency edge erosion.

#ifndef GENESIS_VOLUMETRIC_CLOUDS_DENSITY_MAPS_GLSL
#define GENESIS_VOLUMETRIC_CLOUDS_DENSITY_MAPS_GLSL

//!include "volumetric_clouds_density.glsl"

float vcRemap01(float x, float a, float b)
{
    return saturate1((x - a) / max(b - a, 1e-5));
}

// High-altitude ice cloud. Real cirrus contains detached silky veils, irregular fibers,
// branching feather-like plumes, and occasional denser tufts. Two differently oriented
// fiber fields prevent the whole layer from collapsing into parallel procedural streaks.
float vcCirrusDensity(vec2 xz, vec2 windOffset, vec2 windDirection, float volumeSize)
{
    vec2 along = length(windDirection) > 1e-4 ? normalize(windDirection) : normalize(vec2(0.82, 0.57));
    vec2 across = vec2(-along.y, along.x);
    vec2 world = xz + windOffset;
    float scale = max(volumeSize, 8.0);
    vec2 flowSpace = vec2(dot(world, along), dot(world, across)) / scale;

    float flowNoise = vcFbm(vec3(flowSpace * vec2(0.10, 0.16), 2.17));
    vec2 warped = flowSpace + vec2((flowNoise - 0.5) * 1.35, (flowNoise - 0.5) * 2.10);
    float moisture = vcFbm(vec3(warped * vec2(0.16, 0.34) + vec2(3.1, -1.7), 4.83));

    // Primary wind-aligned fibers are broad enough to survive half-resolution tracing.
    // A rotated secondary family creates hooks, forks, and feathered edges instead of a
    // single repeated stripe direction.
    float primaryNoise = vcFbm(vec3(warped * vec2(0.42, 3.2) + vec2(1.7, 0.0), 7.13));
    vec2 branchSpace = mat2(0.932, -0.362, 0.362, 0.932) * warped;
    float branchNoise = vcFbm(vec3(branchSpace * vec2(0.58, 4.6) + vec2(5.1, 2.7), 11.29));
    float branchMix = smoothstep(0.36, 0.66, flowNoise);
    float filament = mix(
        smoothstep(0.43, 0.70, primaryNoise),
        smoothstep(0.45, 0.72, branchNoise),
        branchMix * 0.72);

    float detachedPatch = smoothstep(0.34, 0.66, moisture + (flowNoise - 0.5) * 0.16);
    float silkyVeil = smoothstep(0.28, 0.72, moisture) * 0.22;
    float fibrousBody = detachedPatch * mix(0.12, 0.82, filament);
    return saturate1(max(silkyVeil * detachedPatch, fibrousBody));
}

// Weather sample: R = regional coverage, G = cloud type (0 low sheet .. 1 towering).
vec2 vcSampleWeather(sampler2D coverageMap, int hasCoverageMap, vec3 worldPos, float volumeSize, vec2 windOffset)
{
    if (hasCoverageMap < 1)
    {
        return vec2(0.55, 0.5);
    }

    float scale = max(volumeSize, 8.0);
    vec2 covUv = fract((worldPos.xz + windOffset) / (scale * 4.0) + 0.5);
    vec2 weather = texture(coverageMap, covUv).rg;
    return weather;
}

// Layer-aware vertical profile. Low cloud types form shallow stratocumulus decks while
// convective types retain a broad body and taper high in the layer. Smooth trapezoids
// preserve a believable flatter condensation base without introducing hard shelves.
float vcHeightGradient(float h, float cloudType)
{
    float type = saturate1(cloudType);
    float bottomFadeEnd = mix(0.045, 0.075, type);
    float topFadeStart = mix(0.38, 0.70, type);
    float topFadeEnd = mix(0.64, 0.99, type);
    // Condensation starts at a common lifting level, producing the characteristic flat base.
    float bottom = smoothstep(0.002, bottomFadeEnd, h);
    float top = 1.0 - smoothstep(topFadeStart, topFadeEnd, h);
    float roundedBody = mix(0.88 + 0.12 * (1.0 - h), 0.70 + 0.30 * h, type);
    return bottom * top * roundedBody;
}

// Cheap upper bound used before the detailed shape material. It must stay positive anywhere
// the full density can be positive; coarse weather mips intentionally over-cover small gaps.
float vcCloudConservativeDensity(vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float coverageScale, float volumeSize,
    sampler2D coverageMap, int hasCoverageMap, vec3 windOffset, float weatherLod)
{
    float altitude = length(worldPos - planetCenter) - planetRadius;
    if (altitude < layerBase || altitude > layerTop)
    {
        return 0.0;
    }

    float layerH = max(layerTop - layerBase, 0.001);
    float h = (altitude - layerBase) / layerH;
    float scale = max(volumeSize, 8.0);
    float coverage = 0.55;
    if (hasCoverageMap > 0)
    {
        vec2 covUv = fract((worldPos.xz + windOffset.xz) / (scale * 4.0) + 0.5);
        coverage = textureLod(coverageMap, covUv, max(weatherLod, 0.0)).r;
    }

    // Slight dilation keeps the conservative test from skipping thin cloud boundaries.
    coverage = saturate1(coverage * coverageScale + 0.08);
    return coverage * vcHeightGradient(h, 0.5);
}

// Base shape without detail erosion. Cheap enough for the sun light march; the full
// density below erodes this with high-frequency detail for cauliflower silhouettes.
float vcCloudBaseDensity(vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float coverageScale, float volumeSize,
    sampler3D cloudNoise, int hasCloudNoise, sampler2D coverageMap, int hasCoverageMap, vec3 windOffset,
    float shapeLod)
{
    float altitude = length(worldPos - planetCenter) - planetRadius;
    if (altitude < layerBase || altitude > layerTop)
    {
        return 0.0;
    }

    float layerH = max(layerTop - layerBase, 0.001);
    float h = (altitude - layerBase) / layerH;

    vec2 weather = vcSampleWeather(coverageMap, hasCoverageMap, worldPos, volumeSize, windOffset.xz);
    float coverage = saturate1(weather.x * coverageScale);
    if (coverage <= 1e-3)
    {
        return 0.0;
    }

    float heightGrad = vcHeightGradient(h, weather.y);
    if (heightGrad <= 1e-3)
    {
        return 0.0;
    }

    float sizeScale = max(volumeSize, 8.0) * 2.0;
    // The baked Perlin-Worley volume already carries multi-scale shape channels. The previous
    // path evaluated three five-octave procedural FBMs here for every view and light sample.
    // Build the texture coordinate in cloud-local altitude space. Low cloud types stay broad
    // and shallow; convective types narrow, drift, and expose more vertical lobes toward the top.
    float type = saturate1(weather.y);
    float horizontalScale = sizeScale * mix(1.16, 0.78, type);
    vec2 upperDrift = vec2(0.19, -0.13) * (h * h * type * horizontalScale);
    vec2 shapeXz = (worldPos.xz + windOffset.xz + upperDrift) / horizontalScale;
    float shapeY = h * mix(0.34, 0.86, type) + windOffset.y / sizeScale;
    vec3 shapeUvw = fract(vec3(shapeXz.x, shapeY, shapeXz.y));
    float base;
    if (hasCloudNoise > 0)
    {
        vec4 n = textureLod(cloudNoise, shapeUvw, max(shapeLod, 0.0));
        float shapeFbm = n.g * 0.625 + n.b * 0.25 + n.a * 0.125;
        // Carve the Perlin-Worley base with the Worley FBM (remap toward its envelope).
        float cellularCarve = mix(shapeFbm - 1.0, shapeFbm * 0.48 - 0.34, 0.32 + type * 0.18);
        base = vcRemap01(n.r, cellularCarve, 1.0);
        // Preserve a coherent vapor body beneath the cellular domes; using only the carved
        // field makes nearby clouds separate into cartoon-like foam islands.
        base = mix(n.r, base, 0.68 + type * 0.16);
    }
    else
    {
        base = vcFbm((worldPos + windOffset) / max(volumeSize, 8.0) * 2.0);
    }

    base *= heightGrad;
    // Coverage erosion: low coverage eats the shape from the outside in, then scales it,
    // so sparse skies hold a few full clouds instead of many faint ones.
    float baseShaped = vcRemap01(base, 1.0 - coverage, 1.0) * coverage;
    return baseShaped;
}


// Full density from a precomputed base shape (avoids duplicate base-density work in the march loop).
float vcCloudDensityFromBase(float base, vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float densityMul, float volumeSize,
    sampler3D detailNoise, int hasDetailNoise, vec3 windOffset)
{
    if (base <= 1e-4)
    {
        return 0.0;
    }

    float erode = 0.0;
    if (hasDetailNoise > 0)
    {
        float layerH = max(layerTop - layerBase, 0.001);
        float altitude = length(worldPos - planetCenter) - planetRadius;
        float h = (altitude - layerBase) / layerH;
        float detailScale = max(volumeSize, 8.0) * 0.5;
        vec3 detailUvw = fract((worldPos + windOffset * 0.5) / detailScale);
        vec3 dn = texture(detailNoise, detailUvw).rgb;
        float detailFbm = dn.r * 0.625 + dn.g * 0.25 + dn.b * 0.125;
        detailFbm = mix(detailFbm, 1.0 - detailFbm, saturate1(h * 5.0));
        // Erode silhouettes without hollowing the cloud body. Strong full-volume erosion
        // made nearby clouds look like disconnected foam/splotches.
        float edgeWeight = 1.0 - smoothstep(0.18, 0.62, base);
        erode = detailFbm * mix(0.10, 0.24, edgeWeight);
    }

    float density = vcRemap01(base, erode, 1.0);
    return density * densityMul;
}

// Full density: base shape eroded by high-frequency detail at cloud edges.
float vcCloudDensityEx(vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float densityMul, float coverageScale, float volumeSize,
    sampler3D cloudNoise, int hasCloudNoise, sampler3D detailNoise, int hasDetailNoise,
    sampler2D coverageMap, int hasCoverageMap, vec3 windOffset)
{
    float base = vcCloudBaseDensity(worldPos, planetCenter, planetRadius, layerBase, layerTop,
        coverageScale, volumeSize, cloudNoise, hasCloudNoise, coverageMap, hasCoverageMap, windOffset, 0.0);
    return vcCloudDensityFromBase(base, worldPos, planetCenter, planetRadius, layerBase, layerTop,
        densityMul, volumeSize, detailNoise, hasDetailNoise, windOffset);
}

#endif // GENESIS_VOLUMETRIC_CLOUDS_DENSITY_MAPS_GLSL
