using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AutoPBR.App.Views;

/// <summary>Applies OS-native window decorations on Linux; Windows keeps custom chrome.</summary>
internal static class PlatformWindowChrome
{
    public static void ApplyLinuxNativeDecorations(
        Window window,
        Control? customTitleBar = null,
        Border? rootBorder = null,
        params Control?[] resizeGrips)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        window.SystemDecorations = SystemDecorations.Full;
        window.ExtendClientAreaToDecorationsHint = false;
        window.TransparencyLevelHint = [];
        // Transparent window chrome fights WM decorations and can wash out tab/header foregrounds.
        if (window.Background is null or ISolidColorBrush { Color.A: < 255 })
        {
            window.Background = Brushes.Black;
        }

        if (rootBorder is not null)
        {
            rootBorder.CornerRadius = default;
            rootBorder.BorderThickness = default;
        }

        if (customTitleBar is not null)
        {
            customTitleBar.IsVisible = false;
            customTitleBar.IsHitTestVisible = false;
            customTitleBar.Height = 0;
            customTitleBar.Margin = default;
            customTitleBar.Opacity = 0;
        }

        foreach (var grip in resizeGrips)
        {
            if (grip is not null)
            {
                grip.IsVisible = false;
                grip.IsHitTestVisible = false;
            }
        }
    }
}
