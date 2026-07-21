using AutoPBR.App.Rendering.OpenGL;

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

                using var temporalTarget = new GlCloudTemporalRenderTarget(gl);
                using var temporalHistory = new GlCloudTemporalRenderTarget(gl);
                Assert.True(temporalTarget.EnsureSize(16, 16),
                    "Cloud temporal MRT failed to initialize.");
                Assert.True(temporalHistory.EnsureSize(16, 16),
                    "Cloud temporal history MRT failed to initialize.");
                Assert.True(temporalHistory.CopyFrom(temporalTarget),
                    "Cloud temporal MRT history copy failed.");

                ValidateCloudDepthOrdering(gl, upsample);
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

            ConfigureUpsampleUniforms(gl, upsample, cloudSize);
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

    private static void ConfigureUpsampleUniforms(GL gl, GlShaderProgram program, int cloudSize)
    {
        program.Use();
        SetUniform1(gl, program, "uClouds", 0);
        SetUniform1(gl, program, "uCloudData", 1);
        SetUniform1(gl, program, "uSceneDepth", 2);
        SetUniform1(gl, program, "uHasSceneDepth", 1);
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
}
