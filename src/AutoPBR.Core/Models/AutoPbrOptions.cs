namespace AutoPBR.Core.Models;

public sealed class AutoPbrOptions
{
    public float NormalIntensity { get; init; } = AutoPbrDefaults.DefaultNormalIntensity;
    public float HeightIntensity { get; init; } = AutoPbrDefaults.DefaultHeightIntensity;
    public bool FastSpecular { get; init; } = false;

    /// <summary>
    /// When true, write specular texture as LabPBR _s (RGBA: smoothness, F0/metal, porosity/subsurface, emissive).
    /// When false, use legacy RGB-only encoding.
    /// </summary>
    public bool ExperimentalSpecular { get; init; } = false;

    /// <summary>
    /// Keys like "\block\stone" (no extension). If a texture's key matches, it is skipped.
    /// </summary>
    public ISet<string> IgnoreTextureKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public SpecularData? SpecularData { get; init; }
}

