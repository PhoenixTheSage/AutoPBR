namespace AutoPBR.Core.Models;

public sealed class TextureOverrides
{
    public float? NormalIntensity { get; set; }
    public bool InvertNormalRed { get; set; }
    public bool InvertNormalGreen { get; set; }

    public float? HeightIntensity { get; set; }
    public float? HeightBrightness { get; set; }
    public bool InvertHeight { get; set; }

    public bool? FastSpecular { get; set; }
    public IReadOnlyList<SpecularRule>? CustomSpecularRules { get; set; }
}

public sealed class TextureWorkItem
{
    public required string FullPath { get; init; }          // e.g. ...\stone.png
    public required string DirectoryPath { get; init; }     // e.g. ...\block\
    public required string Name { get; init; }              // e.g. stone
    public required string Extension { get; init; }         // e.g. .png
    public required string RelativeKey { get; init; }       // e.g. \block\stone

    public TextureOverrides Overrides { get; } = new();

    public string DiffusePath => FullPath;
    public string NormalPath => Path.Combine(DirectoryPath, Name + "_n" + Extension);
    public string SpecularPath => Path.Combine(DirectoryPath, Name + "_s" + Extension);
}

