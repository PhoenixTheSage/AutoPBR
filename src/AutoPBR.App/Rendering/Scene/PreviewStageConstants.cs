namespace AutoPBR.App.Rendering.Scene;

/// <summary>Shared layout for preview grid, ground plane, and orbit framing.</summary>
public static class PreviewStageConstants
{
    public const float GridHalfExtent = 14f;
    public const float GridStep = 0.5f;

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

    /// <summary>Max |relative| hill height in blocks outside the flat pad.</summary>
    public const int TerrainMaxReliefBlocks = 6;

    /// <summary>How many solid layers to keep below each column surface (thickness).</summary>
    public const int TerrainFillDepth = 3;

    /// <summary>Deterministic heightfield seed.</summary>
    public const int TerrainHeightSeed = 0x41504252; // 'APBR'

    /// <summary>Full-detail chunks whose center is within this camera XZ radius keep POM enabled.</summary>
    public const float TerrainNearPomRadius = 22f;

    /// <summary>
    /// Legacy bake LOD stamp. Streaming uses <see cref="TerrainLodRingChunks"/> instead.
    /// </summary>
    public const float TerrainLodMaxDistance = 0f;

    /// <summary>Hard Full-detail Chebyshev radius in chunks (World setting default).</summary>
    public const int TerrainDefaultChunkViewDistance = 8;

    public const int TerrainMinChunkViewDistance = 2;
    public const int TerrainMaxChunkViewDistance = 24;

    /// <summary>Extra Chebyshev chunks beyond hard view distance kept as merged LOD meshes.</summary>
    public const int TerrainLodRingChunks = 6;

    /// <summary>Unload hysteresis past LOD radius (chunks).</summary>
    public const int TerrainUnloadHysteresisChunks = 1;

    /// <summary>Steady-state chunk GPU uploads per frame (keeps fly-cam smooth).</summary>
    public const int TerrainMaxChunkUploadsPerFrame = 4;

    /// <summary>Upload cap while streaming is still catching up to the desired ring.</summary>
    public const int TerrainMaxChunkUploadsPerFrameCatchUp = 12;

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
