using AutoPBR.PreviewGpuAssets;

if (args.Length < 1 || args.Length > 2)
{
    Console.Error.WriteLine(
        "Usage: GeneratePreviewCloudAssets <output-directory> [--v2-only]");
    return 1;
}

var outDir = Path.GetFullPath(args[0]);
var v2Only = args.Length == 2 &&
             string.Equals(args[1], "--v2-only", StringComparison.Ordinal);
if (args.Length == 2 && !v2Only)
{
    Console.Error.WriteLine($"Unknown option: {args[1]}");
    return 1;
}

Directory.CreateDirectory(outDir);

if (!v2Only)
{
    WriteBlob(outDir, "cloud_noise_shape_128.bin", PreviewCloudNoiseTextureGenerator.GenerateRgba8());
    WriteBlob(outDir, "cloud_noise_detail_32.bin", PreviewCloudNoiseTextureGenerator.GenerateDetailRgba8());
    WriteBlob(outDir, "cloud_coverage_256.bin", PreviewCloudCoverageMapGenerator.GenerateRgba8());
    WriteBlob(
        outDir,
        PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetFileName,
        PreviewCloudSpatiotemporalBlueNoiseGenerator.GenerateR8());
}

var cq2 = PreviewCloudDensityAssetGenerator.GenerateAll();
WriteDensityBlob(
    outDir,
    PreviewCloudDensityAssetContract.Shape,
    cq2.ShapeRgba);
WriteDensityBlob(
    outDir,
    PreviewCloudDensityAssetContract.Detail,
    cq2.DetailRgba);
WriteDensityBlob(
    outDir,
    PreviewCloudDensityAssetContract.Weather,
    cq2.WeatherRgba);

foreach (var template in PreviewSparseCloudTemplateAssetGenerator.GenerateAll())
{
    WriteSparseTemplateBlob(outDir, template);
}

foreach (var template in PreviewSparseCloudTemplateAssetGenerator.GenerateAllV2())
{
    WriteSparseTemplateBlob(outDir, template);
}

Console.WriteLine($"Wrote preview cloud assets to {outDir}");
return 0;

static void WriteBlob(string dir, string name, byte[] data)
{
    var path = Path.Combine(dir, name);
    WriteAtomically(path, data);
    Console.WriteLine($"  {name} ({data.Length:N0} bytes)");
}

static void WriteDensityBlob(
    string dir,
    PreviewCloudDensityAssetDescriptor descriptor,
    byte[] data)
{
    if (!PreviewCloudDensityAssetContract.ValidatePayload(descriptor, data, out var reason))
    {
        throw new InvalidDataException(
            $"{descriptor.FileName} failed CQ2 validation: {reason}.");
    }

    var path = Path.Combine(dir, descriptor.FileName);
    WriteAtomically(path, data);
    Console.WriteLine(
        $"  {descriptor.FileName} ({descriptor.DimensionLabel} RGBA8, " +
        $"{data.Length:N0} bytes, sha256=" +
        $"{PreviewCloudDensityAssetGenerator.ComputeSha256Hex(data)})");
}

static void WriteAtomically(string path, byte[] data)
{
    PreviewCloudAssetFileWriter.WriteAtomically(path, data);
}

static void WriteSparseTemplateBlob(
    string dir,
    PreviewSparseCloudTemplateAssetPayload payload)
{
    if (!PreviewSparseCloudTemplateAssetGenerator.ValidatePayloadForVersion(
            payload.Descriptor,
            payload.Rg,
            out var reason))
    {
        throw new InvalidDataException(
            $"{payload.Descriptor.FileName} failed CQ4 validation: {reason}.");
    }

    var path = Path.Combine(dir, payload.Descriptor.FileName);
    WriteAtomically(path, payload.Rg);
    Console.WriteLine(
        $"  {payload.Descriptor.FileName} " +
        $"({payload.Descriptor.DimensionLabel} RG8 v{payload.Descriptor.Version}, " +
        $"{payload.Rg.Length:N0} bytes, seed={payload.Descriptor.Seed}, sha256=" +
        $"{PreviewSparseCloudTemplateAssetGenerator.ComputeSha256Hex(payload.Rg)})");
}
