using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

using Avalonia.Threading;

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _previewGroundTextureCts;
    private PreviewTerrainGrassKit? _previewGroundKit;

    private void SchedulePreviewGroundTextureRefresh()
    {
        if (!IsPreview3D)
        {
            return;
        }

        _ = RefreshPreviewGroundTextureAsync();
    }

    private async Task RefreshPreviewGroundTextureAsync()
    {
        _previewGroundTextureCts?.Cancel();
        _previewGroundTextureCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewGroundTextureCts = cts;

        try
        {
            _specularData ??=
                SpecularData.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json"));

            string? diskPack = null;
            if (PreviewGroundMapsResolver.ShouldPreferScannedPack(HasScannedArchive, IsBatchScanActive) &&
                _exploreController.TryGetDiskPackAndEntryPath(
                    PreviewGroundMapsResolver.GrassBlockTopArchivePath,
                    out var pack,
                    out _))
            {
                diskPack = pack;
            }

            var options = BuildConversionOptions(new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
            var kit = await PreviewTerrainGrassKitResolver.TryResolveAsync(
                    diskPack,
                    diskPack is not null,
                    MinecraftAssetsDirectory,
                    options,
                    cts.Token)
                .ConfigureAwait(false);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || _glPreview is null || !IsPreview3D)
                {
                    return;
                }

                _previewGroundKit = kit;
                EnsurePreviewGrassColormapLoaded();
                PushPreviewGroundKitToGpu();
            });
        }
        catch (OperationCanceledException)
        {
            /* superseded */
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested)
                {
                    AddLogLine($"[Preview 3D] Ground texture resolve failed: {ex.Message}");
                }
            });
        }
    }

    private void PushPreviewGroundMaterialToGpu() => PushPreviewGroundKitToGpu();

    private void PushPreviewGroundKitToGpu()
    {
        if (_glPreview is null || !IsPreview3D)
        {
            return;
        }

        var kit = _previewGroundKit;
        var bake = kit is null
            ? PreviewTerrainGrassBakeSettings.BuiltIn
            : PreviewTerrainGrassBakeSettings.FromKit(kit);
        _glPreview.SetTerrainGrassBakeSettings(bake);

        var slots = BuildPreviewGroundMaterials(kit);
        if (slots is null || slots.Length == 0)
        {
            if (PreviewBundledGroundMapsLoader.TryLoad(out var bundled))
            {
                // Still upload a full alias palette so biome mesh slots bind safely.
                var palette = new PreviewMaterial[PreviewTerrainGrassSlots.MaxCount];
                Array.Fill(palette, bundled);
                _glPreview.SetGroundMaterials(palette, overlayIsCutout: true);
            }

            return;
        }

        _glPreview.SetGroundMaterials(slots, overlayIsCutout: true);
    }

    private PreviewMaterial[]? BuildPreviewGroundMaterials(PreviewTerrainGrassKit? kit)
    {
        if (!PreviewBundledGroundMapsLoader.TryLoad(out var bundledFallback) &&
            kit?.Top is null)
        {
            return null;
        }

        PreviewMaterial Fallback()
        {
            if (kit?.Top is not null)
            {
                return PreviewMaterialMapper.FromCoreMaps(
                    ApplyGrassColormapTintIfNeeded(kit.Top, kit.TopArchivePath),
                    kit.TopArchivePath);
            }

            return bundledFallback!;
        }

        var grassTop = Fallback();
        var slots = new PreviewMaterial[PreviewTerrainGrassSlots.MaxCount];
        slots[PreviewTerrainGrassSlots.Top] = grassTop;

        if (kit is null || kit.Mode == PreviewTerrainGrassMode.BuiltInSingleTop)
        {
            // Alias every biome slot to grass-top / bundled; optional real stone/sand/gravel if resolved.
            slots[PreviewTerrainGrassSlots.Side] = grassTop;
            slots[PreviewTerrainGrassSlots.Dirt] = grassTop;
            slots[PreviewTerrainGrassSlots.Overlay] = grassTop;
            slots[PreviewTerrainGrassSlots.Stone] = MapOrAlias(kit?.Stone, kit?.StoneArchivePath, grassTop);
            slots[PreviewTerrainGrassSlots.Sand] = MapOrAlias(kit?.Sand, kit?.SandArchivePath, grassTop);
            slots[PreviewTerrainGrassSlots.Gravel] = MapOrAlias(kit?.Gravel, kit?.GravelArchivePath, grassTop);
            return slots;
        }

        if (kit.Top is null || kit.Side is null || kit.Dirt is null)
        {
            return null;
        }

        slots[PreviewTerrainGrassSlots.Top] = PreviewMaterialMapper.FromCoreMaps(
            ApplyGrassColormapTintIfNeeded(kit.Top, kit.TopArchivePath),
            kit.TopArchivePath);
        slots[PreviewTerrainGrassSlots.Side] =
            PreviewMaterialMapper.FromCoreMaps(kit.Side, kit.SideArchivePath);
        slots[PreviewTerrainGrassSlots.Dirt] =
            PreviewMaterialMapper.FromCoreMaps(kit.Dirt, kit.DirtArchivePath);

        if (kit.EmitOverlay && kit.Overlay is not null && kit.OverlayArchivePath is not null)
        {
            slots[PreviewTerrainGrassSlots.Overlay] = PreviewMaterialMapper.FromCoreMaps(
                ApplyGrassColormapTintIfNeeded(kit.Overlay, kit.OverlayArchivePath),
                kit.OverlayArchivePath);
        }
        else
        {
            // Keep a valid texture at the overlay index; cutout flag only applies when EmitOverlay draws it.
            slots[PreviewTerrainGrassSlots.Overlay] = slots[PreviewTerrainGrassSlots.Top];
        }

        slots[PreviewTerrainGrassSlots.Stone] =
            MapOrAlias(kit.Stone, kit.StoneArchivePath, slots[PreviewTerrainGrassSlots.Top]);
        slots[PreviewTerrainGrassSlots.Sand] =
            MapOrAlias(kit.Sand, kit.SandArchivePath, slots[PreviewTerrainGrassSlots.Top]);
        slots[PreviewTerrainGrassSlots.Gravel] =
            MapOrAlias(kit.Gravel, kit.GravelArchivePath, slots[PreviewTerrainGrassSlots.Top]);

        return slots;
    }

    private static PreviewMaterial MapOrAlias(
        PreviewTextureMaps? maps,
        string? archivePath,
        PreviewMaterial alias)
    {
        if (maps is null)
        {
            return alias;
        }

        return PreviewMaterialMapper.FromCoreMaps(maps, archivePath);
    }

    private bool PreviewGroundNeedsGrassColormapTint()
    {
        var kit = _previewGroundKit;
        if (kit is null)
        {
            return PreviewGrassColormapTint.NeedsGrassColormapTint(
                PreviewGroundMapsResolver.GrassBlockTopArchivePath);
        }

        if (PreviewGrassColormapTint.NeedsGrassColormapTint(kit.TopArchivePath))
        {
            return true;
        }

        return kit.OverlayArchivePath is not null &&
               PreviewGrassColormapTint.NeedsGrassColormapTint(kit.OverlayArchivePath);
    }
}
