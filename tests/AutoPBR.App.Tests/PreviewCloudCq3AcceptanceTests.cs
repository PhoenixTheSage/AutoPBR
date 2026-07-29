using System.Globalization;
using System.Numerics;
using System.Reflection;
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

public sealed class PreviewCloudCq3AcceptanceTests
{
    private const string EnableAcceptanceEnv = "AUTOPBR_RUN_CQ3_ACCEPTANCE";
    private const string ArtifactDirectoryEnv = "AUTOPBR_CQ3_ACCEPTANCE_ARTIFACT_DIR";
    private const string AcceptanceCaseEnv = "AUTOPBR_CQ3_ACCEPTANCE_CASE";
    private const int Width = 1920;
    private const int Height = 1080;
    private const int WarmupFrames = 32;
    private const int RequiredSamples = 240;
    private const double AcceptedCq2HighLightingP50Ms = 0.552;
    private const double HighLightingRegressionLimit = 1.25;
    private const double MaximumHighAmortizedLightingMs =
        AcceptedCq2HighLightingP50Ms * HighLightingRegressionLimit;
    private static readonly TimeSpan FrameElapsed = TimeSpan.FromSeconds(1.0 / 60.0);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void HiddenWglContext_CapturesCq3LightingVisualAndPerformanceMatrix()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAcceptanceEnv),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(OperatingSystem.IsWindows(), "CQ3 live acceptance requires Windows WGL.");
        var fixtures = ResolveFixtures();
        var diagnostics = new List<string>();
        Cq3AcceptanceRun? run;
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
                        return RunAcceptance(context, diagnostics, fixtures);
                    }
                },
                TimeSpan.FromMinutes(8));
        }

        Assert.NotNull(run);
        var artifactDirectory = ResolveArtifactDirectory();
        if (artifactDirectory is not null)
        {
            WriteArtifacts(run!, diagnostics, artifactDirectory);
        }

        Assert.Equal(fixtures.Length, run!.Cases.Count);
        Assert.Equal(fixtures.Length, run.Captures.Count);
        Assert.All(run.Cases, item => Assert.Equal(RequiredSamples, item.SampleCount));
        Assert.All(run.Captures, capture =>
        {
            Assert.True(capture.Stats.SampledColorCount > 32, $"{capture.Name} is unexpectedly flat.");
            Assert.True(capture.Stats.LumaRange > 0.03, $"{capture.Name} has insufficient luminance range.");
        });
        if (fixtures.Length == AllFixtures.Length)
        {
            AssertFixtureCoverage(run);
            AssertCadenceEvidence(run);
        }

        AssertPerformanceGate(run);
        Assert.DoesNotContain(
            diagnostics,
            line => line.Contains("Detailed clouds are disabled", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Cloud render-state recovery failure", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("shader: link failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("cloud-light cache generation failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            diagnostics,
            line => line.Contains("CQ3.6 schedule active", StringComparison.Ordinal) &&
                    line.Contains("activeRuntime=cache-sampling", StringComparison.Ordinal));
        Assert.Contains(
            diagnostics,
            line => line.Contains("CQ3.5 ground transmittance ready", StringComparison.Ordinal) &&
                    line.Contains("terrain-direct+camera-froxel-direct", StringComparison.Ordinal));
    }

    [Fact]
    public void HiddenWglContext_Cq3GeneratorFailuresDemoteWithoutStaleGroundPublication()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAcceptanceEnv),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        const int width = 384;
        const int height = 216;
        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [
                new GlVersion(GlProfileType.OpenGL, 4, 6),
                new GlVersion(GlProfileType.OpenGL, 4, 3),
            ],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);
        context!.Invoke(
            () =>
            {
                using (context.BindOnOwnerThread())
                {
                    context.EnsureRenderTargetCore(width, height);
                    using var backend = new OpenGlPreviewBackend();
                    backend.SetDiagnosticLog(diagnostics.Add);
                    backend.Initialize(new RenderPreviewInitializationOptions());
                    backend.SetGroundMaterials(
                        CreateGroundMaterials(),
                        overlayIsCutout: false);
                    var settings = CreateSettings(AllFixtures[1]);
                    backend.SetRenderSettings(settings);
                    backend.SetScene(BlockPreviewSceneFactory.Create(settings));
                    backend.GlInitNativeWglPresenter(context.GlInterface);
                    try
                    {
                        RenderUntil(
                            context,
                            backend,
                            width,
                            height,
                            () => GetPrivateField<string>(
                                    backend,
                                    "_cloudLightingCacheResourceDiagnostic")
                                .Contains(
                                    "generatedBy=compute-image-store",
                                    StringComparison.Ordinal),
                            "initial compute cache");

                        var cache = GetPrivateField<GlCloudLightFroxelCache>(
                            backend,
                            "_cloudLightCache");
                        var ground = GetPrivateField<GlCloudGroundTransmittanceTarget>(
                            backend,
                            "_cloudGroundTransmittanceTarget");
                        Assert.True(cache.Near.IsGenerated);
                        Assert.True(cache.Far.IsGenerated);
                        Assert.True(ground.IsCurrent(cache));

                        SetPrivateField(
                            backend,
                            "_cloudLightComputeSessionFaulted",
                            true);
                        cache.Near.InvalidateGeneration();
                        cache.Far.InvalidateGeneration();
                        backend.SetRenderSettings(settings);
                        var fragmentFallbackObservations = new HashSet<string>(
                            StringComparer.Ordinal);
                        RenderUntil(
                            context,
                            backend,
                            width,
                            height,
                            () =>
                            {
                                var resourceDiagnostic = GetPrivateField<string>(
                                    backend,
                                    "_cloudLightingCacheResourceDiagnostic");
                                fragmentFallbackObservations.Add(resourceDiagnostic);
                                return resourceDiagnostic.Contains(
                                    "generatedBy=fragment-slices",
                                    StringComparison.Ordinal);
                            },
                            "fragment fallback cache",
                            () =>
                            {
                                var frameSerial = GetPrivateField<int>(
                                    backend,
                                    "_cloudLightFrameSerial");
                                var state =
                                    $"frameSerial={frameSerial};" +
                                    $"nearGenerated={cache.Near.IsGenerated};" +
                                    $"farGenerated={cache.Far.IsGenerated};" +
                                    $"cloudRuntimeFaulted={GetPrivateField<bool>(backend, "_cloudRuntimeFaulted")}";
                                return state + Environment.NewLine +
                                       string.Join(
                                           Environment.NewLine,
                                           fragmentFallbackObservations) +
                                       Environment.NewLine +
                                       string.Join(
                                           Environment.NewLine,
                                           diagnostics.TakeLast(16));
                            });
                        Assert.True(cache.Near.IsGenerated);
                        Assert.True(cache.Far.IsGenerated);
                        Assert.True(ground.IsCurrent(cache));

                        SetPrivateField<GlCloudLightComputeGenerator?>(
                            backend,
                            "_cloudLightComputeGenerator",
                            null);
                        SetPrivateField<GlCloudLightFragmentSliceGenerator?>(
                            backend,
                            "_cloudLightSliceGenerator",
                            null);
                        cache.Near.InvalidateGeneration();
                        cache.Far.InvalidateGeneration();
                        backend.SetRenderSettings(settings);
                        DrawFrame(context, backend, width, height);
                        context.Gl.Finish();

                        var plan = GetPrivateField<PreviewCloudLightingCachePlan>(
                            backend,
                            "_cloudLightingCachePlan");
                        Assert.Equal(
                            PreviewCloudLightingCacheGenerationPath.ShortMarch,
                            plan.ActiveRuntimePath);
                        Assert.False(cache.Near.IsGenerated);
                        Assert.False(cache.Far.IsGenerated);
                        Assert.False(ground.IsCurrent(cache));
                    }
                    finally
                    {
                        backend.GlDeinit(context.GlInterface);
                    }
                }
            },
            TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void HiddenWglContext_Gl33UsesFragmentCacheGeneration()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAcceptanceEnv),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        const int width = 320;
        const int height = 180;
        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [new GlVersion(GlProfileType.OpenGL, 3, 3)],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);
        context!.Invoke(
            () =>
            {
                using (context.BindOnOwnerThread())
                {
                    context.EnsureRenderTargetCore(width, height);
                    using var backend = new OpenGlPreviewBackend();
                    backend.SetDiagnosticLog(diagnostics.Add);
                    backend.Initialize(new RenderPreviewInitializationOptions());
                    backend.SetGroundMaterials(
                        CreateGroundMaterials(),
                        overlayIsCutout: false);
                    var settings = CreateSettings(AllFixtures[0]);
                    backend.SetRenderSettings(settings);
                    backend.SetScene(BlockPreviewSceneFactory.Create(settings));
                    backend.GlInitNativeWglPresenter(context.GlInterface);
                    try
                    {
                        RenderUntil(
                            context,
                            backend,
                            width,
                            height,
                            () => GetPrivateField<string>(
                                    backend,
                                    "_cloudLightingCacheResourceDiagnostic")
                                .Contains(
                                    "generatedBy=fragment-slices",
                                    StringComparison.Ordinal),
                            "GL 3.3 fragment cache");
                        var plan = GetPrivateField<PreviewCloudLightingCachePlan>(
                            backend,
                            "_cloudLightingCachePlan");
                        Assert.Equal(
                            PreviewCloudLightingCacheGenerationPath.FragmentSlices,
                            plan.PreferredGenerationPath);
                        Assert.Equal(
                            PreviewCloudLightingCacheGenerationPath.CacheSampling,
                            plan.ActiveRuntimePath);
                    }
                    finally
                    {
                        backend.GlDeinit(context.GlInterface);
                    }
                }
            },
            TimeSpan.FromMinutes(2));
    }

    private static Cq3AcceptanceRun RunAcceptance(
        PreviewDesktopWglContext context,
        List<string> diagnostics,
        IReadOnlyList<Fixture> fixtures)
    {
        context.EnsureRenderTargetCore(Width, Height);
        var gl = context.Gl;
        using var backend = new OpenGlPreviewBackend();
        backend.SetDiagnosticLog(diagnostics.Add);
        backend.Initialize(new RenderPreviewInitializationOptions());
        backend.SetGroundMaterials(CreateGroundMaterials(), overlayIsCutout: false);

        var initial = fixtures[0];
        var settings = CreateSettings(initial);
        backend.SetRenderSettings(settings);
        backend.SetScene(BlockPreviewSceneFactory.Create(settings));
        backend.GlInitNativeWglPresenter(context.GlInterface);
        try
        {
            WarmCloudTier(context, backend, diagnostics);
            var cases = new List<Cq3AcceptanceCase>(fixtures.Count);
            var captures = new List<Cq3AcceptanceCapture>(fixtures.Count);
            foreach (var fixture in fixtures)
            {
                RunCase(context, backend, fixture, cases, captures);
            }

            return new Cq3AcceptanceRun(
                DateTimeOffset.UtcNow,
                context.VersionString,
                ReadGlString(gl, StringName.Vendor),
                ReadGlString(gl, StringName.Renderer),
                Width,
                Height,
                WarmupFrames,
                RequiredSamples,
                AcceptedCq2HighLightingP50Ms,
                HighLightingRegressionLimit,
                MaximumHighAmortizedLightingMs,
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
        IReadOnlyList<string> diagnostics)
    {
        for (var frame = 0; frame < 600; frame++)
        {
            DrawFrame(context, backend);
            if (frame >= WarmupFrames &&
                diagnostics.Any(line => line.Contains(
                    "CQ3.6 schedule active",
                    StringComparison.Ordinal)) &&
                backend.TryGetLatestGpuTimingSnapshot(out _, out _))
            {
                return;
            }

            Thread.Sleep(2);
        }

        throw new InvalidOperationException(
            "CQ3 acceptance timed out waiting for cache generation and timer queries.");
    }

    private static void RunCase(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend,
        Fixture fixture,
        List<Cq3AcceptanceCase> cases,
        List<Cq3AcceptanceCapture> captures)
    {
        var settings = CreateSettings(fixture);
        backend.SetRenderSettings(settings);
        backend.SetCameraDebugPose(fixture.Eye, fixture.Target);
        var timingMode =
            fixture.Category == FixtureCategory.Performance &&
            fixture.Quality == PreviewVolumetricQuality.High
                ? "asynchronous-cq2-comparable"
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

        context.Gl.Finish();
        var qualityName = PreviewVolumetricQuality.GetName(fixture.Quality).ToLowerInvariant();
        var name = $"{qualityName}-{fixture.Name}";
        var start = ReadFramebufferSnapshot(
            context.Gl,
            context.RenderFbo,
            Width,
            Height,
            name + "-start");
        var samples = new List<GlGpuTimingSnapshot>(RequiredSamples);
        for (var frame = 0; frame < RequiredSamples + 720 && samples.Count < RequiredSamples; frame++)
        {
            DrawFrame(context, backend);
            if (timingMode == "asynchronous-cq2-comparable")
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

            Thread.Sleep(timingMode == "asynchronous-cq2-comparable" ? 6 : 1);
        }

        Assert.Equal(RequiredSamples, samples.Count);
        context.Gl.Finish();
        var snapshot = ReadFramebufferSnapshot(
            context.Gl,
            context.RenderFbo,
            Width,
            Height,
            name);
        var stats = SummarizeCapture(snapshot);
        var motionDelta = ComputeMeanAbsoluteRgbDelta(start, snapshot);
        captures.Add(new Cq3AcceptanceCapture(name, snapshot, stats));
        cases.Add(SummarizeCase(
            name,
            fixture,
            timingMode,
            samples,
            stats,
            motionDelta));
    }

    private static Cq3AcceptanceCase SummarizeCase(
        string name,
        Fixture fixture,
        string timingMode,
        IReadOnlyList<GlGpuTimingSnapshot> samples,
        CaptureStats capture,
        double motionDelta)
    {
        static TimingSummary Summarize(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return new TimingSummary(
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                sorted.Average(),
                sorted.Count(value => value > 0.000001));
        }

        var trace = Summarize(samples.Select(item => item.CloudTraceMs));
        var near = Summarize(samples.Select(item => item.CloudLightNearMs));
        var far = Summarize(samples.Select(item => item.CloudLightFarMs));
        var lightGenerationMean = near.Mean + far.Mean;
        var amortizedLighting = trace.P50 + lightGenerationMean;
        return new Cq3AcceptanceCase(
            name,
            fixture.Quality,
            PreviewVolumetricQuality.GetName(fixture.Quality),
            fixture.Name,
            fixture.Category,
            fixture.Eye,
            fixture.Target,
            fixture.Density,
            fixture.Coverage,
            fixture.Cirrus,
            fixture.TimeOfDayHours,
            fixture.FreezeWind,
            timingMode,
            samples.Count,
            trace,
            near,
            far,
            lightGenerationMean,
            amortizedLighting,
            amortizedLighting / AcceptedCq2HighLightingP50Ms,
            Summarize(samples.Select(item => item.CloudTemporalMs)),
            Summarize(samples.Select(item => item.CloudRepairMs)),
            Summarize(samples.Select(item => item.CloudUpsampleMs)),
            Summarize(samples.Select(item =>
                item.CloudLightNearMs +
                item.CloudLightFarMs +
                item.CloudTraceMs +
                item.CloudTemporalMs +
                item.CloudRepairMs +
                item.CloudUpsampleMs)),
            Summarize(samples.Select(item => item.TotalMs)),
            motionDelta,
            capture);
    }

    private static void AssertFixtureCoverage(Cq3AcceptanceRun run)
    {
        Assert.Contains(run.Cases, item => item.Category == FixtureCategory.DeepSelfShadow);
        Assert.Contains(run.Cases, item => item.Category == FixtureCategory.BrokenGaps);
        Assert.Contains(run.Cases, item => item.Category == FixtureCategory.NearFarOverlap);
        Assert.Contains(run.Cases, item => item.Category == FixtureCategory.Cirrus);
        Assert.Contains(run.Cases, item => item.Category == FixtureCategory.TerrainShadow);
        Assert.Equal(3, run.Cases.Count(item => item.Category == FixtureCategory.HeightTransition));
        Assert.True(run.Cases.Count(item => item.Category == FixtureCategory.SunTransition) >= 3);
        Assert.Contains(
            run.Cases,
            item => item.Category == FixtureCategory.TerrainShadow &&
                    !item.FreezeWind &&
                    item.MotionDelta > 0.0001);
    }

    private static void AssertCadenceEvidence(Cq3AcceptanceRun run)
    {
        var high = Assert.Single(
            run.Cases,
            item => item.Fixture == "dense-overcast" &&
                    item.Quality == PreviewVolumetricQuality.High);
        var cinematic = Assert.Single(
            run.Cases,
            item => item.Fixture == "dense-overcast" &&
                    item.Quality == PreviewVolumetricQuality.Cinematic);
        var moving = Assert.Single(
            run.Cases,
            item => item.Fixture == "moving-terrain-shadow");

        Assert.Equal(0, high.CloudLightNear.NonZeroSamples);
        Assert.Equal(0, high.CloudLightFar.NonZeroSamples);
        Assert.Equal(0, cinematic.CloudLightNear.NonZeroSamples);
        Assert.Equal(0, cinematic.CloudLightFar.NonZeroSamples);
        Assert.True(
            moving.CloudLightNear.NonZeroSamples >
            moving.CloudLightFar.NonZeroSamples);
        Assert.True(moving.CloudLightFar.NonZeroSamples > 0);
    }

    private static void AssertPerformanceGate(Cq3AcceptanceRun run)
    {
        var high = Assert.Single(
            run.Cases,
            item => item.Fixture == "dense-overcast" &&
                    item.Quality == PreviewVolumetricQuality.High);
        Assert.True(
            high.AmortizedLightingMs <= MaximumHighAmortizedLightingMs,
            string.Create(
                CultureInfo.InvariantCulture,
                $"CQ3 High amortized lighting {high.AmortizedLightingMs:0.###} ms " +
                $"(trace p50 {high.CloudTrace.P50:0.###} + cache mean " +
                $"{high.LightGenerationMeanMs:0.###}) exceeds " +
                $"{HighLightingRegressionLimit:0.00}x the accepted CQ2 High lighting proxy " +
                $"({AcceptedCq2HighLightingP50Ms:0.###} ms; limit " +
                $"{MaximumHighAmortizedLightingMs:0.###} ms)."));
    }

    private static PreviewRenderSettings CreateSettings(Fixture fixture) =>
        new()
        {
            AutoRotate = false,
            AnimateTimeOfDay = false,
            TimeOfDayHours = fixture.TimeOfDayHours,
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
            CloudFreezeWind = fixture.FreezeWind,
            EnablePreviewTaa = true,
            PreviewTaaMode = 1,
            EnableShadows = false,
            EnableShadowCascades = false,
            ShowExpandedGpuTimingHud = false,
        };

    private static PreviewMaterial[] CreateGroundMaterials()
    {
        var material = new PreviewMaterial
        {
            Width = 2,
            Height = 2,
            AlbedoRgba = new byte[]
            {
                60, 118, 45, 255,
                72, 135, 50, 255,
                52, 105, 38, 255,
                66, 125, 44, 255,
            },
            NormalRgba = Enumerable.Repeat(
                    new byte[] { 128, 128, 255, 255 },
                    4)
                .SelectMany(item => item)
                .ToArray(),
            SpecularRgba = Enumerable.Repeat(
                    new byte[] { 0, 110, 0, 255 },
                    4)
                .SelectMany(item => item)
                .ToArray(),
        };
        var materials = new PreviewMaterial[PreviewTerrainGrassSlots.MaxCount];
        Array.Fill(materials, material);
        return materials;
    }

    private static void DrawFrame(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend)
    {
        DrawFrame(context, backend, Width, Height);
    }

    private static void DrawFrame(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend,
        int width,
        int height)
    {
        backend.RenderFrame(FrameElapsed);
        backend.GlRenderNativeWglPresenter(width, height, context.RenderFbo);
    }

    private static void RenderUntil(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend,
        int width,
        int height,
        Func<bool> predicate,
        string milestone,
        Func<string>? failureDiagnostic = null)
    {
        for (var frame = 0; frame < 360; frame++)
        {
            DrawFrame(context, backend, width, height);
            context.Gl.Finish();
            if (predicate())
            {
                return;
            }

            Thread.Sleep(1);
        }

        throw new InvalidOperationException(
            $"CQ3 live acceptance timed out waiting for {milestone}." +
            (failureDiagnostic is null
                ? string.Empty
                : Environment.NewLine + failureDiagnostic()));
    }

    private static T GetPrivateField<T>(
        OpenGlPreviewBackend backend,
        string name)
    {
        var field = typeof(OpenGlPreviewBackend).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(backend);
        Assert.NotNull(value);
        return (T)value!;
    }

    private static void SetPrivateField<T>(
        OpenGlPreviewBackend backend,
        string name,
        T value)
    {
        var field = typeof(OpenGlPreviewBackend).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(backend, value);
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

        return new CaptureStats(
            hash,
            sampledColors.Count,
            minLuma,
            maxLuma,
            maxLuma - minLuma);
    }

    private static double ComputeMeanAbsoluteRgbDelta(
        GlPixelSnapshot first,
        GlPixelSnapshot second)
    {
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

    private static Fixture[] ResolveFixtures()
    {
        var filter = Environment.GetEnvironmentVariable(AcceptanceCaseEnv);
        if (string.IsNullOrWhiteSpace(filter))
        {
            return AllFixtures;
        }

        var selected = AllFixtures
            .Where(item => string.Equals(
                $"{PreviewVolumetricQuality.GetName(item.Quality)}:{item.Name}",
                filter,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException(
                $"Unknown CQ3 acceptance case '{filter}'.");
        }

        return selected;
    }

    private static void WriteArtifacts(
        Cq3AcceptanceRun run,
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
            performanceGate = new
            {
                metric = "High trace p50 + mean scheduled near/far cache generation",
                run.AcceptedCq2HighLightingP50Ms,
                run.HighLightingRegressionLimit,
                run.MaximumHighAmortizedLightingMs,
            },
            cases = run.Cases,
            diagnostics,
        };
        File.WriteAllText(
            Path.Combine(directory, "cq3-acceptance-report.json"),
            JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllLines(
            Path.Combine(directory, "cq3-acceptance-summary.csv"),
            new[]
            {
                "quality,fixture,category,time_of_day,freeze_wind,timing_mode,samples," +
                "trace_p50_ms,trace_p95_ms,near_mean_ms,near_nonzero_samples," +
                "far_mean_ms,far_nonzero_samples,cache_mean_ms,amortized_lighting_ms," +
                "cq2_ratio,cloud_total_p50_ms,cloud_total_p95_ms,frame_p50_ms," +
                "frame_p95_ms,motion_delta,capture_sha256",
            }.Concat(run.Cases.Select(item => string.Join(
                ',',
                item.QualityName,
                item.Fixture,
                item.Category,
                Format(item.TimeOfDayHours),
                item.FreezeWind,
                item.TimingMode,
                item.SampleCount.ToString(CultureInfo.InvariantCulture),
                Format(item.CloudTrace.P50),
                Format(item.CloudTrace.P95),
                Format(item.CloudLightNear.Mean),
                item.CloudLightNear.NonZeroSamples.ToString(CultureInfo.InvariantCulture),
                Format(item.CloudLightFar.Mean),
                item.CloudLightFar.NonZeroSamples.ToString(CultureInfo.InvariantCulture),
                Format(item.LightGenerationMeanMs),
                Format(item.AmortizedLightingMs),
                Format(item.Cq2LightingRatio),
                Format(item.CloudTotal.P50),
                Format(item.CloudTotal.P95),
                Format(item.FrameTotal.P50),
                Format(item.FrameTotal.P95),
                Format(item.MotionDelta),
                item.Capture.Sha256))));
    }

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static unsafe string ReadGlString(GL gl, StringName name)
    {
        var value = gl.GetString(name);
        return value is null
            ? "(unknown)"
            : Marshal.PtrToStringUTF8((nint)value) ?? "(unknown)";
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
            PreviewVolumetricQuality.High,
            new Vector3(-1.68f, 42.78f, 9.67f),
            new Vector3(3.25f, 42.42f, 7.60f),
            0.75f,
            1.63f,
            0.13f,
            6.64f,
            true),
        new(
            "dense-overcast",
            FixtureCategory.Performance,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-1.68f, 42.78f, 9.67f),
            new Vector3(3.25f, 42.42f, 7.60f),
            0.75f,
            1.63f,
            0.13f,
            6.64f,
            true),
        new(
            "deep-tower-self-shadow",
            FixtureCategory.DeepSelfShadow,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(96f, 18f, -82f),
            new Vector3(106f, 56f, 30f),
            0.86f,
            1.58f,
            0.04f,
            12f,
            true),
        new(
            "broken-sunlit-gaps",
            FixtureCategory.BrokenGaps,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-420f, 9f, -360f),
            new Vector3(-340f, 42f, -190f),
            0.38f,
            0.62f,
            0.06f,
            9.5f,
            true),
        new(
            "sunrise",
            FixtureCategory.SunTransition,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-180f, 24f, -240f),
            new Vector3(-80f, 38f, 300f),
            0.68f,
            1.30f,
            0.10f,
            6.0f,
            true),
        new(
            "noon",
            FixtureCategory.SunTransition,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-180f, 24f, -240f),
            new Vector3(-80f, 38f, 300f),
            0.68f,
            1.30f,
            0.10f,
            12f,
            true),
        new(
            "sunset",
            FixtureCategory.SunTransition,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-180f, 24f, -240f),
            new Vector3(-80f, 38f, 300f),
            0.68f,
            1.30f,
            0.10f,
            18f,
            true),
        new(
            "below-layer",
            FixtureCategory.HeightTransition,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(0f, 2f, -6f),
            new Vector3(0f, 22f, 70f),
            0.58f,
            1.05f,
            0.08f,
            12f,
            true),
        new(
            "inside-layer",
            FixtureCategory.HeightTransition,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(256f, 43f, 250f),
            new Vector3(256f, 43f, 330f),
            0.82f,
            1.48f,
            0.08f,
            12f,
            true),
        new(
            "above-layer",
            FixtureCategory.HeightTransition,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(0f, 96f, -6f),
            new Vector3(0f, 52f, 68f),
            0.52f,
            0.92f,
            0.12f,
            12f,
            true),
        new(
            "grazing-near-far-overlap",
            FixtureCategory.NearFarOverlap,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-320f, 22f, 288f),
            new Vector3(-80f, 24f, 1580f),
            0.55f,
            1.05f,
            0.16f,
            6.64f,
            true),
        new(
            "cirrus-over-cumulus",
            FixtureCategory.Cirrus,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(-96f, 64f, 64f),
            new Vector3(-40f, 112f, 180f),
            0.32f,
            0.48f,
            0.85f,
            12f,
            true),
        new(
            "moving-terrain-shadow",
            FixtureCategory.TerrainShadow,
            PreviewVolumetricQuality.Cinematic,
            new Vector3(40f, 16f, -112f),
            new Vector3(40f, 24f, 120f),
            0.78f,
            1.52f,
            0.06f,
            14f,
            false),
    ];

    private enum FixtureCategory
    {
        Performance,
        DeepSelfShadow,
        BrokenGaps,
        SunTransition,
        HeightTransition,
        NearFarOverlap,
        Cirrus,
        TerrainShadow,
    }

    private sealed record Fixture(
        string Name,
        FixtureCategory Category,
        int Quality,
        Vector3 Eye,
        Vector3 Target,
        float Density,
        float Coverage,
        float Cirrus,
        float TimeOfDayHours,
        bool FreezeWind);

    private sealed record TimingSummary(
        double P50,
        double P95,
        double Mean,
        int NonZeroSamples);

    private sealed record CaptureStats(
        string Sha256,
        int SampledColorCount,
        double MinimumLuma,
        double MaximumLuma,
        double LumaRange);

    private sealed record Cq3AcceptanceCase(
        string Name,
        int Quality,
        string QualityName,
        string Fixture,
        FixtureCategory Category,
        Vector3 Eye,
        Vector3 Target,
        float Density,
        float Coverage,
        float Cirrus,
        float TimeOfDayHours,
        bool FreezeWind,
        string TimingMode,
        int SampleCount,
        TimingSummary CloudTrace,
        TimingSummary CloudLightNear,
        TimingSummary CloudLightFar,
        double LightGenerationMeanMs,
        double AmortizedLightingMs,
        double Cq2LightingRatio,
        TimingSummary CloudTemporal,
        TimingSummary CloudRepair,
        TimingSummary CloudUpsample,
        TimingSummary CloudTotal,
        TimingSummary FrameTotal,
        double MotionDelta,
        CaptureStats Capture);

    private sealed record Cq3AcceptanceCapture(
        string Name,
        GlPixelSnapshot Snapshot,
        CaptureStats Stats);

    private sealed record Cq3AcceptanceRun(
        DateTimeOffset TimestampUtc,
        string VersionString,
        string Vendor,
        string Renderer,
        int Width,
        int Height,
        int WarmupFrames,
        int RequiredSamples,
        double AcceptedCq2HighLightingP50Ms,
        double HighLightingRegressionLimit,
        double MaximumHighAmortizedLightingMs,
        IReadOnlyList<Cq3AcceptanceCase> Cases,
        IReadOnlyList<Cq3AcceptanceCapture> Captures);
}
