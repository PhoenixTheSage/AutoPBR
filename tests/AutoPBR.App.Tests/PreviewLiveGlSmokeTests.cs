using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.Preview;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace AutoPBR.App.Tests;

public sealed class PreviewLiveGlSmokeTests
{
    private const string EnableLiveSmokeEnv = "AUTOPBR_RUN_LIVE_GL_SMOKE";
    private const string ReportPathEnv = "AUTOPBR_P23_SMOKE_REPORT";

    [Fact]
    public void P23_HiddenWglContext_ReportsAndCompilesDesktopAccelerationLanes()
    {
        if (!IsEnabled())
        {
            return;
        }

        Assert.True(OperatingSystem.IsWindows(), "P2.3 live WGL smoke requires Windows.");

        var diagnostics = new List<string>();
        var profiles = new[]
        {
            new GlVersion(GlProfileType.OpenGL, 4, 6),
            new GlVersion(GlProfileType.OpenGL, 4, 0),
            new GlVersion(GlProfileType.OpenGL, 3, 3),
        };

        using var context = PreviewDesktopWglContext.TryCreate(
            profiles,
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);

        Assert.NotNull(context);
        var report = context!.Invoke(() =>
        {
            using (context.BindOnOwnerThread())
            {
                context.EnsureRenderTargetCore(64, 64);
                var gl = context.Gl;
                var caps = PreviewGlCapabilities.FromGl(gl, useOpenGlEs: false, context.VersionString);
                diagnostics.Add(caps.FormatDiagnostic());
                diagnostics.Add("[3D preview] P2.3 WGL context suffix: " + caps.FormatContextSuffix());

                CompileDesktopGenesisVariant(gl, caps, diagnostics);
                CompileComputeFroxelVariantIfSupported(gl, caps, diagnostics);
                CompareFragmentAndComputeFroxelInjectIfSupported(gl, caps, diagnostics);
                UploadIndirectDrawCommandsIfSupported(gl, caps, diagnostics);
                RunGpuCommandCompactorIfSupported(gl, caps, diagnostics);
                RunImageHistogramIfSupported(gl, caps, diagnostics);
                RunMaterialTextureArrayIfSupported(gl, caps, diagnostics);
                RunPersistentOverlayUploadIfSupported(gl, caps, diagnostics);
                RunGpuTimerQueryIfSupported(gl, caps, diagnostics);
                EvaluateShaderToolchainPlan(caps, diagnostics);
                RunSeparableProgramPipelineIfSupported(gl, caps, diagnostics);

                return new LiveGlSmokeReport(
                    context.VersionString,
                    caps.FormatDiagnostic(),
                    caps.FormatContextSuffix(),
                    caps.CanUsePersistentUploadRing,
                    caps.CanUseEntitySkinningSsbo,
                    caps.CanUseMaterialDrawRecordSsbo,
                    caps.CanUseComputeFroxelInject,
                    caps.CanUseIndirectDrawCommands,
                    caps.CanUseMultiDrawIndirectGroups,
                    caps.CanUseGpuCommandCompaction,
                    caps.CanUseGpuBatchCulling,
                    caps.CanUseGpuCompactedDrawSubmission,
                    caps.CanUseGpuReductionDiagnostics,
                    caps.CanUseImageHistogram,
                    caps.CanUseMaterialTextureArrays,
                    caps.CanUseGpuTimerQueries,
                    caps.CanUseSpirVShaderBinaries,
                    caps.CanUseSeparableShaderPrograms,
                    diagnostics.ToArray());
            }
        }, TimeSpan.FromSeconds(30));

        Assert.Contains("persistentUpload=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("entitySsbo=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("materialDrawSsbo=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("computeFroxels=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("indirectDraws=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("multiDrawGroups=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("gpuCommandCompaction=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("gpuBatchCulling=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("gpuCompactedDraws=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("gpuReductions=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("imageHistogram=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("materialTextureArrays=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("gpuTimers=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        Assert.Contains("separablePrograms=", report.CapabilityDiagnostic, StringComparison.Ordinal);
        if (report.ComputeFroxels)
        {
            Assert.Contains("compute froxels", report.ContextSuffix, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("fragment froxels", report.ContextSuffix, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            report.MultiDrawGroups ? "multi-draw groups" : report.IndirectDrawCommands ? "indirect draws" : "direct draws",
            report.ContextSuffix,
            StringComparison.OrdinalIgnoreCase);

        WriteReport(report);
    }

    private static void CompileDesktopGenesisVariant(GL gl, PreviewGlCapabilities caps, List<string> diagnostics)
    {
        var defines = OpenGlPreviewBackend.TestBuildGenesisProgramDefines(
            caps.CanUseEntitySkinningSsbo,
            caps.CanUseMaterialDrawRecordSsbo,
            drawRecordBaseInstance: caps.CanUseMultiDrawIndirectGroups,
            materialTextureArrays: caps.CanUseMaterialTextureArrays);
        var ctx = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
        using var program = ctx.CreateProgram(
            "genesis.vert",
            "genesis.frag",
            out var error,
            "p23-smoke-genesis",
            defines);

        Assert.True(program.IsValid, "Desktop Genesis variant failed to compile: " + error);
        diagnostics.Add("[3D preview] P2.3 desktop Genesis variant compiled.");

        using var shadowProgram = ctx.CreateProgram(
            "genesis_shadow.vert",
            "genesis_shadow.frag",
            out var shadowError,
            "p41-smoke-genesis-shadow",
            defines);

        Assert.True(shadowProgram.IsValid, "Desktop Genesis shadow variant failed to compile: " + shadowError);
        diagnostics.Add("[3D preview] P4.1 desktop Genesis base-instance shadow variant compiled.");

        if (caps.CanUseMaterialTextureArrays && caps.CanUseMultiDrawIndirectGroups)
        {
            using var tessProgram = ctx.CreateProgram(
                "genesis.vert",
                "genesis.tcs",
                "genesis.tes",
                "genesis.frag",
                out var tessError,
                "post-roadmap-tess-draw-record-array",
                defines);
            Assert.True(tessProgram.IsValid,
                "Tessellated draw-record/texture-array Genesis variant failed to compile: " + tessError);
            diagnostics.Add(
                "[3D preview] Tessellated Genesis draw-record/base-instance/texture-array variant compiled.");
        }
    }

    private static void RunMaterialTextureArrayIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseMaterialTextureArrays)
        {
            diagnostics.Add("[3D preview] P7 material texture-array live check skipped (capability off).");
            return;
        }

        using var array = new GlTexture2DArray(gl);
        var rgba = new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 0, 255,
        };
        Assert.True(array.UploadRgbaIfChanged(2, 1, 2, rgba, nearest: true));

        var fbo = gl.GenFramebuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTextureLayer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                array.Id,
                0,
                1);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            var actual = new byte[8];
            unsafe
            {
                fixed (byte* p = actual)
                {
                    gl.ReadPixels(0, 0, 2, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }

            Assert.Equal(rgba.AsSpan(8, 8).ToArray(), actual);
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
        }

        diagnostics.Add("[3D preview] P7 material texture-array upload/readback matched layer 1.");
    }

    private static void RunPersistentOverlayUploadIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUsePersistentUploadRing)
        {
            return;
        }

        using var overlay = new GlNativeOverlayRenderer(
            gl,
            useOpenGlEs: false,
            preferPersistentUpload: true,
            out var error);
        Assert.True(overlay.IsValid, "Persistent overlay renderer failed: " + error);
        Assert.True(overlay.UsesPersistentVertexUpload);
        while (gl.GetError() != GLEnum.NoError)
        {
        }
        var pixels = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
        overlay.Draw(64, 64, 2, new PreviewNativeWglOverlayBitmap(4, 4, pixels), null);
        gl.Finish();
        Assert.Equal(GLEnum.NoError, gl.GetError());
        diagnostics.Add("[3D preview] Persistent mapped overlay VBO ring rendered without GL errors.");
    }

    private static void RunGpuTimerQueryIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseGpuTimerQueries)
        {
            diagnostics.Add("[3D preview] P8 GPU timer query live check skipped (capability off).");
            return;
        }

        using var profiler = new GlGpuTimerProfiler(gl);
        Assert.True(profiler.BeginFrame());
        Assert.True(profiler.TryBeginScope(GlGpuTimerScope.Setup));
        gl.ClearColor(0.02f, 0.03f, 0.04f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        profiler.EndScope(GlGpuTimerScope.Setup);
        profiler.EndFrame();

        gl.Finish();
        Assert.True(profiler.BeginFrame());
        profiler.EndFrame();
        Assert.True(profiler.TryTakeLatestSnapshot(out var snapshot));
        Assert.True(snapshot.SetupMs >= 0.0);
        Assert.Contains("GPU ", snapshot.FormatHudLine(), StringComparison.Ordinal);
        diagnostics.Add("[3D preview] P8 GPU timer query returned a non-blocking pass snapshot: " +
                        snapshot.FormatDiagnostic() + ".");
    }

    private static void EvaluateShaderToolchainPlan(
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        var plan = GlShaderToolchainPlan.FromCapabilities(caps, GlSpirVShaderManifest.Bundled.Count);
        Assert.Equal(GlShaderToolchainPlan.PrimaryPath, "GLSL source + program binary cache");
        Assert.Equal(caps.CanUseSeparableShaderPrograms, plan.CanEvaluateSeparablePrograms);
        Assert.Equal(caps.CanUseSpirVShaderBinaries, plan.CanUseSpirVAssets);
        Assert.Equal(caps.CanUseSpirVShaderBinaries ? "ready" : "unsupported", plan.SpirVStatus);
        diagnostics.Add(plan.FormatDiagnostic());
        diagnostics.Add("[3D preview] P9 shader toolchain evaluation complete: " +
                        $"spirv={plan.SpirVStatus}, separable={plan.SeparableProgramStatus}, primary=GLSL.");
    }

    private static void RunSeparableProgramPipelineIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseSeparableShaderPrograms)
        {
            diagnostics.Add("[3D preview] P9 separable program pipeline live check skipped (capability off).");
            return;
        }

        const string vertexSource = """
            #version 410 core
            out gl_PerVertex
            {
                vec4 gl_Position;
            };

            void main()
            {
                vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
                gl_Position = vec4(p, 0.0, 1.0);
            }
            """;
        const string fragmentSource = """
            #version 410 core
            layout(location = 0) out vec4 outColor;
            void main()
            {
                outColor = vec4(0.25, 0.5, 1.0, 1.0);
            }
            """;

        var vertexProgram = gl.CreateShaderProgram(ShaderType.VertexShader, 1, [vertexSource]);
        var fragmentProgram = gl.CreateShaderProgram(ShaderType.FragmentShader, 1, [fragmentSource]);
        var pipeline = 0u;
        try
        {
            AssertLinked(gl, vertexProgram, "P9 separable vertex program");
            AssertLinked(gl, fragmentProgram, "P9 separable fragment program");
            pipeline = gl.GenProgramPipeline();
            gl.UseProgramStages(pipeline, UseProgramStageMask.VertexShaderBit, vertexProgram);
            gl.UseProgramStages(pipeline, UseProgramStageMask.FragmentShaderBit, fragmentProgram);
            gl.ValidateProgramPipeline(pipeline);
            gl.GetProgramPipeline(pipeline, (GLEnum)0x8B83, out int valid);
            Assert.NotEqual(0, valid);
            diagnostics.Add("[3D preview] P9 separable program pipeline validated with GLSL stage programs.");
        }
        finally
        {
            gl.BindProgramPipeline(0);
            if (pipeline != 0)
            {
                gl.DeleteProgramPipeline(pipeline);
            }

            if (vertexProgram != 0)
            {
                gl.DeleteProgram(vertexProgram);
            }

            if (fragmentProgram != 0)
            {
                gl.DeleteProgram(fragmentProgram);
            }
        }
    }

    private static void AssertLinked(GL gl, uint program, string label)
    {
        Assert.NotEqual(0u, program);
        gl.GetProgram(program, GLEnum.LinkStatus, out var linked);
        Assert.True(linked != 0, $"{label}: {gl.GetProgramInfoLog(program)}");
    }

    private static void CompileComputeFroxelVariantIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseComputeFroxelInject)
        {
            diagnostics.Add("[3D preview] P2.3 compute froxel compile skipped; capability gate is off.");
            return;
        }

