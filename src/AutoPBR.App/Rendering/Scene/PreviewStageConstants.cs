namespace AutoPBR.App.Rendering.Scene;

/// <summary>Shared layout for preview grid, ground plane, and orbit framing.</summary>
public static class PreviewStageConstants
{
    public const float GridHalfExtent = 14f;
    public const float GridStep = 0.5f;

    /// <summary>Half-width of ground-grid ribbons in world units (thick quads, not GL lines).</summary>
    public const float GridLineHalfWidth = 0.03f;

    /// <summary>Lift grid slightly above the turf to avoid z-fighting with ground mesh.</summary>
    public const float GridYBias = 0.02f;

    /// <summary>Default ground-grid color (ARGB).</summary>
    public const uint DefaultGridColorArgb = 0xE8F2F2F8;

    /// <summary>XZ plane where background grid lines sit.</summary>
    public const float GridWorldY = -0.56f;

    /// <summary>
    /// Top of the flat pad grass blocks (and fog/volume ground anchor). Matches <see cref="GridWorldY"/>
    /// so entity feet and the line grid sit on voxel turf.
    /// </summary>
    public const float GroundPlaneWorldY = GridWorldY;

    /// <summary>One full 16×16 grass tile per world unit (matches unit cube / block scale).</summary>
    public const float MetersPerGrassTile = 1f;

    /// <summary>Half-extent of baked voxel terrain on XZ (columns from -extent .. extent-1).</summary>
    public const int TerrainHalfExtent = 48;

    /// <summary>Flat pad matching the background grid footprint (forced height 0).</summary>
    public const int TerrainFlatPadHalfExtent = 14;

    /// <summary>Blocks outside the pad over which height blends from flat → noise.</summary>
    public const int TerrainTransitionBlocks = 4;

    /// <summary>Chunk size in columns for streaming / LOD batches.</summary>
    public const int TerrainChunkSize = 16;

    /// <summary>Max |relative| hill height in blocks outside the flat pad (Plains default).</summary>
    public const int TerrainMaxReliefBlocks = 6;

    /// <summary>Desert dune max |relative| height.</summary>
    public const int TerrainDesertMaxReliefBlocks = 10;

    /// <summary>Mountain max |relative| height (allows multi-block cliff steps).</summary>
    public const int TerrainMountainMaxReliefBlocks = 20;

    /// <summary>Beach max |relative| height (kept low/coastal).</summary>
    public const int TerrainBeachMaxReliefBlocks = 2;

    /// <summary>
    /// Neighbor column |Δh| at or above this uses stone/gravel cliff faces in mountain biomes.
    /// </summary>
    public const int TerrainCliffDeltaBlocks = 2;

    /// <summary>
    /// Half-width in climate space [0,1] for soft biome affinity ramps.
    /// Borders blend height geometry instead of hard-classifying each column.
    /// </summary>
    public const float TerrainBiomeBlendHalfWidth = 0.085f;

    /// <summary>
    /// Soft minimum column thickness below the surface. Actual solids extend further down to
    /// <see cref="TerrainSolidFloorRelativeY"/> so multi-block cliffs never open sky holes.
    /// </summary>
    public const int TerrainFillDepth = 3;

    /// <summary>Debounce for World seed / gen sliders (chunk rebuild is expensive).</summary>
    public const int TerrainWorldGenDebounceMs = 250;

    public const float TerrainDefaultBiomeSize = 1f;
    public const float TerrainMinBiomeSize = 0.4f;
    public const float TerrainMaxBiomeSize = 3f;

    public const float TerrainDefaultAmplification = 1f;
    public const float TerrainMinAmplification = 0.25f;
    public const float TerrainMaxAmplification = 2.5f;

    public const float TerrainDefaultErosionStrength = 1f;
    public const float TerrainMinErosionStrength = 0f;
    public const float TerrainMaxErosionStrength = 1.5f;

    public const float TerrainDefaultContinentalness = 1f;
    public const float TerrainMinContinentalness = 0.5f;
    public const float TerrainMaxContinentalness = 1.75f;

    /// <summary>
    /// Inclusive relative-Y floor every column is solid from (up to its surface).
    /// Covers max mountain relief × max amplification so tall shelves cannot float over voids.
    /// </summary>
    public const int TerrainSolidFloorRelativeY =
        -(int)(TerrainMountainMaxReliefBlocks * TerrainMaxAmplification);

    /// <summary>Deterministic heightfield seed.</summary>
    public const int TerrainHeightSeed = 0x41504252; // 'APBR'

    /// <summary>Salt XOR'd into climate noise sampling (biomes).</summary>
    public const int TerrainClimateSeedSalt = unchecked((int)0xC11A7E00);

    /// <summary>Salt XOR'd into vegetation placement hashes (trees / cactus).</summary>
    public const int TerrainVegetationSeedSalt = unchecked((int)0x7EEE0001);

    /// <summary>Full-detail chunks whose center is within this camera XZ radius keep POM at full strength.</summary>
    public const float TerrainNearPomRadius = 22f;

