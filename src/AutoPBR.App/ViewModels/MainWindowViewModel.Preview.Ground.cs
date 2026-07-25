using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Core.Models;

using Avalonia.Threading;

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _previewGroundTextureCts;
    private PreviewTerrainGrassKit? _previewGroundKit;
    private PreviewTerrainVegetationKit? _previewVegetationKit;

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
        if (_previewGroundTextureCts is not null)
        {
            await _previewGroundTextureCts.CancelAsync().ConfigureAwait(false);
        }
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
            var kitTask = PreviewTerrainGrassKitResolver.TryResolveAsync(
                diskPack,
                diskPack is not null,
                MinecraftAssetsDirectory,
                options,
                cts.Token);
            var vegetationTask = PreviewTerrainVegetationKitResolver.TryResolveAsync(
                diskPack,
                diskPack is not null,
                MinecraftAssetsDirectory,
                options,
                cts.Token);

            await Task.WhenAll(kitTask, vegetationTask).ConfigureAwait(false);
            var kit = await kitTask.ConfigureAwait(false);
            var vegetation = await vegetationTask.ConfigureAwait(false);

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
                _previewVegetationKit = vegetation.HasAny ? vegetation : PreviewTerrainVegetationKit.Empty;
                EnsurePreviewGrassColormapLoaded();
                EnsurePreviewFoliageColormapLoaded();
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
        var vegetation = _previewVegetationKit is { HasAny: true }
            ? _previewVegetationKit
            : PreviewTerrainVegetationKit.Empty;
        var bake = kit is null
            ? PreviewTerrainGrassBakeSettings.BuiltIn with
            {
                VegetationIdentity = vegetation.HasAny ? vegetation.Identity : "",
            }
            : PreviewTerrainGrassBakeSettings.FromKit(kit, vegetation);
        _glPreview.SetTerrainGrassBakeSettings(bake);
        _glPreview.SetTerrainVegetationBakePlan(vegetation.HasAny ? vegetation.ToBakePlan() : null);

        var (slots, cutout) = BuildPreviewGroundMaterials(kit, vegetation);
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

        _glPreview.SetGroundMaterials(slots, overlayIsCutout: true, cutout);
    }

    private (PreviewMaterial[]? Slots, bool[]? Cutout) BuildPreviewGroundMaterials(
        PreviewTerrainGrassKit? kit,
        PreviewTerrainVegetationKit vegetation)
    {
        if (!PreviewBundledGroundMapsLoader.TryLoad(out var bundledFallback) &&
            kit?.Top is null)
        {
            return (null, null);
        }

        PreviewMaterial Fallback()
        {
            if (kit?.Top is not null)
            {
                return PreviewMaterialMapper.FromCoreMaps(
                    ApplyGrassColormapTintIfNeeded(kit.Top, kit.TopArchivePath),
                    kit.TopArchivePath);
            }

            return bundledFallback;
        }

        var grassTop = Fallback();
        var slotCount = Math.Max(
            PreviewTerrainGrassSlots.MaxCount,
            vegetation.HasAny ? vegetation.TotalSlotCount : PreviewTerrainGrassSlots.MaxCount);
        var slots = new PreviewMaterial?[slotCount];
        var cutout = new bool[slotCount];
        if (vegetation.CutoutBySlot is { Length: > 0 } vegCutout)
        {
            Array.Copy(vegCutout, cutout, Math.Min(vegCutout.Length, cutout.Length));
        }
        else
        {
            cutout[PreviewTerrainGrassSlots.Overlay] = true;
        }

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
            FillVegetationSlots(slots, vegetation, grassTop);
            return (FinalizeGroundSlots(slots, grassTop), cutout);
        }

        if (kit.Top is null || kit.Side is null || kit.Dirt is null)
        {
            return (null, null);
        }

        slots[PreviewTerrainGrassSlots.Top] = PreviewMaterialMapper.FromCoreMaps(
            ApplyGrassColormapTintIfNeeded(kit.Top, kit.TopArchivePath),
            kit.TopArchivePath);
        slots[PreviewTerrainGrassSlots.Side] =
            PreviewMaterialMapper.FromCoreMaps(kit.Side, kit.SideArchivePath);
        slots[PreviewTerrainGrassSlots.Dirt] =
            PreviewMaterialMapper.FromCoreMaps(kit.Dirt, kit.DirtArchivePath);

        if (kit is { EmitOverlay: true, Overlay: not null, OverlayArchivePath: not null })
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

        var topMaterial = slots[PreviewTerrainGrassSlots.Top]!;
        slots[PreviewTerrainGrassSlots.Stone] =
            MapOrAlias(kit.Stone, kit.StoneArchivePath, topMaterial);
        slots[PreviewTerrainGrassSlots.Sand] =
            MapOrAlias(kit.Sand, kit.SandArchivePath, topMaterial);
        slots[PreviewTerrainGrassSlots.Gravel] =
            MapOrAlias(kit.Gravel, kit.GravelArchivePath, topMaterial);

        FillVegetationSlots(slots, vegetation, topMaterial);
        return (FinalizeGroundSlots(slots, topMaterial), cutout);
    }

    private static PreviewMaterial[] FinalizeGroundSlots(PreviewMaterial?[] slots, PreviewMaterial fallback)
    {
        var result = new PreviewMaterial[slots.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = slots[i] ?? fallback;
        }

        return result;
    }

    private void FillVegetationSlots(
        PreviewMaterial?[] slots,
        PreviewTerrainVegetationKit vegetation,
        PreviewMaterial alias)
    {
        if (!vegetation.HasAny)
        {
            for (var i = PreviewTerrainGrassSlots.VegetationBase; i < slots.Length; i++)
            {
                slots[i] ??= alias;
            }

            return;
        }

        foreach (var species in vegetation.Species)
        {
            if ((uint)species.LogSlot < (uint)slots.Length)
            {
                slots[species.LogSlot] = PreviewMaterialMapper.FromCoreMaps(
                    species.LogMaps,
                    species.LogArchivePath);
            }

            if ((uint)species.LeavesOrTopSlot < (uint)slots.Length)
            {
                var leafMaps = species.IsCactus
                    ? species.LeavesOrTopMaps
                    : ApplyFoliageColormapTintIfNeeded(
                        species.LeavesOrTopMaps,
                        species.LeavesOrTopArchivePath);
                slots[species.LeavesOrTopSlot] = PreviewMaterialMapper.FromCoreMaps(
                    leafMaps,
                    species.LeavesOrTopArchivePath);
            }

            if (species is { LogTopSlot: { } topSlot, LogTopMaps: { } logTopMaps } &&
                (uint)topSlot < (uint)slots.Length)
            {
                slots[topSlot] = PreviewMaterialMapper.FromCoreMaps(
                    logTopMaps,
                    species.LogTopArchivePath);
            }
        }

        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] ??= alias;
        }
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

        return kit is { OverlayArchivePath: not null and var overlayPath } &&
               PreviewGrassColormapTint.NeedsGrassColormapTint(overlayPath);
    }
}
