namespace AutoPBR.Core.Models;

public sealed class AutoPbrOptions
{
    public float NormalIntensity { get; init; } = AutoPbrDefaults.DefaultNormalIntensity;
    public float HeightIntensity { get; init; } = AutoPbrDefaults.DefaultHeightIntensity;
    public bool FastSpecular { get; init; } = false;

    /// <summary>
    /// When true, use the experimental custom ParallelZipReader for extraction instead of the built-in ZipFile-based extractor.
    /// Useful for testing parallel decompression; default is false (safer).
    /// </summary>
    public bool ExperimentalExtractor { get; init; } = false;

    /// <summary>
    /// Maximum worker threads to use for conversion (specular/normal/height). 0 or less = auto (CPU-2, minimum 1).
    /// </summary>
    public int MaxThreads { get; init; } = 0;

    /// <summary>
    /// Optional base directory for temporary working files. When null or empty, the system temp directory is used.
    /// </summary>
    public string? TempDirectory { get; init; }

    /// <summary>Scale for dielectric smoothness (R channel). 1 = unchanged; 0.5–1.5 typical.</summary>
    public float SmoothnessScale { get; init; } = AutoPbrDefaults.DefaultSmoothnessScale;

    /// <summary>Boost for metal smoothness (R channel). 1 = unchanged.</summary>
    public float MetallicBoost { get; init; } = AutoPbrDefaults.DefaultMetallicBoost;

    /// <summary>Offset added to porosity/subsurface (B channel). Can be negative.</summary>
    public int PorosityBias { get; init; } = AutoPbrDefaults.DefaultPorosityBias;

    /// <summary>
    /// When true, process block/textures (block, blocks folders).
    /// </summary>
    public bool ProcessBlocks { get; init; } = true;

    /// <summary>
    /// When true, process item textures (item, items folders).
    /// </summary>
    public bool ProcessItems { get; init; } = true;

    /// <summary>
    /// When true, process armor/entity textures (entity folder).
    /// </summary>
    public bool ProcessArmor { get; init; } = true;

    /// <summary>
    /// When true, process particle textures (particle folder). Particles get specular only (no normal/height).
    /// </summary>
    public bool ProcessParticles { get; init; } = true;

    /// <summary>
    /// Keys like "\block\stone" (no extension). If a texture's key matches, it is skipped.
    /// </summary>
    public ISet<string> IgnoreTextureKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Foliage handling: "Ignore All", "No Height", or "Convert All".</summary>
    public string FoliageMode { get; init; } = "Ignore All";

    public SpecularData? SpecularData { get; init; }
}

