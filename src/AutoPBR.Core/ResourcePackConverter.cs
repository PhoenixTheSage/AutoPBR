using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Colourful;
using AutoPBR.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AutoPBR.Core;

public sealed class ResourcePackConverter
{
    private static int GetEffectiveThreads(int requested)
    {
        var logical = Math.Max(1, Environment.ProcessorCount);
        if (requested <= 0)
            return Math.Max(1, logical - 2);
        return Math.Clamp(requested, 1, logical);
    }

    /// <summary>Set thread name for debugging (e.g. in Visual Studio Threads window). Name can only be set once per thread.</summary>
    private static void SetThreadName(string name)
    {
        try { Thread.CurrentThread.Name ??= name; }
        catch (InvalidOperationException) { /* already set */ }
    }

    private static int GetZipParallelism(AutoPbrOptions options) => GetEffectiveThreads(options.MaxThreads);
    private static int GetConversionParallelism(AutoPbrOptions options) => GetEffectiveThreads(options.MaxThreads);

    /// <summary>LabPBR: G channel &lt;= this value is F0 (dielectric); 230+ = metal.</summary>
    private const byte LabPbrF0CapDielectric = 229;

    /// <summary>Treat texture as metal if name or relative key contains these substrings (case-insensitive). Includes vanilla and common mod metals (Mythic Metals, Create, Tinkers', Thermal, etc.).</summary>
    private static readonly string[] MetalSubstrings =
    [
        "iron", "gold", "copper", "diamond", "netherite", "armor", "helmet",
        "adamantite", "mythril", "quadrillum", "silver", "aquarium", "prometheum", "osmium", "bronze", "steel",
        "durasteel", "hallowed", "celestium", "metallurgium", "palladium", "carmot", "starrite", "platinum",
        "orichalcum", "manganese", "cobalt", "ardite", "manyullyn", "zinc", "brass", "tin", "lead",
        "aluminum", "aluminium", "nickel", "invar", "electrum", "chrome", "titanium", "tungsten",
        "bismuth", "antimony", "cadmium", "iridium", "signalum", "lumium", "enderium", "constantan"
    ];

    private static bool IsMetalTexture(string name, string relativeKey)
    {
        var combined = name + "\0" + relativeKey;
        foreach (var sub in MetalSubstrings)
        {
            if (combined.Contains(sub, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsPathUnderPlantOrPlants(string relativePathNoExt)
    {
        return relativePathNoExt.Contains("\\plant\\", StringComparison.OrdinalIgnoreCase)
            || relativePathNoExt.Contains("\\plants\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for plants that always get no height in "No Height" mode. Grass by name is excluded here; grass gets no height only when it has significant transparency (checked at normal generation).</summary>
    private static bool IsPlantForNoHeight(string relativePathNoExt, string name, string foliageMode)
    {
        if (foliageMode != "No Height") return false;
        return AutoPbrDefaults.PlantTextureKeys.Contains(relativePathNoExt)
            || IsPathUnderPlantOrPlants(relativePathNoExt);
    }

    /// <summary>Same thresholds as Ignore All grass skip: 2D grass sprites have lots of transparency; grass block cubes do not.</summary>
    private static bool HasSignificantTransparency(Image<Rgba32> cropped)
    {
        if (!cropped.DangerousTryGetSinglePixelMemory(out var mem))
            return false;
        var span = mem.Span;
        long sumA = 0;
        int lowAlphaCount = 0;
        var n = span.Length;
        for (var i = 0; i < n; i++)
        {
            var a = span[i].A;
            sumA += a;
            if (a < 128) lowAlphaCount++;
        }
        var meanAlpha = (int)(sumA / n);
        return meanAlpha < 200 || lowAlphaCount > 0.3 * n;
    }

    /// <summary>Precompute per-pixel luminance (0–1) and edge magnitude (0–1) from cropped diffuse for specular heuristics.</summary>
    private static (float[] luminance, float[] edgeMagnitude, float meanLuminance) BuildLuminanceAndEdge(Image<Rgba32> cropped, int width, int height)
    {
        var lum = new float[width * height];
        cropped.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var p = row[x];
                    lum[y * width + x] = (p.R * 0.3f + p.G * 0.6f + p.B * 0.1f) / 255f;
                }
            }
        });
        var sumLum = 0.0;
        for (var i = 0; i < lum.Length; i++)
            sumLum += lum[i];
        var meanLum = (float)(sumLum / lum.Length);

        // VC-Filter: multiple orientation-selective Sobel responses summed to reduce blind zones
        // (see https://kravtsov-development.medium.com/new-high-quality-edge-detector-6757f35a0ee0)
        int[,] kx = { { -1, 0, 1 }, { -2, 0, 2 }, { -1, 0, 1 } };
        int[,] ky = { { -1, -2, -1 }, { 0, 0, 0 }, { 1, 2, 1 } };
        var gx = new float[width * height];
        var gy = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            float sx = 0, sy = 0;
            for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                var rx = Reflect(x + ox, width);
                var ry = Reflect(y + oy, height);
                var v = lum[ry * width + rx];
                sx += v * kx[oy + 1, ox + 1];
                sy += v * ky[oy + 1, ox + 1];
            }
            gx[y * width + x] = sx;
            gy[y * width + x] = sy;
        }

        // Sum absolute response over 12 orientations (0°, 15°, ..., 165°) for isotropic edge strength
        const int VcOrientationCount = 12;
        const float VcAngleStep = MathF.PI / VcOrientationCount;
        var edge = new float[width * height];
        for (var i = 0; i < gx.Length; i++)
        {
            var gxv = gx[i];
            var gyv = gy[i];
            float sum = 0;
            for (var k = 0; k < VcOrientationCount; k++)
            {
                var a = k * VcAngleStep;
                var r = gxv * MathF.Cos(a) + gyv * MathF.Sin(a);
                sum += MathF.Abs(r);
            }
            edge[i] = sum;
        }
        var maxEdge = 0f;
        for (var i = 0; i < edge.Length; i++)
            if (edge[i] > maxEdge) maxEdge = edge[i];
        if (maxEdge > 0f)
        {
            for (var i = 0; i < edge.Length; i++)
                edge[i] = Math.Clamp(edge[i] / maxEdge, 0f, 1f);
        }
        return (lum, edge, meanLum);
    }

