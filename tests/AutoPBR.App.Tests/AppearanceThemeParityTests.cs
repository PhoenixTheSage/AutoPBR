using AutoPBR.App.Services;

using Avalonia.Media;

namespace AutoPBR.App.Tests;

public sealed class AppearanceThemeParityTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Blue")]
    [InlineData("Green")]
    [InlineData("Purple")]
    [InlineData("Amber")]
    [InlineData("Teal")]
    [InlineData("Rose")]
    [InlineData("Mono")]
    [InlineData("Ocean")]
    [InlineData("Sunset")]
    public void ColorSchemes_UseLightForegroundOnDarkWindow(string scheme)
    {
        var palette = AppearanceService.GetPalette(scheme);
        Assert.True(IsDarkBrush(palette.WindowBackground));
        Assert.True(IsLightBrush(palette.ForegroundBrush));
    }

    private static bool IsDarkBrush(IBrush brush) =>
        brush is ISolidColorBrush solid && RelativeLuminance(solid.Color) < 0.35;

    private static bool IsLightBrush(IBrush brush) =>
        brush is ISolidColorBrush solid && RelativeLuminance(solid.Color) > 0.65;

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
}
