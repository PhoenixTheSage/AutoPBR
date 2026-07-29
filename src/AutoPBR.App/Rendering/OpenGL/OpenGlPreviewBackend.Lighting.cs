using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend
{
    private readonly record struct MoonBillboardPlacement(
        Vector3 Center,
        Vector3 Right,
        Vector3 Up,
        Vector3 Facing,
        float Radius,
        float CosDiscEdge);

    private bool TryInitMoonBillboard(GL gl, bool useOpenGlEs)
    {
        if (!PreviewBundledGpuAssetPrewarm.TryGetMoon(
                out var moonRgba))
        {
            return false;
        }

        DestroyMoonBillboard();
        _moonProgram = new GlMoonBillboardProgram(gl, useOpenGlEs, out var moonErr);
        if (_moonProgram is not { IsValid: true })
        {
            _moonProgram?.Dispose();
            _moonProgram = null;
            if (!string.IsNullOrWhiteSpace(moonErr))
            {
                EmitDiagnostic("[3D preview] Moon billboard shader: " + moonErr);
            }

            return true;
        }

        _moonAlbedo = new GlTexture2D(gl, nearestFilter: false);
        _moonAlbedo.UploadRgba(
            PreviewMoonDiscTextureGenerator.Size,
            PreviewMoonDiscTextureGenerator.Size,
            moonRgba,
            nearestFilter: false);

        Span<float> quad =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, -1f, 1f, 1f, 1f
        ];
        _moonVao = gl.GenVertexArray();
        _moonVbo = gl.GenBuffer();
        gl.BindVertexArray(_moonVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _moonVbo);
        gl.BufferData<float>(GLEnum.ArrayBuffer, quad, GLEnum.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
        _moonUniformLocs = ResolveMoonProgramUniformLocs(_moonProgram);
        return true;
    }

    private void DestroyMoonBillboard()
    {
        var gl = _gl;
        if (gl is null)
        {
            _moonProgram?.Dispose();
            _moonProgram = null;
            _moonAlbedo?.Dispose();
            _moonAlbedo = null;
            _moonVao = _moonVbo = 0;
            return;
        }

        if (_moonVbo != 0)
        {
            gl.DeleteBuffer(_moonVbo);
            _moonVbo = 0;
        }

        if (_moonVao != 0)
        {
            gl.DeleteVertexArray(_moonVao);
            _moonVao = 0;
        }

        _moonAlbedo?.Dispose();
        _moonAlbedo = null;
        _moonProgram?.Dispose();
        _moonProgram = null;
    }

    /// <summary>
    /// Textured moon disc along <c>lightPropagationDir</c> (antipodal to the sun).
    /// </summary>
    private void DrawMoonBillboard(GL gl, Matrix4x4 proj, Matrix4x4 view, Vector3 eye, Vector3 lightPropagationDir,
        float farPlane, float discStrength, float discSize, float glowStrength, float textureSharpness,
        bool restoreSolidBackFaceCull)
    {
        if (_moonProgram is null || !_moonProgram.IsValid || _moonVao == 0 || _moonAlbedo is null)
        {
            return;
        }

        var towardMoon = lightPropagationDir;
        var tlm = towardMoon.LengthSquared();
        if (tlm < 1e-12f)
        {
            return;
        }

        towardMoon /= MathF.Sqrt(tlm);

        var towardSun = -lightPropagationDir;
        if (towardSun.LengthSquared() > 1e-12f)
        {
            towardSun = Vector3.Normalize(towardSun);
            if (towardSun.Y > 0.04f)
            {
                return;
            }
        }

        if (!TryComputeMoonBillboardPlacement(eye, lightPropagationDir, farPlane, discSize, out var placement))
        {
            return;
        }

        var viewProj = proj * view;
        _moonProgram.Use();
        var m = _moonUniformLocs;
        SetMatrixLoc(m.ViewProj, viewProj);
        SetVec3Loc(m.MoonCenter, placement.Center);
        SetVec3Loc(m.MoonRight, placement.Right);
        SetVec3Loc(m.MoonUp, placement.Up);
        SetVec3Loc(m.MoonFacing, placement.Facing);
        SetVec3Loc(m.CameraPos, eye);
        SetFloatLoc(m.Radius, placement.Radius);
        SetFloatLoc(m.MoonCosDiscEdge, placement.CosDiscEdge);
        SetFloatLoc(m.DiscStrength, Math.Max(discStrength, 0f));
        SetFloatLoc(m.GlowStrength, Math.Max(glowStrength, 0f));
        SetFloatLoc(m.TextureSharpness, Math.Clamp(textureSharpness, 0f, 4f));
        gl.ActiveTexture(TextureUnit.Texture0);
        _moonAlbedo.Bind(0);
        SetIntLoc(m.MoonAlbedo, 0);

        var blendWasEnabled = gl.IsEnabled(EnableCap.Blend);
        var depthTestWasEnabled = gl.IsEnabled(EnableCap.DepthTest);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);

        gl.BindVertexArray(_moonVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);

        gl.DepthMask(true);
        if (!blendWasEnabled)
        {
            gl.Disable(EnableCap.Blend);
        }

        if (depthTestWasEnabled)
        {
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(GLEnum.Lequal);
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
        }

        if (restoreSolidBackFaceCull)
        {
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(GLEnum.Back);
            gl.FrontFace(GLEnum.Ccw);
        }
    }

    private static bool TryComputeMoonBillboardPlacement(
        Vector3 eye,
        Vector3 lightPropagationDir,
        float farPlane,
        float discSize,
        out MoonBillboardPlacement placement)
    {
        placement = default;
        var towardMoon = lightPropagationDir;
        var tlm = towardMoon.LengthSquared();
        if (tlm < 1e-12f)
        {
            return false;
        }

        towardMoon /= MathF.Sqrt(tlm);
        var moonDist = Math.Clamp(farPlane * 0.72f, 1f, PreviewSunScreenProjection.SunDistance);
        var moonRadius = moonDist * (PreviewSunScreenProjection.MoonRadius / PreviewSunScreenProjection.SunDistance) *
                         Math.Clamp(discSize, 0.05f, 3f);
        var moonCenter = eye + towardMoon * moonDist;

        var right = Vector3.Cross(Vector3.UnitY, towardMoon);
        if (right.LengthSquared() < 1e-10f)
        {
            right = Vector3.Cross(Vector3.UnitZ, towardMoon);
        }

        if (right.LengthSquared() < 1e-10f)
        {
            return false;
        }

        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(towardMoon, right));

        var moonEdgeDir = moonCenter + right * moonRadius - eye;
        var moonEdgeLen2 = moonEdgeDir.LengthSquared();
        var moonCosDiscEdge = moonEdgeLen2 < 1e-12f
            ? 0.9995f
            : Math.Clamp(Vector3.Dot(towardMoon, moonEdgeDir / MathF.Sqrt(moonEdgeLen2)), 0.92f, 0.99998f);

        placement = new MoonBillboardPlacement(
            moonCenter,
            right,
            up,
            towardMoon,
            moonRadius,
            moonCosDiscEdge);
        return true;
    }

    private void SetMoonVec3(GL gl, string name, Vector3 v)
    {
        if (_moonProgram is null || !_moonProgram.IsValid)
        {
            return;
        }

        var loc = _moonProgram.GetUniformLocation(name);
        if (loc >= 0)
        {
            gl.Uniform3(loc, v.X, v.Y, v.Z);
        }
    }

    private void TryInitAtmosphere(GL gl)
    {
        DestroyAtmosphereResources();
        EnsureAtmoQuadBuffer(gl);

        _atmoSkyProgram = CreatePreviewProgram("atmo_sky.vert", "atmo_sky.frag", out var skyErr);
        if (_atmoSkyProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Atmosphere sky shader: " + (skyErr ?? "link failed"));
            _atmoSkyProgram?.Dispose();
            _atmoSkyProgram = null;
            if (!TryEnsureProceduralSkyProgram())
            {
                return;
            }
        }

        _atmoTransProgram = CreatePreviewProgram("atmo_lut.vert", "atmo_transmittance.frag", out var transErr);
        if (_atmoTransProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Atmosphere transmittance shader: " + (transErr ?? "link failed"));
            _atmoTransProgram?.Dispose();
            _atmoTransProgram = null;
        }

        _atmoSkyViewProgram = CreatePreviewProgram("atmo_lut.vert", "atmo_skyview.frag", out var skyViewErr);
        if (_atmoSkyViewProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Atmosphere sky-view shader: " + (skyViewErr ?? "link failed"));
            _atmoSkyViewProgram?.Dispose();
            _atmoSkyViewProgram = null;
        }

        if (_atmoTransProgram is { IsValid: true })
        {
            _atmoTransUniformLocs = ResolveAtmoTransUniformLocs(_atmoTransProgram);
        }

        if (_atmoSkyViewProgram is { IsValid: true })
        {
            _atmoSkyViewUniformLocs = ResolveAtmoSkyViewUniformLocs(_atmoSkyViewProgram);
        }

        if (_atmoSkyProgram is { IsValid: true })
        {
            _atmoSkyUniformLocs = ResolveAtmoSkyUniformLocs(_atmoSkyProgram);
        }

        if (!TryCreateAtmosphereTargets(gl))
        {
            EmitDiagnostic("[3D preview] Atmosphere LUT targets unavailable; procedural sky only.");
        }

        _atmoLutsValid = false;
    }

    private void EnsureAtmoQuadBuffer(GL gl)
    {
        if (_atmoQuadVao != 0)
        {
            return;
        }

        Span<float> quad =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f
        ];
        _atmoQuadVao = gl.GenVertexArray();
        _atmoQuadVbo = gl.GenBuffer();
        gl.BindVertexArray(_atmoQuadVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _atmoQuadVbo);
        gl.BufferData<float>(GLEnum.ArrayBuffer, quad, GLEnum.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
    }

    private bool TryCreateAtmosphereTargets(GL gl)
    {
        const int transW = 256;
        const int transH = 64;
        const int skyW = 192;
        const int skyH = 108;

        _atmoTransmittanceTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _atmoTransmittanceTex);
        var transPixels = new byte[transW * transH * 4];
        unsafe
        {
            fixed (byte* pTrans = transPixels)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, transW, transH, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pTrans);
            }
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _atmoSkyViewTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _atmoSkyViewTex);
        var skyPixels = new byte[skyW * skyH * 4];
        unsafe
        {
            fixed (byte* pSky = skyPixels)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, skyW, skyH, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pSky);
            }
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        // Repeat on S: azimuth wraps at u=0/1 (back meridian); ClampToEdge caused a visible vertical seam.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        var fboOk = true;
        _atmoTransmittanceFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _atmoTransmittanceFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            _atmoTransmittanceTex, 0);
        ConfigureSingleColorDrawBuffer(gl);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            EmitDiagnostic("[3D preview] Atmosphere transmittance FBO incomplete; disabling atmospheric LUT path.");
            fboOk = false;
        }

        _atmoSkyViewFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _atmoSkyViewFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            _atmoSkyViewTex, 0);
        ConfigureSingleColorDrawBuffer(gl);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            EmitDiagnostic("[3D preview] Atmosphere sky-view FBO incomplete; disabling atmospheric LUT path.");
            fboOk = false;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return fboOk;
    }

    private static float EffectiveAtmosphereSunIntensity(Vector3 worldLightDir, float userIntensity)
    {
        var towardSun = -worldLightDir;
        if (towardSun.LengthSquared() < 1e-12f)
        {
            return userIntensity * 0.08f;
        }

        towardSun = Vector3.Normalize(towardSun);
        var dayFactor = Math.Clamp((towardSun.Y + 0.04f) / 0.26f, 0f, 1f);
        return userIntensity * (0.08f + 0.92f * dayFactor);
    }

    private void EnsureAtmosphereLuts(GL gl, Vector3 worldLightDir, PreviewRenderSettingsSnapshot settings)
    {
        if (_atmoTransProgram is not { IsValid: true } || _atmoSkyViewProgram is not { IsValid: true } ||
            _atmoTransmittanceFbo == 0 || _atmoSkyViewFbo == 0 || _atmoQuadVao == 0)
        {
            return;
        }

        var sameSun = Vector3.DistanceSquared(worldLightDir, _lastAtmoSunDir) < 1e-6f;
        var sameParams = MathF.Abs(settings.AtmosphereTurbidity - _lastAtmoTurbidity) < 1e-4f &&
                         MathF.Abs(settings.AtmosphereSunIntensity - _lastAtmoSunIntensity) < 1e-4f &&
                         MathF.Abs(settings.AtmosphereHorizonFalloff - _lastAtmoHorizonFalloff) < 1e-4f;
        if (_atmoLutsValid && sameSun && sameParams)
        {
            return;
        }

        _lastAtmoSunDir = worldLightDir;
        _lastAtmoTurbidity = settings.AtmosphereTurbidity;
        _lastAtmoSunIntensity = settings.AtmosphereSunIntensity;
        _lastAtmoHorizonFalloff = settings.AtmosphereHorizonFalloff;

        while (gl.GetError() != GLEnum.NoError)
        {
        }

        var priorDrawFramebuffer = gl.GetInteger(GetPName.DrawFramebufferBinding);
        var priorViewport = new int[4];
        gl.GetInteger(GetPName.Viewport, priorViewport);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorCullFace = gl.IsEnabled(EnableCap.CullFace);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorDepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        var priorActiveTexture = gl.GetInteger(GetPName.ActiveTexture);

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.Blend);
        gl.DepthMask(false);
        gl.BindVertexArray(_atmoQuadVao);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _atmoTransmittanceFbo);
        ConfigureSingleColorDrawBuffer(gl);
        gl.Viewport(0, 0, 256u, 64u);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        _atmoTransProgram!.Use();
        var trans = _atmoTransUniformLocs;
        SetFloatOnProgramLoc(_atmoTransProgram, trans.Turbidity, settings.AtmosphereTurbidity);
        SetFloatOnProgramLoc(_atmoTransProgram, trans.HorizonFalloff, settings.AtmosphereHorizonFalloff);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _atmoSkyViewFbo);
        ConfigureSingleColorDrawBuffer(gl);
        gl.Viewport(0, 0, 192u, 108u);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        _atmoSkyViewProgram!.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _atmoTransmittanceTex);
        var skyView = _atmoSkyViewUniformLocs;
        SetIntOnProgramLoc(_atmoSkyViewProgram, skyView.TransmittanceLut, 0);
        SetVec3OnProgramLoc(_atmoSkyViewProgram, skyView.SunDir, worldLightDir);
        SetFloatOnProgramLoc(_atmoSkyViewProgram, skyView.Turbidity, settings.AtmosphereTurbidity);
        SetFloatOnProgramLoc(_atmoSkyViewProgram, skyView.SunIntensity,
            EffectiveAtmosphereSunIntensity(worldLightDir, settings.AtmosphereSunIntensity));
        SetFloatOnProgramLoc(_atmoSkyViewProgram, skyView.HorizonFalloff, settings.AtmosphereHorizonFalloff);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        gl.BindVertexArray(0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)Math.Max(0, priorDrawFramebuffer));
        gl.Viewport(priorViewport[0], priorViewport[1], (uint)priorViewport[2], (uint)priorViewport[3]);
        gl.ActiveTexture((TextureUnit)priorActiveTexture);
        gl.DepthMask(priorDepthMask);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
        }

        if (priorCullFace)
        {
            gl.Enable(EnableCap.CullFace);
        }
        else
        {
            gl.Disable(EnableCap.CullFace);
        }

        if (priorBlend)
        {
            gl.Enable(EnableCap.Blend);
        }
        else
        {
            gl.Disable(EnableCap.Blend);
        }

        var glErr = gl.GetError();
        if (glErr != GLEnum.NoError)
        {
            EmitDiagnostic($"[3D preview] Atmosphere LUT pass error: {glErr}. Atmosphere fallback engaged.");
            _atmoLutsValid = false;
            return;
        }

        _atmoLutsValid = true;
    }

    private void ConfigureSingleColorDrawBuffer(GL gl)
    {
        if (_useOpenGlEs)
        {
            unsafe
            {
                var buf = DrawBufferMode.ColorAttachment0;
                gl.DrawBuffers(1, &buf);
            }
        }
        else
        {
            gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        }
    }

    private void DrawAtmosphereSky(GL gl, ref GlRenderFrame frame, bool lutSkyReady)
    {
        if (_atmoQuadVao == 0)
        {
            return;
        }

        var aspect = frame.Vw / (float)Math.Max(frame.Vh, 1);
        var viewProj = frame.Proj * frame.View;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
        {
            invViewProj = Matrix4x4.Identity;
        }

        PreviewSunScreenProjection.Compute(frame.Eye, frame.WorldLightDir, frame.View, frame.Proj, aspect,
            frame.Settings.GodRayConeScale, frame.Settings.AtmosphereSunDiscSize,
            out _, out var sunDiscRadiusUv, out _, out var sunCosDiscEdge);
        PreviewSunScreenProjection.ComputeMoon(frame.Eye, frame.WorldLightDir, frame.View, frame.Proj, aspect,
            out _, out _, out var moonCosDiscEdge);

        if (_atmoSkyProgram is { IsValid: true })
        {
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            _atmoSkyProgram.Use();
            if (lutSkyReady && _atmoSkyViewTex != 0)
            {
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _atmoSkyViewTex);
            }

            var sky = _atmoSkyUniformLocs;
            SetIntOnProgramLoc(_atmoSkyProgram, sky.SkyViewLut, 0);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.Turbidity, frame.Settings.AtmosphereTurbidity);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.HorizonFalloff, frame.Settings.AtmosphereHorizonFalloff);
            SetMatrixOnProgramLoc(_atmoSkyProgram, sky.InvViewProj, invViewProj);
            SetVec3OnProgramLoc(_atmoSkyProgram, sky.CameraPos, frame.Eye);
            SetVec3OnProgramLoc(_atmoSkyProgram, sky.LightDir, frame.WorldLightDir);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.SunIntensity, frame.Settings.AtmosphereSunIntensity);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.HorizonFogStrength,
                frame.Settings.EnableAtmosphericSky ? frame.Settings.AerialFogStrength : 0f);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.SkyExposure, frame.Settings.AtmosphereSkyExposure);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.SunDiscStrength, frame.Settings.AtmosphereSunDiscStrength);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.SunDiscBrightness, frame.Settings.AtmosphereSunDiscBrightness);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.SunCosDiscEdge, sunCosDiscEdge);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.MoonCosDiscEdge, moonCosDiscEdge);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.RenderTime, (float)frame.RenderTime);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.ViewportAspect, aspect);
            SetFloatOnProgramLoc(_atmoSkyProgram, sky.SunDiscRadiusUv, sunDiscRadiusUv);
            SetIntOnProgramLoc(_atmoSkyProgram, sky.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
            gl.BindVertexArray(_atmoQuadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            var glErrAfterSky = gl.GetError();
            if (glErrAfterSky != GLEnum.NoError)
            {
                EmitDiagnostic($"[3D preview] Atmosphere sky pass error: {glErrAfterSky}. Atmosphere fallback engaged.");
                _atmoLutsValid = false;
            }

            return;
        }

        if (TryEnsureProceduralSkyProgram())
        {
            _proceduralSkyProgram!.Use();
            var proc = _proceduralSkyUniformLocs;
            SetMatrixLoc(proc.InvViewProj, invViewProj);
            SetVec3Loc(proc.CameraPos, frame.Eye);
            SetVec3Loc(proc.LightDir, frame.WorldLightDir);
            SetFloatLoc(proc.SunIntensity, frame.Settings.AtmosphereSunIntensity);
            SetFloatLoc(proc.SkyExposure, frame.Settings.AtmosphereSkyExposure);
            SetFloatLoc(proc.RenderTime, (float)frame.RenderTime);
            SetFloatLoc(proc.Turbidity, frame.Settings.AtmosphereTurbidity);
            SetFloatLoc(proc.HorizonFalloff, frame.Settings.AtmosphereHorizonFalloff);
            SetFloatLoc(proc.HorizonFogStrength,
                frame.Settings.EnableAtmosphericSky ? frame.Settings.AerialFogStrength : 0f);
            SetFloatLoc(proc.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatLoc(proc.SunDiscStrength, frame.Settings.AtmosphereSunDiscStrength);
            SetFloatLoc(proc.SunDiscBrightness, frame.Settings.AtmosphereSunDiscBrightness);
            SetFloatLoc(proc.SunCosDiscEdge, sunCosDiscEdge);
            SetFloatLoc(proc.MoonCosDiscEdge, moonCosDiscEdge);
            SetFloatLoc(proc.ViewportAspect, aspect);
            SetFloatLoc(proc.SunDiscRadiusUv, sunDiscRadiusUv);
            var sunElev = frame.WorldLightDir.LengthSquared() > 1e-12f
                ? Math.Max(Vector3.Normalize(-frame.WorldLightDir).Y, 0f)
                : 0f;
            SetFloatLoc(proc.SunElevation, sunElev);
            gl.BindVertexArray(_atmoQuadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
        }
    }

    private void DestroyAtmosphereResources()
    {
        var gl = _gl;
        _atmoLutsValid = false;
        _lastAtmoTurbidity = _lastAtmoSunIntensity = _lastAtmoHorizonFalloff = -1f;

        if (gl is null)
        {
            _atmoTransProgram?.Dispose();
            _atmoSkyViewProgram?.Dispose();
            _atmoSkyProgram?.Dispose();
            _proceduralSkyProgram?.Dispose();
            _atmoTransProgram = null;
            _atmoSkyViewProgram = null;
            _atmoSkyProgram = null;
            _proceduralSkyProgram = null;
            _atmoQuadVao = _atmoQuadVbo = 0;
            _atmoTransmittanceTex = _atmoSkyViewTex = 0;
            _atmoTransmittanceFbo = _atmoSkyViewFbo = 0;
            return;
        }

        if (_atmoTransmittanceFbo != 0)
        {
            gl.DeleteFramebuffer(_atmoTransmittanceFbo);
            _atmoTransmittanceFbo = 0;
        }

        if (_atmoSkyViewFbo != 0)
        {
            gl.DeleteFramebuffer(_atmoSkyViewFbo);
            _atmoSkyViewFbo = 0;
        }

        if (_atmoTransmittanceTex != 0)
        {
            gl.DeleteTexture(_atmoTransmittanceTex);
            _atmoTransmittanceTex = 0;
        }

        if (_atmoSkyViewTex != 0)
        {
            gl.DeleteTexture(_atmoSkyViewTex);
            _atmoSkyViewTex = 0;
        }

        if (_atmoQuadVbo != 0)
        {
            gl.DeleteBuffer(_atmoQuadVbo);
            _atmoQuadVbo = 0;
        }

        if (_atmoQuadVao != 0)
        {
            gl.DeleteVertexArray(_atmoQuadVao);
            _atmoQuadVao = 0;
        }

        _atmoTransProgram?.Dispose();
        _atmoTransProgram = null;
        _atmoSkyViewProgram?.Dispose();
        _atmoSkyViewProgram = null;
        _atmoSkyProgram?.Dispose();
        _atmoSkyProgram = null;
        _proceduralSkyProgram?.Dispose();
        _proceduralSkyProgram = null;
    }

}
