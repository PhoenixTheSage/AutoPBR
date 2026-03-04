using System.IO.Compression;
using Colourful;
using AutoPBR.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AutoPBR.Core;

public sealed class ResourcePackConverter
{
    /// <summary>
    /// Pairs of (texture folder name under assets/minecraft/textures, specular-only).
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

        progress?.Report(new ConversionProgress(ConversionStage.Extracting, 0, 0));

        var tempRoot = Path.Combine(Path.GetTempPath(), "AutoPBR", Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(tempRoot, "pack_unzipped");
        Directory.CreateDirectory(extracted);

        try
        {
            ZipFile.ExtractToDirectory(inputZipPath, extracted);

            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ConversionProgress(ConversionStage.ScanningTextures, 0, 0));
            var textures = ScanTextures(extracted, options);

            cancellationToken.ThrowIfCancellationRequested();

            await GenerateSpecularAsync(textures, options, progress, cancellationToken).ConfigureAwait(false);
            await GenerateNormalsAsync(textures, options, progress, cancellationToken).ConfigureAwait(false);
            await GenerateHeightsAsync(textures, options, progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ConversionProgress(ConversionStage.Packing, 0, 0));

            Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath) ?? ".");
            if (File.Exists(outputZipPath))
                File.Delete(outputZipPath);

            ZipFile.CreateFromDirectory(extracted, outputZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            progress?.Report(new ConversionProgress(ConversionStage.Done, 0, 0));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    public static IReadOnlyList<TextureWorkItem> ScanTextures(string extractedPackRoot, AutoPbrOptions options)
    {
        var texturesRoot = Path.Combine(extractedPackRoot, "assets", "minecraft", "textures");
        var results = new List<TextureWorkItem>();

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
                // Skip files that are already PBR maps (_n normal, _s specular, _e emissive).
                if (name.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("_s", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("_e", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ext = Path.GetExtension(file);
                var directoryPath = Path.GetDirectoryName(file) ?? dir;

                var relativePathNoExt = Path.GetRelativePath(
                    texturesRoot,
                    Path.Combine(directoryPath, name)
                ).Replace('/', '\\');

                if (!relativePathNoExt.StartsWith('\\'))
                    relativePathNoExt = "\\" + relativePathNoExt;

                if (options.IgnoreTextureKeys.Contains(relativePathNoExt))
                    continue;

                results.Add(new TextureWorkItem
                {
                    FullPath = file,
                    DirectoryPath = directoryPath,
                    Name = name,
                    Extension = ext,
                    RelativeKey = relativePathNoExt,
                    SpecularOnly = specularOnly
                });
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

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = textures[i];
                progress?.Report(new ConversionProgress(stage, i + 1, total, t.Name));

                var fast = t.Overrides.FastSpecular ?? options.FastSpecular;
                var rules = t.Overrides.CustomSpecularRules
                            ?? (options.SpecularData!.ByTextureName.TryGetValue(t.Name, out var list) ? list : null);

                using var img = Image.Load<Rgba32>(t.DiffusePath);
                using var cropped = CropToSquare(img, out var size);
                var width = size;
                var height = size;

                // Precompute rule Lab colors for accurate mode.
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

                var useLabPbr = options.ExperimentalSpecular;
                if (useLabPbr)
                {
                    using var outImg = new Image<Rgba32>(width, height);
                    if (!cropped.DangerousTryGetSinglePixelMemory(out var inMem) ||
                        !outImg.DangerousTryGetSinglePixelMemory(out var outMem))
                        throw new InvalidOperationException("Expected contiguous pixel memory.");
                    var inSpan = inMem.Span;
                    var outSpan = outMem.Span;
                    for (var idx = 0; idx < width * height; idx++)
                    {
                        var p = inSpan[idx];
                        var spec = GetSpecularRgba(p, rules, rulesLab, fast, rgbToLab, de2000);
                        outSpan[idx] = new Rgba32(spec.r, spec.g, spec.b, spec.a);
                    }
                    outImg.Save(t.SpecularPath);
                }
                else
                {
                    using var outImg = new Image<Rgb24>(width, height);
                    if (!cropped.DangerousTryGetSinglePixelMemory(out var inMem) ||
                        !outImg.DangerousTryGetSinglePixelMemory(out var outMem))
                        throw new InvalidOperationException("Expected contiguous pixel memory.");
                    var inSpan = inMem.Span;
                    var outSpan = outMem.Span;
                    for (var idx = 0; idx < width * height; idx++)
                    {
                        var p = inSpan[idx];
                        var spec = GetSpecular(p, rules, rulesLab, fast, rgbToLab, de2000);
                        outSpan[idx] = new Rgb24(spec.r, spec.g, spec.b);
                    }
                    outImg.Save(t.SpecularPath);
                }
            }
        }, ct);
    }

    private static (byte r, byte g, byte b) GetSpecular(
        Rgba32 pixel,
        IReadOnlyList<SpecularRule>? rules,
        List<(SpecularRule Rule, LabColor Lab)>? rulesLab,
        bool fast,
        IColorConverter<RGBColor, LabColor> rgbToLab,
        CIEDE2000ColorDifference de2000)
    {
        if (rules is null || rules.Count == 0)
            return (0, 0, 0);

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
            return (bestRule.SpecR, bestRule.SpecG, bestRule.SpecB);
        }

        var pixLab = rgbToLab.Convert(RGBColor.FromRGB8Bit(pr, pg, pb));
        if (rulesLab is null)
            return (0, 0, 0);

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
        return (rule2.SpecR, rule2.SpecG, rule2.SpecB);
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

    private static Task GenerateNormalsAsync(
        IReadOnlyList<TextureWorkItem> textures,
        AutoPbrOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var stage = ConversionStage.GeneratingNormals;
            var total = textures.Count;

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = textures[i];
                if (t.SpecularOnly)
                    continue;
                progress?.Report(new ConversionProgress(stage, i + 1, total, t.Name));

                using var img = Image.Load<Rgba32>(t.DiffusePath);
                using var cropped = CropToSquare(img, out var size);
                var width = size;
                var height = size;

                var normalIntensity = t.Overrides.NormalIntensity ?? options.NormalIntensity;
                var normal = GenerateNormalMap(cropped, width, height, normalIntensity, t.Overrides.InvertNormalRed, t.Overrides.InvertNormalGreen);

                normal.Save(t.NormalPath);
            }
        }, ct);
    }

    private static Task GenerateHeightsAsync(
        IReadOnlyList<TextureWorkItem> textures,
        AutoPbrOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var stage = ConversionStage.GeneratingHeights;
            var total = textures.Count;

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = textures[i];
                if (t.SpecularOnly)
                    continue;
                progress?.Report(new ConversionProgress(stage, i + 1, total, t.Name));

                using var diffuseImg = Image.Load<Rgba32>(t.DiffusePath);
                using var croppedDiffuse = CropToSquare(diffuseImg, out var size);
                var width = size;
                var squareHeight = size;

                var heightIntensity = t.Overrides.HeightIntensity ?? options.HeightIntensity;
                var brightness = t.Overrides.HeightBrightness ?? AutoPbrDefaults.DefaultHeightBrightness;
                var heightMap = GenerateHeightMap(croppedDiffuse, width, squareHeight, heightIntensity, brightness, t.Overrides.InvertHeight);

                // Load the previously saved normal, replace alpha.
                using var normalImg = File.Exists(t.NormalPath)
                    ? Image.Load<Rgba32>(t.NormalPath)
                    : new Image<Rgba32>(width, squareHeight, new Rgba32(128, 128, 255, 255));

                using var croppedNormal = CropToSquare(normalImg, out _);

                // LabPBR: height in normal alpha; linear, 0=25% depth 255=0% depth; min 1 recommended for POM
                croppedNormal.ProcessPixelRows(acc =>
                {
                    for (var y = 0; y < heightMap.Height; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (var x = 0; x < heightMap.Width; x++)
                        {
                            var a = heightMap[x, y];
                            row[x].A = a == 0 ? (byte)1 : a;
                        }
                    }
                });

                croppedNormal.Save(t.NormalPath);
            }
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

        var maxX = gx.Max();
        var maxY = gy.Max();
        var maxValue = MathF.Max(maxX, maxY);
        if (maxValue == 0)
            maxValue = 1;

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
                    var nx = gx[y * width + x] / maxValue;
                    var ny = gy[y * width + x] / maxValue;

                    var len = MathF.Sqrt(nx * nx + ny * ny + z * z);
                    if (len == 0) len = 1;
                    nx /= len;
                    ny /= len;
                    var nz = z / len;

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

