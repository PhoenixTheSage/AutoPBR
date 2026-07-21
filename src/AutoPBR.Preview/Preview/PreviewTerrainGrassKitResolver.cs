using System.IO.Compression;
using System.Text;

using AutoPBR.Core.Models;

namespace AutoPBR.Preview;

/// <summary>
/// Resolves stage-terrain materials: Pack → Minecraft install → Built-in single top.
/// BlockModelFaces requires top + side + dirt; overlay/stone/sand/gravel are optional
/// (missing biome blocks are aliased to grass-top at the VM/GPU layer).
/// </summary>
public static class PreviewTerrainGrassKitResolver
{
    public const string GrassBlockSideArchivePath = "assets/minecraft/textures/block/grass_block_side.png";
    public const string DirtArchivePath = "assets/minecraft/textures/block/dirt.png";
    public const string GrassBlockSideOverlayArchivePath =
        "assets/minecraft/textures/block/grass_block_side_overlay.png";
    public const string StoneArchivePath = "assets/minecraft/textures/block/stone.png";
    public const string SandArchivePath = "assets/minecraft/textures/block/sand.png";
    public const string GravelArchivePath = "assets/minecraft/textures/block/gravel.png";

    public static bool HasValidGrassSet(Func<string, bool> exists) =>
        exists(PreviewGroundMapsResolver.GrassBlockTopArchivePath) &&
        exists(GrassBlockSideArchivePath) &&
        exists(DirtArchivePath);

