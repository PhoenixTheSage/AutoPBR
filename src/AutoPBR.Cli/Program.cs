using AutoPBR.Core;
using AutoPBR.Core.Models;

static int Usage()
{
    Console.WriteLine(
        """
        AutoPBR.Cli

        Usage:
          AutoPBR.Cli <input> <output> [--fast] [--normal <1..3>] [--height <0.01..0.5>] [--normal-operator <sobel|scharr>] [--normal-kernel <3|5|7>] [--normal-derivative <luminance|color|blend|max>] [--ignore-plants]

        Input: .zip (resource pack) or .jar (Minecraft; opened as zip). Output: always .zip (PBR layer only).

        Notes:
          - Specular lookup data is loaded from: <app>/Data/textures_data.json
        """
    );
    return 2;
}

if (args.Length < 2)
    return Usage();

var input = args[0];
var output = args[1];

var fast = args.Any(a => a.Equals("--fast", StringComparison.OrdinalIgnoreCase));
float normal = AutoPbrDefaults.DefaultNormalIntensity;
float height = AutoPbrDefaults.DefaultHeightIntensity;
var normalOperator = NormalOperator.SobelVc;
var normalKernelSize = NormalKernelSize.K3;
var normalDerivative = NormalDerivative.Luminance;
var ignorePlants = args.Any(a => a.Equals("--ignore-plants", StringComparison.OrdinalIgnoreCase));

for (var i = 2; i < args.Length; i++)
{
    if (args[i].Equals("--normal", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
        float.TryParse(args[i + 1], out var n))
        normal = n;
    if (args[i].Equals("--height", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
        float.TryParse(args[i + 1], out var h))
        height = h;
    if (args[i].Equals("--normal-operator", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        var val = args[i + 1];
        if (val.Equals("sobel", StringComparison.OrdinalIgnoreCase))
            normalOperator = NormalOperator.SobelVc;
        else if (val.Equals("scharr", StringComparison.OrdinalIgnoreCase))
            normalOperator = NormalOperator.ScharrVc;
        else
        {
            Console.Error.WriteLine("Invalid value for --normal-operator. Expected 'sobel' or 'scharr'.");
            return 2;
        }
    }

    if (args[i].Equals("--normal-kernel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        var val = args[i + 1];
        if (val is "3" or "3x3")
            normalKernelSize = NormalKernelSize.K3;
        else if (val is "5" or "5x5")
            normalKernelSize = NormalKernelSize.K5;
        else if (val is "7" or "7x7")
            normalKernelSize = NormalKernelSize.K7;
        else
        {
            Console.Error.WriteLine("Invalid value for --normal-kernel. Expected 3, 5, or 7.");
            return 2;
        }

        if (normalOperator == NormalOperator.ScharrVc && normalKernelSize == NormalKernelSize.K7)
        {
            Console.Error.WriteLine("Scharr supports kernel sizes 3 or 5 only (7x7 is invalid).");
            return 2;
        }
    }

    if (args[i].Equals("--normal-derivative", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        var val = args[i + 1];
        if (val.Equals("luminance", StringComparison.OrdinalIgnoreCase))
            normalDerivative = NormalDerivative.Luminance;
        else if (val.Equals("color", StringComparison.OrdinalIgnoreCase))
            normalDerivative = NormalDerivative.Color;
        else if (val.Equals("blend", StringComparison.OrdinalIgnoreCase))
            normalDerivative = NormalDerivative.ColorLuminanceBlend;
        else if (val.Equals("max", StringComparison.OrdinalIgnoreCase))
            normalDerivative = NormalDerivative.ColorLuminanceMax;
        else
        {
            Console.Error.WriteLine(
                "Invalid value for --normal-derivative. Expected 'luminance', 'color', 'blend', or 'max'.");
            return 2;
        }
    }
}

var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
if (!File.Exists(dataPath))
{
    Console.Error.WriteLine($"Missing specular data file: {dataPath}");
    return 1;
}

var options = new AutoPbrOptions
{
    FastSpecular = fast,
    NormalIntensity = normal,
    HeightIntensity = height,
    NormalOperator = normalOperator,
    NormalKernelSize = normalKernelSize,
    NormalDerivative = normalDerivative,
    SpecularData = SpecularData.LoadFromFile(dataPath),
    FoliageMode = ignorePlants ? "Ignore All" : "Convert All",
    IgnoreTextureKeys = ignorePlants
        ? new HashSet<string>(AutoPbrDefaults.PlantTextureKeys, StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
};

var converter = new ResourcePackConverter();
var prog = new Progress<ConversionProgress>(p =>
{
    if (p.Stage is ConversionStage.Extracting or ConversionStage.Packing or ConversionStage.Done)
        Console.WriteLine(p.Stage);
    else
        Console.WriteLine($"{p.Stage} {p.Completed}/{p.Total} {p.CurrentTextureName}");
});

try
{
    await converter.ConvertAsync(input, output, options, prog);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
