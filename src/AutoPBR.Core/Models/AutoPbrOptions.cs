namespace AutoPBR.Core.Models;

public sealed class AutoPbrOptions
{
    public float NormalIntensity { get; init; } = AutoPbrDefaults.DefaultNormalIntensity;
    public float HeightIntensity { get; init; } = AutoPbrDefaults.DefaultHeightIntensity;
    public bool FastSpecular { get; init; } = false;

    /// <summary>
    /// Normal generation operator when not using DeepBump. "SobelVc" = current default
    /// Sobel + VC filter. "ScharrVc" = Scharr gradients + VC filter for stronger, more
    /// isotropic edge response.
    /// </summary>
    public NormalOperator NormalOperator { get; init; } = NormalOperator.SobelVc;

    /// <summary>
    /// Kernel size for Sobel/Scharr normals when not using DeepBump. For Sobel, supports 3x3, 5x5, 7x7.
    /// For Scharr, supports 3x3 and 5x5 (7x7 will be clamped to 5x5).
    /// </summary>
    public NormalKernelSize NormalKernelSize { get; init; } = NormalKernelSize.K3;

    /// <summary>
    /// What to derive normals from when not using DeepBump: Luminance, Color, Color+Luminance blend, or max.
    /// </summary>
    public NormalDerivative NormalDerivative { get; init; } = NormalDerivative.Luminance;

    /// <summary>
    /// When true, use the legacy ZipFile-based extractor instead of the default parallel extractor.
    /// Use only if you hit issues with the default (e.g. exotic zip format).
    /// </summary>
    public bool UseLegacyExtractor { get; init; } = false;

    /// <summary>
    /// When non-null and non-empty, only these zip entry paths are extracted (e.g. from a prior scan).
    /// Reduces extraction time and disk use when only .png textures (and pack.mcmeta) are needed.
    /// </summary>
    public IReadOnlyList<string>? EntriesToExtractOnly { get; init; }

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

    /// <summary>When true, derive height from the generated normal map via Frankot-Chellappa (DeepBump-style) instead of from diffuse luminance.</summary>
    public bool UseHeightFromNormals { get; init; } = false;

    /// <summary>When true and DeepBumpModelPath is valid, generate normals from diffuse using the DeepBump ONNX model (deepbump256.onnx) instead of Sobel/VC.</summary>
    public bool UseDeepBumpNormals { get; init; } = false;

    /// <summary>Path to deepbump256.onnx (from https://github.com/HugoTini/DeepBump). Used when UseDeepBumpNormals is true.</summary>
    public string? DeepBumpModelPath { get; init; }

    /// <summary>DeepBump tile overlap: "Small", "Medium", or "Large". Matches DeepBump --color_to_normals-overlap (default LARGE = best quality).</summary>
    public string DeepBumpOverlap { get; init; } = "Large";

    public SpecularData? SpecularData { get; init; }
}

public enum NormalOperator
{
    SobelVc,
    ScharrVc
}

public enum NormalKernelSize
{
    K3 = 3,
    K5 = 5,
    K7 = 7
}

public enum NormalDerivative
{
    Luminance,
    Color,
    ColorLuminanceBlend,
    ColorLuminanceMax
}