    public static async Task<PreviewTerrainGrassKit> TryResolveAsync(
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

                return await ResolveWithSourcesAsync(
                        packSource,
                        installSource,
                        scannedPackDiskPath,
                        preferScannedPack,
                        minecraftAssetsDirectory,
                        options,
                        cancellationToken)
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

    private static async Task<PreviewTerrainGrassKit> ResolveWithSourcesAsync(
        IAssetSource? packSource,
        IAssetSource? installSource,
        string? scannedPackDiskPath,
        bool preferScannedPack,
        string? minecraftAssetsDirectory,
        AutoPBROptions options,
        CancellationToken cancellationToken)
    {
        var sources = new List<IAssetSource>();
        if (packSource is not null)
        {
            sources.Add(packSource);
        }

        if (installSource is not null)
        {
            sources.Add(installSource);
        }

        IAssetSource? composite = sources.Count > 0 ? new CompositeAssetSource(sources.ToArray()) : null;

        if (sources.Count == 0 || composite is null || !HasValidGrassSet(composite.Exists))
        {
            // Preserve legacy single-top resolve (pack top or install top) for BuiltIn maps.
            var topOnly = await PreviewGroundMapsResolver.TryResolveAsync(
                    scannedPackDiskPath,
                    preferScannedPack,
                    minecraftAssetsDirectory,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildBuiltInKitAsync(topOnly, composite, options, cancellationToken)
                .ConfigureAwait(false);
        }
        var betterGrass = PreviewTerrainBetterGrassProperties.Default;
        if (packSource is not null &&
            packSource.TryReadText(PreviewTerrainBetterGrassProperties.ArchivePath, out var propsText))
        {
            betterGrass = PreviewTerrainBetterGrassProperties.Parse(propsText);
        }

        var topPath = PreviewGroundMapsResolver.GrassBlockTopArchivePath;
        var sidePath = GrassBlockSideArchivePath;
        var dirtPath = DirtArchivePath;
        var overlayPath = GrassBlockSideOverlayArchivePath;

        // OptiFine texture overrides (still require the vanilla required set to enter BlockModelFaces).
        var bgTopPath = PreviewTerrainBetterGrassProperties.ModelTextureToBlockZipPath(betterGrass.TextureGrass);
        var bgSidePath = PreviewTerrainBetterGrassProperties.ModelTextureToBlockZipPath(betterGrass.TextureGrassSide);
        if (betterGrass.Multilayer && composite.Exists(bgSidePath))
        {
            sidePath = bgSidePath;
        }

        var topMaps = await ResolveSlotMapsAsync(composite, topPath, options, cancellationToken)
            .ConfigureAwait(false);
        var sideMaps = await ResolveSlotMapsAsync(composite, sidePath, options, cancellationToken)
            .ConfigureAwait(false);
        var dirtMaps = await ResolveSlotMapsAsync(composite, dirtPath, options, cancellationToken)
            .ConfigureAwait(false);

        if (topMaps is null || sideMaps is null || dirtMaps is null)
        {
            var topOnly = await PreviewGroundMapsResolver.TryResolveAsync(
                    scannedPackDiskPath,
                    preferScannedPack,
                    minecraftAssetsDirectory,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildBuiltInKitAsync(topOnly, composite, options, cancellationToken)
                .ConfigureAwait(false);
        }

        PreviewTextureMaps? overlayMaps = null;
        string? resolvedOverlayPath = null;
        if (composite.Exists(overlayPath))
        {
            overlayMaps = await ResolveSlotMapsAsync(composite, overlayPath, options, cancellationToken)
                .ConfigureAwait(false);
            if (overlayMaps is not null)
            {
                resolvedOverlayPath = overlayPath;
            }
        }

        // Multilayer BetterGrass: layer2 = texture.grass (tinted) as overlay when present.
        if (betterGrass.Multilayer &&
            betterGrass.GrassEnabled &&
            composite.Exists(bgTopPath))
        {
            var multilayerOverlay = await ResolveSlotMapsAsync(composite, bgTopPath, options, cancellationToken)
                .ConfigureAwait(false);
            if (multilayerOverlay is not null)
            {
                overlayMaps = multilayerOverlay;
                resolvedOverlayPath = bgTopPath;
            }
        }

        // Fancy BetterGrass sides share the Top slot. When texture.grass differs from grass_block_top,
        // prefer the OptiFine override for that shared slot (up faces follow the same override).
        if (betterGrass.GrassEnabled &&
            !string.Equals(bgTopPath, topPath, StringComparison.OrdinalIgnoreCase) &&
            composite.Exists(bgTopPath))
        {
            var overrideTop = await ResolveSlotMapsAsync(composite, bgTopPath, options, cancellationToken)
                .ConfigureAwait(false);
            if (overrideTop is not null)
            {
                topMaps = overrideTop;
                topPath = bgTopPath;
            }
        }

        var stoneMaps = await ResolveSlotMapsAsync(composite, StoneArchivePath, options, cancellationToken)
            .ConfigureAwait(false);
        var sandMaps = await ResolveSlotMapsAsync(composite, SandArchivePath, options, cancellationToken)
            .ConfigureAwait(false);
        var gravelMaps = await ResolveSlotMapsAsync(composite, GravelArchivePath, options, cancellationToken)
            .ConfigureAwait(false);

        var stoneAliased = stoneMaps is null;
        var sandAliased = sandMaps is null;
        var gravelAliased = gravelMaps is null;

        var identity = BuildIdentity(
            Mode: PreviewTerrainGrassMode.BlockModelFaces,
            topPath,
            sidePath,
            dirtPath,
            resolvedOverlayPath,
            betterGrass,
            stoneAliased,
            sandAliased,
            gravelAliased);

        return new PreviewTerrainGrassKit
        {
            Mode = PreviewTerrainGrassMode.BlockModelFaces,
            Identity = identity,
            Top = topMaps,
            Side = sideMaps,
            Dirt = dirtMaps,
            Overlay = overlayMaps,
            Stone = stoneMaps,
            Sand = sandMaps,
            Gravel = gravelMaps,
            StoneAliased = stoneAliased,
            SandAliased = sandAliased,
            GravelAliased = gravelAliased,
            TopArchivePath = topPath,
            SideArchivePath = sidePath,
            DirtArchivePath = dirtPath,
            OverlayArchivePath = resolvedOverlayPath,
            BetterGrass = betterGrass,
        };
    }

    private static string BuildIdentity(
        PreviewTerrainGrassMode Mode,
        string topPath,
        string sidePath,
        string dirtPath,
        string? overlayPath,
        PreviewTerrainBetterGrassProperties betterGrass,
        bool stoneAliased,
        bool sandAliased,
        bool gravelAliased)
    {
        var sb = new StringBuilder(256);
        sb.Append((int)Mode).Append('|')
            .Append(topPath).Append('|')
            .Append(sidePath).Append('|')
            .Append(dirtPath).Append('|')
            .Append(overlayPath ?? "-").Append('|')
            .Append(betterGrass.GrassEnabled ? '1' : '0').Append('|')
            .Append(betterGrass.Multilayer ? '1' : '0').Append('|')
            .Append(betterGrass.TextureGrass).Append('|')
            .Append(betterGrass.TextureGrassSide).Append('|')
            .Append(stoneAliased ? 'A' : 'R').Append('|')
            .Append(sandAliased ? 'A' : 'R').Append('|')
            .Append(gravelAliased ? 'A' : 'R');
        return sb.ToString();
    }

    private static async Task<PreviewTerrainGrassKit> BuildBuiltInKitAsync(
        PreviewTextureMaps? topOnly,
        IAssetSource? composite,
        AutoPBROptions options,
        CancellationToken cancellationToken)
    {
        PreviewTextureMaps? stone = null;
        PreviewTextureMaps? sand = null;
        PreviewTextureMaps? gravel = null;
        if (composite is not null)
        {
            stone = await ResolveSlotMapsAsync(composite, StoneArchivePath, options, cancellationToken)
                .ConfigureAwait(false);
            sand = await ResolveSlotMapsAsync(composite, SandArchivePath, options, cancellationToken)
                .ConfigureAwait(false);
            gravel = await ResolveSlotMapsAsync(composite, GravelArchivePath, options, cancellationToken)
                .ConfigureAwait(false);
        }

        var kit = PreviewTerrainGrassKit.BuiltIn(topOnly);
        return new PreviewTerrainGrassKit
        {
            Mode = kit.Mode,
            Identity = $"builtin-single-top|{(stone is null ? 'A' : 'R')}|{(sand is null ? 'A' : 'R')}|{(gravel is null ? 'A' : 'R')}",
            Top = kit.Top,
            Stone = stone,
            Sand = sand,
            Gravel = gravel,
            StoneAliased = stone is null,
            SandAliased = sand is null,
            GravelAliased = gravel is null,
            BetterGrass = kit.BetterGrass,
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
