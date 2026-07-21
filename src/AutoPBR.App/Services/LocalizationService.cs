using System.Globalization;

using AutoPBR.App.Lang;
using AutoPBR.App.ViewModels;
using AutoPBR.Core.Models;

namespace AutoPBR.App.Services;

/// <summary>Applies culture and builds localized option lists for dropdowns.</summary>
internal static class LocalizationService
{
    /// <summary>Sets thread and Resources culture, then returns a new LocalizedStrings instance.</summary>
    public static LocalizedStrings ApplyCulture(string cultureCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureCode);
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
            Resources.Culture = culture;
        }
        catch
        {
            Resources.Culture = null;
        }

        return new LocalizedStrings();
    }

    public static IReadOnlyList<FoliageModeOption> GetFoliageModeOptions(LocalizedStrings _) =>
    [
        new FoliageModeOption(LocalizedStrings.IgnoreAll, "Ignore All"),
        new FoliageModeOption(LocalizedStrings.NoHeight, "No Height"),
        new FoliageModeOption(LocalizedStrings.ConvertAll, "Convert All")
    ];

    public static IReadOnlyList<FoliageModeOption> GetDeepBumpOverlapOptions(LocalizedStrings _) =>
    [
        new FoliageModeOption(LocalizedStrings.DeepBumpOverlapSmall, "Small"),
        new FoliageModeOption(LocalizedStrings.DeepBumpOverlapMedium, "Medium"),
        new FoliageModeOption(LocalizedStrings.DeepBumpOverlapLarge, "Large")
    ];

    public static IReadOnlyList<FoliageModeOption> GetDeepBumpInputModeOptions(LocalizedStrings _) =>
    [
        new FoliageModeOption(LocalizedStrings.DeepBumpInputModeAuto, nameof(DeepBumpInputMode.Auto)),
        new FoliageModeOption(LocalizedStrings.DeepBumpInputModeGrayscale, nameof(DeepBumpInputMode.Grayscale)),
        new FoliageModeOption(LocalizedStrings.DeepBumpInputModeRgb, nameof(DeepBumpInputMode.Rgb))
    ];

    public static IReadOnlyList<FoliageModeOption> GetNormalOperatorOptions() =>
    [
        new FoliageModeOption("Sobel + VC (default)", nameof(NormalOperator.SobelVc)),
        new FoliageModeOption("Scharr + VC (stronger edges)", nameof(NormalOperator.ScharrVc))
    ];

    public static IReadOnlyList<FoliageModeOption> GetNormalKernelSizeOptions(string normalOperator)
    {
        var isScharr = string.Equals(normalOperator, nameof(NormalOperator.ScharrVc),
            StringComparison.OrdinalIgnoreCase);
        var list = new List<FoliageModeOption>
        {
            new("3x3", "3"),
            new("5x5", "5")
        };
        if (!isScharr)
        {
            list.Add(new FoliageModeOption("7x7", "7"));
        }

        return list;
    }

    public static IReadOnlyList<FoliageModeOption> GetNormalDerivativeOptions() =>
    [
        new FoliageModeOption(Resources.GetString("NormalDerivative_Luminance"),
            nameof(NormalDerivative.Luminance)),
        new FoliageModeOption(Resources.GetString("NormalDerivative_Color"),
            nameof(NormalDerivative.Color)),
        new FoliageModeOption(Resources.GetString("NormalDerivative_ColorLuminanceBlend"),
            nameof(NormalDerivative.ColorLuminanceBlend)),
        new FoliageModeOption(Resources.GetString("NormalDerivative_ColorLuminanceMax"),
            nameof(NormalDerivative.ColorLuminanceMax))
    ];

    public static IReadOnlyList<FoliageModeOption> GetColorSchemeOptions() =>
    [
        new FoliageModeOption(Resources.GetString("ColorSchemeDark"), "Dark"),
        new FoliageModeOption(Resources.GetString("ColorSchemeBlue"), "Blue"),
        new FoliageModeOption(Resources.GetString("ColorSchemeGreen"), "Green"),
        new FoliageModeOption(Resources.GetString("ColorSchemePurple"), "Purple"),
        new FoliageModeOption(Resources.GetString("ColorSchemeAmber"), "Amber"),
        new FoliageModeOption(Resources.GetString("ColorSchemeTeal"), "Teal"),
        new FoliageModeOption(Resources.GetString("ColorSchemeRose"), "Rose"),
        new FoliageModeOption(Resources.GetString("ColorSchemeMono"), "Mono"),
        new FoliageModeOption(Resources.GetString("ColorSchemeOcean"), "Ocean"),
        new FoliageModeOption(Resources.GetString("ColorSchemeSunset"), "Sunset")
    ];

    public static IReadOnlyList<FoliageModeOption> GetPreviewHdrModeOptions() =>
    [
        new FoliageModeOption(Resources.PreviewHdrModeAuto, "Auto"),
        new FoliageModeOption(Resources.PreviewHdrModeSdr, "Sdr")
    ];
}
