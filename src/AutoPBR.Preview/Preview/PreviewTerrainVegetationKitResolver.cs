using System.IO.Compression;
using System.Text;

using AutoPBR.Core.Models;

namespace AutoPBR.Preview;

/// <summary>
/// Discovers matching log/leaves (and cactus) textures from Pack → Minecraft install.
/// A partial scanned pack (e.g. leaves-only overhaul) composites over the install with the
/// pack as the override; vanilla never replaces pack textures that are present.
/// When neither source can complete any pair, returns <see cref="PreviewTerrainVegetationKit.Empty"/>
/// so stage terrain skips tree generation.
/// </summary>
public static class PreviewTerrainVegetationKitResolver
{
    public const string BlockTexturesPrefix = "assets/minecraft/textures/block/";

    public static string LogArchivePath(string stem) =>
        BlockTexturesPrefix + stem + "_log.png";

    public static string LogTopArchivePath(string stem) =>
        BlockTexturesPrefix + stem + "_log_top.png";

    public static string LeavesArchivePath(string stem) =>
        BlockTexturesPrefix + stem + "_leaves.png";

    public static string CactusSideArchivePath { get; } = BlockTexturesPrefix + "cactus_side.png";

    public static string CactusTopArchivePath { get; } = BlockTexturesPrefix + "cactus_top.png";

    public static bool HasMatchingWoodPair(Func<string, bool> exists, string stem) =>
        exists(LogArchivePath(stem)) && exists(LeavesArchivePath(stem));

    public static bool HasCactusPair(Func<string, bool> exists) =>
        exists(CactusSideArchivePath) && exists(CactusTopArchivePath);

