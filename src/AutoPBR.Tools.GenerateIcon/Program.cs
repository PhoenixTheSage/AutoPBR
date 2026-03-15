// Generates multi-resolution AutoPBR-logo.ico from AutoPBR-logo.png.
// Run from repo root: dotnet run --project src/AutoPBR.Tools.GenerateIcon
// Or: dotnet run --project src/AutoPBR.Tools.GenerateIcon -- <path-to-png> [path-to-ico]

using AutoPBR.Tools.GenerateIcon;

var baseDir = AppContext.BaseDirectory;
var pngPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(baseDir, "..", "..", "..", "AutoPBR.App", "Assets", "AutoPBR-logo.png");
var icoPath = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(Path.GetDirectoryName(pngPath)!, "AutoPBR-logo.ico");

try
{
    MultiResolutionIcoGenerator.Generate(pngPath, icoPath);
    Console.WriteLine($"Written: {icoPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
