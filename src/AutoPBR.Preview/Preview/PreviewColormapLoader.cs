using System.IO.Compression;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.Preview;

/// <summary>
/// Resolves vanilla colormap PNGs from scanned pack zip, optional Minecraft install, or bundled native catalogs.
/// </summary>
public static class PreviewColormapLoader
{
    public static bool TryLoadGrassColormap(
        string? packZipPath,
        string? minecraftAssetsDirectory,
        string? nativeRootDirectory,
        out PreviewColormapImage? image)
    {
        image = null;
        using var pack = TryOpenPackZip(packZipPath);
        var packSource = pack is not null ? new ZipAssetSource(pack) : null;
        IDisposable? installLifetime = null;
        try
        {
            IAssetSource? installSource = null;
            if (!MinecraftInstallAssetSource.TryOpen(minecraftAssetsDirectory, out installSource, out installLifetime))
            {
                installSource = null;
            }

            IAssetSource? nativeSource = null;
            if (!string.IsNullOrWhiteSpace(nativeRootDirectory) && Directory.Exists(nativeRootDirectory))
            {
                nativeSource = new DirectoryAssetSource(nativeRootDirectory);
            }

            var sources = new List<IAssetSource>();
            if (packSource is not null)
            {
                sources.Add(packSource);
            }

            if (installSource is not null)
            {
                sources.Add(installSource);
            }

            if (nativeSource is not null)
            {
                sources.Add(nativeSource);
            }

            if (sources.Count == 0)
            {
                return false;
            }

            var composite = sources.Count == 1 ? sources[0] : new CompositeAssetSource(sources.ToArray());
            if (!composite.TryReadBytes(PreviewGrassColormapTint.GrassColormapArchivePath, out var bytes) ||
                bytes.Length == 0)
            {
                return false;
            }

            return TryDecodeRgba(bytes, out image);
        }
        finally
        {
            installLifetime?.Dispose();
        }
    }

    private static ZipArchive? TryOpenPackZip(string? packZipPath)
    {
        if (string.IsNullOrWhiteSpace(packZipPath) || !File.Exists(packZipPath))
        {
            return null;
        }

        try
        {
            return ZipFile.OpenRead(packZipPath);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDecodeRgba(byte[] pngBytes, out PreviewColormapImage? image)
    {
        image = null;
        try
        {
            using var ms = new MemoryStream(pngBytes, writable: false);
            using var img = Image.Load<Rgba32>(ms);
            if (img.Width < 1 || img.Height < 1)
            {
                return false;
            }

            var rgba = new byte[img.Width * img.Height * 4];
            img.CopyPixelDataTo(rgba);
            image = new PreviewColormapImage
            {
                Width = img.Width,
                Height = img.Height,
                Rgba = rgba,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
