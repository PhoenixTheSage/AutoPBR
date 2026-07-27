using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using AutoPBR.PreviewGpuAssets;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

using System.Numerics;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudLiveGlSmokeTests
{
    [Fact]
    public void HiddenWglContext_CompilesCurvedCloudShaders()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AUTOPBR_RUN_LIVE_GL_SMOKE"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [new GlVersion(GlProfileType.OpenGL, 4, 6), new GlVersion(GlProfileType.OpenGL, 3, 3)],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);

        context!.Invoke(() =>
        {
            using (context.BindOnOwnerThread())
            {
                var gl = context.Gl;
                var caps = PreviewGlCapabilities.FromGl(gl, useOpenGlEs: false, context.VersionString);
                var compile = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
                using var clouds = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds.frag",
                    out var cloudError,
                    "cloud-shell-live-smoke");
                Assert.True(clouds.IsValid, "Curved cloud shader failed to compile: " + cloudError);

                using var temporal = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds_temporal.frag",
                    out var temporalError,
                    "cloud-temporal-live-smoke");
                Assert.True(temporal.IsValid, "Cloud temporal shader failed to compile: " + temporalError);

                using var upsample = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds_upsample.frag",
                    out var upsampleError,
                    "cloud-upsample-live-smoke");
                Assert.True(upsample.IsValid, "Cloud upsample shader failed to compile: " + upsampleError);

                using var repair = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds_repair.frag",
                    out var repairError,
                    "cloud-edge-repair-live-smoke");
                Assert.True(repair.IsValid, "Cloud edge-repair shader failed to compile: " + repairError);

                using var fallbackComposite = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_godrays_composite.frag",
                    out var fallbackCompositeError,
                    "cloud-final-fallback-live-smoke");
                Assert.True(fallbackComposite.IsValid,
                    "Cloud final-composite fallback shader failed to compile: " + fallbackCompositeError);

                using var volumeIntegrate = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_volume_integrate.frag",
                    out var volumeIntegrateError,
                    "cloud-shared-volume-integrate-live-smoke");
                Assert.True(volumeIntegrate.IsValid,
                    "Cloud-aware volume integrate shader failed to compile: " + volumeIntegrateError);

                using var volumeIntegrateLite = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_volume_integrate_lite.frag",
                    out var volumeIntegrateLiteError,
                    "cloud-shared-volume-integrate-lite-live-smoke");
                Assert.True(volumeIntegrateLite.IsValid,
                    "Cloud-aware lite volume integrate shader failed to compile: " + volumeIntegrateLiteError);

                while (gl.GetError() != GLEnum.NoError)
                {
                }

                using var stbnTexture = new GlTexture3D(gl);
                stbnTexture.UploadR8(
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.Width,
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.Height,
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.FrameCount,
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.GenerateR8());
                Assert.Equal(
                    GLEnum.NoError,
                    gl.GetError());

                using var temporalTarget = new GlCloudTemporalRenderTarget(gl);
                using var temporalHistory = new GlCloudTemporalRenderTarget(gl);
                Assert.True(temporalTarget.EnsureSize(16, 16),
                    "Cloud temporal MRT failed to initialize.");
                Assert.True(temporalHistory.EnsureSize(16, 16),
                    "Cloud temporal history MRT failed to initialize.");
                Assert.True(temporalHistory.CopyFrom(temporalTarget),
                    "Cloud temporal MRT history copy failed.");

                var floatingProfile = GlCloudRenderFormatProfile.Select(
                    caps, PreviewVolumetricQuality.Medium);
                Assert.True(floatingProfile.UsesDirectMetadata);
                using var floatingTarget = new GlCloudTemporalRenderTarget(gl, floatingProfile);
                using var floatingHistory = new GlCloudTemporalRenderTarget(gl, floatingProfile);
                Assert.True(floatingTarget.EnsureSize(16, 16),
                    "CQ1 RGBA16F/RG32F cloud MRT failed to initialize.");
                Assert.True(floatingHistory.EnsureSize(16, 16),
                    "CQ1 RGBA16F/RG32F cloud history MRT failed to initialize.");
                floatingTarget.Clear();
                Assert.True(floatingHistory.CopyFrom(floatingTarget),
                    "CQ1 floating-point cloud MRT history copy failed.");
                Assert.False(floatingHistory.CopyFrom(temporalTarget),
                    "Cloud histories with different metadata ABIs must not be copied.");

                var momentProfile = GlCloudRenderFormatProfile.Select(
                    caps, PreviewVolumetricQuality.High);
                Assert.True(momentProfile.UsesTemporalMoments);
                using var momentTarget = new GlCloudTemporalRenderTarget(gl, momentProfile);
                using var momentHistory = new GlCloudTemporalRenderTarget(gl, momentProfile);
                Assert.True(momentTarget.EnsureSize(16, 16),
                    "CQ1.6 RG16F moment MRT failed to initialize.");
                Assert.True(momentHistory.EnsureSize(16, 16),
                    "CQ1.6 RG16F moment history failed to initialize.");
                Assert.Equal(3, momentTarget.AttachmentCount);
                Assert.NotEqual(0u, momentTarget.MomentTextureHandle);
                Assert.True(momentHistory.CopyFrom(momentTarget),
                    "CQ1.6 moment history copy failed.");

                var cinematicTraceSize = PreviewCloudTraceSizing.Resolve(
                    575, 455, PreviewVolumetricQuality.Cinematic);
                Assert.Equal((384, 304), (cinematicTraceSize.Width, cinematicTraceSize.Height));
                Assert.True(momentTarget.EnsureSize(
                    cinematicTraceSize.Width, cinematicTraceSize.Height));
                Assert.True(momentHistory.EnsureSize(
                    cinematicTraceSize.Width, cinematicTraceSize.Height));
                Assert.True(momentHistory.CopyFrom(momentTarget),
                    "CQ1.7 odd-viewport Cinematic target copy failed.");
                var highTraceSize = PreviewCloudTraceSizing.Resolve(
                    575, 455, PreviewVolumetricQuality.High);
                Assert.Equal((287, 227), (highTraceSize.Width, highTraceSize.Height));
                Assert.True(momentTarget.EnsureSize(highTraceSize.Width, highTraceSize.Height));
                Assert.True(momentHistory.EnsureSize(highTraceSize.Width, highTraceSize.Height));
                Assert.True(momentHistory.CopyFrom(momentTarget),
                    "CQ1.7 Cinematic-to-High target resize/copy failed.");

                ValidateCloudDepthOrdering(gl, upsample);
                ValidateDirectCloudDepthOrdering(gl, upsample);
                ValidateLinearCloudRadiance(gl, upsample);
                ValidateCloudTemporalMoments(gl, temporal);
                ValidateCloudEdgeRepair(gl, repair, stbnTexture);
            }

            return true;
        }, TimeSpan.FromSeconds(30));
    }

    private static void ValidateCloudDepthOrdering(GL gl, GlShaderProgram upsample)
    {
        const int cloudSize = 4;
        const int outputSize = 8;
        ReadOnlySpan<byte> clear = [7, 11, 19, 255];
        using var source = new GlCloudTemporalRenderTarget(gl);
        using var output = new GlPixelRenderHarness(gl, outputSize, outputSize);
        Assert.True(source.EnsureSize(cloudSize, cloudSize));

        var cloudColor = RepeatPixel(cloudSize, cloudSize, 220, 110, 48, 255);
        UploadRgba8(gl, source.ColorTextureHandle, cloudSize, cloudSize, cloudColor);

        var sceneDepthTexture = gl.GenTexture();
        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            // With identity inverse VP and camera z=-1, depth 0.5 reconstructs an opaque
            // receiver roughly one unit down the center ray.
            AllocateRgba8(gl, sceneDepthTexture, outputSize, outputSize,
                RepeatPixel(outputSize, outputSize, 128, 0, 0, 255));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

            ConfigureUpsampleUniforms(gl, upsample, cloudSize, directMetadata: false);
            var frontData = RepeatPackedDistance(cloudSize, cloudSize, 0.05f);
            UploadRgba8(gl, source.DataTextureHandle, cloudSize, cloudSize, frontData);
            var front = output.Capture("cloud-in-front-of-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            var behindData = RepeatPackedDistance(cloudSize, cloudSize, 10f);
            UploadRgba8(gl, source.DataTextureHandle, cloudSize, cloudSize, behindData);
            var behind = output.Capture("cloud-behind-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            Assert.True(front.CountPixelsOutside(clear, tolerance: 2) >= front.PixelCount * 3 / 4,
                "Cloud in front of opaque depth was incorrectly erased.");
            Assert.Equal(0, behind.CountPixelsOutside(clear, tolerance: 2));
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.DeleteTexture(sceneDepthTexture);
        }
    }

    private static void ValidateDirectCloudDepthOrdering(GL gl, GlShaderProgram upsample)
    {
        const int cloudSize = 4;
        const int outputSize = 8;
        ReadOnlySpan<byte> clear = [7, 11, 19, 255];
        using var source = new GlCloudTemporalRenderTarget(
            gl, GlCloudRenderFormatProfile.DesktopFloatingPoint);
        using var output = new GlPixelRenderHarness(gl, outputSize, outputSize);
        Assert.True(source.EnsureSize(cloudSize, cloudSize));

        var cloudColor = RepeatPixel(cloudSize, cloudSize, 220, 110, 48, 255);
        UploadRgba8(gl, source.ColorTextureHandle, cloudSize, cloudSize, cloudColor);

        var sceneDepthTexture = gl.GenTexture();
        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            AllocateRgba8(gl, sceneDepthTexture, outputSize, outputSize,
                RepeatPixel(outputSize, outputSize, 128, 0, 0, 255));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

            ConfigureUpsampleUniforms(gl, upsample, cloudSize, directMetadata: true);
            UploadRg32f(gl, source.DataTextureHandle, cloudSize, cloudSize,
                RepeatDirectMetadata(cloudSize, cloudSize, 0.05f, 0.5f));
            var front = output.Capture("direct-cloud-in-front-of-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            UploadRg32f(gl, source.DataTextureHandle, cloudSize, cloudSize,
                RepeatDirectMetadata(cloudSize, cloudSize, 10f, 0.5f));
            var behind = output.Capture("direct-cloud-behind-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            Assert.True(front.CountPixelsOutside(clear, tolerance: 2) >= front.PixelCount * 3 / 4,
                "Direct-metadata cloud in front of opaque depth was incorrectly erased.");
            Assert.Equal(0, behind.CountPixelsOutside(clear, tolerance: 2));
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.DeleteTexture(sceneDepthTexture);
        }
    }

    private static void ValidateLinearCloudRadiance(GL gl, GlShaderProgram upsample)
    {
        const int cloudSize = 4;
        const int outputSize = 8;
        using var source = new GlCloudTemporalRenderTarget(
            gl, GlCloudRenderFormatProfile.DesktopFloatingPoint);
        using var history = new GlCloudTemporalRenderTarget(
            gl, GlCloudRenderFormatProfile.DesktopFloatingPoint);
        using var output = new GlPixelRenderHarness(gl, outputSize, outputSize);
        Assert.True(source.EnsureSize(cloudSize, cloudSize));
        Assert.True(history.EnsureSize(cloudSize, cloudSize));

        UploadRgba32f(gl, source.ColorTextureHandle, cloudSize, cloudSize,
            RepeatRgbaFloat(cloudSize, cloudSize, 2.5f, 0.75f, 0.25f, 1f));
        UploadRg32f(gl, source.DataTextureHandle, cloudSize, cloudSize,
            RepeatDirectMetadata(cloudSize, cloudSize, 0.05f, 0.5f));
        Assert.True(history.CopyFrom(source), "Floating-point cloud history copy failed.");
        var copied = ReadTextureRgbaFloat(gl, history.ColorTextureHandle, cloudSize, cloudSize);
        Assert.InRange(copied[0], 2.45f, 2.55f);

        var sceneDepthTexture = gl.GenTexture();
        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            AllocateRgba8(gl, sceneDepthTexture, outputSize, outputSize,
                RepeatPixel(outputSize, outputSize, 255, 0, 0, 255));

            ConfigureUpsampleUniforms(
                gl,
                upsample,
                cloudSize,
                directMetadata: true,
                hasSceneDepth: false,
                hdrPresent: true,
                applyCloudEncoding: true);
            var hdr = output.Capture("linear-cloud-final-hdr", _ =>
                DrawUpsample(gl, upsample, quadVao, history, sceneDepthTexture));

            ConfigureUpsampleUniforms(
                gl,
                upsample,
                cloudSize,
                directMetadata: true,
                hasSceneDepth: false,
                hdrPresent: false,
                applyCloudEncoding: true);
            var sdr = output.Capture("linear-cloud-final-sdr", _ =>
                DrawUpsample(gl, upsample, quadVao, history, sceneDepthTexture));

            var hdrRed = hdr.GetRgbaSpan()[0];
            var sdrRed = sdr.GetRgbaSpan()[0];
            Assert.InRange(hdrRed, (byte)240, (byte)252);
            Assert.InRange(sdrRed, (byte)(hdrRed + 1), (byte)254);
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.DeleteTexture(sceneDepthTexture);
        }
    }

    private static void ValidateCloudTemporalMoments(GL gl, GlShaderProgram temporal)
    {
        const int cloudSize = 4;
        var profile = GlCloudRenderFormatProfile.DesktopFloatingPointMoments;
        using var current = new GlCloudTemporalRenderTarget(gl, profile);
        using var resolve = new GlCloudTemporalRenderTarget(gl, profile);
        using var history = new GlCloudTemporalRenderTarget(gl, profile);
        Assert.True(current.EnsureSize(cloudSize, cloudSize));
        Assert.True(resolve.EnsureSize(cloudSize, cloudSize));
        Assert.True(history.EnsureSize(cloudSize, cloudSize));

        // Premultiplied RGB corresponds to straight linear radiance (0.8, 0.4, 0.2).
        UploadRgba32f(gl, current.ColorTextureHandle, cloudSize, cloudSize,
            RepeatRgbaFloat(cloudSize, cloudSize, 0.4f, 0.2f, 0.1f, 0.5f));
        UploadRg32f(gl, current.DataTextureHandle, cloudSize, cloudSize,
            RepeatDirectMetadata(cloudSize, cloudSize, 1f, 0.5f));
        history.Clear();
        var invalidHistoryMoments = ReadTextureRgFloat(
            gl, history.MomentTextureHandle, cloudSize, cloudSize);
        Assert.InRange(invalidHistoryMoments[0], -1.01f, -0.99f);

        resolve.BindDraw();
        resolve.Clear();
        resolve.BindDraw();
        temporal.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, current.ColorTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, current.DataTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, history.ColorTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, history.DataTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture4);
        gl.BindTexture(TextureTarget.Texture2D, history.MomentTextureHandle);
        SetUniform1(gl, temporal, "uCurrentClouds", 0);
        SetUniform1(gl, temporal, "uCurrentCloudData", 1);
        SetUniform1(gl, temporal, "uHistoryClouds", 2);
        SetUniform1(gl, temporal, "uHistoryCloudData", 3);
        SetUniform1(gl, temporal, "uHistoryCloudMoments", 4);
        SetUniform1(gl, temporal, "uTemporalWeight", 0.72f);
        SetUniform1(gl, temporal, "uMomentSigma", 1.5f);
        SetUniform1(gl, temporal, "uMomentMinBand", 0.015f);
        SetUniform1(gl, temporal, "uHistoryConfidence", 0f);
        SetUniform1(gl, temporal, "uHasHistory", 0);
        SetUniform1(gl, temporal, "uHasMoments", 1);
        SetUniform1(gl, temporal, "uCloudDataDirect", 1);
        SetUniform2(gl, temporal, "uTexelSize", 1f / cloudSize, 1f / cloudSize);
        SetUniform2(gl, temporal, "uWindDelta", 0f, 0f);
        SetUniform2(gl, temporal, "uCirrusWindDelta", 0f, 0f);
        SetUniform3(gl, temporal, "uCameraPos", 0f, 0f, 0f);
        SetUniform3(gl, temporal, "uPrevCameraPos", 0f, 0f, 0f);
        SetIdentityMatrix(gl, temporal, "uInvViewProj");
        SetIdentityMatrix(gl, temporal, "uPrevViewProj");

        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            gl.BindVertexArray(quadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            var moments = ReadTextureRgFloat(
                gl, resolve.MomentTextureHandle, cloudSize, cloudSize);
            const float expectedLuminance = 0.4706f;
            Assert.InRange(moments[0], expectedLuminance - 0.005f, expectedLuminance + 0.005f);
            Assert.InRange(
                moments[1],
                expectedLuminance * expectedLuminance - 0.01f,
                expectedLuminance * expectedLuminance + 0.01f);

            Assert.True(history.CopyFrom(resolve));
            var copied = ReadTextureRgFloat(
                gl, history.MomentTextureHandle, cloudSize, cloudSize);
            Assert.InRange(copied[0], expectedLuminance - 0.005f, expectedLuminance + 0.005f);

            // Exercise the accepted-history branch as well as initial moment generation.
            resolve.BindDraw();
            resolve.Clear();
            resolve.BindDraw();
            temporal.Use();
            SetUniform1(gl, temporal, "uHasHistory", 1);
            SetUniform1(gl, temporal, "uHistoryConfidence", 0.5f);
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            gl.BindVertexArray(quadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            var accumulated = ReadTextureRgFloat(
                gl, resolve.MomentTextureHandle, cloudSize, cloudSize);
            Assert.All(accumulated, value => Assert.True(float.IsFinite(value)));
            Assert.True(accumulated[0] >= 0f);
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private static void ValidateCloudEdgeRepair(
        GL gl,
        GlShaderProgram repair,
        GlTexture3D texture3d)
    {
        const int sourceSize = 4;
        const int outputSize = 8;
        var profile = GlCloudRenderFormatProfile.DesktopFloatingPoint;
        using var source = new GlCloudTemporalRenderTarget(gl, profile);
        using var output = new GlCloudTemporalRenderTarget(gl, profile);
        Assert.True(source.EnsureSize(sourceSize, sourceSize));
        Assert.True(output.EnsureSize(outputSize, outputSize));

        var colors = new float[sourceSize * sourceSize * 4];
        var metadata = new float[sourceSize * sourceSize * 2];
        for (var y = 0; y < sourceSize; y++)
        {
            for (var x = 0; x < sourceSize; x++)
            {
                var colorIndex = (y * sourceSize + x) * 4;
                var alpha = x < sourceSize / 2 ? 0.05f : 0.65f;
                colors[colorIndex] = 0.3f * alpha;
                colors[colorIndex + 1] = 0.4f * alpha;
                colors[colorIndex + 2] = 0.5f * alpha;
                colors[colorIndex + 3] = alpha;
                var dataIndex = (y * sourceSize + x) * 2;
                metadata[dataIndex] = x < sourceSize / 2 ? 110f : 114f;
                metadata[dataIndex + 1] = 0.5f;
            }
        }

        UploadRgba32f(gl, source.ColorTextureHandle, sourceSize, sourceSize, colors);
        UploadRg32f(gl, source.DataTextureHandle, sourceSize, sourceSize, metadata);
        output.Clear();
        output.BindDraw(includeMoments: false);
        repair.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, source.ColorTextureHandle);
        SetUniform1(gl, repair, "uClouds", 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uCloudData", 1);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uSceneDepth", 2);
        texture3d.Bind(3);
        SetUniform1(gl, repair, "uCloudNoise", 3);
        texture3d.Bind(4);
        SetUniform1(gl, repair, "uDetailNoise", 4);
        texture3d.Bind(5);
        SetUniform1(gl, repair, "uCloudStbn", 5);
        gl.ActiveTexture(TextureUnit.Texture6);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uCoverageMap", 6);
        gl.ActiveTexture(TextureUnit.Texture7);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uSkyViewLut", 7);

        SetUniform2(gl, repair, "uCloudTexelSize", 1f / sourceSize, 1f / sourceSize);
        SetIdentityMatrix(gl, repair, "uInvViewProj");
        SetUniform3(gl, repair, "uCameraPos", 0f, 0f, -1f);
        SetUniform3(gl, repair, "uSunDir", -0.3f, -0.8f, -0.4f);
        SetUniform1(gl, repair, "uSunIntensity", 1f);
        SetUniform1(gl, repair, "uGroundWorldY", -100f);
        SetUniform1(gl, repair, "uPlanetRadius", 1f);
        SetUniform1(gl, repair, "uLayerHeight", 4.8f);
        SetUniform1(gl, repair, "uVolumeHeight", 60f);
        SetUniform1(gl, repair, "uDensity", 0.75f);
        SetUniform1(gl, repair, "uCoverageScale", 1.2f);
        SetUniform1(gl, repair, "uVolumeSize", 178f);
        SetUniform3(gl, repair, "uWindOffset", 0f, 0f, 0f);
        SetUniform1(gl, repair, "uCirrusStrength", 0.13f);
        SetUniform2(gl, repair, "uCirrusWindOffset", 0f, 0f);
        SetUniform2(gl, repair, "uCirrusWindDir", 0.8f, 0.6f);
        SetUniform1(gl, repair, "uMarchSteps", 0);
        SetUniform1(gl, repair, "uHasSceneDepth", 0);
        SetUniform1(gl, repair, "uHasCloudNoise", 0);
        SetUniform1(gl, repair, "uHasDetailNoise", 0);
        SetUniform1(gl, repair, "uHasCloudStbn", 0);
        SetUniform1(gl, repair, "uHasCoverageMap", 0);
        SetUniform1(gl, repair, "uHasSkyLut", 0);
        SetUniform1(gl, repair, "uSourceCloudDataDirect", 1);
        SetUniform1(gl, repair, "uCloudFrameIndex", 7);

        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            gl.BindVertexArray(quadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            var repaired = ReadTextureRgbaFloat(
                gl,
                output.ColorTextureHandle,
                outputSize,
                outputSize);
            Assert.All(repaired, value => Assert.True(float.IsFinite(value)));
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private static void DrawUpsample(
        GL gl,
        GlShaderProgram program,
        uint quadVao,
        GlCloudTemporalRenderTarget source,
        uint sceneDepthTexture)
    {
        program.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, source.ColorTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, sceneDepthTexture);
        gl.BindVertexArray(quadVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
    }

    private static void ConfigureUpsampleUniforms(
        GL gl,
        GlShaderProgram program,
        int cloudSize,
        bool directMetadata,
        bool hasSceneDepth = true,
        bool hdrPresent = true,
        bool applyCloudEncoding = false)
    {
        program.Use();
        SetUniform1(gl, program, "uClouds", 0);
        SetUniform1(gl, program, "uCloudData", 1);
        SetUniform1(gl, program, "uSceneDepth", 2);
        SetUniform1(gl, program, "uHasSceneDepth", hasSceneDepth ? 1 : 0);
        SetUniform1(gl, program, "uCloudDataDirect", directMetadata ? 1 : 0);
        SetUniform1(gl, program, "uCloudExposure", 1f);
        SetUniform1(gl, program, "uHdrPresent", hdrPresent ? 1 : 0);
        SetUniform1(gl, program, "uApplyCloudEncoding", applyCloudEncoding ? 1 : 0);
        SetUniform1(gl, program, "uCloudSourceFullResolution", 0);
        SetUniform2(gl, program, "uCloudTexelSize", 1f / cloudSize, 1f / cloudSize);
        SetUniform3(gl, program, "uCameraPos", 0f, 0f, -1f);
        SetUniform1(gl, program, "uGroundWorldY", -100f);
        SetUniform1(gl, program, "uPlanetRadius", 1f);
        var matrixLoc = program.GetUniformLocation("uInvViewProj");
        if (matrixLoc >= 0)
        {
            var identity = Matrix4x4.Identity;
            gl.UniformMatrix4(matrixLoc, 1, false, in identity.M11);
        }
    }

    private static byte[] RepeatPackedDistance(int width, int height, float distance)
    {
        var normalized = Math.Max(distance, 0f) / (Math.Max(distance, 0f) + 256f);
        var encodedY = normalized * 255f - MathF.Floor(normalized * 255f);
        var encodedX = normalized - encodedY / 255f;
        return RepeatPixel(width, height,
            (byte)Math.Clamp((int)MathF.Round(encodedX * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(encodedY * 255f), 0, 255),
            128,
            255);
    }

    private static byte[] RepeatPixel(int width, int height, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }

    private static float[] RepeatDirectMetadata(
        int width,
        int height,
        float distance,
        float cloudKind)
    {
        var pixels = new float[width * height * 2];
        for (var i = 0; i < pixels.Length; i += 2)
        {
            pixels[i] = distance;
            pixels[i + 1] = cloudKind;
        }

        return pixels;
    }

    private static float[] RepeatRgbaFloat(
        int width,
        int height,
        float red,
        float green,
        float blue,
        float alpha)
    {
        var pixels = new float[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = red;
            pixels[i + 1] = green;
            pixels[i + 2] = blue;
            pixels[i + 3] = alpha;
        }

        return pixels;
    }

    private static unsafe void UploadRgba8(GL gl, uint texture, int width, int height, byte[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* ptr = pixels)
        {
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }
    }

    private static unsafe void AllocateRgba8(GL gl, uint texture, int width, int height, byte[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* ptr = pixels)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }
    }

    private static unsafe void UploadRg32f(GL gl, uint texture, int width, int height, float[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (float* ptr = pixels)
        {
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
                PixelFormat.RG, PixelType.Float, ptr);
        }
    }

    private static unsafe void UploadRgba32f(GL gl, uint texture, int width, int height, float[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (float* ptr = pixels)
        {
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.Float, ptr);
        }
    }

    private static unsafe float[] ReadTextureRgbaFloat(
        GL gl,
        uint texture,
        int width,
        int height)
    {
        var framebuffer = gl.GenFramebuffer();
        var pixels = new float[width * height * 4];
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            Assert.Equal(
                GLEnum.FramebufferComplete,
                gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            fixed (float* ptr = pixels)
            {
                gl.ReadPixels(
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    PixelFormat.Rgba,
                    PixelType.Float,
                    ptr);
            }

            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
        }

        return pixels;
    }

    private static unsafe float[] ReadTextureRgFloat(
        GL gl,
        uint texture,
        int width,
        int height)
    {
        var framebuffer = gl.GenFramebuffer();
        var pixels = new float[width * height * 2];
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            Assert.Equal(
                GLEnum.FramebufferComplete,
                gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            fixed (float* ptr = pixels)
            {
                gl.ReadPixels(
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    PixelFormat.RG,
                    PixelType.Float,
                    ptr);
            }

            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
        }

        return pixels;
    }

    private static uint CreateFullscreenQuad(GL gl, out uint vbo)
    {
        var vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        ReadOnlySpan<float> vertices =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f,
        ];
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
        return vao;
    }

    private static void SetUniform1(GL gl, GlShaderProgram program, string name, int value)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform1(location, value);
        }
    }

    private static void SetUniform1(GL gl, GlShaderProgram program, string name, float value)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform1(location, value);
        }
    }

    private static void SetUniform2(GL gl, GlShaderProgram program, string name, float x, float y)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform2(location, x, y);
        }
    }

    private static void SetUniform3(GL gl, GlShaderProgram program, string name, float x, float y, float z)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform3(location, x, y, z);
        }
    }

    private static void SetIdentityMatrix(GL gl, GlShaderProgram program, string name)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            var identity = Matrix4x4.Identity;
            gl.UniformMatrix4(location, 1, false, in identity.M11);
        }
    }
}