        var ctx = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
        using var program = ctx.CreateComputeProgram(
            "genesis_volume_inject.comp",
            out var error,
            "p23-smoke-volume-inject-compute");

        Assert.True(program.IsValid, "Compute froxel injector failed to compile: " + error);
        diagnostics.Add("[3D preview] P2.3 compute froxel injector compiled.");
    }

    private static void CompareFragmentAndComputeFroxelInjectIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseComputeFroxelInject)
        {
            diagnostics.Add("[3D preview] P3 froxel parity skipped; compute/image-store capability gate is off.");
            return;
        }

        const int width = 32;
        const int height = 24;
        const int slices = 8;

        using var fragmentTarget = new GlVolumeFroxelTarget(gl, useOpenGlEs: false);
        using var computeTarget = new GlVolumeFroxelTarget(gl, useOpenGlEs: false);
        Assert.True(fragmentTarget.EnsureSize(width, height, slices), "Fragment froxel target failed to initialize.");
        Assert.True(computeTarget.EnsureSize(width, height, slices), "Compute froxel target failed to initialize.");

        var ctx = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
        using var fragmentProgram = ctx.CreateProgram(
            "genesis_godrays.vert",
            "genesis_volume_inject.frag",
            out var fragmentError,
            "p3-smoke-volume-inject-fragment");
        using var computeProgram = ctx.CreateComputeProgram(
            "genesis_volume_inject.comp",
            out var computeError,
            "p3-smoke-volume-inject-compute");

        Assert.True(fragmentProgram.IsValid, "Fragment froxel injector failed to compile: " + fragmentError);
        Assert.True(computeProgram.IsValid, "Compute froxel injector failed to compile: " + computeError);

        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            ApplyFixedFroxelSceneUniforms(gl, fragmentProgram, width, height, slices, isCompute: false);
            gl.BindVertexArray(quadVao);
            for (var layer = 0; layer < slices; layer++)
            {
                Assert.True(fragmentTarget.BindDrawLayer(layer), $"Fragment target layer {layer} failed to bind.");
                gl.Clear(ClearBufferMask.ColorBufferBit);
                SetUniform1(gl, fragmentProgram, "uSliceIndex", layer);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            }

            fragmentTarget.Unbind();

            Assert.True(computeTarget.BindImagesForCompute(0, 1), "Compute target failed to bind image outputs.");
            ApplyFixedFroxelSceneUniforms(gl, computeProgram, width, height, slices, isCompute: true);
            gl.DispatchCompute((uint)((width + 7) / 8), (uint)((height + 7) / 8), (uint)slices);
            gl.MemoryBarrier(0x00000020 | 0x00000008);

            var fragmentRgba = ReadArrayAttachment(gl, fragmentTarget, width, height, slices, ReadBufferMode.ColorAttachment0, PixelFormat.Rgba, 4);
            var computeRgba = ReadArrayAttachment(gl, computeTarget, width, height, slices, ReadBufferMode.ColorAttachment0, PixelFormat.Rgba, 4);
            var fragmentOcc = ReadArrayAttachment(gl, fragmentTarget, width, height, slices, ReadBufferMode.ColorAttachment1, PixelFormat.Red, 1);
            var computeOcc = ReadArrayAttachment(gl, computeTarget, width, height, slices, ReadBufferMode.ColorAttachment1, PixelFormat.Red, 1);

            AssertWithinByteTolerance(fragmentRgba, computeRgba, tolerance: 1, "froxel RGBA");
            AssertWithinByteTolerance(fragmentOcc, computeOcc, tolerance: 1, "froxel occupancy");

            diagnostics.Add(
                "[3D preview] P3 fragment-vs-compute froxel inject parity passed " +
                $"({width}x{height}x{slices}, rgbaHash={HashBytes(computeRgba):X8}, occHash={HashBytes(computeOcc):X8}).");
        }
        finally
        {
            if (quadVbo != 0)
            {
                gl.DeleteBuffer(quadVbo);
            }

            if (quadVao != 0)
            {
                gl.DeleteVertexArray(quadVao);
            }
        }
    }

    private static uint CreateFullscreenQuad(GL gl, out uint vbo)
    {
        var vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        ReadOnlySpan<float> verts =
        [
            -1f, -1f,
             1f, -1f,
             1f,  1f,
            -1f, -1f,
             1f,  1f,
            -1f,  1f,
        ];
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
        return vao;
    }

    private static void UploadIndirectDrawCommandsIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseIndirectDrawCommands)
        {
            diagnostics.Add("[3D preview] P4 indirect draw command upload skipped; capability gate is off.");
            return;
        }

        using var commands = new GlIndirectDrawCommandBuffer(gl);
        PreviewDrawBatch[] batches =
        [
            new(0, 3, 0),
            new(3, 6, 1),
        ];

        Assert.True(commands.Upload(batches), "Indirect draw command buffer upload failed.");
        Assert.True(commands.IsValid, "Indirect draw command buffer did not become valid after upload.");
        Assert.Equal(batches.Length, commands.CommandCount);
        commands.Bind();
        commands.Unbind();
        diagnostics.Add("[3D preview] P4 indirect draw command buffer upload passed (2 commands).");
    }

    private static void RunGpuCommandCompactorIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseGpuCommandCompaction)
        {
            diagnostics.Add("[3D preview] P5 GPU command compaction skipped; capability gate is off.");
            return;
        }

        var ctx = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
        using var program = ctx.CreateComputeProgram(
            "genesis_indirect_compact.comp",
            out var error,
            "p5-smoke-indirect-compact");

        Assert.True(program.IsValid, "GPU indirect command compactor failed to compile: " + error);

        using var source = new GlIndirectDrawCommandBuffer(gl);
        PreviewDrawBatch[] batches =
        [
            new(0, 3, 0),
            new(3, 6, 1),
            new(9, 0, 1),
            new(9, 12, 0),
        ];
        Assert.True(source.Upload(batches), "Source indirect command upload failed.");

        using var compactor = new GlGpuDrawCommandCompactor(gl);
        Assert.True(
            compactor.Dispatch(
                program,
                source,
                [1u, 0u, 1u, 1u],
                readBackCounter: true,
                collectDiagnostics: true),
            "GPU indirect command compaction dispatch failed.");
        Assert.Equal(2, compactor.LastVisibleCount);
        var flagDiagnostics = compactor.ReadReductionDiagnostics();
        Assert.Equal(
            new GlGpuDrawReductionSnapshot(4, 2, 0, 0, 1, 1, 0, 12),
            flagDiagnostics);
        Assert.True(flagDiagnostics.IsConsistent);

        var dwords = compactor.ReadOutputCommandDwords(compactor.LastVisibleCount);
        Assert.Equal(
            [3u, 1u, 0u, 0u, 0u, 12u, 1u, 9u, 0u, 3u],
            dwords);
        diagnostics.Add("[3D preview] P5 GPU indirect command compaction passed (4 source commands -> 2 visible commands).");

        Assert.True(
            compactor.Dispatch(
                program,
                source,
                [1u, 1u, 1u, 1u],
                readBackCounter: true,
                collectDiagnostics: true,
                preserveOrder: true),
            "Stable GPU alpha command compaction dispatch failed.");
        Assert.Equal(3, compactor.LastVisibleCount);
        var stableDwords = compactor.ReadOutputCommandDwords(compactor.LastVisibleCount);
        Assert.Equal(
            [
                3u, 1u, 0u, 0u, 0u,
                6u, 1u, 3u, 0u, 1u,
                12u, 1u, 9u, 0u, 3u,
            ],
            stableDwords);
        diagnostics.Add(
            "[3D preview] Ordered GPU alpha compaction retained source/base-instance order.");

        PreviewDrawBatch[] cullBatches =
        [
            new(0, 3, 0)
            {
                BoundsCenter = Vector3.Zero,
                BoundsRadius = 0.25f,
            },
            new(3, 6, 1)
            {
                BoundsCenter = new Vector3(3f, 0f, 0f),
                BoundsRadius = 0.25f,
            },
            new(9, 0, 1)
            {
                BoundsCenter = Vector3.Zero,
                BoundsRadius = 0.25f,
            },
            new(9, 12, 0)
            {
                BoundsCenter = new Vector3(0f, 0f, -5f),
                BoundsRadius = 0.25f,
                LodMaxDistance = 3f,
            },
            new(21, 9, 0)
            {
                BoundsCenter = new Vector3(0f, 0f, -2f),
                BoundsRadius = 0.25f,
            },
        ];
        using var cullSource = new GlIndirectDrawCommandBuffer(gl);
        Assert.True(cullSource.Upload(cullBatches), "GPU culling source indirect command upload failed.");
        Vector4[] planes =
        [
            new( 1f,  0f,  0f, 1f),
            new(-1f,  0f,  0f, 1f),
            new( 0f,  1f,  0f, 1f),
            new( 0f, -1f,  0f, 1f),
            new( 0f,  0f,  1f, 10f),
            new( 0f,  0f, -1f, 1f),
        ];
        Assert.True(
            compactor.DispatchWithGpuCulling(
                program,
                cullSource,
                cullBatches,
                planes,
                Vector3.Zero,
                readBackCounter: true,
                collectDiagnostics: true),
            "GPU indirect command culling dispatch failed.");
        Assert.Equal(2, compactor.LastVisibleCount);
        var cullDiagnostics = compactor.ReadReductionDiagnostics();
        Assert.Equal(
            new GlGpuDrawReductionSnapshot(5, 2, 1, 1, 1, 0, 0, 9),
            cullDiagnostics);
        Assert.True(cullDiagnostics.IsConsistent);

        var culledDwords = compactor.ReadOutputCommandDwords(compactor.LastVisibleCount);
        Assert.Equal(
            [3u, 1u, 0u, 0u, 0u, 9u, 1u, 21u, 0u, 4u],
            culledDwords);
        diagnostics.Add("[3D preview] P5.1 GPU batch bounds culling passed (5 source commands -> 2 visible commands).");

        if (caps.CanUseGpuReductionDiagnostics)
        {
            Assert.True(compactor.Dispatch(
                program,
                source,
                [1u, 1u, 1u, 1u],
                readBackCounter: true,
                collectDiagnostics: true,
                outputCapacity: 1));
            Assert.Equal(1, compactor.LastVisibleCount);
            var overflowDiagnostics = compactor.ReadReductionDiagnostics();
            Assert.Equal(
                new GlGpuDrawReductionSnapshot(4, 1, 0, 0, 1, 0, 2, 12),
                overflowDiagnostics);
            Assert.True(overflowDiagnostics.IsConsistent);
            diagnostics.Add(
                "[3D preview] P6.0 bounded GPU draw reductions passed: " +
                overflowDiagnostics.FormatDiagnostic() + ".");
        }

        if (caps.CanUseGpuCompactedDrawSubmission)
        {
            using var mesh = new GlMeshBuffer(gl);
            Assert.True(mesh.SupportsIndirectCount, "GL 4.6 indirect-count entry point did not resolve.");
            float[] triangleVertices =
            [
                -0.5f, -0.5f, 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f,
                 0.5f, -0.5f, 0f, 0f, 0f, 1f, 1f, 0f, 1f, 0f, 0f, 1f,
                 0.0f,  0.5f, 0f, 0f, 0f, 1f, 0.5f, 1f, 1f, 0f, 0f, 1f,
            ];
            mesh.Upload(triangleVertices, [0u, 1u, 2u]);
            PreviewDrawBatch[] drawBatches =
            [
                new(0, 3, 0),
                new(0, 3, 0),
                new(0, 3, 0),
                new(0, 3, 0),
            ];
            using var drawSource = new GlIndirectDrawCommandBuffer(gl);
            Assert.True(drawSource.Upload(drawBatches));
            Assert.True(compactor.Dispatch(program, drawSource, [1u, 0u, 1u, 1u]));
            using var drawProgram = CreateMinimalDrawProgram(gl);
            drawProgram.Use();
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            Assert.True(mesh.MultiDrawIndirectCount(
                compactor.OutputCommands,
                compactor.CounterBufferHandle,
                drawBatches.Length));
            gl.Finish();
            Assert.Equal(GLEnum.NoError, gl.GetError());
            diagnostics.Add(
                "[3D preview] P5.2 GPU indirect-count submission executed without CPU counter readback " +
                "(4 source commands -> 3 submitted draws).");
        }
    }

    private static void RunImageHistogramIfSupported(
        GL gl,
        PreviewGlCapabilities caps,
        List<string> diagnostics)
    {
        if (!caps.CanUseImageHistogram)
        {
            diagnostics.Add("[3D preview] P6.1 image histogram skipped; capability gate is off.");
            return;
        }

        const int width = 16;
        const int height = 8;
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = (byte)(i * 17);
            rgba[i * 4 + 1] = (byte)(255 - i * 11);
            rgba[i * 4 + 2] = (byte)(i * 29);
            rgba[i * 4 + 3] = 255;
        }

        var texture = gl.GenTexture();
        var fbo = gl.GenFramebuffer();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, texture);
            unsafe
            {
                fixed (byte* ptr = rgba)
                {
                    gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                        PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
                }
            }
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, texture, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            var rgb = GlFramebufferReadback.TryReadRgb8(gl, 0, 0, width, height);
            Assert.NotNull(rgb);
            var cpu = GlLuminanceHistogramSnapshot.FromRgb8(rgb!, width, height);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, 0);

            var ctx = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
            using var program = ctx.CreateComputeProgram(
                "genesis_luminance_histogram.comp",
                out var error,
                "p61-smoke-luminance-histogram");
            Assert.True(program.IsValid, "P6.1 image histogram failed to compile: " + error);

            using var histogram = new GlImageLuminanceHistogram(gl);
            Assert.True(histogram.Dispatch(program, texture, width, height, out var gpu));
            Assert.Equal(cpu.SampleCount, gpu.SampleCount);
            Assert.Equal(cpu.OverflowCount, gpu.OverflowCount);
            Assert.Equal(cpu.Bins, gpu.Bins);
            diagnostics.Add("[3D preview] P6.1 GPU image histogram matched FBO/readback fallback: " +
                            gpu.FormatDiagnostic() + ".");
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteTexture(texture);
        }
    }

    private static void ApplyFixedFroxelSceneUniforms(
        GL gl,
        GlShaderProgram program,
        int width,
        int height,
        int slices,
        bool isCompute)
    {
        program.Use();
        SetUniform3(gl, program, "uCameraPos", 0f, 2f, -10f);
        SetUniform3(gl, program, "uCamRight", 1f, 0f, 0f);
        SetUniform3(gl, program, "uCamUp", 0f, 1f, 0f);
        SetUniform3(gl, program, "uCamForward", 0f, 0f, 1f);
        SetUniform3(gl, program, "uLightDir", -0.35f, -0.8f, -0.25f);
        SetUniform3(gl, program, "uLightColor", 1f, 0.86f, 0.68f);
        SetUniform3(gl, program, "uHalfExtent", 9f, 6f, 18f);
        if (isCompute)
        {
            SetUniform3i(gl, program, "uFroxelSize", width, height, slices);
        }

        SetUniform1(gl, program, "uSliceCount", slices);
        SetUniform1(gl, program, "uDepthDistribution", 0.55f);
        SetUniform1(gl, program, "uLayerHeight", 0f);
        SetUniform1(gl, program, "uVolumeHeight", 9f);
        SetUniform1(gl, program, "uCloudDensity", 0.72f);
        SetUniform1(gl, program, "uVolumeSize", 48f);
        SetUniform1(gl, program, "uGroundWorldY", -1f);
        SetUniform1(gl, program, "uFogSlabHeight", 0f);
        SetUniform1(gl, program, "uHeightFogStrength", 0f);
        SetUniform1(gl, program, "uDebugDensity", 0.03f);
        SetUniform1(gl, program, "uEnableShadowMap", 0);
        SetUniform1(gl, program, "uEnableShadowCascades", 0);
        SetUniform1(gl, program, "uCascadeSplitDistance", 12f);
        SetUniform1(gl, program, "uCascadeMidSplitDistance", 36f);
        SetUniform1(gl, program, "uCascadeBlendWidth", 2f);
        SetUniform1(gl, program, "uShadowDistance", 128f);
        SetUniform1(gl, program, "uShadowFadeStart", 108.8f);
        SetUniform1(gl, program, "uShadowMinBias", 0.001f);
        SetUniform2(gl, program, "uShadowTexelSize", 1f / 1024f, 1f / 1024f);
        SetUniform2(gl, program, "uShadowTexelSizeNear", 1f / 4096f, 1f / 4096f);
        SetUniform2(gl, program, "uShadowTexelSizeMid", 1f / 2048f, 1f / 2048f);
    }

    private static GlShaderProgram CreateMinimalDrawProgram(GL gl)
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec3 aPos;
            void main() { gl_Position = vec4(aPos, 1.0); }
            """;
        const string fragmentSource = """
            #version 330 core
            layout(location = 0) out vec4 outColor;
            void main() { outColor = vec4(1.0); }
            """;

        var vertex = CompileMinimalShader(gl, ShaderType.VertexShader, vertexSource);
        var fragment = CompileMinimalShader(gl, ShaderType.FragmentShader, fragmentSource);
        var handle = gl.CreateProgram();
        gl.AttachShader(handle, vertex);
        gl.AttachShader(handle, fragment);
        gl.LinkProgram(handle);
        gl.GetProgram(handle, GLEnum.LinkStatus, out var linked);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        Assert.NotEqual(0, linked);
        return new GlShaderProgram(gl, handle);
    }

    private static uint CompileMinimalShader(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        Assert.True(compiled != 0, gl.GetShaderInfoLog(shader));
        return shader;
    }

    private static byte[] ReadArrayAttachment(
        GL gl,
        GlVolumeFroxelTarget target,
        int width,
        int height,
        int slices,
        ReadBufferMode attachment,
        PixelFormat format,
        int bytesPerPixel)
    {
        var bytes = new byte[width * height * slices * bytesPerPixel];
        var layerBytes = width * height * bytesPerPixel;
        for (var layer = 0; layer < slices; layer++)
        {
            Assert.True(target.BindDrawLayer(layer), $"Readback layer {layer} failed to bind.");
            gl.ReadBuffer(attachment);
            unsafe
            {
                fixed (byte* p = bytes.AsSpan(layer * layerBytes, layerBytes))
                {
                    gl.ReadPixels(0, 0, (uint)width, (uint)height, format, PixelType.UnsignedByte, p);
                }
            }
        }

        target.Unbind();
        return bytes;
    }

    private static void AssertWithinByteTolerance(byte[] expected, byte[] actual, int tolerance, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        var maxDiff = 0;
        var offByMore = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var diff = Math.Abs(expected[i] - actual[i]);
            maxDiff = Math.Max(maxDiff, diff);
            if (diff > tolerance)
            {
                offByMore++;
            }
        }

        Assert.True(offByMore == 0,
            $"{label} mismatch: {offByMore}/{expected.Length} bytes exceeded tolerance {tolerance}; maxDiff={maxDiff}.");
    }

    private static uint HashBytes(ReadOnlySpan<byte> bytes)
    {
        const uint fnvPrime = 16777619;
        var hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= fnvPrime;
        }

        return hash;
    }

    private static void SetUniform1(GL gl, GlShaderProgram program, string name, int value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            program.Use();
            gl.Uniform1(loc, value);
        }
    }

    private static void SetUniform1(GL gl, GlShaderProgram program, string name, float value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            program.Use();
            gl.Uniform1(loc, value);
        }
    }

    private static void SetUniform2(GL gl, GlShaderProgram program, string name, float x, float y)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            program.Use();
            gl.Uniform2(loc, x, y);
        }
    }

    private static void SetUniform3(GL gl, GlShaderProgram program, string name, float x, float y, float z)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            program.Use();
            gl.Uniform3(loc, x, y, z);
        }
    }

    private static void SetUniform3i(GL gl, GlShaderProgram program, string name, int x, int y, int z)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            program.Use();
            gl.Uniform3(loc, x, y, z);
        }
    }

    private static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnableLiveSmokeEnv);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteReport(LiveGlSmokeReport report)
    {
        var path = Environment.GetEnvironmentVariable(ReportPathEnv);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(FindRepoRoot(), path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path,
        [
            "P2.3 live GL smoke",
            $"Timestamp UTC: {DateTimeOffset.UtcNow:O}",
            $"WGL version: {report.VersionString}",
            report.CapabilityDiagnostic,
            "Context suffix: " + report.ContextSuffix,
            $"persistentUpload: {(report.PersistentUpload ? "on" : "off")}",
            $"entitySsbo: {(report.EntitySsbo ? "on" : "off")}",
            $"materialDrawSsbo: {(report.MaterialDrawSsbo ? "on" : "off")}",
            $"computeFroxels: {(report.ComputeFroxels ? "on" : "off")}",
            $"indirectDraws: {(report.IndirectDrawCommands ? "on" : "off")}",
            $"multiDrawGroups: {(report.MultiDrawGroups ? "on" : "off")}",
            $"gpuCommandCompaction: {(report.GpuCommandCompaction ? "on" : "off")}",
            $"gpuBatchCulling: {(report.GpuBatchCulling ? "on" : "off")}",
            $"gpuCompactedDraws: {(report.GpuCompactedDraws ? "on" : "off")}",
            $"gpuReductions: {(report.GpuReductions ? "on" : "off")}",
            $"imageHistogram: {(report.ImageHistogram ? "on" : "off")}",
            $"materialTextureArrays: {(report.MaterialTextureArrays ? "on" : "off")}",
            $"gpuTimers: {(report.GpuTimers ? "on" : "off")}",
            $"spirvShaderBinaries: {(report.SpirVShaderBinaries ? "on" : "off")}",
            $"separableShaderPrograms: {(report.SeparableShaderPrograms ? "on" : "off")}",
            "",
            "Diagnostics:",
            .. report.Diagnostics,
            "",
            "ANGLE/GLES fallback coverage:",
            "Verified by PreviewGlCapabilitiesTests and PreviewGlslEsAdaptTests in the same test run.",
        ]);
    }

    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFilePath), AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AutoPBR.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private sealed record LiveGlSmokeReport(
        string VersionString,
        string CapabilityDiagnostic,
        string ContextSuffix,
        bool PersistentUpload,
        bool EntitySsbo,
        bool MaterialDrawSsbo,
        bool ComputeFroxels,
        bool IndirectDrawCommands,
        bool MultiDrawGroups,
        bool GpuCommandCompaction,
        bool GpuBatchCulling,
        bool GpuCompactedDraws,
        bool GpuReductions,
        bool ImageHistogram,
        bool MaterialTextureArrays,
        bool GpuTimers,
        bool SpirVShaderBinaries,
        bool SeparableShaderPrograms,
        string[] Diagnostics);
}
