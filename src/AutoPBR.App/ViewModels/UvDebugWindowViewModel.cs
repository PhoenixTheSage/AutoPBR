using AutoPBR.App.Lang;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoPBR.App.ViewModels;

public partial class UvDebugWindowViewModel : ViewModelBase, IThemedWindowAppearance
{
    private readonly MainWindowViewModel _main;
    private bool _suppressDebugWrites;

    public UvDebugWindowViewModel(MainWindowViewModel main)
    {
        _main = main;
        _main.PropertyChanged += MainOnPropertyChanged;

        var baseline = PreviewUvBakePolicy.EntityCuboidBaseline;
        _flipU = UvDebugSettings.TryGetFlipUOverride(out var flipU) ? flipU : baseline.FlipU;
        _flipV = UvDebugSettings.TryGetFlipVOverride(out var flipV) ? flipV : baseline.FlipV;
        _offsetUPixels = UvDebugSettings.TryGetOffsetUPixelsOverride(out var offsetU) ? offsetU : 0;
        _offsetVPixels = UvDebugSettings.TryGetOffsetVPixelsOverride(out var offsetV) ? offsetV : 0;
        _globalFaceRotationDegrees = UvDebugSettings.TryGetGlobalFaceRotationDegreesOverride(out var rotation) ? rotation : 0;
        _swapFaceNorthSouth = UvDebugSettings.TryGetSwapFaceNorthSouthOverride(out var swapNs) ? swapNs : baseline.SwapFaceNorthSouth;
        _swapFaceEastWest = UvDebugSettings.TryGetSwapFaceEastWestOverride(out var swapEw) ? swapEw : baseline.SwapFaceEastWest;
        _swapFaceUpDown = UvDebugSettings.TryGetSwapFaceUpDownOverride(out var swapUd) ? swapUd : baseline.SwapFaceUpDown;
        _preserveDirectionalBounds = UvDebugSettings.TryGetPreserveDirectionalBoundsOverride(out var preserve) ? preserve : baseline.PreserveDirectionalBounds;
        _useBottomLeftUvOrigin = UvDebugSettings.TryGetUseBottomLeftUvOriginOverride(out var bottomLeft) ? bottomLeft : baseline.UseBottomLeftUvOrigin;
        _uvCornerOrderMode = UvDebugSettings.TryGetUvCornerOrderModeOverride(out var cornerMode) ? cornerMode : 0;
    }

    public LocalizedStrings Strings => _main.Strings;

    public IBrush WindowBackground => _main.WindowBackground;
    public IBrush CardBackground => _main.CardBackground;
    public IBrush CardBorderBrush => _main.CardBorderBrush;
    public IBrush ForegroundBrush => _main.ForegroundBrush;
    public IBrush AccentBrush => _main.AccentBrush;

    [ObservableProperty] private bool _flipU;
    [ObservableProperty] private bool _flipV;
    [ObservableProperty] private double _offsetUPixels;
    [ObservableProperty] private double _offsetVPixels;
    [ObservableProperty] private int _globalFaceRotationDegrees;
    [ObservableProperty] private bool _swapFaceNorthSouth;
    [ObservableProperty] private bool _swapFaceEastWest;
    [ObservableProperty] private bool _swapFaceUpDown;
    [ObservableProperty] private bool _preserveDirectionalBounds;
    [ObservableProperty] private bool _useBottomLeftUvOrigin;
    [ObservableProperty] private int _uvCornerOrderMode;

    public bool Rotation0
    {
        get => GlobalFaceRotationDegrees == 0;
        set
        {
            if (value)
            {
                GlobalFaceRotationDegrees = 0;
            }
        }
    }

    public bool Rotation90
    {
        get => GlobalFaceRotationDegrees == 90;
        set
        {
            if (value)
            {
                GlobalFaceRotationDegrees = 90;
            }
        }
    }

    public bool Rotation180
    {
        get => GlobalFaceRotationDegrees == 180;
        set
        {
            if (value)
            {
                GlobalFaceRotationDegrees = 180;
            }
        }
    }

    public bool Rotation270
    {
        get => GlobalFaceRotationDegrees == 270;
        set
        {
            if (value)
            {
                GlobalFaceRotationDegrees = 270;
            }
        }
    }

    public bool CornerOrderDefault
    {
        get => UvCornerOrderMode == 0;
        set
        {
            if (value)
            {
                UvCornerOrderMode = 0;
            }
        }
    }

    public bool CornerOrderRotate90
    {
        get => UvCornerOrderMode == 1;
        set
        {
            if (value)
            {
                UvCornerOrderMode = 1;
            }
        }
    }

