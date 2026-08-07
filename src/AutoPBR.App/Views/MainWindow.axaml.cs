using System.ComponentModel;

using AutoPBR.App.Models;
using AutoPBR.App.ViewModels;

using Avalonia;
using Avalonia.Controls;

namespace AutoPBR.App.Views;

public partial class MainWindow : Window
{
    private const double RoundedCornerRadius = 8;
    private Border? _rootBorder;
    private double _lastUiScaleForWindow = 1.0;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        PlatformWindowChrome.ApplyLinuxNativeDecorations(
            this,
            this.FindControl<Control>("CustomTitleBar"),
            this.FindControl<Control>("ResizeGripWest")!,
            this.FindControl<Control>("ResizeGripEast")!,
            this.FindControl<Control>("ResizeGripSouth")!,
            this.FindControl<Control>("ResizeGripNorthWest")!,
            this.FindControl<Control>("ResizeGripNorthEast")!,
            this.FindControl<Control>("ResizeGripSouthWest")!,
            this.FindControl<Control>("ResizeGripSouthEast")!);
        TryEnableWindowsSnap();
        _rootBorder = this.FindControl<Border>("RootBorder");
        RestoreWindowLayout();
        if (DataContext is MainWindowViewModel vmOpen)
        {
            _lastUiScaleForWindow = vmOpen.UiScale;
            vmOpen.PropertyChanged += ViewModel_OnPropertyChanged;
        }

        UpdateCornerRadiusFromCurrentState();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
            {
                UpdateCornerRadiusFromCurrentState();
            }

        };
        Resized += (_, _) => UpdateCornerRadiusFromCurrentState();
        PositionChanged += (_, _) => UpdateCornerRadiusFromCurrentState();
    }

    private void RestoreWindowLayout()
    {
        var state = WindowLayoutState.Load();
        Position = new PixelPoint((int)state.X, (int)state.Y);
        Width = state.Width;
        Height = state.Height;
        if (state.State is >= 0 and <= 2)
        {
            WindowState = (WindowState)state.State;
        }

        var contentGrid = this.FindControl<Grid>("ContentGrid");
        if (contentGrid?.ColumnDefinitions.Count >= 3)
        {
            contentGrid.ColumnDefinitions[2].Width = new GridLength(state.PreviewColumnWidth, GridUnitType.Pixel);
        }

    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vmClose)
        {
            vmClose.PropertyChanged -= ViewModel_OnPropertyChanged;
        }

        var contentGrid = this.FindControl<Grid>("ContentGrid");
        var state = new WindowLayoutState
        {
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
            State = (int)WindowState,
            PreviewColumnWidth = 280
        };
        if (contentGrid?.ColumnDefinitions.Count is >= 3 &&
            contentGrid.ColumnDefinitions[2].Width.IsAbsolute)
        {
            state.PreviewColumnWidth = contentGrid.ColumnDefinitions[2].Width.Value;
        }


        state.Save();
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.UiScale))
        {
            return;
        }

        if (sender is not MainWindowViewModel vm)
        {
            return;
        }

        if (WindowState != WindowState.Normal)
        {
            _lastUiScaleForWindow = vm.UiScale;
            return;
        }

        var newS = vm.UiScale;
        var oldS = _lastUiScaleForWindow;
        if (Math.Abs(newS - oldS) < 1e-9)
        {
            return;
        }

        _lastUiScaleForWindow = newS;
        Width *= newS / oldS;
        Height *= newS / oldS;
    }
}
