using AutoPBR.Core;
using AutoPBR.Core.Models;

namespace AutoPBR.Preview;

/// <summary>
/// Resolves LabPBR-ready maps for the preview ground plane (<c>grass_block_top</c>).
/// Priority: single-pack scan, optional Minecraft install assets, then caller-supplied bundled fallback.
/// </summary>
public static class PreviewGroundMapsResolver
{
    public const string GrassBlockTopArchivePath = "assets/minecraft/textures/block/grass_block_top.png";

    /// <summary>
    /// When true, use the scanned resource pack as the diffuse source (not batch scans).
    /// </summary>
    public static bool ShouldPreferScannedPack(bool hasScannedArchive, bool isBatchScanActive) =>
        hasScannedArchive && !isBatchScanActive;

    public static async Task<PreviewTextureMaps?> TryResolveAsync(
        string? scannedPackDiskPath,
        bool preferScannedPack,
        string? minecraftAssetsDirectory,
        AutoPBROptions options,
        CancellationToken cancellationToken = default)
    {
        if (preferScannedPack &&
            !string.IsNullOrWhiteSpace(scannedPackDiskPath) &&
            File.Exists(scannedPackDiskPath))
        {
            try
            {
                var detailed = await ResourcePackPreviewRenderer.RenderPreviewDetailedAsync(
                        scannedPackDiskPath,
                        GrassBlockTopArchivePath,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                return detailed.Maps;
            }
            catch
            {
                /* fall through to install */
            }
        }

        if (MinecraftInstallAssetSource.TryOpen(minecraftAssetsDirectory, out var installSource, out var installLifetime))
        {
            try
            {
                if (installSource!.TryReadBytes(GrassBlockTopArchivePath, out var bytes) && bytes.Length > 0)
                {
                    return await TryResolveFromDiffuseBytesAsync(bytes, GrassBlockTopArchivePath, options, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                installLifetime?.Dispose();
            }
        }

        return null;
    }

    /// <summary>Runs the single-texture conversion preview pipeline on a loose diffuse PNG.</summary>
    public static Task<PreviewTextureMaps?> TryResolveFromDiffuseFileAsync(
        string diffuseFilePath,
        AutoPBROptions options,
        CancellationToken cancellationToken = default) =>
        TryResolveFromDiffuseFileAsync(diffuseFilePath, GrassBlockTopArchivePath, options, cancellationToken);

    /// <summary>Runs the single-texture conversion preview pipeline on a loose diffuse PNG at a zip-relative path.</summary>
    public static async Task<PreviewTextureMaps?> TryResolveFromDiffuseFileAsync(
        string diffuseFilePath,
        string archivePath,
        AutoPBROptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(diffuseFilePath) || !File.Exists(diffuseFilePath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(diffuseFilePath, cancellationToken).ConfigureAwait(false);
        return await TryResolveFromDiffuseBytesAsync(bytes, archivePath, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs the single-texture conversion preview pipeline on in-memory diffuse PNG bytes.</summary>
    public static async Task<PreviewTextureMaps?> TryResolveFromDiffuseBytesAsync(
        byte[] diffusePngBytes,
        string archivePath,
        AutoPBROptions options,
        CancellationToken cancellationToken = default)
    {
        if (diffusePngBytes is null || diffusePngBytes.Length == 0 || string.IsNullOrWhiteSpace(archivePath))
        {
            return null;
        }

        if (options.SpecularData is null)
        {
            throw new InvalidOperationException("SpecularData is required (load textures_data.json first).");
        }

        var targetRel = archivePath.Replace('\\', '/').TrimStart('/');
        var baseTemp = string.IsNullOrWhiteSpace(options.TempDirectory)
            ? Path.GetTempPath()
            : options.TempDirectory;
        var tempRoot = Path.Combine(baseTemp, "AutoPBR_PreviewGround", Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(tempRoot, "pack_unzipped");
        Directory.CreateDirectory(extracted);

        try
        {
            var rel = targetRel.Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.Combine(extracted, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, diffusePngBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<TextureWorkItem> textures = TextureScanner.ScanTextures(
                extracted,
                options,
                applyFoliageIgnoreFilter: false,
                cancellationToken: cancellationToken);
            if (textures.Count == 0)
            {
                return null;
            }

            TextureWorkItem target = textures[0];
            foreach (var t in textures)
            {
                var relPath = Path.GetRelativePath(extracted, t.DiffusePath).Replace('\\', '/');
                if (string.Equals(relPath, targetRel, StringComparison.OrdinalIgnoreCase))
                {
                    target = t;
                    break;
                }
            }

            var single = new List<TextureWorkItem> { target };
            await NormalHeightGenerator.GenerateAsync(single, options, null, cancellationToken).ConfigureAwait(false);
            await SpecularGenerator.GenerateAsync(single, options, null, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return PreviewTextureMapsLoader.Load(target);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
}
