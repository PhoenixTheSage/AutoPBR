namespace AutoPBR.Core.Models;

public sealed class PbrifyOptions
{
    public float NormalIntensity { get; init; } = PbrifyDefaults.DefaultNormalIntensity;
    public float HeightIntensity { get; init; } = PbrifyDefaults.DefaultHeightIntensity;
    public bool FastSpecular { get; init; } = false;

    /// <summary>
    /// Keys like "\block\stone" (no extension). If a texture's key matches, it is skipped.
    /// </summary>
    public ISet<string> IgnoreTextureKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public SpecularData? SpecularData { get; init; }
}

