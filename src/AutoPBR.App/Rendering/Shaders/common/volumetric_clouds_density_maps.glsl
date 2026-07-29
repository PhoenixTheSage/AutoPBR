// Cloud density model (not included on GLES froxel lite inject path).
// Remap chain: weather coverage -> Perlin-Worley base shape -> high-frequency edge erosion.

#ifndef GENESIS_VOLUMETRIC_CLOUDS_DENSITY_MAPS_GLSL
#define GENESIS_VOLUMETRIC_CLOUDS_DENSITY_MAPS_GLSL

//!include "volumetric_clouds_density.glsl"

float vcRemap01(float x, float a, float b)
{
    return saturate1((x - a) / max(b - a, 1e-5));
}

// Explicit ray-footprint mip policy for samples reached through dynamic march loops.
// A non-positive footprint intentionally selects LOD zero for exact debug inspection.
float vcCloudRayFootprintLod(
    float sampleFootprint,
    float worldRepeatSize,
    float textureDimension,
    float lodBias)
{
    if (sampleFootprint <= 0.0)
    {
        return 0.0;
    }

    float dimension = max(textureDimension, 1.0);
    float worldTexelSize = max(worldRepeatSize, 1e-4) / dimension;
    float lod = log2(max(sampleFootprint / worldTexelSize, 1.0)) + lodBias;
    float maxMip = floor(log2(dimension));
    return clamp(lod, 0.0, maxMip);
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

// Cinematic v2 adds a single explicitly filtered detail lookup to bend and feather the
// procedural cirrus field. High and every v1/compatibility path return the established
// procedural result without touching the detail texture.
float vcCirrusDensityWithDetail(
    vec2 xz,
    vec2 windOffset,
    vec2 windDirection,
    float volumeSize,
    sampler3D detailNoise,
    int hasDetailNoise,
    float sampleFootprint,
    float detailLodBias,
    int quality,
    int densityAssetVersion)
{
    float baseDensity = vcCirrusDensity(
        xz,
        windOffset,
        windDirection,
        volumeSize);
    if (quality < 3 || densityAssetVersion < 2 || hasDetailNoise < 1)
    {
        return baseDensity;
    }

    float detailScale = max(volumeSize, 8.0) * 0.5;
    vec2 detailWorld = xz + windOffset;
    vec3 detailUvw = fract(
        vec3(detailWorld.x, volumeSize * 0.37, detailWorld.y) / detailScale +
        vec3(0.193, 0.617, 0.431));
    ivec3 detailSize = textureSize(detailNoise, 0);
    float detailDimension = float(max(
        detailSize.x,
        max(detailSize.y, detailSize.z)));
    float detailLod = vcCloudRayFootprintLod(
        sampleFootprint,
        detailScale,
        detailDimension,
        detailLodBias);
    vec4 detail = textureLod(detailNoise, detailUvw, detailLod);

    // B is the anisotropic wispy field and A is the zero-mean curl/distortion field.
    // Rotate their signed pair before applying the warp so the added feathering does not
    // line up with the volume axes or replace the wind-aligned large-scale silhouette.
    vec2 signedDetail = detail.ba * 2.0 - 1.0;
    const mat2 detailRotation = mat2(
        0.768221, -0.640184,
        0.640184, 0.768221);
    vec2 distortion = detailRotation * signedDetail;
    float distortedDensity = vcCirrusDensity(
        xz + distortion * detailScale * 0.045,
        windOffset,
        windDirection,
        volumeSize);
    float boundary = smoothstep(0.025, 0.42, baseDensity) *
        (1.0 - smoothstep(0.62, 0.94, baseDensity));
    float warpedDensity = mix(
        baseDensity,
        distortedDensity,
        boundary * 0.42);
    float wispyFeather = mix(0.86, 1.12, detail.b);
    return saturate1(warpedDensity * mix(1.0, wispyFeather, boundary * 0.35));
}

// Weather sample: R = coverage, G = cloud type, B = density/precipitation
// potential, A = convection. Legacy v1 stores placeholders in B/A, so the
// version branch supplies neutral values rather than reinterpreting them.
vec4 vcSampleWeather(
    sampler2D coverageMap,
    int hasCoverageMap,
    vec3 worldPos,
    float volumeSize,
    vec2 windOffset,
    float sampleFootprint,
    int densityAssetVersion)
{
    if (hasCoverageMap < 1)
    {
        return vec4(0.55, 0.5, 0.5, 0.0);
    }

    float scale = max(volumeSize, 8.0);
    float primaryPeriod = scale * (densityAssetVersion >= 2 ? 16.0 : 4.0);
    vec2 weatherWorld = worldPos.xz + windOffset;
    vec2 covUv = fract(weatherWorld / primaryPeriod + 0.5);
    ivec2 weatherSize = textureSize(coverageMap, 0);
    float weatherDimension = float(max(weatherSize.x, weatherSize.y));
    float weatherLod = vcCloudRayFootprintLod(
        sampleFootprint,
        primaryPeriod,
        weatherDimension,
        0.0);
    vec4 weather = textureLod(coverageMap, covUv, weatherLod);
    if (densityAssetVersion < 2)
    {
        weather.ba = vec2(0.5, 0.0);
        return weather;
    }

    // A second world-anchored address breaks the obvious square repetition without
    // requiring another weather texture. Both fields receive the same wind translation,
    // so their systems advect together rather than sliding through one another.
    // Integer coefficients preserve the toroidal seam while acting as a 26.6-degree
    // rotation with sqrt(5) frequency scaling.
    const mat2 weatherRotationScale = mat2(2.0, -1.0, 1.0, 2.0);
    float secondaryPeriod = primaryPeriod * 0.447214;
    vec2 secondaryUv = fract(
        weatherRotationScale * covUv +
        vec2(0.173, 0.619));
    float secondaryLod = vcCloudRayFootprintLod(
        sampleFootprint,
        secondaryPeriod,
        weatherDimension,
        0.0);
    vec4 secondaryWeather = textureLod(
        coverageMap,
        secondaryUv,
        secondaryLod);
    float secondaryBlend = mix(0.08, 0.22, saturate1(weather.a));
    weather = mix(weather, secondaryWeather, secondaryBlend);
    return weather;
}

// Layer-aware vertical profile. Low cloud types form shallow stratocumulus decks while
// convective types retain a broad body and taper high in the layer. Smooth trapezoids
// preserve a believable flatter condensation base without introducing hard shelves.
float vcHeightGradient(
    float h,
    float cloudType,
    float convection,
    int densityAssetVersion)
{
    float type = saturate1(cloudType);
    float bottomFadeEnd = mix(0.045, 0.075, type);
    float topFadeStart = mix(0.38, 0.70, type);
    float topFadeEnd = mix(0.64, 0.99, type);
    float convectionLift = densityAssetVersion >= 2
        ? saturate1(convection) * type
        : 0.0;
    topFadeStart = min(topFadeStart + convectionLift * 0.10, 0.82);
    // Condensation starts at a common lifting level, producing the characteristic flat base.
    float bottom = smoothstep(0.002, bottomFadeEnd, h);
    float top = 1.0 - smoothstep(topFadeStart, topFadeEnd, h);
    float roundedBody = mix(0.88 + 0.12 * (1.0 - h), 0.70 + 0.30 * h, type);
    if (densityAssetVersion >= 2)
    {
        float upperDevelopment = mix(
            1.0,
            mix(0.92, 1.12, smoothstep(0.32, 0.88, h)),
            convectionLift);
        roundedBody *= upperDevelopment;
    }

    return bottom * top * roundedBody;
}

// Cheap upper bound used before the detailed shape material. It must stay positive anywhere
// the full density can be positive; coarse weather mips intentionally over-cover small gaps.
float vcCloudConservativeDensity(vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float coverageScale, float volumeSize,
    sampler2D coverageMap, int hasCoverageMap, vec3 windOffset, float sampleFootprint,
    int densityAssetVersion)
{
    float altitude = length(worldPos - planetCenter) - planetRadius;
    if (altitude < layerBase || altitude > layerTop)
    {
        return 0.0;
    }

    float layerH = max(layerTop - layerBase, 0.001);
    float h = (altitude - layerBase) / layerH;
    float coverage = vcSampleWeather(
        coverageMap,
        hasCoverageMap,
        worldPos,
        volumeSize,
        windOffset.xz,
        sampleFootprint * 2.0,
        densityAssetVersion).r;

    // Slight dilation keeps the conservative test from skipping thin cloud boundaries.
    coverage = saturate1(coverage * coverageScale + 0.08);
    float heightUpper = vcHeightGradient(h, 0.5, 0.0, densityAssetVersion);
    if (densityAssetVersion >= 2)
    {
        heightUpper = max(
            heightUpper,
            vcHeightGradient(h, 1.0, 1.0, densityAssetVersion));
    }

    return coverage * heightUpper;
}

// Base shape without detail erosion from one already-filtered weather sample.
// Keeping the sample outside this function lets the view and light paths reuse B/A
// without paying for a second weather lookup.
float vcCloudBaseDensityFromWeather(vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float coverageScale, float volumeSize,
    sampler3D cloudNoise, int hasCloudNoise, vec3 windOffset,
    float sampleFootprint, vec4 weather, int densityAssetVersion)
{
    float altitude = length(worldPos - planetCenter) - planetRadius;
    if (altitude < layerBase || altitude > layerTop)
    {
        return 0.0;
    }

    float layerH = max(layerTop - layerBase, 0.001);
    float h = (altitude - layerBase) / layerH;

    float coverage = saturate1(weather.x * coverageScale);
    if (coverage <= 1e-3)
    {
        return 0.0;
    }

    float heightGrad = vcHeightGradient(
        h,
        weather.y,
        weather.w,
        densityAssetVersion);
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
    float convection = densityAssetVersion >= 2 ? saturate1(weather.w) : 0.0;
    float convectionLift = type * convection;
    float horizontalScale = sizeScale * mix(1.16, 0.78, type);
    if (densityAssetVersion >= 2)
    {
        horizontalScale *= mix(1.04, 0.86, convectionLift);
    }

    vec2 upperDrift = vec2(0.19, -0.13) *
        (h * h * type * horizontalScale) *
        mix(1.0, 1.42, convection);
    vec2 shapeXz = (worldPos.xz + windOffset.xz + upperDrift) / horizontalScale;
    float shapeY = h * mix(0.34, 0.86, type) + windOffset.y / sizeScale;
    if (densityAssetVersion >= 2)
    {
        shapeY += h * h * convectionLift * 0.08;
    }

    vec3 shapeUvw = fract(vec3(shapeXz.x, shapeY, shapeXz.y));
    float base;
    if (hasCloudNoise > 0)
    {
        ivec3 shapeSize = textureSize(cloudNoise, 0);
        float shapeDimension = float(max(shapeSize.x, max(shapeSize.y, shapeSize.z)));
        float shapeLod = vcCloudRayFootprintLod(
            sampleFootprint,
            horizontalScale,
            shapeDimension,
            0.0);
        vec4 n = textureLod(cloudNoise, shapeUvw, shapeLod);
        float shapeFbm;
        if (densityAssetVersion >= 2)
        {
            float broadWeight = mix(0.68, 0.50, convectionLift);
            float mediumWeight = mix(0.22, 0.31, convectionLift);
            float fineWeight = 1.0 - broadWeight - mediumWeight;
            shapeFbm =
                n.g * broadWeight +
                n.b * mediumWeight +
                n.a * fineWeight;
        }
        else
        {
            shapeFbm = n.g * 0.625 + n.b * 0.25 + n.a * 0.125;
        }

        // Carve the Perlin-Worley base with the Worley FBM (remap toward its envelope).
        float carveMix = 0.32 + type * 0.18;
        if (densityAssetVersion >= 2)
        {
            carveMix += convectionLift * 0.06;
        }

        float cellularCarve = mix(
            shapeFbm - 1.0,
            shapeFbm * 0.48 - 0.34,
            carveMix);
        base = vcRemap01(n.r, cellularCarve, 1.0);
        // Preserve a coherent vapor body beneath the cellular domes; using only the carved
        // field makes nearby clouds separate into cartoon-like foam islands.
        float cellularAmount = 0.68 + type * 0.16;
        if (densityAssetVersion >= 2)
        {
            cellularAmount = min(cellularAmount + convectionLift * 0.04, 0.90);
        }

        base = mix(n.r, base, cellularAmount);
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

// Convenience wrapper for callers which do not otherwise need the weather material.
float vcCloudBaseDensity(vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float coverageScale, float volumeSize,
    sampler3D cloudNoise, int hasCloudNoise, sampler2D coverageMap, int hasCoverageMap,
    vec3 windOffset, float sampleFootprint, int densityAssetVersion)
{
    vec4 weather = vcSampleWeather(
        coverageMap,
        hasCoverageMap,
        worldPos,
        volumeSize,
        windOffset.xz,
        sampleFootprint,
        densityAssetVersion);
    return vcCloudBaseDensityFromWeather(
        worldPos,
        planetCenter,
        planetRadius,
        layerBase,
        layerTop,
        coverageScale,
        volumeSize,
        cloudNoise,
        hasCloudNoise,
        windOffset,
        sampleFootprint,
        weather,
        densityAssetVersion);
}

float vcCloudDensityPotentialScale(
    float h,
    float densityPotential,
    int densityAssetVersion)
{
    if (densityAssetVersion < 2)
    {
        return 1.0;
    }

    float lowerBody = 1.0 - smoothstep(0.38, 0.82, h);
    float potentialScale = mix(0.80, 1.28, saturate1(densityPotential));
    return mix(1.0, potentialScale, mix(0.58, 0.92, lowerBody));
}

// Full density from a precomputed base shape (avoids duplicate base-density work in the march loop).
float vcCloudDensityFromBase(float base, vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float densityMul, float volumeSize,
    sampler3D detailNoise, int hasDetailNoise, vec3 windOffset,
    float sampleFootprint, float detailLodBias, int quality,
    float densityPotential, float convection, int densityAssetVersion)
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
        vec3 detailWorld = worldPos + windOffset * 0.5;
        vec3 detailUvw = fract(detailWorld / detailScale);
        ivec3 detailSize = textureSize(detailNoise, 0);
        float detailDimension = float(max(detailSize.x, max(detailSize.y, detailSize.z)));
        float detailLod = vcCloudRayFootprintLod(
            sampleFootprint,
            detailScale,
            detailDimension,
            detailLodBias);
        vec4 dn = textureLod(detailNoise, detailUvw, detailLod);
        float detailFbm;
        if (densityAssetVersion >= 2)
        {
            float upperBoundary = smoothstep(0.24, 0.72, h);
            float billowMix = mix(0.30, 0.48, saturate1(convection));
            float billow = mix(dn.r, dn.g, billowMix);
            float wispy = dn.b;
            detailFbm = mix(wispy, billow, upperBoundary);

            // High/Cinematic pay for the decorrelating lookup only at a cloud boundary.
            // Rotating the complete advected world coordinate preserves a shared wind
            // velocity, while the fixed offset and curl channel break short-period tiling.
            float edgeWeight = 1.0 - smoothstep(0.18, 0.62, base);
            if (quality >= 2 && edgeWeight > 1e-3)
            {
                const mat2 detailRotation = mat2(
                    0.838671, -0.544639,
                    0.544639, 0.838671);
                vec2 rotatedXz = detailRotation * detailWorld.xz;
                float curl = dn.a * 2.0 - 1.0;
                vec3 rotatedUvw = fract(
                    vec3(rotatedXz.x, detailWorld.y, rotatedXz.y) / detailScale +
                    vec3(0.371, 0.683, 0.193) +
                    vec3(curl * 0.055, -curl * 0.035, curl * 0.047));
                vec4 rotatedDn = textureLod(
                    detailNoise,
                    rotatedUvw,
                    detailLod);
                float rotatedBillow = mix(
                    rotatedDn.r,
                    rotatedDn.g,
                    billowMix);
                float rotatedDetail = mix(
                    rotatedDn.b,
                    rotatedBillow,
                    upperBoundary);
                float rotatedBlend = quality >= 3 ? 0.50 : 0.35;
                detailFbm = mix(
                    detailFbm,
                    rotatedDetail,
                    rotatedBlend);
            }
        }
        else
        {
            detailFbm = dn.r * 0.625 + dn.g * 0.25 + dn.b * 0.125;
            detailFbm = mix(detailFbm, 1.0 - detailFbm, saturate1(h * 5.0));
        }

        // Erode silhouettes without hollowing the cloud body. Strong full-volume erosion
        // made nearby clouds look like disconnected foam/splotches.
        float edgeWeight = 1.0 - smoothstep(0.18, 0.62, base);
        erode = detailFbm * mix(0.10, 0.24, edgeWeight);
    }

    float density = vcRemap01(base, erode, 1.0);
    float layerH = max(layerTop - layerBase, 0.001);
    float altitude = length(worldPos - planetCenter) - planetRadius;
    float h = saturate1((altitude - layerBase) / layerH);
    density *= vcCloudDensityPotentialScale(
        h,
        densityPotential,
        densityAssetVersion);
    return density * densityMul;
}

// Full density: base shape eroded by high-frequency detail at cloud edges.
float vcCloudDensityEx(vec3 worldPos, vec3 planetCenter, float planetRadius,
    float layerBase, float layerTop, float densityMul, float coverageScale, float volumeSize,
    sampler3D cloudNoise, int hasCloudNoise, sampler3D detailNoise, int hasDetailNoise,
    sampler2D coverageMap, int hasCoverageMap, vec3 windOffset,
    float sampleFootprint, float detailLodBias, int quality, int densityAssetVersion)
{
    vec4 weather = vcSampleWeather(
        coverageMap,
        hasCoverageMap,
        worldPos,
        volumeSize,
        windOffset.xz,
        sampleFootprint,
        densityAssetVersion);
    float base = vcCloudBaseDensityFromWeather(
        worldPos,
        planetCenter,
        planetRadius,
        layerBase,
        layerTop,
        coverageScale,
        volumeSize,
        cloudNoise,
        hasCloudNoise,
        windOffset,
        sampleFootprint,
        weather,
        densityAssetVersion);
    return vcCloudDensityFromBase(base, worldPos, planetCenter, planetRadius, layerBase, layerTop,
        densityMul, volumeSize, detailNoise, hasDetailNoise, windOffset,
        sampleFootprint, detailLodBias, quality,
        weather.z, weather.w, densityAssetVersion);
}

#endif // GENESIS_VOLUMETRIC_CLOUDS_DENSITY_MAPS_GLSL
