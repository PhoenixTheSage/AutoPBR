using System.Collections.ObjectModel;

using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel
{
    private PreviewHdrDisplayInfo _lastHdrDisplayInfo = PreviewHdrDisplayInfo.Unsupported;
    private bool _lastHdrNativeWglActive;
    private bool _lastHdrPresentPathFailed;

    [ObservableProperty] private string _previewHdrMode = "Auto";

    [ObservableProperty] private double _previewHdrPaperWhiteNits = PreviewHdrPresentPolicy.DefaultPaperWhiteNits;

    [ObservableProperty] private string _previewHdrStatusText = string.Empty;

    [ObservableProperty] private bool _previewHdrPresentActive;

    [ObservableProperty] private FoliageModeOption? _selectedPreviewHdrModeOption;

    public ObservableCollection<FoliageModeOption> PreviewHdrModeOptions { get; } = new();

    public bool PreviewHdrPaperWhiteControlsEnabled =>
        string.Equals(PreviewHdrMode, "Auto", StringComparison.OrdinalIgnoreCase);

    public bool IsPreviewGlSurfaceVisible => IsPreview3D || (IsPreview2D && PreviewHdrPresentActive);

    public bool IsPreview2DAvaloniaImageVisible => IsPreview2D && !PreviewHdrPresentActive;

    partial void OnPreviewHdrModeChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(PreviewHdrPaperWhiteControlsEnabled));
        SyncSelectedPreviewHdrModeOption();
        if (_loadingSettings)
        {
            return;
        }

        SaveSettings();
        // Allow Auto to retry after a prior present-path fault (e.g. flip/interop glitch).
        _glPreview?.ClearHdrPresentFailureLatch();
        RecomputePreviewHdrDecision();
        Push3DRenderSettingsOnly();
    }

    partial void OnPreviewHdrPaperWhiteNitsChanged(double value)
    {
        var clamped = PreviewHdrPresentPolicy.ClampPaperWhiteNits((float)value);
        if (Math.Abs(clamped - value) > 0.01)
        {
            PreviewHdrPaperWhiteNits = clamped;
            return;
        }

        if (_loadingSettings)
        {
            return;
        }

        SaveSettings();
        RecomputePreviewHdrDecision();
        Push3DRenderSettingsOnly();
    }

    partial void OnPreviewHdrPresentActiveChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsPreviewGlSurfaceVisible));
        OnPropertyChanged(nameof(IsPreview2DAvaloniaImageVisible));
    }

    partial void OnSelectedPreviewHdrModeOptionChanged(FoliageModeOption? value)
    {
        if (value is null || string.Equals(PreviewHdrMode, value.Value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PreviewHdrMode = value.Value;
    }

    private void RefreshPreviewHdrModeOptions()
    {
        PreviewHdrModeOptions.Clear();
        foreach (var o in LocalizationService.GetPreviewHdrModeOptions())
        {
            PreviewHdrModeOptions.Add(o);
        }

        SyncSelectedPreviewHdrModeOption();
    }

    private void SyncSelectedPreviewHdrModeOption()
    {
        SelectedPreviewHdrModeOption =
            PreviewHdrModeOptions.FirstOrDefault(x =>
                string.Equals(x.Value, PreviewHdrMode, StringComparison.OrdinalIgnoreCase))
            ?? PreviewHdrModeOptions.FirstOrDefault();
    }

    internal void UpdatePreviewHdrProbe(
        in PreviewHdrDisplayInfo display,
        bool nativeWglActive,
        bool presentPathFailed = false)
    {
        _lastHdrDisplayInfo = display;
        _lastHdrNativeWglActive = nativeWglActive;
        _lastHdrPresentPathFailed = presentPathFailed;
        RecomputePreviewHdrDecision();
    }

    internal PreviewHdrPresentDecision RecomputePreviewHdrDecision()
    {
        var decision = PreviewHdrPresentPolicy.Resolve(
            PreviewHdrPresentPolicy.ParseMode(PreviewHdrMode),
            _lastHdrDisplayInfo,
            _lastHdrNativeWglActive,
            (float)PreviewHdrPaperWhiteNits,
            _lastHdrPresentPathFailed);
        PreviewHdrPresentActive = decision.HdrPresentActive;
        PreviewHdrStatusText = PreviewHdrStatusFormatter.Format(decision);
        SyncHdr2DPresentPreference();
        return decision;
    }

    private void PushHdr2DCompositeFromPng(byte[]? pngBytes)
    {
        if (_glPreview is null || pngBytes is null || pngBytes.Length == 0)
        {
            _glPreview?.SetHdr2DCompositeRgba(null, 0, 0);
            return;
        }

        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngBytes);
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);
            _glPreview.SetHdr2DCompositeRgba(rgba, image.Width, image.Height);
        }
        catch
        {
            _glPreview.SetHdr2DCompositeRgba(null, 0, 0);
        }
    }
}
