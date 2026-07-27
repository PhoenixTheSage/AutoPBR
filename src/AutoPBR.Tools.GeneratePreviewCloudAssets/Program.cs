using AutoPBR.PreviewGpuAssets;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: GeneratePreviewCloudAssets <output-directory>");
    return 1;
}

var outDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);

WriteBlob(outDir, "cloud_noise_shape_128.bin", PreviewCloudNoiseTextureGenerator.GenerateRgba8());
WriteBlob(outDir, "cloud_noise_detail_32.bin", PreviewCloudNoiseTextureGenerator.GenerateDetailRgba8());
WriteBlob(outDir, "cloud_coverage_256.bin", PreviewCloudCoverageMapGenerator.GenerateRgba8());
WriteBlob(
    outDir,
    PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetFileName,
    PreviewCloudSpatiotemporalBlueNoiseGenerator.GenerateR8());

Console.WriteLine($"Wrote preview cloud assets to {outDir}");
return 0;

static void WriteBlob(string dir, string name, byte[] data)
{
    var path = Path.Combine(dir, name);
    File.WriteAllBytes(path, data);
    Console.WriteLine($"  {name} ({data.Length:N0} bytes)");
}
