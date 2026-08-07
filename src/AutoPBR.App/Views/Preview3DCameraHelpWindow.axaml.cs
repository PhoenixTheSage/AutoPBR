using AutoPBR.App.ViewModels;

using Avalonia.Controls;
using Avalonia.Input;

namespace AutoPBR.App.Views;

public partial class Preview3DCameraHelpWindow : Window
{
    public Preview3DCameraHelpWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PlatformWindowChrome.ApplyLinuxNativeDecorations(
            this,
            this.FindControl<Control>("CustomTitleBar"));
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is Preview3DCameraHelpWindowViewModel vm)
        {
            vm.Detach();
        }
    }

    private void CloseWindow_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
