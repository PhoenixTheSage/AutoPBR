using AutoPBR.App.ViewModels;

using Avalonia.Controls;
using Avalonia.Input;

namespace AutoPBR.App.Views;

public partial class EntityPreviewDebugWindow : Window
{
    public EntityPreviewDebugWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PlatformWindowChrome.ApplyLinuxNativeDecorations(
            this,
            this.FindControl<Control>("CustomTitleBar"));
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is EntityPreviewDebugWindowViewModel vm)
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