    public static async Task<PreviewTerrainVegetationKit> TryResolveAsync(
        string? scannedPackDiskPath,
        bool preferScannedPack,
        string? minecraftAssetsDirectory,
        AutoPBROptions options,
        CancellationToken cancellationToken = default)
    {
        ZipArchive? packZip = null;
        IAssetSource? packSource = null;
        try
        {
            if (preferScannedPack &&
                !string.IsNullOrWhiteSpace(scannedPackDiskPath) &&
                File.Exists(scannedPackDiskPath))
            {
                try
                {
                    packZip = ZipFile.OpenRead(scannedPackDiskPath);
                    packSource = new ZipAssetSource(packZip);
                }
                catch
                {
                    packZip?.Dispose();
                    packZip = null;
                    packSource = null;
                }
            }

            IDisposable? installLifetime = null;
            try
            {
                if (!MinecraftInstallAssetSource.TryOpen(
                        minecraftAssetsDirectory,
                        out var installSource,
                        out installLifetime))
                {
                    installSource = null;
                }

                var source = BuildResolveSource(packSource, installSource);
                if (source is null)
                {
                    return PreviewTerrainVegetationKit.Empty;
                }

                return await ResolveFromSourceAsync(source, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                installLifetime?.Dispose();
            }
        }
        finally
        {
            packZip?.Dispose();
        }
    }

    /// <summary>
    /// Pack-first composite when both exist so scanned textures override vanilla fill-ins.
    /// Pack-only or install-only when the other is missing.
    /// </summary>
    internal static IAssetSource? BuildResolveSource(IAssetSource? packSource, IAssetSource? installSource)
    {
        if (packSource is not null && installSource is not null)
        {
            // Scan overrides install for any path present in the pack (partial leaf overhauls, etc.).
            return new CompositeAssetSource(packSource, installSource);
        }

        return packSource ?? installSource;
    }

    private static async Task<PreviewTerrainVegetationKit> ResolveFromSourceAsync(
        IAssetSource source,
        AutoPBROptions options,
        CancellationToken cancellationToken)
    {
        var speciesKits = new List<PreviewTerrainVegetationSpeciesKit>(
            PreviewTerrainTreeSpeciesIds.WoodTextureStems.Length + 1);
        var cutout = new List<bool>(PreviewTerrainGrassSlots.MaxCount + 16);
        for (var i = 0; i < PreviewTerrainGrassSlots.MaxCount; i++)
        {
            cutout.Add(i == PreviewTerrainGrassSlots.Overlay);
        }

        var nextSlot = PreviewTerrainGrassSlots.VegetationBase;
        var identity = new StringBuilder(128);
        identity.Append("veg");

        foreach (var stem in PreviewTerrainTreeSpeciesIds.WoodTextureStems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasMatchingWoodPair(source.Exists, stem))
            {
                continue;
            }

            if (!PreviewTerrainTreeSpeciesIds.TryParseTextureStem(stem, out var species))
            {
                continue;
            }

            var logPath = LogArchivePath(stem);
            var leavesPath = LeavesArchivePath(stem);
            var logMaps = await ResolveSlotMapsAsync(source, logPath, options, cancellationToken)
                .ConfigureAwait(false);
            var leavesMaps = await ResolveSlotMapsAsync(source, leavesPath, options, cancellationToken)
                .ConfigureAwait(false);
            if (logMaps is null || leavesMaps is null)
            {
                continue;
            }

            var logSlot = nextSlot++;
            cutout.Add(false);
            var leavesSlot = nextSlot++;
            cutout.Add(true);

            PreviewTextureMaps? logTopMaps = null;
            string? logTopPath = null;
            int? logTopSlot = null;
            var topPath = LogTopArchivePath(stem);
            if (source.Exists(topPath))
            {
                logTopMaps = await ResolveSlotMapsAsync(source, topPath, options, cancellationToken)
                    .ConfigureAwait(false);
                if (logTopMaps is not null)
                {
                    logTopPath = topPath;
                    logTopSlot = nextSlot++;
                    cutout.Add(false);
                }
            }

            speciesKits.Add(new PreviewTerrainVegetationSpeciesKit
            {
                Species = species,
                TextureStem = stem,
                LogSlot = logSlot,
                LeavesOrTopSlot = leavesSlot,
                LogMaps = logMaps,
                LeavesOrTopMaps = leavesMaps,
                LogArchivePath = logPath,
                LeavesOrTopArchivePath = leavesPath,
                LogTopMaps = logTopMaps,
                LogTopArchivePath = logTopPath,
                LogTopSlot = logTopSlot,
            });

            identity.Append('|').Append(stem);
            if (logTopSlot is not null)
            {
                identity.Append('+');
            }
        }

        if (HasCactusPair(source.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sideMaps = await ResolveSlotMapsAsync(source, CactusSideArchivePath, options, cancellationToken)
                .ConfigureAwait(false);
            var topMaps = await ResolveSlotMapsAsync(source, CactusTopArchivePath, options, cancellationToken)
                .ConfigureAwait(false);
            if ((sideMaps, topMaps) is (not null, not null))
            {
                var sideSlot = nextSlot++;
                cutout.Add(true); // cactus_side needs alpha cutout for notch holes
                var topSlot = nextSlot++;
                cutout.Add(false);
                speciesKits.Add(new PreviewTerrainVegetationSpeciesKit
                {
                    Species = PreviewTerrainTreeSpecies.Cactus,
                    TextureStem = PreviewTerrainTreeSpeciesIds.Cactus,
                    LogSlot = sideSlot,
                    LeavesOrTopSlot = topSlot,
                    LogMaps = sideMaps,
                    LeavesOrTopMaps = topMaps,
                    LogArchivePath = CactusSideArchivePath,
                    LeavesOrTopArchivePath = CactusTopArchivePath,
                });
                identity.Append("|cactus");
            }
        }

        if (speciesKits.Count == 0)
        {
            return PreviewTerrainVegetationKit.Empty;
        }

        var identityString = identity.ToString();
        var modelTemplates = PreviewTerrainBlockModelTemplates.TryBuild(
            source,
            speciesKits,
            identityString);

        return new PreviewTerrainVegetationKit
        {
            Identity = identityString,
            Species = speciesKits,
            TotalSlotCount = nextSlot,
            CutoutBySlot = [.. cutout],
            ModelTemplates = modelTemplates,
        };
    }

    private static async Task<PreviewTextureMaps?> ResolveSlotMapsAsync(
        IAssetSource source,
        string archivePath,
        AutoPBROptions options,
        CancellationToken cancellationToken)
    {
        if (!source.TryReadBytes(archivePath, out var bytes) || bytes.Length == 0)
        {
            return null;
        }

        return await PreviewGroundMapsResolver
            .TryResolveFromDiffuseBytesAsync(bytes, archivePath, options, cancellationToken)
            .ConfigureAwait(false);
    }
}
