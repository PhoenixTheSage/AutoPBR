using Avalonia.Controls;

namespace AutoPBR.App.Views;

/// <summary>Applies OS-native window decorations on Linux; Windows keeps custom chrome.</summary>
internal static class PlatformWindowChrome
{
    public static void ApplyLinuxNativeDecorations(
        Window window,
        Control? customTitleBar = null,
        params Control?[] resizeGrips)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        window.SystemDecorations = SystemDecorations.Full;
        window.ExtendClientAreaToDecorationsHint = false;
        window.TransparencyLevelHint = [];

        if (customTitleBar is not null)
        {
            customTitleBar.IsVisible = false;
        }

        foreach (var grip in resizeGrips)
        {
            if (grip is not null)
            {
                grip.IsVisible = false;
            }
        }
    }
}
