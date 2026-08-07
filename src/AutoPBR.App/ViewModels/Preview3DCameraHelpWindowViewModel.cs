using AutoPBR.App.Lang;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoPBR.App.ViewModels;

public sealed partial class Preview3DCameraHelpWindowViewModel : ViewModelBase, IThemedWindowAppearance
{
    private readonly MainWindowViewModel _main;

    public Preview3DCameraHelpWindowViewModel(MainWindowViewModel main)
    {
        _main = main;
        _main.PropertyChanged += MainOnPropertyChanged;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Avalonia binding ({Binding HeaderTitle}) requires an instance member.")]
    public string HeaderTitle => LocalizedStrings.Preview3DCameraSection;

    public IBrush WindowBackground => _main.WindowBackground;
    public IBrush CardBackground => _main.CardBackground;
    public IBrush CardBorderBrush => _main.CardBorderBrush;
    public IBrush ForegroundBrush => _main.ForegroundBrush;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Avalonia bindings require instance members.")]
    public string OrbitPanSection => LocalizedStrings.Preview3DCameraHelpOrbitPanSection;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Avalonia bindings require instance members.")]
    public string FlySection => LocalizedStrings.Preview3DCameraHelpFlySection;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Avalonia bindings require instance members.")]
    public string FramingSection => LocalizedStrings.Preview3DCameraHelpFramingSection;

    public IReadOnlyList<Preview3DCameraHelpItem> OrbitPanItems { get; } =
    [
        new(LocalizedStrings.Preview3DCameraHelpItemOrbitInput, LocalizedStrings.Preview3DCameraHelpItemOrbitDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemPanInput, LocalizedStrings.Preview3DCameraHelpItemPanDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemZoomInput, LocalizedStrings.Preview3DCameraHelpItemZoomDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemHoldZoomInput, LocalizedStrings.Preview3DCameraHelpItemHoldZoomDesc),
    ];

    public IReadOnlyList<Preview3DCameraHelpItem> FlyItems { get; } =
    [
        new(LocalizedStrings.Preview3DCameraHelpItemFlyLookInput, LocalizedStrings.Preview3DCameraHelpItemFlyLookDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemFlyWasdInput, LocalizedStrings.Preview3DCameraHelpItemFlyWasdDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemFlyQeInput, LocalizedStrings.Preview3DCameraHelpItemFlyQeDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemFlyShiftInput, LocalizedStrings.Preview3DCameraHelpItemFlyShiftDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemFlyCtrlInput, LocalizedStrings.Preview3DCameraHelpItemFlyCtrlDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemFlyWheelInput, LocalizedStrings.Preview3DCameraHelpItemFlyWheelDesc),
    ];

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1863:Cache a 'CompositeFormat' for repeated use in this formatting operation",
        Justification = "Format string is localized and may change when culture changes.")]
    public string ResetKeyInput => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        LocalizedStrings.Preview3DCameraResetKeyFormat,
        _main.Preview3DCameraResetKey);

    public IReadOnlyList<Preview3DCameraHelpItem> FramingItems =>
    [
        new(LocalizedStrings.Preview3DCameraHelpItemFrameInput, LocalizedStrings.Preview3DCameraHelpItemFrameDesc),
        new(ResetKeyInput, LocalizedStrings.Preview3DCameraHelpItemResetDesc),
        new(LocalizedStrings.Preview3DCameraHelpItemFocusInput, LocalizedStrings.Preview3DCameraHelpItemFocusDesc),
    ];

    public void Detach() => _main.PropertyChanged -= MainOnPropertyChanged;

    private void MainOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.WindowBackground):
                OnPropertyChanged(nameof(WindowBackground));
                break;
            case nameof(MainWindowViewModel.CardBackground):
                OnPropertyChanged(nameof(CardBackground));
                break;
            case nameof(MainWindowViewModel.CardBorderBrush):
                OnPropertyChanged(nameof(CardBorderBrush));
                break;
            case nameof(MainWindowViewModel.ForegroundBrush):
                OnPropertyChanged(nameof(ForegroundBrush));
                break;
            case nameof(MainWindowViewModel.Strings):
                OnPropertyChanged(nameof(OrbitPanSection));
                OnPropertyChanged(nameof(FlySection));
                OnPropertyChanged(nameof(FramingSection));
                OnPropertyChanged(nameof(OrbitPanItems));
                OnPropertyChanged(nameof(FlyItems));
                OnPropertyChanged(nameof(ResetKeyInput));
                OnPropertyChanged(nameof(FramingItems));
                break;
            case nameof(MainWindowViewModel.Preview3DCameraResetKey):
                OnPropertyChanged(nameof(ResetKeyInput));
                OnPropertyChanged(nameof(FramingItems));
                break;
        }
    }
}
