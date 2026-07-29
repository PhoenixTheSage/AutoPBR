using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudCq2AcceptanceTests
{
    private const string EnableAcceptanceEnv = "AUTOPBR_RUN_CQ2_ACCEPTANCE";
    private const string ArtifactDirectoryEnv = "AUTOPBR_CQ2_ACCEPTANCE_ARTIFACT_DIR";
    private const int Width = 1920;
    private const int Height = 1080;
    private const int WarmupFrames = 32;
    private const int RequiredSamples = 240;
    private const double AcceptedCq1HighTraceP50Ms = 0.757;
    private const double HighTraceRegressionLimit = 1.20;
    private const double MaximumHighTraceP50Ms =
        AcceptedCq1HighTraceP50Ms * HighTraceRegressionLimit;
    private static readonly TimeSpan FrameElapsed = TimeSpan.FromSeconds(1.0 / 60.0);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] HeightTransitionFixtureNames =
        ["below-cumulus", "inside-cumulus", "above-cumulus"];
    private static readonly int[] CirrusComparisonQualities =
        [PreviewVolumetricQuality.High, PreviewVolumetricQuality.Cinematic];
    private static readonly string[] WeatherClassNames =
        ["fair-weather", "broken", "congested", "overcast"];
    private static readonly int[] TranslationSequenceIndices = [0, 1, 2];

    [Fact]
    public void HiddenWglContext_CapturesCq2DensityVisualAndPerformanceMatrix()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAcceptanceEnv),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(OperatingSystem.IsWindows(), "CQ2 live acceptance requires Windows WGL.");
        var diagnostics = new List<string>();
        Cq2AcceptanceRun? run;
        using (var context = PreviewDesktopWglContext.TryCreate(
                   [
                       new GlVersion(GlProfileType.OpenGL, 4, 6),
                       new GlVersion(GlProfileType.OpenGL, 4, 0),
                       new GlVersion(GlProfileType.OpenGL, 3, 3),
                   ],
                   IntPtr.Zero,
                   diagnostics.Add,
                   probePresentationAdapter: false))
        {
            Assert.NotNull(context);
            run = context!.Invoke(
                () =>
                {
                    using (context.BindOnOwnerThread())
                    {
                        return RunAcceptance(context, diagnostics);
                    }
                },
                TimeSpan.FromMinutes(6));
        }

        Assert.NotNull(run);
        var artifactDirectory = ResolveArtifactDirectory();
        if (artifactDirectory is not null)
        {
            WriteArtifacts(run!, diagnostics, artifactDirectory);
        }

        Assert.Equal(AllFixtures.Length, run!.Cases.Count);
        Assert.Equal(AllFixtures.Length, run.Captures.Count);
        Assert.All(run.Cases, item => Assert.Equal(RequiredSamples, item.SampleCount));
        Assert.All(run.Cases, item => Assert.Equal(PreviewCloudDebugView.Off, item.DebugView));
        Assert.All(run.Captures, capture =>
        {
            Assert.True(capture.Stats.SampledColorCount > 32, $"{capture.Name} is unexpectedly flat.");
            Assert.True(capture.Stats.LumaRange > 0.03, $"{capture.Name} has insufficient luminance range.");
        });

        AssertFixtureCoverage(run);
        AssertPerformanceGate(run);
        AssertTranslationSequence(run);
        Assert.DoesNotContain(
            diagnostics,
            line => line.Contains("Detailed clouds are disabled", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Cloud render-state recovery failure", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("shader: link failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("density asset upload failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            diagnostics,
            line => line.Contains(
                        "densityAssets=cq2-v2/v2-bundled/cq2-density-v2;upload-valid",
                        StringComparison.Ordinal) &&
                    line.Contains("densitySemantics=v2", StringComparison.Ordinal));
    }

    private static void AssertFixtureCoverage(Cq2AcceptanceRun run)
    {
        Assert.All(
            HeightTransitionFixtureNames,
            fixture => Assert.Contains(run.Cases, item => item.Fixture == fixture));
        Assert.Contains(run.Cases, item => item.Fixture == "thin-upper-billows");
        Assert.Contains(run.Cases, item => item.Fixture == "long-horizon");
        Assert.Equal(
            3,
            run.Cases.Count(item => item.Category == FixtureCategory.MipTranslation));
        Assert.All(
            CirrusComparisonQualities,
            quality => Assert.Contains(
                run.Cases,
                item => item.Category == FixtureCategory.CirrusComparison &&
                        item.Quality == quality));
        Assert.All(
            WeatherClassNames,
            weather => Assert.Contains(run.Cases, item => item.WeatherClass == weather));
    }

    private static void AssertPerformanceGate(Cq2AcceptanceRun run)
    {
        var high = Assert.Single(
            run.Cases,
            item => item.Fixture == "dense-overcast" &&
                    item.Quality == PreviewVolumetricQuality.High);
        Assert.True(
            high.CloudTrace.P50 <= MaximumHighTraceP50Ms,
            string.Create(
                CultureInfo.InvariantCulture,
                $"CQ2 High cloud-trace p50 {high.CloudTrace.P50:0.###} ms exceeds " +
                $"{HighTraceRegressionLimit:0.00}x the accepted CQ1 density-stage proxy " +
                $"({AcceptedCq1HighTraceP50Ms:0.###} ms; limit {MaximumHighTraceP50Ms:0.###} ms)."));
    }

    private static void AssertTranslationSequence(Cq2AcceptanceRun run)
    {
        var sequence = run.Captures
            .Where(item => item.Category == FixtureCategory.MipTranslation)
            .OrderBy(item => item.SequenceIndex)
            .ToArray();
        Assert.Equal(3, sequence.Length);
        Assert.Equal(TranslationSequenceIndices, sequence.Select(item => item.SequenceIndex));
        Assert.NotEqual(sequence[0].Stats.Sha256, sequence[1].Stats.Sha256);
        Assert.NotEqual(sequence[1].Stats.Sha256, sequence[2].Stats.Sha256);

        static void AssertBoundedTranslationDelta(double delta)
        {
            Assert.InRange(delta, 0.0001, 0.50);
        }

        AssertBoundedTranslationDelta(
            ComputeMeanAbsoluteRgbDelta(sequence[0].Snapshot, sequence[1].Snapshot));
        AssertBoundedTranslationDelta(
            ComputeMeanAbsoluteRgbDelta(sequence[1].Snapshot, sequence[2].Snapshot));
    }

    private static Cq2AcceptanceRun RunAcceptance(
        PreviewDesktopWglContext context,
        List<string> diagnostics)
    {
        context.EnsureRenderTargetCore(Width, Height);
        var gl = context.Gl;
        using var backend = new OpenGlPreviewBackend();
        backend.SetDiagnosticLog(diagnostics.Add);
        backend.Initialize(new RenderPreviewInitializationOptions());
        backend.SetGroundMaterials(CreateGroundMaterials(), overlayIsCutout: false);

        var initialFixture = AllFixtures[0];
        var settings = CreateSettings(initialFixture);
        backend.SetRenderSettings(settings);
        backend.SetScene(BlockPreviewSceneFactory.Create(settings));
        backend.GlInitNativeWglPresenter(context.GlInterface);
        try
        {
            WarmCloudTier(context, backend, diagnostics);
            var cases = new List<Cq2AcceptanceCase>(AllFixtures.Length);
            var captures = new List<Cq2AcceptanceCapture>(AllFixtures.Length);
            foreach (var fixture in AllFixtures)
            {
                RunCase(context, backend, fixture, cases, captures);
            }

            return new Cq2AcceptanceRun(
                DateTimeOffset.UtcNow,
                context.VersionString,
                ReadGlString(gl, StringName.Vendor),
                ReadGlString(gl, StringName.Renderer),
                Width,
                Height,
                WarmupFrames,
                RequiredSamples,
                AcceptedCq1HighTraceP50Ms,
                HighTraceRegressionLimit,
                MaximumHighTraceP50Ms,
                cases,
                captures);
        }
        finally
        {
            backend.GlDeinit(context.GlInterface);
        }
    }

    private static void WarmCloudTier(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend,
        List<string> diagnostics)
    {
        for (var frame = 0; frame < 600; frame++)
        {
            DrawFrame(context, backend);
            if (frame >= WarmupFrames &&
                diagnostics.Any(line => line.Contains(
                    "Volumetric cloud GPU tier ready",
                    StringComparison.Ordinal)) &&
                backend.TryGetLatestGpuTimingSnapshot(out _, out _))
            {
                return;
            }

            Thread.Sleep(2);
        }

        throw new InvalidOperationException(
            "CQ2 acceptance timed out waiting for the cloud GPU tier and timer queries.");
    }

    private static void RunCase(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend,
        Fixture fixture,
        List<Cq2AcceptanceCase> cases,
        List<Cq2AcceptanceCapture> captures)
    {
        var settings = CreateSettings(fixture);
        Assert.Equal(PreviewCloudDebugView.Off, settings.CloudDebugView);
        backend.SetRenderSettings(settings);
        backend.SetCameraDebugPose(fixture.Eye, fixture.Target);
        var timingMode =
            fixture.Name == "dense-overcast" &&
            fixture.Quality == PreviewVolumetricQuality.High
            ? "asynchronous-cq1-comparable"
            : "serialized-visual-fixture";

        long lastSequence = -1;
        for (var frame = 0; frame < WarmupFrames; frame++)
        {
            DrawFrame(context, backend);
            if (backend.TryGetLatestGpuTimingSnapshot(out _, out var sequence))
            {
                lastSequence = Math.Max(lastSequence, sequence);
            }

            Thread.Sleep(1);
        }

        var samples = new List<GlGpuTimingSnapshot>(RequiredSamples);
        for (var frame = 0; frame < RequiredSamples + 720 && samples.Count < RequiredSamples; frame++)
        {
            DrawFrame(context, backend);
            if (timingMode == "asynchronous-cq1-comparable")
            {
                context.Gl.Flush();
            }
            else
            {
                context.Gl.Finish();
            }

            if (backend.TryGetLatestGpuTimingSnapshot(out var timingSnapshot, out var sequence) &&
                sequence > lastSequence)
            {
                samples.Add(timingSnapshot);
                lastSequence = sequence;
            }

            // Preserve asynchronous pass timing for the gate. Non-gated visual fixtures
            // serialize retirement because terrain-heavy poses can otherwise keep all five
            // timer slots occupied while unprofiled frames continue adding GPU work.
            Thread.Sleep(timingMode == "asynchronous-cq1-comparable" ? 6 : 1);
        }

        Assert.True(
            samples.Count == RequiredSamples,
            $"{fixture.Name} ({PreviewVolumetricQuality.GetName(fixture.Quality)}) retained " +
            $"{samples.Count}/{RequiredSamples} GPU timing samples.");
        context.Gl.Finish();
        var qualityName = PreviewVolumetricQuality.GetName(fixture.Quality).ToLowerInvariant();
        var name = $"{qualityName}-{fixture.Name}";
        var snapshot = ReadFramebufferSnapshot(context.Gl, context.RenderFbo, Width, Height, name);
        var stats = SummarizeCapture(snapshot);
        captures.Add(new Cq2AcceptanceCapture(
            name,
            fixture.Category,
            fixture.SequenceIndex,
            snapshot,
            stats));
        cases.Add(SummarizeCase(name, fixture, timingMode, samples, stats));
    }

    private static void DrawFrame(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend)
    {
        backend.RenderFrame(FrameElapsed);
        backend.GlRenderNativeWglPresenter(Width, Height, context.RenderFbo);
    }

    private static PreviewRenderSettings CreateSettings(Fixture fixture) =>
        new()
        {
            AutoRotate = false,
            AnimateTimeOfDay = false,
            TimeOfDayHours = 6.64f,
            DrawPreviewSubject = false,
            ShowGroundMesh = true,
            ShowBackgroundGrid = false,
            ShowCornerAxes = false,
            ChunkViewDistance = 4,
            LodRingChunks = 4,
            EnableVolumetricClouds = true,
            VolumetricQuality = fixture.Quality,
            CloudDensity = fixture.Density,
            CloudCoverageScale = fixture.Coverage,
            CloudLayerHeight = 4.8f,
            CloudVolumeHeight = 60f,
            CloudVolumeSize = 178f,
            CloudWindSpeed = 1.5f,
            CloudWindHeadingDegrees = 35f,
            CloudCirrusStrength = fixture.Cirrus,
            CloudDebugView = PreviewCloudDebugView.Off,
            CloudFreezeWind = true,
            EnablePreviewTaa = true,
            PreviewTaaMode = 1,
            EnableShadows = false,
            EnableShadowCascades = false,
            ShowExpandedGpuTimingHud = false,
        };

    private static PreviewMaterial[] CreateGroundMaterials()
    {
        var albedo = new byte[]
        {
            60, 118, 45, 255,
            72, 135, 50, 255,
            52, 105, 38, 255,
            66, 125, 44, 255,
        };
        var normal = Enumerable.Repeat(new byte[] { 128, 128, 255, 255 }, 4)
            .SelectMany(item => item)
            .ToArray();
        var specular = Enumerable.Repeat(new byte[] { 0, 110, 0, 255 }, 4)
            .SelectMany(item => item)
            .ToArray();
        var material = new PreviewMaterial
        {
            Width = 2,
            Height = 2,
            AlbedoRgba = albedo,
            NormalRgba = normal,
            SpecularRgba = specular,
        };
        var materials = new PreviewMaterial[PreviewTerrainGrassSlots.MaxCount];
        Array.Fill(materials, material);
        return materials;
    }

    private static Cq2AcceptanceCase SummarizeCase(
        string name,
        Fixture fixture,
        string timingMode,
        IReadOnlyList<GlGpuTimingSnapshot> samples,
        CaptureStats capture)
    {
        static TimingSummary Summarize(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return new TimingSummary(
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95));
        }

        var cloudTrace = Summarize(samples.Select(item => item.CloudTraceMs));
        return new Cq2AcceptanceCase(
            name,
            fixture.Quality,
            PreviewVolumetricQuality.GetName(fixture.Quality),
            fixture.Name,
            fixture.Category,
            fixture.WeatherClass,
            fixture.SequenceIndex,
            fixture.Eye,
            fixture.Target,
            fixture.Density,
            fixture.Coverage,
            fixture.Cirrus,
            PreviewCloudDebugView.Off,
            timingMode,
            samples.Count,
            cloudTrace,
            cloudTrace.P50 / AcceptedCq1HighTraceP50Ms,
            Summarize(samples.Select(item => item.CloudTemporalMs)),
            Summarize(samples.Select(item => item.CloudRepairMs)),
            Summarize(samples.Select(item => item.CloudUpsampleMs)),
            Summarize(samples.Select(item =>
                item.CloudTraceMs +
                item.CloudTemporalMs +
                item.CloudRepairMs +
                item.CloudUpsampleMs)),
            Summarize(samples.Select(item => item.TotalMs)),
            capture);
    }

    private static CaptureStats SummarizeCapture(GlPixelSnapshot snapshot)
    {
        var pixels = snapshot.Rgba.Span;
        var hash = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        var sampledColors = new HashSet<uint>();
        var minLuma = 1.0;
        var maxLuma = 0.0;
        const int pixelStride = 97;
        var pixelCount = pixels.Length / 4;
        for (var pixel = 0; pixel < pixelCount; pixel += pixelStride)
        {
            var offset = pixel * 4;
            var red = pixels[offset] / 255.0;
            var green = pixels[offset + 1] / 255.0;
            var blue = pixels[offset + 2] / 255.0;
            var luma = red * 0.2126 + green * 0.7152 + blue * 0.0722;
            minLuma = Math.Min(minLuma, luma);
            maxLuma = Math.Max(maxLuma, luma);
            sampledColors.Add(
                (uint)(pixels[offset] << 24 |
                       pixels[offset + 1] << 16 |
                       pixels[offset + 2] << 8 |
                       pixels[offset + 3]));
        }

        return new CaptureStats(hash, sampledColors.Count, minLuma, maxLuma, maxLuma - minLuma);
    }

    private static double ComputeMeanAbsoluteRgbDelta(
        GlPixelSnapshot first,
        GlPixelSnapshot second)
    {
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        var a = first.Rgba.Span;
        var b = second.Rgba.Span;
        long difference = 0;
        for (var offset = 0; offset < a.Length; offset += 4)
        {
            difference += Math.Abs(a[offset] - b[offset]);
            difference += Math.Abs(a[offset + 1] - b[offset + 1]);
            difference += Math.Abs(a[offset + 2] - b[offset + 2]);
        }

        return difference / (double)(first.Width * first.Height * 3 * 255);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(sorted.Length * percentile) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private static string? ResolveArtifactDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ArtifactDirectoryEnv);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(FindRepositoryRoot(), configured));
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));

    private static void WriteArtifacts(
        Cq2AcceptanceRun run,
        IReadOnlyList<string> diagnostics,
        string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var capture in run.Captures)
        {
            using var image = Image.LoadPixelData<Rgba32>(
                capture.Snapshot.Rgba.Span,
                capture.Snapshot.Width,
                capture.Snapshot.Height);
            image.Save(Path.Combine(directory, capture.Name + ".png"));
        }

        var translationCaptures = run.Captures
            .Where(item => item.Category == FixtureCategory.MipTranslation)
            .OrderBy(item => item.SequenceIndex)
            .ToArray();
        var translationDeltas = translationCaptures
            .Zip(
                translationCaptures.Skip(1),
                (from, to) => new
                {
                    from = from.Name,
                    to = to.Name,
                    meanAbsoluteRgbDelta = ComputeMeanAbsoluteRgbDelta(
                        from.Snapshot,
                        to.Snapshot),
                })
            .ToArray();
        var report = new
        {
            schemaVersion = 1,
            run.TimestampUtc,
            run.VersionString,
            run.Vendor,
            run.Renderer,
            viewport = new { run.Width, run.Height },
            warmupFrames = run.WarmupFrames,
            sampleFrames = run.RequiredSamples,
            sun = new { timeOfDayHours = 6.64, animated = false },
            wind = new { frozen = true, speed = 1.5, headingDegrees = 35.0 },
            densityProfile = "v2-bundled/cq2-density-v2",
            debugView = PreviewCloudDebugView.Off.ToString(),
            performanceGate = new
            {
                metric = "High cloud-trace p50 (conservative density-stage proxy)",
                run.AcceptedCq1HighTraceP50Ms,
                run.HighTraceRegressionLimit,
                run.MaximumHighTraceP50Ms,
            },
            cases = run.Cases,
            translationDeltas,
            diagnostics,
        };
        File.WriteAllText(
            Path.Combine(directory, "cq2-acceptance-report.json"),
            JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllLines(
            Path.Combine(directory, "cq2-acceptance-summary.csv"),
            new[]
            {
                "quality,fixture,category,weather_class,timing_mode,samples,trace_p50_ms,trace_p95_ms," +
                "cq1_trace_p50_ratio,temporal_p50_ms,temporal_p95_ms,repair_p50_ms," +
                "repair_p95_ms,upsample_p50_ms,upsample_p95_ms,cloud_p50_ms,cloud_p95_ms," +
                "total_p50_ms,total_p95_ms,capture_sha256",
            }.Concat(run.Cases.Select(item => string.Join(
                ',',
                item.QualityName,
                item.Fixture,
                item.Category,
                item.WeatherClass,
                item.TimingMode,
                item.SampleCount.ToString(CultureInfo.InvariantCulture),
                Format(item.CloudTrace.P50),
                Format(item.CloudTrace.P95),
                Format(item.Cq1HighTraceP50Ratio),
                Format(item.CloudTemporal.P50),
                Format(item.CloudTemporal.P95),
                Format(item.CloudRepair.P50),
                Format(item.CloudRepair.P95),
                Format(item.CloudUpsample.P50),
                Format(item.CloudUpsample.P95),
                Format(item.CloudTotal.P50),
                Format(item.CloudTotal.P95),
                Format(item.FrameTotal.P50),
                Format(item.FrameTotal.P95),
                item.Capture.Sha256))));
    }

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static unsafe string ReadGlString(GL gl, StringName name)
    {
        var value = gl.GetString(name);
        return value is null ? "(unknown)" : Marshal.PtrToStringUTF8((nint)value) ?? "(unknown)";
    }

    private static unsafe GlPixelSnapshot ReadFramebufferSnapshot(
        GL gl,
        int framebuffer,
        int width,
        int height,
        string name)
    {
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)framebuffer);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        var bottomUp = new byte[checked(width * height * 4)];
        fixed (byte* pixels = bottomUp)
        {
            gl.ReadPixels(
                0,
                0,
                (uint)width,
                (uint)height,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pixels);
        }

        return GlPixelSnapshot.FromBottomUpRgba8(name, width, height, bottomUp);
    }

    private static readonly Fixture[] AllFixtures =
    [
        new(
            "dense-overcast",
            FixtureCategory.Performance,
            "overcast",
            PreviewVolumetricQuality.High,
            new Vector3(-1.68f, 42.78f, 9.67f),
            new Vector3(3.25f, 42.42f, 7.60f),
            0.75f,
            1.63f,
            0.13f),
        new(
            "dense-overcast",
            FixtureCategory.Weather,
            "overcast",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-1.68f, 42.78f, 9.67f),
            new Vector3(3.25f, 42.42f, 7.60f),
            0.75f,
            1.63f,
            0.13f),
        new(
            "below-cumulus",
            FixtureCategory.HeightTransition,
            "fair-weather",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(0f, 2f, -6f),
            new Vector3(0f, 22f, 70f),
            0.50f,
            0.92f,
            0.08f),
        new(
            "inside-cumulus",
            FixtureCategory.HeightTransition,
            "congested",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(256f, 43f, 250f),
            new Vector3(256f, 43f, 330f),
            0.82f,
            1.48f,
            0.08f),
        new(
            "above-cumulus",
            FixtureCategory.HeightTransition,
            "fair-weather",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(0f, 96f, -6f),
            new Vector3(0f, 52f, 68f),
            0.52f,
            0.92f,
            0.12f),
        new(
            "thin-upper-billows",
            FixtureCategory.MaterialStructure,
            "congested",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(96f, 37f, -82f),
            new Vector3(106f, 61f, 10f),
            0.70f,
            1.32f,
            0.05f),
        new(
            "long-horizon",
            FixtureCategory.HorizonTiling,
            "fair-weather",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-320f, 22f, 288f),
            new Vector3(-80f, 24f, 1580f),
            0.55f,
            1.05f,
            0.16f),
        new(
            "mip-translation-00",
            FixtureCategory.MipTranslation,
            "fair-weather",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(40f, 28f, -112f),
            new Vector3(40f, 40f, 120f),
            0.58f,
            1.10f,
            0.10f,
            SequenceIndex: 0),
        new(
            "mip-translation-01",
            FixtureCategory.MipTranslation,
            "fair-weather",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(41.5f, 28f, -110.5f),
            new Vector3(41.5f, 40f, 121.5f),
            0.58f,
            1.10f,
            0.10f,
            SequenceIndex: 1),
        new(
            "mip-translation-02",
            FixtureCategory.MipTranslation,
            "fair-weather",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(43f, 28f, -109f),
            new Vector3(43f, 40f, 123f),
            0.58f,
            1.10f,
            0.10f,
            SequenceIndex: 2),
        new(
            "cirrus-comparison",
            FixtureCategory.CirrusComparison,
            "fair-weather",
            PreviewVolumetricQuality.High,
            new Vector3(-96f, 96f, 64f),
            new Vector3(-40f, 170f, 180f),
            0.12f,
            0.12f,
            1f),
        new(
            "cirrus-comparison",
            FixtureCategory.CirrusComparison,
            "fair-weather",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-96f, 96f, 64f),
            new Vector3(-40f, 170f, 180f),
            0.12f,
            0.12f,
            1f),
        new(
            "sparse-broken",
            FixtureCategory.Weather,
            "broken",
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-420f, 9f, -360f),
            new Vector3(-340f, 42f, -190f),
            0.38f,
            0.62f,
            0.06f),
    ];

    private enum FixtureCategory
    {
        Performance,
        Weather,
        HeightTransition,
        MaterialStructure,
        HorizonTiling,
        MipTranslation,
        CirrusComparison,
    }

    private sealed record Fixture(
        string Name,
        FixtureCategory Category,
        string WeatherClass,
        int Quality,
        Vector3 Eye,
        Vector3 Target,
        float Density,
        float Coverage,
        float Cirrus,
        int SequenceIndex = -1);

    private sealed record TimingSummary(double P50, double P95);

    private sealed record CaptureStats(
        string Sha256,
        int SampledColorCount,
        double MinimumLuma,
        double MaximumLuma,
        double LumaRange);

    private sealed record Cq2AcceptanceCase(
        string Name,
        int Quality,
        string QualityName,
        string Fixture,
        FixtureCategory Category,
        string WeatherClass,
        int SequenceIndex,
        Vector3 Eye,
        Vector3 Target,
        float Density,
        float Coverage,
        float Cirrus,
        PreviewCloudDebugView DebugView,
        string TimingMode,
        int SampleCount,
        TimingSummary CloudTrace,
        double Cq1HighTraceP50Ratio,
        TimingSummary CloudTemporal,
        TimingSummary CloudRepair,
        TimingSummary CloudUpsample,
        TimingSummary CloudTotal,
        TimingSummary FrameTotal,
        CaptureStats Capture);

    private sealed record Cq2AcceptanceCapture(
        string Name,
        FixtureCategory Category,
        int SequenceIndex,
        GlPixelSnapshot Snapshot,
        CaptureStats Stats);

    private sealed record Cq2AcceptanceRun(
        DateTimeOffset TimestampUtc,
        string VersionString,
        string Vendor,
        string Renderer,
        int Width,
        int Height,
        int WarmupFrames,
        int RequiredSamples,
        double AcceptedCq1HighTraceP50Ms,
        double HighTraceRegressionLimit,
        double MaximumHighTraceP50Ms,
        IReadOnlyList<Cq2AcceptanceCase> Cases,
        IReadOnlyList<Cq2AcceptanceCapture> Captures);
}
