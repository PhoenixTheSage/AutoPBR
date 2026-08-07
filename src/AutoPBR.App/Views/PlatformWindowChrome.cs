using AutoPBR.App.ViewModels;

using Avalonia.Controls;
using Avalonia.Media;

namespace AutoPBR.App.Views;

/// <summary>Applies OS-native window decorations on Linux; Windows keeps custom chrome.</summary>
internal static class PlatformWindowChrome
{
    private static readonly IBrush FallbackWindowBackground =
        new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x18));

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
        SyncLinuxThemeChrome(window);

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

    /// <summary>
    /// Keep the Linux client/header fill + foreground aligned with the active color scheme
    /// (Windows uses transparent window chrome over the themed <c>RootBorder</c>).
    /// </summary>
    public static void SyncLinuxThemeChrome(Window window)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (window.DataContext is IThemedWindowAppearance theme)
        {
            window.Background = theme.WindowBackground;
            window.Foreground = theme.ForegroundBrush;
            return;
        }

        if (window.Background is null or ISolidColorBrush { Color.A: < 255 })
        {
            window.Background = FallbackWindowBackground;
        }
    }
}
