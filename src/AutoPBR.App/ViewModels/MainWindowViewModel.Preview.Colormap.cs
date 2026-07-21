using AutoPBR.App.Rendering.Scene;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel
{
    private PreviewColormapImage? _previewGrassColormap;
    private CancellationTokenSource? _previewGrassColormapDebounceCts;

    [ObservableProperty] private double _preview3DGrassColormapTemperature = PreviewStageConstants.DefaultGrassColormapTemperature;
    [ObservableProperty] private double _preview3DGrassColormapDownfall = PreviewStageConstants.DefaultGrassColormapDownfall;
    [ObservableProperty] private Bitmap? _preview3DGrassColormapImage;
    [ObservableProperty] private string? _preview3DGrassColormapSampleText;

    public bool IsPreview3DGrassColormapVisible =>
        IsPreview3D &&
        _previewGrassColormap is not null &&
        PreviewNeedsGrassColormapTint();

    private bool PreviewNeedsGrassColormapTint()
    {
        if (_lastPreviewModelSubject?.MaterialArchivePaths is { } paths &&
            PreviewGrassColormapTint.NeedsGrassColormapTint(paths))
        {
            return true;
        }

        return PreviewGrassColormapTint.NeedsGrassColormapTint(PreviewArchivePath) ||
               PreviewGroundNeedsGrassColormapTint();
    }

    /// <summary>Load grass colormap from the scanned pack, install path, or native catalogs and refresh ground tint.</summary>
    private void RefreshPreviewGrassColormapState()
    {
        var diskPack = TryResolveGrassColormapPackZipPath();
        if (!PreviewColormapLoader.TryLoadGrassColormap(diskPack, MinecraftAssetsDirectory, null, out var image) ||
            image is null)
        {
            _previewGrassColormap = null;
            Preview3DGrassColormapImage = null;
            Preview3DGrassColormapSampleText = null;
            OnPropertyChanged(nameof(IsPreview3DGrassColormapVisible));
            PushPreviewGroundMaterialToGpu();
            return;
        }

        _previewGrassColormap = image;
        Preview3DGrassColormapImage = TryBuildColormapBitmap(image);
        UpdateGrassColormapSampleText();
        OnPropertyChanged(nameof(IsPreview3DGrassColormapVisible));
        PushPreviewGroundMaterialToGpu();
    }

    private string? TryResolveGrassColormapPackZipPath()
    {
        if (_exploreController.TryGetDiskPackAndEntryPath(PreviewArchivePath ?? "", out var pack, out _) &&
            !string.IsNullOrWhiteSpace(pack))
        {
            return pack;
        }

        if (HasScannedArchive && !IsBatchScanActive &&
            _exploreController.TryGetDiskPackAndEntryPath(string.Empty, out pack, out _) &&
            !string.IsNullOrWhiteSpace(pack))
        {
            return pack;
        }

        return null;
    }

    private void EnsurePreviewGrassColormapLoaded()
    {
        if (_previewGrassColormap is not null)
        {
            return;
        }

        var diskPack = TryResolveGrassColormapPackZipPath();
        if (!PreviewColormapLoader.TryLoadGrassColormap(diskPack, MinecraftAssetsDirectory, null, out var image) ||
            image is null)
        {
            return;
        }

        _previewGrassColormap = image;
    }

    private void UpdateGrassColormapSampleText()
    {
        if (_previewGrassColormap is null)
        {
            Preview3DGrassColormapSampleText = null;
            return;
        }

        var tint = SamplePreviewGrassTint();
        Preview3DGrassColormapSampleText =
            $"Grass tint RGB {tint.R},{tint.G},{tint.B} · temp {Preview3DGrassColormapTemperature:F2} · rain {Preview3DGrassColormapDownfall:F2}";
    }

    private Rgba32 SamplePreviewGrassTint() =>
        _previewGrassColormap is null
            ? new Rgba32(91, 139, 54, 255)
            : PreviewGrassColormapTint.SampleGrassTint(
                _previewGrassColormap,
                Preview3DGrassColormapTemperature,
                Preview3DGrassColormapDownfall);

    private PreviewTextureMaps ApplyGrassColormapTintIfNeeded(PreviewTextureMaps maps, string? archivePath)
    {
        if (!PreviewGrassColormapTint.NeedsGrassColormapTint(archivePath))
        {
            return maps;
        }

        EnsurePreviewGrassColormapLoaded();
        if (_previewGrassColormap is not null)
        {
            return PreviewGrassColormapTint.WithGrassTint(
                maps,
                archivePath,
                _previewGrassColormap,
                Preview3DGrassColormapTemperature,
                Preview3DGrassColormapDownfall);
        }

        return PreviewGrassColormapTint.WithGrassTint(maps, archivePath, SamplePreviewGrassTint());
    }

    private static Bitmap? TryBuildColormapBitmap(PreviewColormapImage image)
    {
        try
        {
            using var img = Image.LoadPixelData<Rgba32>(image.Rgba, image.Width, image.Height);
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    partial void OnPreview3DGrassColormapTemperatureChanged(double value)
    {
        _ = value;
        UpdateGrassColormapSampleText();
        OnPropertyChanged(nameof(IsPreview3DGrassColormapVisible));
        if (!_loadingSettings)
        {
            SaveSettings();
        }

        ScheduleDebouncedGrassColormapGpuRefresh();
    }

    partial void OnPreview3DGrassColormapDownfallChanged(double value)
    {
        _ = value;
        UpdateGrassColormapSampleText();
        OnPropertyChanged(nameof(IsPreview3DGrassColormapVisible));
        if (!_loadingSettings)
        {
            SaveSettings();
        }

        ScheduleDebouncedGrassColormapGpuRefresh();
    }

    private void ScheduleDebouncedGrassColormapGpuRefresh()
    {
        if (!IsPreview3D)
        {
            return;
        }

        _previewGrassColormapDebounceCts?.Cancel();
        _previewGrassColormapDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewGrassColormapDebounceCts = cts;
        _ = RunDebouncedGrassColormapGpuRefreshAsync(cts);
    }

    private async Task RunDebouncedGrassColormapGpuRefreshAsync(CancellationTokenSource debounceCts)
    {
        try
        {
            await Task.Delay(PreviewStageConstants.GrassColormapTintDebounceMs, debounceCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_previewGrassColormapDebounceCts, debounceCts))
        {
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_previewGrassColormapDebounceCts, debounceCts) || !IsPreview3D)
            {
                return;
            }

            Push3DPreviewStateToGpu();
            PushPreviewGroundMaterialToGpu();
        });
    }
}
