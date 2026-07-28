using System.Text.Json;
using System.Globalization;
using System.Numerics;
using System.Reflection;

using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.App.Tests;

public sealed class PreviewPixelRenderingHarnessTests
{
    private const string EnableHarnessEnv = "AUTOPBR_RUN_PIXEL_GL_HARNESS";
    private const string EnableLiveSmokeEnv = "AUTOPBR_RUN_LIVE_GL_SMOKE";
    private const string ArtifactDirectoryEnv = "AUTOPBR_PIXEL_HARNESS_ARTIFACT_DIR";
    private const int Width = 160;
    private const int Height = 120;
    private static readonly byte[] ClearRgba = [7, 11, 19, 255];
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new() { WriteIndented = true };

    [Fact]
    public void HiddenWglContext_AccelerationLanesMatchDirectPixelBaseline()
    {
        if (!IsEnabled())
        {
            return;
        }

        Assert.True(OperatingSystem.IsWindows(), "Live pixel rendering harness requires Windows WGL.");
        var diagnostics = new List<string>();
        GlPixelHarnessRun? run;
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
                        return RunPixelMatrix(context.Gl, context.VersionString, diagnostics);
                    }
                },
                TimeSpan.FromSeconds(45));
        }

        var artifactDirectory = ResolveArtifactDirectory(run);
        if (artifactDirectory is not null)
        {
            WriteArtifacts(run, artifactDirectory);
        }

        Assert.True(
            run.Baseline.CountPixelsOutside(ClearRgba, tolerance: 1) >= run.Baseline.PixelCount / 3,
            "Direct baseline did not render enough fixture pixels to validate submission parity.");
        Assert.NotEmpty(run.Comparisons);
        foreach (var comparison in run.Comparisons)
        {
            Assert.True(
                comparison.Passed,
                comparison.FormatDiagnostic() +
                (artifactDirectory is null ? string.Empty : $" Artifacts: {artifactDirectory}"));
        }

        if (run.Capabilities.CanUseIndirectDrawCommands)
        {
            Assert.Contains(run.Comparisons, item => item.ActualName == "indirect-per-command");
        }

        if (run.Capabilities.CanUseMultiDrawIndirectGroups)
        {
            Assert.Contains(run.Comparisons, item => item.ActualName == "multi-draw-indirect");
        }

        if (run.Capabilities.CanUseGpuCompactedDrawSubmission)
        {
            Assert.Contains(run.Comparisons, item => item.ActualName == "gpu-compacted-indirect-count");
        }

        if (run.Capabilities.CanUseMaterialTextureArrays)
        {
            Assert.Contains(run.Comparisons, item =>
                item.ExpectedName == "legacy-material-samplers" && item.ActualName == "material-texture-array");
        }
    }

    [Fact]
    public void HiddenWglContext_IdleStageRendersResidentTerrainPixels()
    {
        if (!IsEnabled())
        {
            return;
        }

        Assert.True(OperatingSystem.IsWindows(), "Live terrain rendering harness requires Windows WGL.");
        // Match the production preview size used by the terrain/cloud regression capture.
        const int width = 862;
        const int height = 683;
        var diagnostics = new List<string>();
        var groundFixture = LoadGroundFixture();
        GlPixelSnapshot? snapshot;
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
            snapshot = context!.Invoke(
                () =>
                {
                    using (context.BindOnOwnerThread())
                    {
                        context.EnsureRenderTargetCore(width, height);
                        using var backend = new OpenGlPreviewBackend();
                        backend.SetDiagnosticLog(diagnostics.Add);
                        backend.Initialize(new RenderPreviewInitializationOptions());
                        var settings = new PreviewRenderSettings
                        {
                            AutoRotate = false,
                            DrawPreviewSubject = true,
                            ShowGroundMesh = true,
                            ShowBackgroundGrid = false,
                            ShowCornerAxes = false,
                            EnableVolumetricClouds = true,
                            VolumetricQuality = PreviewVolumetricQuality.Cinematic,
                        };
                        backend.SetRenderSettings(settings);
                        backend.SetScene(BlockPreviewSceneFactory.Create(settings));
                        var blockSubject = CreateDdaTransitionSubject();
                        backend.SetBlockModelPreview(
                            blockSubject,
                            [.. blockSubject.Materials.Select(maps => PreviewMaterialMapper.FromCoreMaps(maps))]);
                        backend.SetCameraSensitivities(
                            orbitRadPerPx: 0.006f,
                            panPerPixel: 0.02f,
                            zoomPerWheelStep: 0.12f,
                            flyLookRadPerPx: 0.006f,
                            invertLookY: false,
                            flyMoveSpeed: 1f,
                            flySmoothAcceleration: true);
                        backend.SetTerrainGrassBakeSettings(groundFixture.BakeSettings);
                        backend.SetTerrainVegetationBakePlan(groundFixture.VegetationPlan);
                        backend.SetGroundMaterials(
                            groundFixture.Materials,
                            overlayIsCutout: true,
                            groundFixture.CutoutBySlot);
                        backend.GlInitNativeWglPresenter(context.GlInterface);
                        try
                        {
                            var chunksField = typeof(OpenGlPreviewBackend).GetField(
                                "_terrainGpuChunks",
                                BindingFlags.Instance | BindingFlags.NonPublic);
                            var poolField = typeof(OpenGlPreviewBackend).GetField(
                                "_terrainMeshPool",
                                BindingFlags.Instance | BindingFlags.NonPublic);
                            var lastResident = -1;
                            long lastVertexCapacity = -1;
                            long lastIndexCapacity = -1;
                            var firstPoolFailureFrame = -1;
                            var fullResidencyFrame = -1;
                            var postResidencyRecenterFrame = -1;
                            var desiredDiameter =
                                (settings.ChunkViewDistance + settings.LodRingChunks) * 2 + 1;
                            var expectedResidentChunks = desiredDiameter * desiredDiameter;
                            for (var frame = 0; frame < 1200; frame++)
                            {
                                if (frame == 48)
                                {
                                    backend.ApplyCameraPanPixels(-665f, -65f);
                                }
                                else if (fullResidencyFrame >= 0 &&
                                         postResidencyRecenterFrame < 0 &&
                                         frame >= fullResidencyFrame + 20)
                                {
                                    backend.ApplyCameraPanPixels(-1200f, 0f);
                                    postResidencyRecenterFrame = frame;
                                    diagnostics.Add(
                                        $"[terrain harness] frame={frame} post-residency camera recenter.");
                                }

                                backend.RenderFrame(TimeSpan.FromSeconds(1.0 / 60.0));
                                backend.GlRenderNativeWglPresenter(width, height, context.RenderFbo);
                                if (backend.GpuInitProgress.CoreReady && frame >= 32)
                                {
                                    Thread.Sleep(5);
                                }

                                var resident = chunksField?.GetValue(backend) is System.Collections.ICollection chunks
                                    ? chunks.Count
                                    : -1;
                                var pool = poolField?.GetValue(backend) as GlTerrainMeshPool;
                                var vertexCapacity = pool?.VertexCapacityBytes ?? -1;
                                var indexCapacity = pool?.IndexCapacityBytes ?? -1;
                                if (vertexCapacity != lastVertexCapacity || indexCapacity != lastIndexCapacity)
                                {
                                    diagnostics.Add(
                                        $"[terrain harness] frame={frame} pool={vertexCapacity / (1024 * 1024)}MiB-vbo/" +
                                        $"{indexCapacity / (1024 * 1024)}MiB-ebo resident={resident}.");
                                    lastVertexCapacity = vertexCapacity;
                                    lastIndexCapacity = indexCapacity;
                                }

                                if (pool is { AllocationFailureCount: > 0 } && firstPoolFailureFrame < 0)
                                {
                                    firstPoolFailureFrame = frame;
                                    diagnostics.Add(
                                        $"[terrain harness] frame={frame} bounded pool rejected growth; " +
                                        $"capacity={pool.TotalCapacityBytes / (1024 * 1024)}MiB, " +
                                        $"budget={pool.MaxTotalBufferBytes / (1024 * 1024)}MiB, " +
                                        $"resident={resident}.");
                                }

                                if (resident >= expectedResidentChunks && fullResidencyFrame < 0)
                                {
                                    fullResidencyFrame = frame;
                                    diagnostics.Add(
                                        $"[terrain harness] frame={frame} full residency reached: " +
                                        $"resident={resident}/{expectedResidentChunks}, " +
                                        $"capacity={pool?.TotalCapacityBytes / (1024 * 1024)}MiB, " +
                                        $"highWater={pool?.VertexHighWaterBytes / (1024 * 1024)}MiB-vbo/" +
                                        $"{pool?.IndexHighWaterBytes / (1024 * 1024)}MiB-ebo.");
                                }

                                if (resident / 64 != lastResident / 64)
                                {
                                    diagnostics.Add($"[terrain harness] frame={frame} resident={resident}.");
                                }

                                lastResident = resident;
                                if ((firstPoolFailureFrame >= 0 && frame >= firstPoolFailureFrame + 60) ||
                                    (postResidencyRecenterFrame >= 0 &&
                                     frame >= postResidencyRecenterFrame + 100))
                                {
                                    break;
                                }
                            }

                            context.Gl.Finish();
                            if (backend.TryGetCameraDebugPose(out var eye, out var target))
                            {
                                diagnostics.Add(
                                    FormattableString.Invariant(
                                        $"[terrain harness] final eye={eye}, target={target}."));
                            }

                            return ReadFramebufferSnapshot(
                                context.Gl,
                                context.RenderFbo,
                                width,
                                height,
                                "idle-stage-resident-terrain");
                        }
                        finally
                        {
                            backend.GlDeinit(context.GlInterface);
                        }
                    }
                },
                TimeSpan.FromSeconds(45));
        }

        Assert.NotNull(snapshot);
        var configured = Environment.GetEnvironmentVariable(ArtifactDirectoryEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var directory = Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(FindRepoRoot(), configured));
            Directory.CreateDirectory(directory);
            WritePng(snapshot, Path.Combine(directory, snapshot.Name + ".png"));
            File.WriteAllLines(
                Path.Combine(directory, snapshot.Name + ".log"),
                diagnostics);
        }

        var rgba = snapshot.Rgba.Span;
        var terrainPixels = 0;
        for (var y = snapshot.Height / 2; y < snapshot.Height; y++)
        {
            for (var x = 0; x < snapshot.Width; x++)
            {
                var i = (y * snapshot.Width + x) * 4;
                var r = rgba[i];
                var g = rgba[i + 1];
                var b = rgba[i + 2];
                var green = g > 35 && g > r * 1.12f && g > b * 1.08f;
                var mineral = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 28 &&
                              r is > 35 and < 245;
                var sand = r > b + 10 && g > b + 5 && r > 55;
                if (green || mineral || sand)
                {
                    terrainPixels++;
                }
            }
        }

        var lowerHalfPixels = snapshot.Width * (snapshot.Height - snapshot.Height / 2);
        Assert.True(
            terrainPixels >= lowerHalfPixels / 4,
            $"Resident terrain produced too few visible lower-frame pixels ({terrainPixels}/{lowerHalfPixels}). " +
            string.Join(Environment.NewLine, diagnostics.TakeLast(24)));
        Assert.Contains(
            diagnostics,
            line => line.Contains("cameraChunkResident=True", StringComparison.Ordinal));
        Assert.Contains(
            diagnostics,
            line => line.Contains("full residency reached", StringComparison.Ordinal));
        Assert.Contains(
            diagnostics,
            line => line.Contains("post-residency camera recenter", StringComparison.Ordinal));
        Assert.DoesNotContain(
            diagnostics,
            line => line.Contains("bounded pool rejected growth", StringComparison.Ordinal) ||
                    line.Contains("preserving existing terrain", StringComparison.Ordinal));
    }

    private static PreviewModelSubject CreateDdaTransitionSubject()
    {
        const int materialCount = 4;
        const int stride = PreviewMesh.FloatsPerVertex;
        var vertices = new float[materialCount * 4 * stride];
        var indices = new uint[materialCount * 6];
        var batches = new PreviewDrawBatch[materialCount];
        var materials = new PreviewTextureMaps[materialCount];
        for (var batch = 0; batch < materialCount; batch++)
        {
            var x0 = (batch - 1.5f) * 0.45f;
            var vertexBase = batch * 4;
            WriteDdaTransitionVertex(vertices, (vertexBase + 0) * stride, x0, 0f, 0f, 0f);
            WriteDdaTransitionVertex(vertices, (vertexBase + 1) * stride, x0 + 0.4f, 0f, 0f, 1f);
            WriteDdaTransitionVertex(vertices, (vertexBase + 2) * stride, x0 + 0.4f, 0.8f, 0f, 1f);
            WriteDdaTransitionVertex(vertices, (vertexBase + 3) * stride, x0, 0.8f, 0f, 0f);

            var firstIndex = batch * 6;
            indices[firstIndex + 0] = (uint)(vertexBase + 0);
            indices[firstIndex + 1] = (uint)(vertexBase + 1);
            indices[firstIndex + 2] = (uint)(vertexBase + 2);
            indices[firstIndex + 3] = (uint)(vertexBase + 2);
            indices[firstIndex + 4] = (uint)(vertexBase + 3);
            indices[firstIndex + 5] = (uint)(vertexBase + 0);
            batches[batch] = new PreviewDrawBatch(firstIndex, 6, batch)
            {
                BoundsCenter = new Vector3(x0 + 0.2f, 0.4f, 0f),
                BoundsRadius = 0.5f,
            };

            var tint = (byte)(70 + batch * 35);
            materials[batch] = new PreviewTextureMaps
            {
                Width = 2,
                Height = 2,
                DiffuseRgba =
                [
                    tint, (byte)(150 - batch * 15), (byte)(65 + batch * 10), 255,
                    (byte)(tint + 16), (byte)(135 - batch * 10), 55, 255,
                    (byte)(tint + 8), (byte)(165 - batch * 12), 75, 255,
                    (byte)(tint + 24), (byte)(145 - batch * 8), 60, 255,
                ],
            };
        }

        return new PreviewModelSubject
        {
            InterleavedVertices = vertices,
            Indices = indices,
            DrawBatches = batches,
            Materials = materials,
            PrimaryMaterialIndex = 0,
        };
    }

    private static void WriteDdaTransitionVertex(
        float[] vertices,
        int offset,
        float x,
        float y,
        float u,
        float v)
    {
        vertices[offset + 0] = x;
        vertices[offset + 1] = y;
        vertices[offset + 2] = 0f;
        vertices[offset + 3] = 0f;
        vertices[offset + 4] = 0f;
        vertices[offset + 5] = 1f;
        vertices[offset + 6] = u;
        vertices[offset + 7] = v;
        vertices[offset + 8] = 1f;
        vertices[offset + 9] = 0f;
        vertices[offset + 10] = 0f;
        vertices[offset + 11] = 1f;
    }

    private static GlPixelHarnessRun RunPixelMatrix(GL gl, string version, List<string> diagnostics)
    {
        var capabilities = PreviewGlCapabilities.FromGl(gl, useOpenGlEs: false, version);
        diagnostics.Add(capabilities.FormatDiagnostic());
        using var target = new GlPixelRenderHarness(gl, Width, Height);
        using var mesh = CreateSubmissionFixtureMesh(gl, out var batches);
        using var commands = new GlIndirectDrawCommandBuffer(gl);
        Require(commands.Upload(batches), "Unable to upload pixel-harness indirect commands.");
        using var drawProgram = CreateProgram(gl, SubmissionVertexSource, SubmissionFragmentSource, "submission fixture");

        var snapshots = new List<GlPixelSnapshot>();
        var baseline = target.Capture("direct-draw-range", _ =>
        {
            drawProgram.Use();
            for (var i = 0; i < batches.Length; i++)
            {
                mesh.DrawRange(batches[i].FirstIndex, batches[i].IndexCount, keepBound: true);
            }

            mesh.UnbindVertexArray();
        });
        snapshots.Add(baseline);

        if (capabilities.CanUseIndirectDrawCommands)
        {
            snapshots.Add(target.Capture("indirect-per-command", _ =>
            {
                drawProgram.Use();
                for (var i = 0; i < batches.Length; i++)
                {
                    mesh.DrawIndirect(commands, i, keepBound: true);
                }

                mesh.UnbindVertexArray();
            }));
        }
        else
        {
            diagnostics.Add("Pixel harness skipped per-command indirect submission: capability unavailable.");
        }

        if (capabilities.CanUseMultiDrawIndirectGroups)
        {
            snapshots.Add(target.Capture("multi-draw-indirect", _ =>
            {
                drawProgram.Use();
                mesh.MultiDrawIndirect(commands, 0, commands.CommandCount);
            }));
        }
        else
        {
            diagnostics.Add("Pixel harness skipped grouped multi-draw submission: capability unavailable.");
        }

        if (capabilities.CanUseGpuCompactedDrawSubmission)
        {
            var shaderContext = new GlShaderCompileContext(
                gl,
                useOpenGlEs: false,
                capabilities.Vendor,
                capabilities.Renderer);
            using var compactionProgram = shaderContext.CreateComputeProgram(
                "genesis_indirect_compact.comp",
                out var compactionError,
                "pixel-harness-indirect-compact");
            Require(compactionProgram.IsValid, "Pixel harness compaction shader failed: " + compactionError);
            using var compactor = new GlGpuDrawCommandCompactor(gl);
            snapshots.Add(target.Capture("gpu-compacted-indirect-count", _ =>
            {
                Require(
                    compactor.Dispatch(compactionProgram, commands, [1u, 1u, 1u, 1u]),
                    "Pixel harness compaction dispatch failed.");
                drawProgram.Use();
                Require(
                    mesh.MultiDrawIndirectCount(
                        compactor.OutputCommands,
                        compactor.CounterBufferHandle,
                        commands.CommandCount),
                    "Pixel harness indirect-count entry point was unavailable.");
            }));
        }
        else
        {
            diagnostics.Add("Pixel harness skipped GPU-compacted indirect-count submission: capability unavailable.");
        }

        if (capabilities.CanUseMaterialTextureArrays)
        {
            AddMaterialTextureParitySnapshots(gl, target, snapshots);
        }
        else
        {
            diagnostics.Add("Pixel harness skipped material texture-array parity: capability unavailable.");
        }

        var comparisons = new List<GlPixelComparison>();
        foreach (var snapshot in snapshots)
        {
            if (snapshot != baseline && !snapshot.Name.StartsWith("material-", StringComparison.Ordinal) &&
                snapshot.Name != "legacy-material-samplers")
            {
                comparisons.Add(baseline.CompareTo(snapshot, GlPixelComparisonOptions.Exact));
            }
        }

        var legacyMaterials = snapshots.FirstOrDefault(item => item.Name == "legacy-material-samplers");
        var arrayMaterials = snapshots.FirstOrDefault(item => item.Name == "material-texture-array");
        if (legacyMaterials is not null && arrayMaterials is not null)
        {
            comparisons.Add(legacyMaterials.CompareTo(arrayMaterials, GlPixelComparisonOptions.Exact));
        }

        diagnostics.AddRange(comparisons.Select(item => item.FormatDiagnostic()));
        return new GlPixelHarnessRun(
            DateTimeOffset.UtcNow,
            version,
            capabilities,
            baseline,
            snapshots,
            comparisons,
            diagnostics.ToArray());
    }

    private static GlMeshBuffer CreateSubmissionFixtureMesh(GL gl, out PreviewDrawBatch[] batches)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();
        var rectangles = new (float Left, float Bottom, float Right, float Top)[]
        {
            (-0.92f, -0.88f, -0.08f, -0.08f),
            ( 0.08f, -0.88f,  0.92f, -0.08f),
            (-0.92f,  0.08f, -0.08f,  0.88f),
            ( 0.08f,  0.08f,  0.92f,  0.88f),
        };
        batches = new PreviewDrawBatch[rectangles.Length];
        for (var rectangleIndex = 0; rectangleIndex < rectangles.Length; rectangleIndex++)
        {
            var rectangle = rectangles[rectangleIndex];
            var firstVertex = (uint)(vertices.Count / 12);
            AddVertex(vertices, rectangle.Left, rectangle.Bottom, 0, 0);
            AddVertex(vertices, rectangle.Right, rectangle.Bottom, 1, 0);
            AddVertex(vertices, rectangle.Right, rectangle.Top, 1, 1);
            AddVertex(vertices, rectangle.Left, rectangle.Top, 0, 1);
            var firstIndex = indices.Count;
            indices.AddRange(
            [
                firstVertex, firstVertex + 1, firstVertex + 2,
                firstVertex, firstVertex + 2, firstVertex + 3,
            ]);
            batches[rectangleIndex] = new PreviewDrawBatch(firstIndex, 6, 0);
        }

        var mesh = new GlMeshBuffer(gl);
        mesh.Upload(vertices.ToArray(), indices.ToArray());
        return mesh;
    }

    private static void AddVertex(List<float> vertices, float x, float y, float u, float v)
    {
        vertices.AddRange(
        [
            x, y, 0,
            0, 0, 1,
            u, v,
            1, 0, 0, 1,
        ]);
    }

    private static void AddMaterialTextureParitySnapshots(
        GL gl,
        GlPixelRenderHarness target,
        List<GlPixelSnapshot> snapshots)
    {
        byte[][] layers =
        [
            [240, 40, 30, 255, 180, 20, 10, 255, 255, 100, 30, 255, 210, 60, 20, 255],
            [20, 220, 70, 255, 10, 150, 40, 255, 90, 255, 120, 255, 40, 190, 80, 255],
            [30, 80, 240, 255, 20, 40, 170, 255, 100, 150, 255, 255, 60, 100, 220, 255],
            [230, 200, 40, 255, 160, 120, 20, 255, 255, 240, 100, 255, 200, 170, 60, 255],
        ];

        using var quad = CreateFullscreenQuadMesh(gl);
        using var legacyProgram = CreateProgram(gl, TextureVertexSource, LegacyTextureFragmentSource, "legacy material fixture");
        using var arrayProgram = CreateProgram(gl, TextureVertexSource, ArrayTextureFragmentSource, "array material fixture");
        var legacyTextures = layers.Select(layer =>
        {
            var texture = new GlTexture2D(gl);
            texture.UploadRgba(2, 2, layer, nearestFilter: true);
            return texture;
        }).ToArray();
        using var arrayTexture = new GlTexture2DArray(gl);
        try
        {
            var allLayers = layers.SelectMany(layer => layer).ToArray();
            Require(arrayTexture.UploadRgbaIfChanged(2, 2, layers.Length, allLayers, nearest: true),
                "Pixel harness texture-array upload failed.");

            snapshots.Add(target.Capture("legacy-material-samplers", _ =>
            {
                legacyProgram.Use();
                for (var unit = 0; unit < legacyTextures.Length; unit++)
                {
                    legacyTextures[unit].Bind((uint)unit);
                    var location = legacyProgram.GetUniformLocation("uTexture" + unit);
                    Require(location >= 0, "Legacy material sampler uniform was optimized out.");
                    gl.Uniform1(location, unit);
                }

                quad.Draw();
            }));

            snapshots.Add(target.Capture("material-texture-array", _ =>
            {
                arrayProgram.Use();
                arrayTexture.Bind(0);
                var location = arrayProgram.GetUniformLocation("uTextures");
                Require(location >= 0, "Material texture-array uniform was optimized out.");
                gl.Uniform1(location, 0);
                quad.Draw();
            }));
        }
        finally
        {
            foreach (var texture in legacyTextures)
            {
                texture.Dispose();
            }
        }
    }

    private static GlMeshBuffer CreateFullscreenQuadMesh(GL gl)
    {
        float[] vertices =
        [
            -1, -1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1,
             1, -1, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1,
             1,  1, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1,
            -1,  1, 0, 0, 0, 1, 0, 1, 1, 0, 0, 1,
        ];
        var mesh = new GlMeshBuffer(gl);
        mesh.Upload(vertices, [0, 1, 2, 0, 2, 3]);
        return mesh;
    }

    private static GlShaderProgram CreateProgram(GL gl, string vertexSource, string fragmentSource, string label)
    {
        var vertex = CompileShader(gl, ShaderType.VertexShader, vertexSource, label);
        var fragment = CompileShader(gl, ShaderType.FragmentShader, fragmentSource, label);
        var program = gl.CreateProgram();
        try
        {
            gl.AttachShader(program, vertex);
            gl.AttachShader(program, fragment);
            gl.LinkProgram(program);
            gl.GetProgram(program, GLEnum.LinkStatus, out var linked);
            if (linked == 0)
            {
                throw new InvalidOperationException($"Pixel harness {label} link failed: {gl.GetProgramInfoLog(program)}");
            }

            return new GlShaderProgram(gl, program);
        }
        catch
        {
            gl.DeleteProgram(program);
            throw;
        }
        finally
        {
            gl.DeleteShader(vertex);
            gl.DeleteShader(fragment);
        }
    }

    private static uint CompileShader(GL gl, ShaderType type, string source, string label)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            var error = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"Pixel harness {label} {type} compile failed: {error}");
        }

        return shader;
    }

    private static void WriteArtifacts(GlPixelHarnessRun run, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var snapshot in run.Snapshots)
        {
            WritePng(snapshot, Path.Combine(directory, Sanitize(snapshot.Name) + ".png"));
        }

        foreach (var comparison in run.Comparisons.Where(item => !item.Passed))
        {
            var expected = run.Snapshots.Single(item => item.Name == comparison.ExpectedName);
            var actual = run.Snapshots.Single(item => item.Name == comparison.ActualName);
            var diff = new GlPixelSnapshot(
                comparison.ExpectedName + "-vs-" + comparison.ActualName + "-diff",
                expected.Width,
                expected.Height,
                expected.CreateDifferenceRgba(actual));
            WritePng(diff, Path.Combine(directory, Sanitize(diff.Name) + ".png"));
        }

        var report = new
        {
            schemaVersion = 1,
            run.TimestampUtc,
            run.VersionString,
            capabilities = run.Capabilities.FormatDiagnostic(),
            snapshots = run.Snapshots.Select(item => new
            {
                item.Name,
                item.Width,
                item.Height,
                fingerprint = item.Fingerprint.ToString("X8", CultureInfo.InvariantCulture),
            }),
            comparisons = run.Comparisons.Select(item => new
            {
                item.ExpectedName,
                item.ActualName,
                item.Passed,
                item.DifferentPixels,
                item.DifferentChannels,
                item.MaximumChannelDifference,
                item.MeanAbsoluteError,
                item.RootMeanSquareError,
                item.MismatchBounds,
                diagnostic = item.FormatDiagnostic(),
            }),
            run.Diagnostics,
        };
        File.WriteAllText(
            Path.Combine(directory, "pixel-harness-report.json"),
            JsonSerializer.Serialize(report, ArtifactJsonOptions));
    }

    private static void WritePng(GlPixelSnapshot snapshot, string path)
    {
        using var image = Image.LoadPixelData<Rgba32>(snapshot.Rgba.Span, snapshot.Width, snapshot.Height);
        image.Save(path);
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

    private static PreviewMaterial LoadRepoBundledGroundMaterial()
    {
        var previewDirectory = Path.Combine(
            FindRepoRoot(),
            "src",
            "AutoPBR.App",
            "Assets",
            "Preview");
        using var albedoStream = File.OpenRead(Path.Combine(previewDirectory, "grass_block_top.png"));
        Assert.True(
            PreviewGrassTextureLoader.TryDecodeTinted(
                albedoStream,
                out var albedo,
                out var width,
                out var height));

        byte[] LoadMap(string fileName)
        {
            using var stream = File.OpenRead(Path.Combine(previewDirectory, fileName));
            Assert.True(
                PreviewGrassTextureLoader.TryDecodeRgba(
                    stream,
                    out var rgba,
                    out var mapWidth,
                    out var mapHeight));
            Assert.Equal((width, height), (mapWidth, mapHeight));
            return rgba;
        }

        var normal = LoadMap("grass_block_top_n.png");
        var specular = LoadMap("grass_block_top_s.png");
        var heightRgba = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            var source = normal[i * 4 + 3];
            var offset = i * 4;
            heightRgba[offset] = source;
            heightRgba[offset + 1] = source;
            heightRgba[offset + 2] = source;
            heightRgba[offset + 3] = 255;
        }

        return new PreviewMaterial
        {
            Width = width,
            Height = height,
            AlbedoRgba = albedo,
            NormalRgba = normal,
            SpecularRgba = specular,
            HeightRgba = heightRgba,
            GlUploadFlipRows = false,
        };
    }

    private static GroundFixture LoadGroundFixture()
    {
        var fallback = LoadRepoBundledGroundMaterial();
        var assetSource = Environment.GetEnvironmentVariable("AUTOPBR_TERRAIN_ASSET_SOURCE");
        var packSource = Environment.GetEnvironmentVariable("AUTOPBR_TERRAIN_PACK_SOURCE");
        if (string.IsNullOrWhiteSpace(assetSource) && string.IsNullOrWhiteSpace(packSource))
        {
            var aliases = new PreviewMaterial[PreviewTerrainGrassSlots.MaxCount];
            Array.Fill(aliases, fallback);
            return new GroundFixture(
                aliases,
                PreviewTerrainGrassBakeSettings.BuiltIn,
                PreviewTerrainVegetationBakePlan.Empty,
                null);
        }

        var options = new AutoPBROptions
        {
            FastSpecular = true,
            SpecularData = SpecularData.LoadFromFile(
                Path.Combine(
                    FindRepoRoot(),
                    "src",
                    "AutoPBR.Core",
                    "Data",
                    "textures_data.json")),
        };
        var grass = PreviewTerrainGrassKitResolver.TryResolveAsync(
                packSource,
                preferScannedPack: !string.IsNullOrWhiteSpace(packSource),
                assetSource,
                options)
            .GetAwaiter()
            .GetResult();
        var vegetation = PreviewTerrainVegetationKitResolver.TryResolveAsync(
                packSource,
                preferScannedPack: !string.IsNullOrWhiteSpace(packSource),
                assetSource,
                options)
            .GetAwaiter()
            .GetResult();
        var count = Math.Max(
            PreviewTerrainGrassSlots.MaxCount,
            vegetation.HasAny ? vegetation.TotalSlotCount : PreviewTerrainGrassSlots.MaxCount);
        var materials = new PreviewMaterial?[count];
        PreviewMaterial Map(PreviewTextureMaps? maps, string? path, PreviewMaterial alias) =>
            maps is null ? alias : PreviewMaterialMapper.FromCoreMaps(maps, path);
        var top = Map(grass.Top, grass.TopArchivePath, fallback);
        materials[PreviewTerrainGrassSlots.Top] = top;
        materials[PreviewTerrainGrassSlots.Side] = Map(grass.Side, grass.SideArchivePath, top);
        materials[PreviewTerrainGrassSlots.Dirt] = Map(grass.Dirt, grass.DirtArchivePath, top);
        materials[PreviewTerrainGrassSlots.Overlay] =
            Map(grass.Overlay, grass.OverlayArchivePath, top);
        materials[PreviewTerrainGrassSlots.Stone] = Map(grass.Stone, grass.StoneArchivePath, top);
        materials[PreviewTerrainGrassSlots.Sand] = Map(grass.Sand, grass.SandArchivePath, top);
        materials[PreviewTerrainGrassSlots.Gravel] = Map(grass.Gravel, grass.GravelArchivePath, top);
        foreach (var species in vegetation.Species)
        {
            materials[species.LogSlot] =
                PreviewMaterialMapper.FromCoreMaps(species.LogMaps, species.LogArchivePath);
            materials[species.LeavesOrTopSlot] =
                PreviewMaterialMapper.FromCoreMaps(
                    species.LeavesOrTopMaps,
                    species.LeavesOrTopArchivePath);
            if (species is { LogTopSlot: { } topSlot, LogTopMaps: { } logTopMaps })
            {
                materials[topSlot] =
                    PreviewMaterialMapper.FromCoreMaps(logTopMaps, species.LogTopArchivePath);
            }
        }

        var finalized = new PreviewMaterial[materials.Length];
        for (var i = 0; i < finalized.Length; i++)
        {
            finalized[i] = materials[i] ?? top;
        }

        return new GroundFixture(
            finalized,
            PreviewTerrainGrassBakeSettings.FromKit(grass, vegetation),
            vegetation.HasAny ? vegetation.ToBakePlan() : PreviewTerrainVegetationBakePlan.Empty,
            vegetation.CutoutBySlot);
    }

    private sealed record GroundFixture(
        PreviewMaterial[] Materials,
        PreviewTerrainGrassBakeSettings BakeSettings,
        PreviewTerrainVegetationBakePlan VegetationPlan,
        bool[]? CutoutBySlot);

    private static string? ResolveArtifactDirectory(GlPixelHarnessRun run)
    {
        var configured = Environment.GetEnvironmentVariable(ArtifactDirectoryEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(FindRepoRoot(), configured));
        }

        return run.Comparisons.Any(item => !item.Passed)
            ? Path.Combine(
                Path.GetTempPath(),
                "AutoPBR-pixel-harness-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture))
            : null;
    }

    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        foreach (var start in new[]
                 {
                     Path.GetDirectoryName(sourceFilePath),
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                 })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AutoPBR.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static bool IsEnabled()
    {
        return IsTruthy(Environment.GetEnvironmentVariable(EnableHarnessEnv)) ||
               IsTruthy(Environment.GetEnvironmentVariable(EnableLiveSmokeEnv));
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record GlPixelHarnessRun(
        DateTimeOffset TimestampUtc,
        string VersionString,
        PreviewGlCapabilities Capabilities,
        GlPixelSnapshot Baseline,
        IReadOnlyList<GlPixelSnapshot> Snapshots,
        IReadOnlyList<GlPixelComparison> Comparisons,
        string[] Diagnostics);

    private const string SubmissionVertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 2) in vec2 aUv;
        out vec2 vPosition;
        out vec2 vUv;
        void main()
        {
            vPosition = aPosition.xy;
            vUv = aUv;
            gl_Position = vec4(aPosition, 1.0);
        }
        """;

    private const string SubmissionFragmentSource = """
        #version 330 core
        in vec2 vPosition;
        in vec2 vUv;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec3 baseColor;
            if (vPosition.x < 0.0 && vPosition.y < 0.0)
                baseColor = vec3(0.92, 0.18, 0.10);
            else if (vPosition.x >= 0.0 && vPosition.y < 0.0)
                baseColor = vec3(0.12, 0.82, 0.28);
            else if (vPosition.x < 0.0)
                baseColor = vec3(0.12, 0.35, 0.94);
            else
                baseColor = vec3(0.93, 0.76, 0.12);
            float checker = mod(floor(vUv.x * 8.0) + floor(vUv.y * 8.0), 2.0);
            outColor = vec4(baseColor * mix(0.72, 1.0, checker), 1.0);
        }
        """;

    private const string TextureVertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 2) in vec2 aUv;
        out vec2 vUv;
        void main()
        {
            vUv = aUv;
            gl_Position = vec4(aPosition, 1.0);
        }
        """;

    private const string LegacyTextureFragmentSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTexture0;
        uniform sampler2D uTexture1;
        uniform sampler2D uTexture2;
        uniform sampler2D uTexture3;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            int layer = (vUv.x >= 0.5 ? 1 : 0) + (vUv.y >= 0.5 ? 2 : 0);
            vec2 localUv = fract(vUv * 2.0);
            if (layer == 0) outColor = texture(uTexture0, localUv);
            else if (layer == 1) outColor = texture(uTexture1, localUv);
            else if (layer == 2) outColor = texture(uTexture2, localUv);
            else outColor = texture(uTexture3, localUv);
        }
        """;

    private const string ArrayTextureFragmentSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2DArray uTextures;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            int layer = (vUv.x >= 0.5 ? 1 : 0) + (vUv.y >= 0.5 ? 2 : 0);
            vec2 localUv = fract(vUv * 2.0);
            outColor = texture(uTextures, vec3(localUv, float(layer)));
        }
        """;
}
