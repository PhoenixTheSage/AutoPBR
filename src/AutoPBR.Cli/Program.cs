using AutoPBR.Core;
using AutoPBR.Core.Models;

static int Usage()
{
    Console.WriteLine(
        """
        AutoPBR.Cli

        Usage:
          AutoPBR.Cli <input> <output> [--fast] [--normal <1..3>] [--height <0.01..0.5>] [--ignore-plants]

        Input/output: .zip (resource pack) or .jar (Minecraft; opened as zip). JAR in → JAR out (no suffix).

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
var ignorePlants = args.Any(a => a.Equals("--ignore-plants", StringComparison.OrdinalIgnoreCase));

for (var i = 2; i < args.Length; i++)
{
    if (args[i].Equals("--normal", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && float.TryParse(args[i + 1], out var n))
        normal = n;
    if (args[i].Equals("--height", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && float.TryParse(args[i + 1], out var h))
        height = h;
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
    SpecularData = SpecularData.LoadFromFile(dataPath),
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
