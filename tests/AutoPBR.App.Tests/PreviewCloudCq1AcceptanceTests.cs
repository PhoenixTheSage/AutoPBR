using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudCq1AcceptanceTests
{
    private const string EnableAcceptanceEnv = "AUTOPBR_RUN_CQ1_ACCEPTANCE";
    private const string ArtifactDirectoryEnv = "AUTOPBR_CQ1_ACCEPTANCE_ARTIFACT_DIR";
    private const int Width = 1920;
    private const int Height = 1080;
    private const int WarmupFrames = 32;
    private const int RequiredSamples = 240;
    private static readonly TimeSpan FrameElapsed = TimeSpan.FromSeconds(1.0 / 60.0);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void HiddenWglContext_CapturesCq1PresetAndCameraAcceptanceMatrix()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAcceptanceEnv),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(OperatingSystem.IsWindows(), "CQ1 live acceptance requires Windows WGL.");
        var diagnostics = new List<string>();
        Cq1AcceptanceRun? run;
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
                TimeSpan.FromMinutes(4));
        }

        Assert.NotNull(run);
        Assert.Equal(12, run!.Cases.Count);
        Assert.All(run.Cases, item => Assert.Equal(RequiredSamples, item.SampleCount));
        Assert.All(
            [
                PreviewVolumetricQuality.Low,
                PreviewVolumetricQuality.Medium,
                PreviewVolumetricQuality.High,
                PreviewVolumetricQuality.Cinematic,
            ],
            quality => Assert.Contains(
                run.Cases,
                item => item.Quality == quality && item.Fixture == "dense-overcast"));
        Assert.All(
            RequiredFixtureNames,
            fixture => Assert.Contains(
                run.Cases,
                item => item.Quality == PreviewVolumetricQuality.Cinematic &&
                        item.Fixture == fixture));
        Assert.DoesNotContain(
            diagnostics,
            line => line.Contains("Detailed clouds are disabled", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Cloud render-state recovery failure", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("shader: link failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            diagnostics,
            line => line.Contains("stbn=asset-v1", StringComparison.Ordinal) &&
                    line.Contains("stbnActive=True", StringComparison.Ordinal));

        var artifactDirectory = ResolveArtifactDirectory();
        if (artifactDirectory is not null)
        {
            WriteArtifacts(run, diagnostics, artifactDirectory);
        }
    }

    private static readonly string[] RequiredFixtureNames =
    [
        "ground-below",
        "inside-cumulus",
        "above-cumulus",
        "grazing-horizon",
        "broken-cumulus",
        "cirrus-heavy",
        "inside-cirrus",
        "above-both-layers",
    ];

    private static Cq1AcceptanceRun RunAcceptance(
        PreviewDesktopWglContext context,
        List<string> diagnostics)
    {
        context.EnsureRenderTargetCore(Width, Height);
        var gl = context.Gl;
        using var backend = new OpenGlPreviewBackend();
        backend.SetDiagnosticLog(diagnostics.Add);
        backend.Initialize(new RenderPreviewInitializationOptions());
        backend.SetGroundMaterials(CreateGroundMaterials(), overlayIsCutout: false);

        var initialFixture = Fixture.DenseOvercast;
        var settings = CreateSettings(PreviewVolumetricQuality.Cinematic, initialFixture);
        backend.SetRenderSettings(settings);
        backend.SetScene(BlockPreviewSceneFactory.Create(settings));
        backend.GlInitNativeWglPresenter(context.GlInterface);
        try
        {
            WarmCloudTier(context, backend, diagnostics);
            var cases = new List<Cq1AcceptanceCase>(12);
            var captures = new List<GlPixelSnapshot>(12);

            foreach (var quality in new[]
                     {
                         PreviewVolumetricQuality.Low,
                         PreviewVolumetricQuality.Medium,
                         PreviewVolumetricQuality.High,
                         PreviewVolumetricQuality.Cinematic,
                     })
            {
                RunCase(context, backend, quality, Fixture.DenseOvercast, cases, captures);
            }

            foreach (var fixture in AllFixtures.Where(item => item.Name != Fixture.DenseOvercast.Name))
            {
                RunCase(
                    context,
                    backend,
                    PreviewVolumetricQuality.Cinematic,
                    fixture,
                    cases,
                    captures);
            }

            return new Cq1AcceptanceRun(
                DateTimeOffset.UtcNow,
                context.VersionString,
                ReadGlString(gl, StringName.Vendor),
                ReadGlString(gl, StringName.Renderer),
                Width,
                Height,
                WarmupFrames,
                RequiredSamples,
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
            "CQ1 acceptance timed out waiting for the cloud GPU tier and timer queries.");
    }

    private static void RunCase(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend,
        int quality,
        Fixture fixture,
        List<Cq1AcceptanceCase> cases,
        List<GlPixelSnapshot> captures)
    {
        var settings = CreateSettings(quality, fixture);
        backend.SetRenderSettings(settings);
        backend.SetCameraDebugPose(fixture.Eye, fixture.Target);

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
        for (var frame = 0; frame < RequiredSamples + 360 && samples.Count < RequiredSamples; frame++)
        {
            DrawFrame(context, backend);
            if (backend.TryGetLatestGpuTimingSnapshot(out var snapshot, out var sequence) &&
                sequence > lastSequence)
            {
                samples.Add(snapshot);
                lastSequence = sequence;
            }

            Thread.Sleep(1);
        }

        Assert.Equal(
            RequiredSamples,
            samples.Count);
        context.Gl.Finish();
        var name =
            $"{PreviewVolumetricQuality.GetName(quality).ToLowerInvariant()}-{fixture.Name}";
        captures.Add(ReadFramebufferSnapshot(context.Gl, context.RenderFbo, Width, Height, name));
        cases.Add(SummarizeCase(name, quality, fixture, samples));
    }

    private static void DrawFrame(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend)
    {
        backend.RenderFrame(FrameElapsed);
        backend.GlRenderNativeWglPresenter(Width, Height, context.RenderFbo);
    }

    private static PreviewRenderSettings CreateSettings(int quality, Fixture fixture) =>
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
            VolumetricQuality = quality,
            CloudDensity = fixture.Density,
            CloudCoverageScale = fixture.Coverage,
            CloudLayerHeight = 4.8f,
            CloudVolumeHeight = 60f,
            CloudVolumeSize = 178f,
            CloudWindSpeed = 1.5f,
            CloudWindHeadingDegrees = 35f,
            CloudCirrusStrength = fixture.Cirrus,
            CloudFreezeWind = true,
            EnablePreviewTaa = true,
            PreviewTaaMode = 1,
            EnableShadows = false,
            EnableShadowCascades = false,
            ShowExpandedGpuTimingHud = true,
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

    private static Cq1AcceptanceCase SummarizeCase(
        string name,
        int quality,
        Fixture fixture,
        IReadOnlyList<GlGpuTimingSnapshot> samples)
    {
        static TimingSummary Summarize(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return new TimingSummary(
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95));
        }

        return new Cq1AcceptanceCase(
            name,
            quality,
            PreviewVolumetricQuality.GetName(quality),
            fixture.Name,
            fixture.Eye,
            fixture.Target,
            fixture.Density,
            fixture.Coverage,
            fixture.Cirrus,
            samples.Count,
            Summarize(samples.Select(item => item.CloudTraceMs)),
            Summarize(samples.Select(item => item.CloudTemporalMs)),
            Summarize(samples.Select(item => item.CloudRepairMs)),
            Summarize(samples.Select(item => item.CloudUpsampleMs)),
            Summarize(samples.Select(item =>
                item.CloudTraceMs +
                item.CloudTemporalMs +
                item.CloudRepairMs +
                item.CloudUpsampleMs)),
            Summarize(samples.Select(item => item.TotalMs)));
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
        Cq1AcceptanceRun run,
        IReadOnlyList<string> diagnostics,
        string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var capture in run.Captures)
        {
            using var image = Image.LoadPixelData<Rgba32>(
                capture.Rgba.Span,
                capture.Width,
                capture.Height);
            image.Save(Path.Combine(directory, capture.Name + ".png"));
        }

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
            cases = run.Cases,
            diagnostics,
        };
        File.WriteAllText(
            Path.Combine(directory, "cq1-acceptance-report.json"),
            JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllLines(
            Path.Combine(directory, "cq1-acceptance-summary.csv"),
            new[]
            {
                "quality,fixture,samples,trace_p50_ms,trace_p95_ms,temporal_p50_ms,temporal_p95_ms," +
                "repair_p50_ms,repair_p95_ms,upsample_p50_ms,upsample_p95_ms,cloud_p50_ms,cloud_p95_ms,total_p50_ms,total_p95_ms",
            }.Concat(run.Cases.Select(item => string.Join(
                ',',
                item.QualityName,
                item.Fixture,
                item.SampleCount.ToString(CultureInfo.InvariantCulture),
                Format(item.CloudTrace.P50),
                Format(item.CloudTrace.P95),
                Format(item.CloudTemporal.P50),
                Format(item.CloudTemporal.P95),
                Format(item.CloudRepair.P50),
                Format(item.CloudRepair.P95),
                Format(item.CloudUpsample.P50),
                Format(item.CloudUpsample.P95),
                Format(item.CloudTotal.P50),
                Format(item.CloudTotal.P95),
                Format(item.FrameTotal.P50),
                Format(item.FrameTotal.P95)))));
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
        new("ground-below", new Vector3(0f, 2f, -6f), new Vector3(0f, 22f, 70f), 0.55f, 1.05f, 0.13f),
        new("inside-cumulus", new Vector3(0f, 43f, -6f), new Vector3(0f, 43f, 74f), 0.65f, 1.25f, 0.13f),
        new("above-cumulus", new Vector3(0f, 96f, -6f), new Vector3(0f, 52f, 68f), 0.55f, 1.05f, 0.2f),
        new("grazing-horizon", new Vector3(0f, 24f, -6f), new Vector3(0f, 24f, 100f), 0.6f, 1.25f, 0.13f),
        new("broken-cumulus", new Vector3(0f, 8f, -6f), new Vector3(0f, 34f, 72f), 0.42f, 0.72f, 0.08f),
        Fixture.DenseOvercast,
        new("cirrus-heavy", new Vector3(0f, 96f, -6f), new Vector3(0f, 170f, 66f), 0.18f, 0.2f, 1f),
        new("inside-cirrus", new Vector3(0f, 174f, -6f), new Vector3(0f, 174f, 74f), 0.18f, 0.2f, 1f),
        new("above-both-layers", new Vector3(0f, 194f, -6f), new Vector3(0f, 150f, 66f), 0.35f, 0.75f, 0.75f),
    ];

    private sealed record Fixture(
        string Name,
        Vector3 Eye,
        Vector3 Target,
        float Density,
        float Coverage,
        float Cirrus)
    {
        public static Fixture DenseOvercast { get; } =
            new(
                "dense-overcast",
                new Vector3(-1.68f, 42.78f, 9.67f),
                new Vector3(3.25f, 42.42f, 7.60f),
                0.75f,
                1.63f,
                0.13f);
    }

    private sealed record TimingSummary(double P50, double P95);

    private sealed record Cq1AcceptanceCase(
        string Name,
        int Quality,
        string QualityName,
        string Fixture,
        Vector3 Eye,
        Vector3 Target,
        float Density,
        float Coverage,
        float Cirrus,
        int SampleCount,
        TimingSummary CloudTrace,
        TimingSummary CloudTemporal,
        TimingSummary CloudRepair,
        TimingSummary CloudUpsample,
        TimingSummary CloudTotal,
        TimingSummary FrameTotal);

    private sealed record Cq1AcceptanceRun(
        DateTimeOffset TimestampUtc,
        string VersionString,
        string Vendor,
        string Renderer,
        int Width,
        int Height,
        int WarmupFrames,
        int RequiredSamples,
        IReadOnlyList<Cq1AcceptanceCase> Cases,
        IReadOnlyList<GlPixelSnapshot> Captures);
}