    public bool CornerOrderRotate180
    {
        get => UvCornerOrderMode == 2;
        set
        {
            if (value)
            {
                UvCornerOrderMode = 2;
            }
        }
    }

    public bool CornerOrderRotate270
    {
        get => UvCornerOrderMode == 3;
        set
        {
            if (value)
            {
                UvCornerOrderMode = 3;
            }
        }
    }

    public bool CornerOrderReverseWinding
    {
        get => UvCornerOrderMode == 4;
        set
        {
            if (value)
            {
                UvCornerOrderMode = 4;
            }
        }
    }

    public void Detach() => _main.PropertyChanged -= MainOnPropertyChanged;

    public void ResetOverridesFromBaseline()
    {
        UvDebugSettings.ResetAllOverrides();
        _suppressDebugWrites = true;
        var baseline = PreviewUvBakePolicy.EntityCuboidBaseline;
        FlipU = baseline.FlipU;
        FlipV = baseline.FlipV;
        OffsetUPixels = 0;
        OffsetVPixels = 0;
        GlobalFaceRotationDegrees = 0;
        SwapFaceNorthSouth = baseline.SwapFaceNorthSouth;
        SwapFaceEastWest = baseline.SwapFaceEastWest;
        SwapFaceUpDown = baseline.SwapFaceUpDown;
        PreserveDirectionalBounds = baseline.PreserveDirectionalBounds;
        UseBottomLeftUvOrigin = baseline.UseBottomLeftUvOrigin;
        UvCornerOrderMode = 0;
        _suppressDebugWrites = false;
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnFlipUChanged(bool value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetFlipU(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnFlipVChanged(bool value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetFlipV(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnOffsetUPixelsChanged(double value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetOffsetUPixels(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnOffsetVPixelsChanged(double value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetOffsetVPixels(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnGlobalFaceRotationDegreesChanged(int value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        var snapped = NormalizeRotation(value);
        if (snapped != value)
        {
            GlobalFaceRotationDegrees = snapped;
            return;
        }

        UvDebugSettings.SetGlobalFaceRotationDegrees(snapped);
        OnPropertyChanged(nameof(Rotation0));
        OnPropertyChanged(nameof(Rotation90));
        OnPropertyChanged(nameof(Rotation180));
        OnPropertyChanged(nameof(Rotation270));
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnSwapFaceNorthSouthChanged(bool value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetSwapFaceNorthSouth(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnSwapFaceEastWestChanged(bool value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetSwapFaceEastWest(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnSwapFaceUpDownChanged(bool value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetSwapFaceUpDown(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnPreserveDirectionalBoundsChanged(bool value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetPreserveDirectionalBounds(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnUseBottomLeftUvOriginChanged(bool value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        UvDebugSettings.SetUseBottomLeftUvOrigin(value);
        _main.TriggerPreviewRefreshForDebug();
    }

    partial void OnUvCornerOrderModeChanged(int value)
    {
        if (_suppressDebugWrites)
        {
            return;
        }

        var clamped = Math.Clamp(value, 0, 4);
        if (clamped != value)
        {
            UvCornerOrderMode = clamped;
            return;
        }

        UvDebugSettings.SetUvCornerOrderMode(clamped);
        OnPropertyChanged(nameof(CornerOrderDefault));
        OnPropertyChanged(nameof(CornerOrderRotate90));
        OnPropertyChanged(nameof(CornerOrderRotate180));
        OnPropertyChanged(nameof(CornerOrderRotate270));
        OnPropertyChanged(nameof(CornerOrderReverseWinding));
        _main.TriggerPreviewRefreshForDebug();
    }

    private void MainOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Strings))
        {
            OnPropertyChanged(nameof(Strings));
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.WindowBackground) or
            nameof(MainWindowViewModel.CardBackground) or
            nameof(MainWindowViewModel.CardBorderBrush) or
            nameof(MainWindowViewModel.ForegroundBrush) or
            nameof(MainWindowViewModel.AccentBrush))
        {
            OnPropertyChanged(nameof(WindowBackground));
            OnPropertyChanged(nameof(CardBackground));
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(ForegroundBrush));
            OnPropertyChanged(nameof(AccentBrush));
        }
    }

    private static int NormalizeRotation(int value)
    {
        var normalized = ((value % 360) + 360) % 360;
        return normalized switch
        {
            < 45 => 0,
            < 135 => 90,
            < 225 => 180,
            < 315 => 270,
            _ => 0,
        };
    }
}