    /// <summary>
    /// Extra XZ distance beyond <see cref="TerrainNearPomRadius"/> where ground POM / parallax shadow fade to off.
    /// Prevents a hard lighting seam when approaching chunks.
    /// </summary>
    public const float TerrainNearPomFadeWidth = 8f;

    /// <summary>Genesis POM displacement slider / <c>uHeightStrength</c> minimum.</summary>
    public const double ParallaxHeightStrengthMin = 0.0;

    /// <summary>Genesis POM displacement slider / <c>uHeightStrength</c> maximum (UV scale ≈ value × 0.92).</summary>
    public const double ParallaxHeightStrengthMax = 4.0;

    /// <summary>Genesis POM max UV-shift soft-cap minimum.</summary>
    public const double ParallaxMaxUvShiftMin = 0.05;

    /// <summary>Genesis POM max UV-shift soft-cap maximum (multi-tile travel on continuous ground UVs).</summary>
    public const double ParallaxMaxUvShiftMax = 4.0;

    /// <summary>
    /// Legacy bake LOD stamp. Streaming uses <see cref="TerrainLodRingChunks"/> instead.
    /// </summary>
    public const float TerrainLodMaxDistance = 0f;

    /// <summary>Hard Full-detail Chebyshev radius in chunks (World setting default).</summary>
    public const int TerrainDefaultChunkViewDistance = 8;

    public const int TerrainMinChunkViewDistance = 2;
    public const int TerrainMaxChunkViewDistance = 24;

    /// <summary>Default extra Chebyshev chunks beyond hard view distance kept as merged LOD meshes.</summary>
    public const int TerrainDefaultLodRingChunks = 16;

    public const int TerrainMinLodRingChunks = 2;
    public const int TerrainMaxLodRingChunks = 32;

    /// <summary>Legacy alias for <see cref="TerrainDefaultLodRingChunks"/>.</summary>
    public const int TerrainLodRingChunks = TerrainDefaultLodRingChunks;

    /// <summary>Unload hysteresis past LOD radius (chunks).</summary>
    public const int TerrainUnloadHysteresisChunks = 1;

    /// <summary>Steady-state chunk GPU uploads per frame (keeps fly-cam smooth).</summary>
    public const int TerrainMaxChunkUploadsPerFrame = 4;

    /// <summary>Upload cap while streaming is still catching up to the desired ring.</summary>
    public const int TerrainMaxChunkUploadsPerFrameCatchUp = 12;

    /// <summary>Steady-state terrain vertex/index bytes submitted to GL per frame.</summary>
    public const long TerrainMaxUploadBytesPerFrame = 8L * 1024L * 1024L;

    /// <summary>Catch-up terrain vertex/index bytes submitted to GL per frame.</summary>
    public const long TerrainMaxUploadBytesPerFrameCatchUp = 24L * 1024L * 1024L;

    /// <summary>Max LongRunning bake workers (pool size is also capped by ProcessorCount-1).</summary>
    public const int TerrainMaxBakeWorkers = 8;

    /// <summary>If frustum cull yields zero chunks, draw this many nearest by XZ (never blank the pad).</summary>
    public const int TerrainFrustumDrawFallbackCount = 64;

    /// <summary>Max chunk GPU disposals per frame.</summary>
    public const int TerrainMaxChunkDisposalsPerFrame = 8;

    /// <summary>Half extent of the 1×1 item/sprite preview card in world units.</summary>
    public const float SpritePlaneHalfSize = 0.5f;

    /// <summary>Minimum sprite cuboid depth (0 = single-sided plane).</summary>
    public const double SpriteThicknessMin = 0.0;

    /// <summary>Maximum sprite cuboid depth (~25% of the 1×1 face width; at max, voxel depth matches texel size).</summary>
    public const double SpriteThicknessMax = 0.25;

    /// <summary>UI step for the sprite thickness slider and numeric field.</summary>
    public const double SpriteThicknessStep = 0.002;

    /// <summary>Debounce before rebuilding per-texel sprite voxel meshes after slider drags.</summary>
    public const int SpriteThicknessMeshDebounceMs = 200;

    /// <summary>Debounce before re-tinting grass colormap preview materials after slider drags.</summary>
    public const int GrassColormapTintDebounceMs = 200;

    /// <summary>Debounce before pushing TXAA slider/mode changes to the GPU preview.</summary>
    public const int PreviewTaaGpuDebounceMs = 200;

    public const double DefaultGrassColormapTemperature = 0.72;
    public const double DefaultGrassColormapDownfall = 0.45;

    /// <summary>Base Y for the volumetric cloud layer before user height offset.</summary>
    public const float CloudLayerBaseY = 18f;

    public static float CloudLayerBaseWorldY(float layerHeightOffset) => CloudLayerBaseY + layerHeightOffset;

    /// <summary>Artistic curved-cloud planet radius; see <see cref="PreviewCloudShellGeometry"/>.</summary>
    public const float CloudPlanetRadius = PreviewCloudShellGeometry.PlanetRadius;

    /// <summary>World-anchored ground mist slab height above <see cref="GroundPlaneWorldY"/>.</summary>
    public const float GroundFogSlabHeight = 4f;
}
