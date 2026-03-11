namespace AutoPBR.Core.Models;

public enum ConversionStage
{
    Extracting,
    ScanningTextures,
    GeneratingSpecular,
    GeneratingNormals,
    Packing,
    Done
}

public sealed record ConversionProgress(
    ConversionStage Stage,
    int Completed,
    int Total,
    string? CurrentTextureName = null,
    string? InfoMessage = null
);

