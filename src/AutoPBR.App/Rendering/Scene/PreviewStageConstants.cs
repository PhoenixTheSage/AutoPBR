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

    /// <summary>Default extra Chebyshev chunks beyond hard view distance kept as combined LOD sections.</summary>
    public const int TerrainDefaultLodRingChunks = 128;

    public const int TerrainMinLodRingChunks = 2;

    /// <summary>
    /// Extreme distant-horizon style ring (chunk Chebyshev). Combined sections escalate to
    /// 128×128 so residency stays tractable at this radius.
    /// </summary>
    public const int TerrainMaxLodRingChunks = 1024;

    /// <summary>Common far presets exposed in tooling/docs (slider still continuous).</summary>
    public const int TerrainLodRingPresetMedium = 256;
    public const int TerrainLodRingPresetFar = 512;
    public const int TerrainLodRingPresetExtreme = 1024;

    /// <summary>
    /// Occluder height atlas fine ring: Full + this much LOD ring at 1 m columns.
    /// Extreme LOD rings use the separate coarse multi-res atlas instead.
    /// </summary>
    public const int TerrainOccluderAtlasMaxLodRingChunks = 16;

    /// <summary>Minimum cell size (meters) for the coarse DDA height atlas.</summary>
    public const int TerrainOccluderCoarseMinCellMeters = 8;

    /// <summary>
    /// Coarse DDA atlas cell size: at least <see cref="TerrainOccluderCoarseMinCellMeters"/>,
    /// otherwise the sample step of the coarsest active LOD band so Extreme rings stay tiny.
    /// </summary>
    public static int ResolveCoarseOccluderCellMeters(int lodRingChunks)
    {
        var levels = TerrainChunkStreamer.ResolveActiveLodLevelCount(lodRingChunks);
        if (levels <= 0)
        {
            return TerrainOccluderCoarseMinCellMeters;
        }

        var coarsest = TerrainResidencyKey.SampleStepMetersForLevel((byte)levels);
        return Math.Max(TerrainOccluderCoarseMinCellMeters, coarsest);
    }

    /// <summary>Max CPU LOD section meshes retained in <c>TerrainLodSectionCache</c>.</summary>
    public const int TerrainLodCacheMaxEntries = 2048;

    /// <summary>Approx CPU LOD section cache byte budget (vertices + indices).</summary>
    public const long TerrainLodCacheMaxBytes = 384L * 1024L * 1024L;

    /// <summary>Max on-disk LOD section mesh files retained under terrain-lod-cache.</summary>
    public const int TerrainLodDiskCacheMaxEntries = 2048;

    /// <summary>Approx on-disk LOD section cache byte budget.</summary>
    public const long TerrainLodDiskCacheMaxBytes = 2L * 1024L * 1024L * 1024L;

    /// <summary>Legacy alias for <see cref="TerrainDefaultLodRingChunks"/>.</summary>
    public const int TerrainLodRingChunks = TerrainDefaultLodRingChunks;

    /// <summary>Minimum unload hysteresis past LOD radius (chunks).</summary>
    public const int TerrainUnloadHysteresisChunks = 2;

    /// <summary>Steady-state chunk GPU uploads per frame (keeps fly-cam smooth).</summary>
    public const int TerrainMaxChunkUploadsPerFrame = 4;

    /// <summary>Upload cap while streaming is still catching up to the desired ring.</summary>
    public const int TerrainMaxChunkUploadsPerFrameCatchUp = 8;

    /// <summary>Steady-state terrain vertex/index bytes submitted to GL per frame.</summary>
    public const long TerrainMaxUploadBytesPerFrame = 8L * 1024L * 1024L;

    /// <summary>
    /// Catch-up terrain vertex/index bytes submitted to GL per frame. Kept below a full
    /// LOD-section stamp burst so budget recovery does not hitch the preview frame.
    /// </summary>
    public const long TerrainMaxUploadBytesPerFrameCatchUp = 16L * 1024L * 1024L;

    /// <summary>
    /// Max BelowNormal LongRunning bake workers. Two lanes keep terrain generation from
    /// saturating the CPU while the WGL driver is compiling/warming post effects.
    /// </summary>
    public const int TerrainMaxBakeWorkers = 2;

    /// <summary>
    /// Maximum claimed/ready terrain meshes ahead of GPU upload. Prevents post-Core shader
    /// initialization from accumulating an entire hard disk of CPU meshes that may be invalidated.
    /// </summary>
    public const int TerrainMaxBakeJobsAhead = 12;

    /// <summary>
    /// Min LOD level eligible for Stage-2 budgeted LOD meshing (LOD1–2 stay on worker CPU
    /// for Full fade-seam parity).
    /// </summary>
    public const byte TerrainGpuLodMinLevel = 3;

    /// <summary>If frustum cull yields zero chunks, draw this many nearest by XZ (never blank the pad).</summary>
    public const int TerrainFrustumDrawFallbackCount = 64;

    /// <summary>Max chunk GPU disposals per frame (steady state).</summary>
    public const int TerrainMaxChunkDisposalsPerFrame = 8;

    /// <summary>Disposals per frame while the mesh pool is under budget pressure / catch-up.</summary>
    public const int TerrainMaxChunkDisposalsPerFramePressure = 24;

    /// <summary>
    /// Soft hysteresis (chunks) for residents that left the desired set but are still inside the
    /// hard unload radius. Keeps brief trail coverage without pinning multi-GiB rings in VRAM.
    /// </summary>
    public const int TerrainSoftUnloadHysteresisChunks = 4;

    /// <summary>
    /// World-meter blend width where each finer detail level dithers out at its outer edge
    /// over solid coarser LOD underneath (never dither the only coverage — that punches sky holes).
    /// Kept modest for VRAM underlay cost; wide enough to hide Full↔LOD1 Chebyshev seams.
    /// </summary>
    public const float TerrainLodDetailFadeWidthMeters = 32f;

    /// <summary>
    /// Max LOD level that stamps Full voxel vegetation meshes. LOD≥2 keeps the same placement
    /// roots but uses crossed-plane impostors for VRAM.
    /// </summary>
    public const byte TerrainLodVegetationFullVoxelMaxLevel = 1;

    /// <summary>
    /// Max LOD level that stamps vegetation *placements* from the Full root set (stable subset).
    /// Mesh density may use impostors beyond <see cref="TerrainLodVegetationFullVoxelMaxLevel"/>.
    /// </summary>
    public const byte TerrainLodVegetationBlockSpaceMaxLevel = TerrainResidencyKey.MaxLodLevel;

    /// <summary>
    /// LOD vegetation contract: every kept root is a stable member of the Full placement set.
    /// Full + LOD1 share full cardinality and voxel occupancy; LOD2 keeps full cardinality with
    /// impostors; LOD≥3 may thin to a hash subset (never emit-off / bare silhouette).
    /// </summary>
    public const bool TerrainLodVegetationBlockSpaceIdentity = true;

    /// <summary>
    /// LOD levels at or below this keep 100% of Full vegetation roots (LOD1 voxel, LOD2 impostor).
    /// </summary>
    public const byte TerrainLodVegetationFullKeepMaxLevel = 2;

    /// <summary>
    /// LOD levels at or below this (and above <see cref="TerrainLodVegetationFullKeepMaxLevel"/>)
    /// keep 50% of Full roots; coarser levels keep 25%.
    /// </summary>
    public const byte TerrainLodVegetationHalfKeepMaxLevel = 4;

    /// <summary>Whether a LOD section bake should stamp vegetation (voxel or impostor).</summary>
    public static bool ShouldEmitLodBlockSpaceVegetation(byte lodLevel) =>
        lodLevel > 0 && lodLevel <= TerrainLodVegetationBlockSpaceMaxLevel;

    /// <summary>Vegetation mesh mode for a streamed LOD section.</summary>
    public static PreviewTerrainVegetationEmitMode ResolveLodVegetationEmitMode(byte lodLevel) =>
        lodLevel <= TerrainLodVegetationFullVoxelMaxLevel
            ? PreviewTerrainVegetationEmitMode.FullVoxel
            : PreviewTerrainVegetationEmitMode.Impostor;

    /// <summary>
    /// Power-of-two keep mask for distant LOD vegetation thinning. Keep a Full root when
    /// <c>(stableHash(root) &amp; (mask - 1)) == 0</c>. Mask 1 = 100%, 2 = 50%, 4 = 25%.
    /// </summary>
    public static int ResolveLodVegetationKeepMask(byte lodLevel)
    {
        if (lodLevel <= TerrainLodVegetationFullKeepMaxLevel)
        {
            return 1;
        }

        if (lodLevel <= TerrainLodVegetationHalfKeepMaxLevel)
        {
            return 2;
        }

        return 4;
    }

    /// <summary>Expected keep fraction for budget estimates (inverse of keep mask).</summary>
    public static float ResolveLodVegetationKeepFraction(byte lodLevel) =>
        1f / ResolveLodVegetationKeepMask(lodLevel);

    /// <summary>
    /// Soft-start unlock radius (Chebyshev chunks from camera). Transition Full/LOD1/LOD2 seam
    /// keys are always eligible; coarser LOD unlocks by band when the window is idle.
    /// </summary>
    public const int TerrainStreamSoftStartInitialRing = 2;

    /// <summary>Steady-state Full chunk GPU uploads per frame.</summary>
    public const int TerrainMaxFullUploadsPerFrame = 2;

    /// <summary>Steady-state LOD section GPU uploads per frame (separate from Full).</summary>
    public const int TerrainMaxLodUploadsPerFrame = 2;

    /// <summary>Catch-up Full uploads per frame while streaming is behind.</summary>
    public const int TerrainMaxFullUploadsPerFrameCatchUp = 4;

    /// <summary>Catch-up LOD uploads per frame while streaming is behind.</summary>
    public const int TerrainMaxLodUploadsPerFrameCatchUp = 4;

    /// <summary>Steady-state Full upload byte budget per frame.</summary>
    public const long TerrainMaxFullUploadBytesPerFrame = 4L * 1024L * 1024L;

    /// <summary>Steady-state LOD upload byte budget per frame.</summary>
    public const long TerrainMaxLodUploadBytesPerFrame = 4L * 1024L * 1024L;

    /// <summary>Catch-up Full upload byte budget per frame.</summary>
    public const long TerrainMaxFullUploadBytesPerFrameCatchUp = 8L * 1024L * 1024L;

    /// <summary>Catch-up LOD upload byte budget per frame.</summary>
    public const long TerrainMaxLodUploadBytesPerFrameCatchUp = 8L * 1024L * 1024L;

    /// <summary>Throttle for flying residency diagnostic lines (seconds).</summary>
    public const double TerrainResidencyDiagIntervalSeconds = 2.0;

    /// <summary>Floor for the shared terrain mesh-pool VRAM ceiling.</summary>
    public const long TerrainMeshPoolBudgetFloorBytes = 1024L * 1024L * 1024L;

    /// <summary>Default / starting terrain mesh-pool VRAM ceiling when adapter memory is unknown.</summary>
    public const long TerrainMeshPoolBudgetDefaultBytes = 1536L * 1024L * 1024L;

    /// <summary>
    /// Ceiling used when dedicated VRAM cannot be queried (legacy fixed ladder).
    /// </summary>
    public const long TerrainMeshPoolBudgetUnknownCeilingBytes = 3072L * 1024L * 1024L;

    /// <summary>
    /// Absolute safety rail even on very large GPUs — overflow stays on CPU bake + defer/evict,
    /// not unbounded GL buffer growth.
    /// </summary>
    public const long TerrainMeshPoolBudgetAbsoluteCeilingBytes = 12L * 1024L * 1024L * 1024L;

    /// <summary>Backward-compatible alias for the unknown-VRAM ceiling.</summary>
    public const long TerrainMeshPoolBudgetCeilingBytes = TerrainMeshPoolBudgetUnknownCeilingBytes;

    /// <summary>
    /// Fraction of (dedicated VRAM − reserve) allowed for the terrain mesh pool working set.
    /// </summary>
    public const float TerrainMeshPoolVramFraction = 0.35f;

    /// <summary>
    /// Headroom reserved for framebuffers, shadows, clouds, atlases, and other preview GPU use.
    /// </summary>
    public const long TerrainMeshPoolVramReserveBytes = 768L * 1024L * 1024L;

    /// <summary>Enter budget-pressure thrash controls at this fraction of the active ceiling.</summary>
    public const float TerrainMeshPoolPressureEnterRatio = 0.90f;

    /// <summary>Leave budget-pressure thrash controls below this fraction (hysteresis).</summary>
    public const float TerrainMeshPoolPressureExitRatio = 0.70f;

    /// <summary>~MiB per Full chunk (greedy solids + 1:1 vegetation).</summary>
    public const long TerrainMeshPoolEstimateFullChunkBytes = 384L * 1024L;

    /// <summary>
    /// Extra VRAM per world-chunk of LOD that still stamps Full voxel vegetation (LOD1).
    /// </summary>
    public const long TerrainMeshPoolEstimateLodVegBytesPerWorldChunk = 256L * 1024L;

    /// <summary>
    /// Extra VRAM per world-chunk of LOD that stamps impostor vegetation (LOD≥2).
    /// </summary>
    public const long TerrainMeshPoolEstimateLodImpostorBytesPerWorldChunk = 16L * 1024L;

    /// <summary>Hull-only LOD section baseline (before vegetation).</summary>
    public const long TerrainMeshPoolEstimateLodHullSectionBytes = 96L * 1024L;

    /// <summary>Headroom multiplier over the a-priori / high-water need.</summary>
    public const float TerrainMeshPoolBudgetHeadroom = 1.15f;

    /// <summary>
    /// Veg-aware estimate of resident Full + LOD mesh bytes for the current hard/LOD radii,
    /// including adjacent-only fade underlay on each LOD band.
    /// </summary>
    public static long EstimateTerrainMeshPoolNeedBytes(int hardRadiusChunks, int lodRingChunks)
    {
        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        lodRingChunks = Math.Max(0, lodRingChunks);

        var fullSide = 2L * hardRadiusChunks + 1;
        var need = fullSide * fullSide * TerrainMeshPoolEstimateFullChunkBytes;

        Span<TerrainChunkStreamer.LodBand> bands =
            stackalloc TerrainChunkStreamer.LodBand[TerrainResidencyKey.MaxLodLevel];
        var bandCount = TerrainChunkStreamer.ResolveLodBands(hardRadiusChunks, lodRingChunks, bands);
        if (bandCount > 0)
        {
            var fade = TerrainChunkStreamer.ResolveLodFadeOverlapChunks();
            for (var i = 0; i < bandCount; i++)
            {
                var band = bands[i];
                var scale = TerrainResidencyKey.ChunksPerSideForLevel(band.Level);
                // Adjacent underlay only: expand into the previous band (Full for LOD1), not deeper.
                var underlayFloor = i == 0 ? 0 : bands[i - 1].DMin;
                var dMin = Math.Max(underlayFloor, band.DMin - fade);
                var dMax = band.DMax;
                var sections = EstimateChebyshevSectionCount(dMin, dMax, scale);
                var vegPerChunk = band.Level <= TerrainLodVegetationFullVoxelMaxLevel
                    ? TerrainMeshPoolEstimateLodVegBytesPerWorldChunk
                    : (long)(TerrainMeshPoolEstimateLodImpostorBytesPerWorldChunk *
                             ResolveLodVegetationKeepFraction(band.Level));
                var perSection = TerrainMeshPoolEstimateLodHullSectionBytes +
                                 vegPerChunk * scale * scale;
                need += sections * perSection;
            }
        }

        return (long)(need * TerrainMeshPoolBudgetHeadroom);
    }

    /// <summary>
    /// Bytes that must stay available for Full + LOD1 fade underlay (seam coverage).
    /// Distant coarse LOD fills only the remainder of the soft ceiling.
    /// </summary>
    public static long EstimateTerrainMeshPoolReservedBytes(int hardRadiusChunks, int lodRingChunks)
    {
        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        lodRingChunks = Math.Max(0, lodRingChunks);

        var fullSide = 2L * hardRadiusChunks + 1;
        var need = fullSide * fullSide * TerrainMeshPoolEstimateFullChunkBytes;

        Span<TerrainChunkStreamer.LodBand> bands =
            stackalloc TerrainChunkStreamer.LodBand[TerrainResidencyKey.MaxLodLevel];
        var bandCount = TerrainChunkStreamer.ResolveLodBands(hardRadiusChunks, lodRingChunks, bands);
        if (bandCount > 0)
        {
            var fade = TerrainChunkStreamer.ResolveLodFadeOverlapChunks();
            var band = bands[0];
            var scale = TerrainResidencyKey.ChunksPerSideForLevel(band.Level);
            var dMin = Math.Max(0, band.DMin - fade);
            var dMax = band.DMax;
            var sections = EstimateChebyshevSectionCount(dMin, dMax, scale);
            need += sections * (TerrainMeshPoolEstimateLodHullSectionBytes +
                                TerrainMeshPoolEstimateLodVegBytesPerWorldChunk * scale * scale);
        }

        return (long)(need * TerrainMeshPoolBudgetHeadroom);
    }

    /// <summary>Estimated distant LOD (beyond reserved Full + LOD1 underlay) working set.</summary>
    public static long EstimateTerrainMeshPoolDistantLodBytes(int hardRadiusChunks, int lodRingChunks)
    {
        var total = EstimateTerrainMeshPoolNeedBytes(hardRadiusChunks, lodRingChunks);
        var reserved = EstimateTerrainMeshPoolReservedBytes(hardRadiusChunks, lodRingChunks);
        return Math.Max(0L, total - reserved);
    }

    /// <summary>
    /// Approximate section count covering Chebyshev distances [dMin, dMax] at the given
    /// chunks-per-side scale.
    /// </summary>
    public static long EstimateChebyshevSectionCount(int dMin, int dMax, int chunksPerSide)
    {
        dMin = Math.Max(0, dMin);
        dMax = Math.Max(dMin, dMax);
        chunksPerSide = Math.Max(1, chunksPerSide);
        var outer = 2L * dMax + 1;
        var inner = dMin == 0 ? 0L : 2L * (dMin - 1) + 1;
        var chunkCells = outer * outer - inner * inner;
        var scaleArea = (long)chunksPerSide * chunksPerSide;
        return Math.Max(1L, (chunkCells + scaleArea - 1) / scaleArea);
    }

    /// <summary>
    /// Scales the terrain mesh-pool ceiling with view distance + LOD ring (veg-aware), then
    /// clamps to a hardware-derived VRAM fraction when dedicated memory is known (else the
    /// unknown ceiling). Optional live high-water prevents undersizing after real uploads.
    /// Overflow stays on CPU residency / defer / evict — not page-file-backed GL buffers.
    /// </summary>
    public static long ResolveTerrainMeshPoolBudgetBytes(
        int hardRadiusChunks,
        int lodRingChunks,
        long dedicatedVideoMemoryBytes = 0,
        long liveHighWaterBytes = 0)
    {
        var scaled = EstimateTerrainMeshPoolNeedBytes(hardRadiusChunks, lodRingChunks);
        scaled = Math.Max(scaled, TerrainMeshPoolBudgetDefaultBytes);
        if (liveHighWaterBytes > 0)
        {
            scaled = Math.Max(
                scaled,
                (long)(liveHighWaterBytes * TerrainMeshPoolBudgetHeadroom));
        }

        var ceiling = ResolveTerrainMeshPoolCeilingBytes(dedicatedVideoMemoryBytes);
        return Math.Clamp(scaled, TerrainMeshPoolBudgetFloorBytes, ceiling);
    }

    /// <summary>Effective growth ceiling from detected VRAM (or the unknown-VRAM ladder).</summary>
    public static long ResolveTerrainMeshPoolCeilingBytes(long dedicatedVideoMemoryBytes)
    {
        if (dedicatedVideoMemoryBytes <= 0)
        {
            return TerrainMeshPoolBudgetUnknownCeilingBytes;
        }

        var usable = Math.Max(0L, dedicatedVideoMemoryBytes - TerrainMeshPoolVramReserveBytes);
        var vramCap = (long)(usable * TerrainMeshPoolVramFraction);
        return Math.Clamp(
            vramCap,
            TerrainMeshPoolBudgetFloorBytes,
            TerrainMeshPoolBudgetAbsoluteCeilingBytes);
    }

    /// <summary>Minimum vertical skirt depth (blocks) on LOD section edges to hide cracks.</summary>
    public const int TerrainLodEdgeSkirtMinBlocks = 8;

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

    /// <summary>
    /// Legacy shader-ABI radius used only to recover the flat ground altitude from
    /// <c>center.y + radius</c>. It no longer bends cloud geometry.
    /// </summary>
    public const float CloudLegacyAltitudeReferenceRadius = 72_000f;

    /// <summary>
    /// Valley mist scale reference above <see cref="GroundPlaneWorldY"/>. Used as the soft
    /// near-ground boost scale height (~0.55×); column haze continues above with no hard top.
    /// </summary>
    public const float GroundFogSlabHeight = 48f;

    /// <summary>
    /// Scales mesh aerial fog when froxel atmospheric fill is active so distant terrain is not fogged twice.
    /// 0 = keep full mesh fog, 1 = fully defer fill to the froxel path.
    /// </summary>
    public const float VolumeAerialFillMeshFogGate = 0.72f;
}