    /// <summary>
    /// Pairs of (texture folder name under assets/&lt;namespace&gt;/textures, specular-only).
    /// Specular-only folders (e.g. particle) get only _s; no _n or height.
    /// </summary>
    private static IEnumerable<(string folder, bool specularOnly)> GetEnabledFolders(AutoPbrOptions options)
    {
        if (options.ProcessBlocks)
        {
            yield return ("blocks", false);
            yield return ("block", false);
        }
        if (options.ProcessItems)
        {
            yield return ("items", false);
            yield return ("item", false);
        }
        if (options.ProcessArmor)
            yield return ("entity", false);
        if (options.ProcessParticles)
            yield return ("particle", true);
    }

    /// <summary>Enumerates asset namespaces (e.g. minecraft, optifine, mod ids) under extractedPackRoot/assets.</summary>
    private static IEnumerable<string> GetAssetNamespaces(string extractedPackRoot)
    {
        var assetsDir = Path.Combine(extractedPackRoot, "assets");
        if (!Directory.Exists(assetsDir))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(assetsDir))
            yield return Path.GetFileName(dir);
    }

    public async Task ConvertAsync(
        string inputZipPath,
        string outputZipPath,
        AutoPbrOptions options,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputZipPath))
            throw new FileNotFoundException("Input pack not found.", inputZipPath);

        if (options.SpecularData is null)
            throw new InvalidOperationException("SpecularData is required (load textures_data.json first).");

        var baseTemp = string.IsNullOrWhiteSpace(options.TempDirectory)
            ? Path.GetTempPath()
            : options.TempDirectory;
        var tempRoot = Path.Combine(baseTemp, "AutoPBR", Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(tempRoot, "pack_unzipped");
        Directory.CreateDirectory(extracted);

        try
        {
            await Task.Run(() =>
            {
                if (options.ExperimentalExtractor)
                    ParallelZipReader.ExtractZip(inputZipPath, extracted, progress, ConversionStage.Extracting, cancellationToken);
                else
                    ExtractWithProgress(inputZipPath, extracted, options, progress, cancellationToken);
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ConversionProgress(ConversionStage.ScanningTextures, 0, 0));
            var textures = ScanTextures(extracted, options);

            cancellationToken.ThrowIfCancellationRequested();

            await GenerateSpecularAsync(textures, options, progress, cancellationToken).ConfigureAwait(false);
            await GenerateNormalsAndHeightsAsync(textures, options, progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            WritePackMcmeta(extracted);

            Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath) ?? ".");
            if (File.Exists(outputZipPath))
                File.Delete(outputZipPath);

            await Task.Run(() => CreateWithProgress(extracted, outputZipPath, textures, progress, cancellationToken)).ConfigureAwait(false);
            progress?.Report(new ConversionProgress(ConversionStage.Done, 0, 0));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void ExtractWithProgress(
        string inputZipPath,
        string extracted,
        AutoPbrOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        SetThreadName("AutoPBR.Extract");
        List<string> entryNames;
        using (var archive = ZipFile.OpenRead(inputZipPath))
            entryNames = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName).ToList();

        var total = entryNames.Count;
        progress?.Report(new ConversionProgress(ConversionStage.Extracting, 0, total));

        var completed = 0;
        var lastReported = -1;
        var reportLock = new object();
        void ReportProgress()
        {
            var current = Interlocked.Increment(ref completed);
            lock (reportLock)
            {
                if (current <= total && current > lastReported)
                {
                    lastReported = current;
                    progress?.Report(new ConversionProgress(ConversionStage.Extracting, current, total));
                }
            }
        }

        var degree = Math.Min(GetZipParallelism(options), entryNames.Count);
        if (degree <= 1)
        {
            using var archive = ZipFile.OpenRead(inputZipPath);
            foreach (var fullName in entryNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(fullName);
                if (entry is null) continue;
                var destPath = Path.Combine(extracted, fullName);
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (!string.IsNullOrEmpty(entry.Name))
                    entry.ExtractToFile(destPath, overwrite: true);
                ReportProgress();
            }
            return;
        }

        var partitionSize = (entryNames.Count + degree - 1) / degree;
        var partitions = new List<List<string>>(degree);
        for (var i = 0; i < degree; i++)
        {
            var start = i * partitionSize;
            var count = Math.Min(partitionSize, entryNames.Count - start);
            if (count > 0)
                partitions.Add(entryNames.GetRange(start, count));
        }

        Parallel.ForEach(partitions, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken }, partition =>
        {
            SetThreadName("AutoPBR.Extract");
            using var archive = ZipFile.OpenRead(inputZipPath);
            foreach (var fullName in partition)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(fullName);
                if (entry is null) continue;
                var destPath = Path.Combine(extracted, fullName);
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (!string.IsNullOrEmpty(entry.Name))
                    entry.ExtractToFile(destPath, overwrite: true);
                ReportProgress();
            }
        });
    }

    /// <summary>Writes pack.mcmeta with description "Generated by AutoPBR", preserving pack_format from source if present.</summary>
    private static void WritePackMcmeta(string extracted)
    {
        var path = Path.Combine(extracted, "pack.mcmeta");
        var packFormat = 22; // default for recent versions
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("pack", out var pack) &&
                    pack.TryGetProperty("pack_format", out var pf))
                    packFormat = pf.GetInt32();
            }
            catch { /* keep default */ }
        }
        var mcmeta = new Dictionary<string, object>
        {
            ["pack"] = new Dictionary<string, object>
            {
                ["pack_format"] = packFormat,
                ["description"] = "Generated by AutoPBR"
            }
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(mcmeta, options));
    }

    private static void CreateWithProgress(
        string extracted,
        string outputZipPath,
        IReadOnlyList<TextureWorkItem> textures,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        SetThreadName("AutoPBR.PackMain");
        var files = new List<string>();

        // Pack metadata: pack.png (when present), pack.mcmeta (always written), license (when present)
        var packPng = Path.Combine(extracted, "pack.png");
        if (File.Exists(packPng))
            files.Add(packPng);
        var packMcmeta = Path.Combine(extracted, "pack.mcmeta");
        files.Add(packMcmeta);
        var licensePath = Path.Combine(extracted, "LICENSE");
        if (File.Exists(licensePath))
            files.Add(licensePath);
        else
        {
            var licenseLower = Path.Combine(extracted, "license");
            if (File.Exists(licenseLower))
                files.Add(licenseLower);
        }

        // Only include converted files (normals and specular) in their folder hierarchy
        foreach (var t in textures)
        {
            if (!t.SpecularOnly && File.Exists(t.NormalPath))
                files.Add(t.NormalPath);
            if (File.Exists(t.SpecularPath))
                files.Add(t.SpecularPath);
        }

        ParallelZipWriter.WriteZip(outputZipPath, files, extracted, progress, ConversionStage.Packing, cancellationToken);
    }

    /// <summary>Scans for textures under assets/&lt;namespace&gt;/textures (minecraft, optifine, mod ids, etc.).</summary>
    public static IReadOnlyList<TextureWorkItem> ScanTextures(string extractedPackRoot, AutoPbrOptions options)
    {
        var results = new List<TextureWorkItem>();

        foreach (var namespaceName in GetAssetNamespaces(extractedPackRoot))
        {
            var texturesRoot = Path.Combine(extractedPackRoot, "assets", namespaceName, "textures");
            if (Directory.Exists(texturesRoot))
            {
                foreach (var (folder, specularOnly) in GetEnabledFolders(options))
                {
                    var dir = Path.Combine(texturesRoot, folder);
                    if (!Directory.Exists(dir))
                        continue;

                    foreach (var file in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories))
                    {
                        var fileName = Path.GetFileName(file);
                        if (AutoPbrDefaults.ExcludedFileNames.Contains(fileName))
                            continue;
                        if (fileName.Contains("sapling", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (fileName.Contains("mcmeta", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var name = Path.GetFileNameWithoutExtension(file);
                        if (name.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith("_s", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith("_e", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var ext = Path.GetExtension(file);
                        var directoryPath = Path.GetDirectoryName(file) ?? dir;

                        var relativeToTextures = Path.GetRelativePath(
                            texturesRoot,
                            Path.Combine(directoryPath, name)
                        ).Replace('/', '\\');
                        var relativePathNoExt = "\\" + namespaceName + "\\" + relativeToTextures;

                        if (options.IgnoreTextureKeys.Contains(relativePathNoExt))
                            continue;
                        if (options.FoliageMode == "Ignore All" && IsPathUnderPlantOrPlants(relativePathNoExt))
                            continue;

                        results.Add(new TextureWorkItem
                        {
                            FullPath = file,
                            DirectoryPath = directoryPath,
                            Name = name,
                            Extension = ext,
                            RelativeKey = relativePathNoExt,
                            SpecularOnly = specularOnly,
                            IsPlantForNoHeight = IsPlantForNoHeight(relativePathNoExt, name, options.FoliageMode)
                        });
                    }
                }
            }

            // Also scan OptiFine-style CTM textures: assets/<namespace>/optifine/ctm/**.png
            var ctmRoot = Path.Combine(extractedPackRoot, "assets", namespaceName, "optifine", "ctm");
            if (Directory.Exists(ctmRoot))
            {
                foreach (var file in Directory.EnumerateFiles(ctmRoot, "*.png", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Contains("mcmeta", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("_s", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("_e", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ext = Path.GetExtension(file);
                    var directoryPath = Path.GetDirectoryName(file) ?? ctmRoot;

                    var relativeToNamespace = Path.GetRelativePath(
                        Path.Combine(extractedPackRoot, "assets", namespaceName),
                        Path.Combine(directoryPath, name)
                    ).Replace('/', '\\');
                    var relativePathNoExt = "\\" + namespaceName + "\\" + relativeToNamespace;

                    if (options.IgnoreTextureKeys.Contains(relativePathNoExt))
                        continue;

                    results.Add(new TextureWorkItem
                    {
                        FullPath = file,
                        DirectoryPath = directoryPath,
                        Name = name,
                        Extension = ext,
                        RelativeKey = relativePathNoExt,
                        SpecularOnly = false,
                        IsPlantForNoHeight = false
                    });
                }
            }

            // OptiFine plant/plants: only include when not Ignore All; No Height => no height in normal alpha
            if (options.FoliageMode != "Ignore All")
            {
                foreach (var plantFolder in new[] { "plant", "plants" })
                {
                    var plantRoot = Path.Combine(extractedPackRoot, "assets", namespaceName, "optifine", plantFolder);
                    if (!Directory.Exists(plantRoot))
                        continue;
                    foreach (var file in Directory.EnumerateFiles(plantRoot, "*.png", SearchOption.AllDirectories))
                    {
                        var fileName = Path.GetFileName(file);
                        if (fileName.Contains("mcmeta", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (name.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith("_s", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith("_e", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var ext = Path.GetExtension(file);
                        var directoryPath = Path.GetDirectoryName(file) ?? plantRoot;
                        var relativeToNamespace = Path.GetRelativePath(
                            Path.Combine(extractedPackRoot, "assets", namespaceName),
                            Path.Combine(directoryPath, name)
                        ).Replace('/', '\\');
                        var relativePathNoExt = "\\" + namespaceName + "\\" + relativeToNamespace;
                        if (options.IgnoreTextureKeys.Contains(relativePathNoExt))
                            continue;
                        results.Add(new TextureWorkItem
                        {
                            FullPath = file,
                            DirectoryPath = directoryPath,
                            Name = name,
                            Extension = ext,
                            RelativeKey = relativePathNoExt,
                            SpecularOnly = false,
                            IsPlantForNoHeight = options.FoliageMode == "No Height"
                        });
                    }
                }
            }
        }

        return results;
    }

    private static Task GenerateSpecularAsync(
        IReadOnlyList<TextureWorkItem> textures,
        AutoPbrOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var stage = ConversionStage.GeneratingSpecular;
            var total = textures.Count;

            var rgbToLab = new ConverterBuilder()
                .FromRGB(RGBWorkingSpaces.sRGB)
                .ToLab(Illuminants.D65)
                .Build();

            var de2000 = new CIEDE2000ColorDifference();
            var completed = 0;

            Parallel.ForEach(textures, new ParallelOptions { MaxDegreeOfParallelism = GetConversionParallelism(options), CancellationToken = ct }, t =>
            {
                SetThreadName("AutoPBR.Specular");
                ct.ThrowIfCancellationRequested();
                var fast = t.Overrides.FastSpecular ?? options.FastSpecular;
                var rules = t.Overrides.CustomSpecularRules
                            ?? (options.SpecularData!.ByTextureName.TryGetValue(t.Name, out var list) ? list : null)
                            ?? (options.SpecularData.ByTextureName.TryGetValue("*", out var def) ? def : null);

                using var img = Image.Load<Rgba32>(t.DiffusePath);
                using var cropped = CropToSquare(img, out var size);
                var width = size;
                var height = size;

                // Ignore All: skip grass textures with significant transparency in diffuse
                if (options.FoliageMode == "Ignore All" &&
                    (t.Name.Contains("grass", StringComparison.OrdinalIgnoreCase) || t.RelativeKey.Contains("grass", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!cropped.DangerousTryGetSinglePixelMemory(out var alphaCheckMem))
                        throw new InvalidOperationException("Expected contiguous pixel memory.");
                    var alphaSpan = alphaCheckMem.Span;
                    long sumA = 0;
                    int lowAlphaCount = 0;
                    var pixelCount = width * height;
                    for (var i = 0; i < pixelCount; i++)
                    {
                        var a = alphaSpan[i].A;
                        sumA += a;
                        if (a < 128) lowAlphaCount++;
                    }
                    var meanAlpha = (int)(sumA / pixelCount);
                    if (meanAlpha < 200 || lowAlphaCount > 0.3 * pixelCount)
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(new ConversionProgress(stage, done, total, t.Name));
                        return;
                    }
                }

                List<(SpecularRule Rule, LabColor Lab)>? rulesLab = null;
                if (!fast && rules is not null)
                {
                    rulesLab = new List<(SpecularRule, LabColor)>(rules.Count);
                    foreach (var r in rules)
                    {
                        var rgb = RGBColor.FromRGB8Bit(r.ColorR, r.ColorG, r.ColorB);
                        rulesLab.Add((r, rgbToLab.Convert(rgb)));
                    }
                }

                // LabPBR specular only: _s RGBA (smoothness, F0/metal, porosity/subsurface, emissive)
                var (luminance, edgeMagnitude, meanLuminance) = BuildLuminanceAndEdge(cropped, width, height);
                var isMetal = IsMetalTexture(t.Name, t.RelativeKey);
                var nPixels = width * height;
                var rBuf = new byte[nPixels];
                var gBuf = new byte[nPixels];
                var bBuf = new byte[nPixels];
                var aBuf = new byte[nPixels];
                if (!cropped.DangerousTryGetSinglePixelMemory(out var inMem))
                    throw new InvalidOperationException("Expected contiguous pixel memory.");
                var inSpan = inMem.Span;

                for (var idx = 0; idx < nPixels; idx++)
                {
                    var p = inSpan[idx];
                    var spec = GetSpecularRgba(p, rules, rulesLab, fast, rgbToLab, de2000);
                    var lum = luminance[idx];
                    var edge = edgeMagnitude[idx];

                    int rr = spec.r, gg = spec.g, bb = spec.b;
                    if (isMetal)
                    {
                        rr = (int)Math.Min(255, (int)(spec.r * options.MetallicBoost));
                        gg = 255;
                        bb = 0;
                    }
                    else
                    {
                        gg = Math.Min(spec.g, LabPbrF0CapDielectric);
                        rr = (int)Math.Min(255, (int)(spec.r * options.SmoothnessScale));
                        rr = (int)(rr * (1f - 0.2f * edge));
                        if (lum > 0.92f && meanLuminance < 0.25f)
                            rr = Math.Min(rr, 220);
                        bb = (byte)Math.Clamp(spec.b + options.PorosityBias, 0, 255);
                    }
                    rBuf[idx] = (byte)Math.Clamp(rr, 0, 255);
                    gBuf[idx] = (byte)Math.Clamp(gg, 0, 255);
                    bBuf[idx] = (byte)bb;
                    aBuf[idx] = spec.a;
                }

                // Per-texture R normalization: remap to 10–200 when there is variation
                byte minR = 255, maxR = 0;
                for (var i = 0; i < nPixels; i++)
                {
                    var v = rBuf[i];
                    if (v < minR) minR = v;
                    if (v > maxR) maxR = v;
                }
                if (maxR > minR)
                {
                    for (var i = 0; i < nPixels; i++)
                        rBuf[i] = (byte)Math.Clamp(10 + (rBuf[i] - minR) * 190 / (maxR - minR), 0, 255);
                }

                var hasData = false;
                using (var outImg = new Image<Rgba32>(width, height))
                {
                    outImg.ProcessPixelRows(acc =>
                    {
                        for (var y = 0; y < height; y++)
                        {
                            var row = acc.GetRowSpan(y);
                            for (var x = 0; x < width; x++)
                            {
                                var idx = y * width + x;
                                var rr = rBuf[idx]; var gg = gBuf[idx]; var bb = bBuf[idx]; var aa = aBuf[idx];
                                if (rr != 0 || gg != 0 || bb != 0 || aa != 255) hasData = true;
                                row[x] = new Rgba32(rr, gg, bb, aa);
                            }
                        }
                    });
                    if (hasData)
                        outImg.Save(t.SpecularPath);
                    else if (File.Exists(t.SpecularPath))
                        File.Delete(t.SpecularPath);
                }

                var n = Interlocked.Increment(ref completed);
                progress?.Report(new ConversionProgress(stage, n, total, t.Name));
            });
        }, ct);
    }

    /// <summary>
    /// Returns specular as LabPBR _s RGBA: R=perceptual smoothness, G=F0/metal, B=porosity/subsurface, A=emissive (255=off).
    /// </summary>
    private static (byte r, byte g, byte b, byte a) GetSpecularRgba(
        Rgba32 pixel,
        IReadOnlyList<SpecularRule>? rules,
        List<(SpecularRule Rule, LabColor Lab)>? rulesLab,
        bool fast,
        IColorConverter<RGBColor, LabColor> rgbToLab,
        CIEDE2000ColorDifference de2000)
    {
        if (rules is null || rules.Count == 0)
            return (0, 0, 0, 255); // LabPBR: alpha 255 = no emission

        var pr = pixel.R;
        var pg = pixel.G;
        var pb = pixel.B;

        var bestIdx = -1;
        double best = double.MaxValue;

        if (fast)
        {
            for (var i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                var d = FastDistance(pr, pg, pb, r.ColorR, r.ColorG, r.ColorB);
                if (d < best)
                {
                    best = d;
                    bestIdx = i;
                }
            }
            var bestRule = rules[bestIdx];
            return (bestRule.SpecR, bestRule.SpecG, bestRule.SpecB, bestRule.SpecA);
        }

        var pixLab = rgbToLab.Convert(RGBColor.FromRGB8Bit(pr, pg, pb));
        if (rulesLab is null)
            return (0, 0, 0, 255);

        for (var i = 0; i < rulesLab.Count; i++)
        {
            var d = de2000.ComputeDifference(pixLab, rulesLab[i].Lab);
            if (d < best)
            {
                best = d;
                bestIdx = i;
            }
        }

        var rule2 = rulesLab[bestIdx].Rule;
        return (rule2.SpecR, rule2.SpecG, rule2.SpecB, rule2.SpecA);
    }

    // Matches upstream Python `getFastDistance`.
    private static double FastDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var cR = r1 - r2;
        var cG = g1 - g2;
        var cB = b1 - b2;
        var uR = r1 + r2;
        return cR * cR * (2 + uR / 256.0) + cG * cG * 4 + cB * cB * (2 + (255 - uR) / 256.0);
    }

    private static Task GenerateNormalsAndHeightsAsync(
        IReadOnlyList<TextureWorkItem> textures,
        AutoPbrOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var stage = ConversionStage.GeneratingNormals;
            var toProcess = textures.Where(t => !t.SpecularOnly).ToList();
            var total = toProcess.Count;
            var completed = 0;

            Parallel.ForEach(toProcess, new ParallelOptions { MaxDegreeOfParallelism = GetConversionParallelism(options), CancellationToken = ct }, t =>
            {
                SetThreadName("AutoPBR.Normals");
                ct.ThrowIfCancellationRequested();

                using var diffuseImg = Image.Load<Rgba32>(t.DiffusePath);
                using var croppedDiffuse = CropToSquare(diffuseImg, out var size);
                var width = size;
                var height = size;

                var normalIntensity = t.Overrides.NormalIntensity ?? options.NormalIntensity;
                using var normal = GenerateNormalMap(croppedDiffuse, width, height, normalIntensity, t.Overrides.InvertNormalRed, t.Overrides.InvertNormalGreen);

                var heightIntensity = t.Overrides.HeightIntensity ?? options.HeightIntensity;
                var brightness = t.Overrides.HeightBrightness ?? AutoPbrDefaults.DefaultHeightBrightness;
                var heightMap = GenerateHeightMap(croppedDiffuse, width, height, heightIntensity, brightness, t.Overrides.InvertHeight);

                // No Height mode: skip height for plants (and for grass only when significant transparency, so grass blocks keep height).
                var skipHeightInAlpha = t.IsPlantForNoHeight;
                if (!skipHeightInAlpha && options.FoliageMode == "No Height" &&
                    (t.Name.Contains("grass", StringComparison.OrdinalIgnoreCase) || t.RelativeKey.Contains("grass", StringComparison.OrdinalIgnoreCase)))
                    skipHeightInAlpha = HasSignificantTransparency(croppedDiffuse);

                // Apply height data into the normal map alpha channel only (no dedicated _h file).
                normal.ProcessPixelRows(acc =>
                {
                    for (var y = 0; y < heightMap.Height; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (var x = 0; x < heightMap.Width; x++)
                        {
                            byte a;
                            if (skipHeightInAlpha)
                                a = 255;
                            else
                            {
                                // White = highest (255), black = lowest (0); clamp 0 -> 1 to avoid problematic fully-black alpha
                                var h = heightMap[x, y];
                                a = h == 0 ? (byte)1 : h;
                            }
                            row[x].A = a;
                        }
                    }
                });

                normal.Save(t.NormalPath);

                var n = Interlocked.Increment(ref completed);
                progress?.Report(new ConversionProgress(stage, n, total, t.Name));
            });
        }, ct);
    }

    private static Image<Rgba32> GenerateNormalMap(Image<Rgba32> cropped, int width, int height, float normalIntensity, bool invertR, bool invertG)
    {
        var grey = new float[width * height];
        cropped.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var p = row[x];
                    grey[y * width + x] = p.R * 0.3f + p.G * 0.6f + p.B * 0.1f;
                }
            }
        });

        // Optional: small unsharp mask to enhance form without adding speckle
        var blurred = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            float sum = 0;
            var count = 0;
            for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                var rx = Reflect(x + ox, width);
                var ry = Reflect(y + oy, height);
                sum += grey[ry * width + rx];
                count++;
            }
            blurred[y * width + x] = sum / count;
        }
        const float amount = 0.5f;
        for (var i = 0; i < grey.Length; i++)
        {
            var v = grey[i] + amount * (grey[i] - blurred[i]);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            grey[i] = v;
        }

        var gx = new float[width * height];
        var gy = new float[width * height];

        // Sobel kernels.
        int[,] kx = { { -1, 0, 1 }, { -2, 0, 2 }, { -1, 0, 1 } };
        int[,] ky = { { -1, -2, -1 }, { 0, 0, 0 }, { 1, 2, 1 } };

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            float sx = 0, sy = 0;
            for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                var rx = Reflect(x + ox, width);
                var ry = Reflect(y + oy, height);
                var v = grey[ry * width + rx];
                sx += v * kx[oy + 1, ox + 1];
                sy += v * ky[oy + 1, ox + 1];
            }
            gx[y * width + x] = sx;
            gy[y * width + x] = sy;
        }

        // VC-Filter magnitude: isotropic edge strength (reduces Sobel blind zones) while we keep direction from (gx, gy)
        const int VcOrientationCount = 12;
        const float VcAngleStep = MathF.PI / VcOrientationCount;
        var vcMag = new float[width * height];
        for (var i = 0; i < gx.Length; i++)
        {
            var gxv = gx[i];
            var gyv = gy[i];
            float sum = 0;
            for (var k = 0; k < VcOrientationCount; k++)
            {
                var a = k * VcAngleStep;
                sum += MathF.Abs(gxv * MathF.Cos(a) + gyv * MathF.Sin(a));
            }
            vcMag[i] = sum;
        }

        // Per-pixel gradient magnitude (direction preserved); enhance with VC so blind zones get stronger edges
        var gradMag = new float[width * height];
        for (var i = 0; i < gx.Length; i++)
        {
            var gxv = gx[i];
            var gyv = gy[i];
            gradMag[i] = MathF.Sqrt(gxv * gxv + gyv * gyv);
        }
        var maxGradMag = gradMag.Max();
        var maxVcMag = vcMag.Max();
        const float Eps = 1e-6f;
        if (maxGradMag < Eps) maxGradMag = 1f;
        if (maxVcMag < Eps) maxVcMag = 1f;
        // Scale VC magnitude to same range as gradient magnitude, then take max so we never reduce strength
        var vcScale = maxGradMag / maxVcMag;
        var maxValue = 0f;
        for (var i = 0; i < gradMag.Length; i++)
        {
            var enhanced = MathF.Max(gradMag[i], vcMag[i] * vcScale);
            if (enhanced > maxValue) maxValue = enhanced;
        }
        if (maxValue < Eps) maxValue = 1f;

        var intensity = 1f / normalIntensity;
        var z = intensity;

        var outImg = new Image<Rgba32>(width, height);
        outImg.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var idx = y * width + x;
                    var gxv = gx[idx];
                    var gyv = gy[idx];
                    var mag = gradMag[idx];
                    var enhancedMag = MathF.Max(mag, vcMag[idx] * vcScale);
                    // Direction from (-gx, -gy) unchanged; magnitude enhanced by VC-Filter (retain or boost)
                    var scale = mag >= Eps ? enhancedMag / mag : 0f;
                    var nx = -gxv * scale / maxValue;
                    var ny = -gyv * scale / maxValue;

                    var len = MathF.Sqrt(nx * nx + ny * ny + z * z);
                    if (len == 0) len = 1;
                    nx /= len;
                    ny /= len;

                    // LabPBR: R = normal X, G = normal Y, B = AO (0=100% occlusion, 255=0%); Z reconstructed by shader
                    var r = ToByte(nx);
                    var g = ToByte(ny);
                    var b = (byte)255; // No AO data from diffuse; 255 = 0% occlusion

                    if (invertR) r = (byte)(255 - r);
                    if (invertG) g = (byte)(255 - g);

                    row[x] = new Rgba32(r, g, b, 255);
                }
            }
        });

        return outImg;
    }

    private static byte ToByte(float v)
    {
        var scaled = (v * 0.5f + 0.5f) * 255f;
        return (byte)Math.Clamp((int)MathF.Round(scaled), 0, 255);
    }

    private sealed class HeightMap
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Data { get; init; }
        public byte this[int x, int y]
        {
            get => Data[y * Width + x];
            set => Data[y * Width + x] = value;
        }
    }

    private static HeightMap GenerateHeightMap(Image<Rgba32> cropped, int width, int height, float heightIntensity, float brightness, bool invertHeight)
    {
        var grey = new byte[width * height];
        cropped.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var p = row[x];
                    // Mimic OpenCV imread(..., 0): simple luminance is fine here.
                    var v = (int)MathF.Round(p.R * 0.3f + p.G * 0.6f + p.B * 0.1f);
                    grey[y * width + x] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        });

        var delta = (int)MathF.Round(50f * brightness);
        delta = Math.Clamp(delta, 0, 255);
        var threshold = 255 - delta;

        for (var i = 0; i < grey.Length; i++)
        {
            var v = grey[i];
            if (v < threshold)
            {
                var nv = v + delta;
                grey[i] = (byte)(nv > 255 ? 255 : nv);
            }
        }

        var outData = new byte[grey.Length];
        for (var i = 0; i < grey.Length; i++)
        {
            var normalized = grey[i] / 255.0;
            var mapped = 255.0 * Math.Pow(normalized, heightIntensity);
            outData[i] = (byte)Math.Clamp((int)Math.Round(mapped), 0, 255);
        }

        if (invertHeight)
        {
            var lowest = outData.Min();
            var highest = outData.Max();
            for (var i = 0; i < outData.Length; i++)
                outData[i] = (byte)(highest - outData[i] + lowest);
        }

        return new HeightMap { Width = width, Height = height, Data = outData };
    }

    private static int Reflect(int i, int max)
    {
        if (i < 0) return -i - 1;
        if (i >= max) return max - (i - max) - 1;
        return i;
    }

    private static Image<Rgba32> CropToSquare(Image<Rgba32> img, out int size)
    {
        var s = Math.Min(img.Width, img.Height);
        size = s;
        if (img.Width == s && img.Height == s)
            return img.Clone();

        return img.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(0, 0, s, s)));
    }
}

