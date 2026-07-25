using AutoPBR.App.Rendering.Scene;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoPBR.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _previewWorldGenDebounceCts;

    [ObservableProperty] private double _preview3DWorldSeed = PreviewStageConstants.TerrainHeightSeed;
    [ObservableProperty] private double _preview3DTerrainBiomeSize = PreviewStageConstants.TerrainDefaultBiomeSize;
    [ObservableProperty] private double _preview3DTerrainAmplification = PreviewStageConstants.TerrainDefaultAmplification;
    [ObservableProperty] private double _preview3DTerrainErosionStrength = PreviewStageConstants.TerrainDefaultErosionStrength;
    [ObservableProperty] private double _preview3DTerrainContinentalness = PreviewStageConstants.TerrainDefaultContinentalness;

    partial void OnPreview3DChunkViewDistanceChanged(double value) =>
        ClampAndDebounceWorldModifier(
            value,
            PreviewStageConstants.TerrainMinChunkViewDistance,
            PreviewStageConstants.TerrainMaxChunkViewDistance,
            rounded: true,
            apply: v => Preview3DChunkViewDistance = v);

    partial void OnPreview3DWorldSeedChanged(double value) =>
        ClampAndDebounceWorldModifier(
            value,
            min: 0,
            max: int.MaxValue,
            rounded: true,
            apply: v => Preview3DWorldSeed = v);

    [RelayCommand]
    private void RandomizePreview3DWorldSeed()
    {
        // Full inclusive range matching the seed NumericUpDown / world-gen clamp.
        Preview3DWorldSeed = Random.Shared.NextInt64(0, int.MaxValue + 1L);
    }

    partial void OnPreview3DTerrainBiomeSizeChanged(double value) =>
        ClampAndDebounceWorldModifier(
            value,
            PreviewStageConstants.TerrainMinBiomeSize,
            PreviewStageConstants.TerrainMaxBiomeSize,
            rounded: false,
            apply: v => Preview3DTerrainBiomeSize = v);

    partial void OnPreview3DTerrainAmplificationChanged(double value) =>
        ClampAndDebounceWorldModifier(
            value,
            PreviewStageConstants.TerrainMinAmplification,
            PreviewStageConstants.TerrainMaxAmplification,
            rounded: false,
            apply: v => Preview3DTerrainAmplification = v);

    partial void OnPreview3DTerrainErosionStrengthChanged(double value) =>
        ClampAndDebounceWorldModifier(
            value,
            PreviewStageConstants.TerrainMinErosionStrength,
            PreviewStageConstants.TerrainMaxErosionStrength,
            rounded: false,
            apply: v => Preview3DTerrainErosionStrength = v);

    partial void OnPreview3DTerrainContinentalnessChanged(double value) =>
        ClampAndDebounceWorldModifier(
            value,
            PreviewStageConstants.TerrainMinContinentalness,
            PreviewStageConstants.TerrainMaxContinentalness,
            rounded: false,
            apply: v => Preview3DTerrainContinentalness = v);

    /// <summary>
    /// Shared clamp + debounce for every World numeric modifier (seed, view distance, gen knobs).
    /// </summary>
    private void ClampAndDebounceWorldModifier(
        double value,
        double min,
        double max,
        bool rounded,
        Action<double> apply)
    {
        var clamped = Math.Clamp(value, min, max);
        if (rounded)
        {
            clamped = Math.Round(clamped);
        }

        if (Math.Abs(clamped - value) > 1e-9)
        {
            apply(clamped);
            return;
        }

        if (_loadingSettings)
        {
            return;
        }

        SaveSettings();
        ScheduleDebouncedWorldGenGpuRefresh();
    }

    private void ScheduleDebouncedWorldGenGpuRefresh()
    {
        if (!IsPreview3D)
        {
            return;
        }

        _previewWorldGenDebounceCts?.Cancel();
        _previewWorldGenDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewWorldGenDebounceCts = cts;
        _ = RunDebouncedWorldGenGpuRefreshAsync(cts);
    }

    private async Task RunDebouncedWorldGenGpuRefreshAsync(CancellationTokenSource debounceCts)
    {
        try
        {
            await Task.Delay(PreviewStageConstants.TerrainWorldGenDebounceMs, debounceCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_previewWorldGenDebounceCts, debounceCts))
        {
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_previewWorldGenDebounceCts, debounceCts) || !IsPreview3D)
            {
                return;
            }

            Push3DRenderSettingsOnly();
        });
    }

    private PreviewTerrainWorldGenSettings BuildTerrainWorldGenSettings() =>
        new PreviewTerrainWorldGenSettings(
            Seed: (int)Math.Clamp(Math.Round(Preview3DWorldSeed), 0, int.MaxValue),
            BiomeSize: (float)Math.Clamp(
                Preview3DTerrainBiomeSize,
                PreviewStageConstants.TerrainMinBiomeSize,
                PreviewStageConstants.TerrainMaxBiomeSize),
            Amplification: (float)Math.Clamp(
                Preview3DTerrainAmplification,
                PreviewStageConstants.TerrainMinAmplification,
                PreviewStageConstants.TerrainMaxAmplification),
            ErosionStrength: (float)Math.Clamp(
                Preview3DTerrainErosionStrength,
                PreviewStageConstants.TerrainMinErosionStrength,
                PreviewStageConstants.TerrainMaxErosionStrength),
            Continentalness: (float)Math.Clamp(
                Preview3DTerrainContinentalness,
                PreviewStageConstants.TerrainMinContinentalness,
                PreviewStageConstants.TerrainMaxContinentalness)).Clamped();
}
