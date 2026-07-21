using System.Globalization;
using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private GlShaderProgram? _taaResolveProgram;
    private GlColorRenderTarget? _taaScratchTarget;
    private GlColorRenderTarget? _taaResolveTarget;
    private GlColorRenderTarget? _taaHistoryTarget;
    private Matrix4x4 _taaPrevViewProj = Matrix4x4.Identity;
    private bool _taaHistoryValid;
    private int _taaHistoryW;
    private int _taaHistoryH;
    private bool _prevEnablePreviewTaa = true;
    private bool _prevPreviewTaaActive;
    private int _prevPreviewTaaMode;
    private int _prevPreviewTaaSettingsKey;
    private int _taaFrameIndex;
    private int _lastPreviewTaaDiagnosticKey;
    private double _lastPreviewTaaDiagnosticMs = double.NegativeInfinity;
    private int _lastPreviewTaaReadbackKey;
    private double _lastPreviewTaaReadbackMs = double.NegativeInfinity;
    private Matrix4x4 _taaPrevSubjectModel = Matrix4x4.Identity;
    private bool _taaPrevSubjectModelValid;

    private void TryInitPreviewTaa(GL gl, bool useOpenGlEs)
    {
        DestroyPreviewTaaResources();
        _taaResolveProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_taa_resolve.frag",
            out var err, "preview-taa");
        if (_taaResolveProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Preview TAA shader: " + (err ?? "link failed"));
            _taaResolveProgram?.Dispose();
            _taaResolveProgram = null;
            return;
        }

        _taaResolveUniformLocs = ResolveTaaResolveUniformLocs(_taaResolveProgram);

        EmitPreviewTaaShaderDiagnostic(useOpenGlEs);

        _taaScratchTarget = new GlColorRenderTarget(gl, useOpenGlEs);
        _taaResolveTarget = new GlColorRenderTarget(gl, useOpenGlEs);
        _taaHistoryTarget = new GlColorRenderTarget(gl, useOpenGlEs);
        if (!TryInitSceneCaptureCore(gl, useOpenGlEs, out var sceneErr))
        {
            EmitDiagnostic("[3D preview] Preview TAA scene capture: " + TrimShaderDiagnostic(sceneErr));
        }
    }

    private void DestroyPreviewTaaResources()
    {
        _taaResolveProgram?.Dispose();
        _taaResolveProgram = null;
        _taaScratchTarget?.Dispose();
        _taaScratchTarget = null;
        _taaResolveTarget?.Dispose();
        _taaResolveTarget = null;
        _taaHistoryTarget?.Dispose();
        _taaHistoryTarget = null;
        _taaHistoryValid = false;
    }

    private void SyncPreviewTaaToggleState(in PreviewRenderSettingsSnapshot settings)
    {
        var active = IsPreviewTaaActive(settings);
        var settingsKey = ComputePreviewTaaSettingsKey(settings);
        if (_prevEnablePreviewTaa == settings.EnablePreviewTaa &&
            _prevPreviewTaaActive == active &&
            _prevPreviewTaaMode == settings.PreviewTaaMode &&
            _prevPreviewTaaSettingsKey == settingsKey)
        {
            return;
        }

        _prevEnablePreviewTaa = settings.EnablePreviewTaa;
        _prevPreviewTaaActive = active;
        _prevPreviewTaaMode = settings.PreviewTaaMode;
        _prevPreviewTaaSettingsKey = settingsKey;
        InvalidatePreviewTaaHistory();
        _taaFrameIndex = 0;
    }

    private bool IsPreviewTaaActive(in PreviewRenderSettingsSnapshot settings)
    {
        if (!settings.EnablePreviewTaa || _taaResolveProgram is not { IsValid: true } ||
            _taaScratchTarget is null || _taaResolveTarget is null || _taaHistoryTarget is null || _godRayQuadVao == 0)
        {
            return false;
        }

        var taa = ResolveEffectivePreviewTaa(settings);
        return taa.TemporalWeight > 0f || taa.FxaaEdgeStrength > 0f || settings.PreviewTaaForceFxaa;
    }

    private bool ShouldContinuouslyAccumulatePreviewTaa(in PreviewRenderSettingsSnapshot settings)
    {
        if (!IsPreviewTaaActive(settings))
        {
            return false;
        }

        var taa = ResolveEffectivePreviewTaa(settings);
        return taa.TemporalWeight > 0f && taa.JitterScale > 0f;
    }

    private Vector2 CurrentPreviewTaaJitter(int width, int height, in PreviewRenderSettingsSnapshot settings)
    {
        if (!_taaHistoryValid)
        {
            return Vector2.Zero;
        }

        var scale = ResolveEffectivePreviewTaa(settings).JitterScale;
        if (scale <= 0f)
        {
            return Vector2.Zero;
        }

        var sample = (_taaFrameIndex & 7) + 1;
        var pixelJitter = new Vector2(Halton(sample, 2) - 0.5f, Halton(sample, 3) - 0.5f) * scale;
        return new Vector2(
            2f * pixelJitter.X / Math.Max(1, width),
            2f * pixelJitter.Y / Math.Max(1, height));
    }

    private static PreviewVolumetricQuality.TaaProfile ResolveEffectivePreviewTaa(in PreviewRenderSettingsSnapshot settings)
    {
        var taa = PreviewVolumetricQuality.ResolvePreviewTaa(settings.VolumetricQuality, settings.PreviewTaaMode);
        var temporal = Math.Clamp(taa.TemporalWeight * Math.Clamp(settings.PreviewTaaTemporalScale, 0f, 1.25f), 0f, 0.98f);
        var jitter = Math.Clamp(taa.JitterScale * Math.Clamp(settings.PreviewTaaJitterScale, 0f, 2f), 0f, 2f);
        var source = Math.Clamp(taa.SourceFilterStrength * Math.Clamp(settings.PreviewTaaSourceFilterScale, 0f, 2f), 0f, 1f);
        var edge = Math.Clamp(taa.EdgeAaBlend * Math.Clamp(settings.PreviewTaaEdgeBlendScale, 0f, 2f), 0f, 1f);
        var fxaa = Math.Clamp(taa.FxaaEdgeStrength * Math.Clamp(settings.PreviewTaaFxaaStrengthScale, 0f, 5f), 0f, 3f);
        return new PreviewVolumetricQuality.TaaProfile(
            TemporalWeight: temporal,
            JitterScale: jitter,
            StableTemporalBoost: taa.StableTemporalBoost,
            MaxStableTemporal: Math.Max(temporal, taa.MaxStableTemporal),
            SharpenStrength: taa.SharpenStrength,
            DepthEdgeHistoryFloor: taa.DepthEdgeHistoryFloor,
            EdgeAaBlend: edge,
            SourceFilterStrength: source,
            SilhouetteHistoryWeight: taa.SilhouetteHistoryWeight,
            FxaaEdgeStrength: fxaa);
    }

    private static int ComputePreviewTaaSettingsKey(in PreviewRenderSettingsSnapshot settings)
    {
        var hasher = new HashCode();
        hasher.Add(settings.EnablePreviewTaa);
        hasher.Add(settings.PreviewTaaMode);
        hasher.Add(MathF.Round(settings.PreviewTaaTemporalScale * 1000f));
        hasher.Add(MathF.Round(settings.PreviewTaaJitterScale * 1000f));
        hasher.Add(MathF.Round(settings.PreviewTaaSourceFilterScale * 1000f));
        hasher.Add(MathF.Round(settings.PreviewTaaEdgeBlendScale * 1000f));
        hasher.Add(MathF.Round(settings.PreviewTaaFxaaStrengthScale * 1000f));
        hasher.Add(MathF.Round(settings.PreviewTaaFxaaLumaEdgeScale * 1000f));
        hasher.Add(MathF.Round(settings.PreviewTaaFxaaLumaThreshold * 10000f));
        hasher.Add(settings.PreviewTaaForceFxaa);
        return hasher.ToHashCode();
    }

    private static float Halton(int index, int radix)
    {
        var result = 0f;
        var fraction = 1f / radix;
        while (index > 0)
        {
            result += fraction * (index % radix);
            index /= radix;
            fraction /= radix;
        }

        return result;
    }

    private Matrix4x4 ResolvePreviewTaaPrevViewProj(Matrix4x4 currentViewProj) =>
        _taaHistoryValid ? _taaPrevViewProj : currentViewProj;

    private Matrix4x4 ResolvePreviewTaaPrevSubjectModel(Matrix4x4 currentModel) =>
        _taaHistoryValid && _taaPrevSubjectModelValid ? _taaPrevSubjectModel : currentModel;

    private void InvalidatePreviewTaaHistory()
    {
        _taaHistoryValid = false;
        _taaPrevSubjectModel = Matrix4x4.Identity;
        _taaPrevSubjectModelValid = false;
        InvalidatePreviousEntitySkinningBones();
    }

    private void DrawPreviewTaa(ref GlRenderFrame frame)
    {
        SyncPreviewTaaToggleState(frame.Settings);
        if (!IsPreviewTaaActive(frame.Settings))
        {
            return;
        }

        var gl = frame.Gl;
        var w = Math.Max(1, frame.Vw);
        var h = Math.Max(1, frame.Vh);
        if (_taaHistoryW != w || _taaHistoryH != h)
        {
            _taaHistoryValid = false;
            _taaPrevSubjectModelValid = false;
            _taaHistoryW = w;
            _taaHistoryH = h;
        }

        var scratchTarget = _taaScratchTarget!;
        var resolveTarget = _taaResolveTarget!;
        var historyTarget = _taaHistoryTarget!;
        var useFloat = frame.Settings.HdrPresentActive;
        if (!scratchTarget.EnsureSize(w, h, useFloat) ||
            !resolveTarget.EnsureSize(w, h, useFloat) ||
            !historyTarget.EnsureSize(w, h, useFloat))
        {
            return;
        }

        var readFbo = (uint)Math.Max(0, frame.DefaultFbo);
        if (!scratchTarget.CopyColorFromFramebuffer(readFbo, w, h))
        {
            _taaHistoryValid = false;
            return;
        }

        var viewProj = frame.UnjitteredProj * frame.View;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
        {
            return;
        }

        var taa = ResolveEffectivePreviewTaa(frame.Settings);
        var hasSceneDepth = frame.GodRayCaptureActive && _sceneCapture is { IsValid: true };
        var hasTaaSignal = hasSceneDepth && _sceneCapture!.TaaSignalTextureHandle != 0;
        MaybeLogPreviewTaaDiagnostics(ref frame, taa, hasSceneDepth, hasTaaSignal, w, h);

        resolveTarget.BindDraw();
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorCullFace = gl.IsEnabled(EnableCap.CullFace);
        var priorDepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(false);
        gl.ColorMask(true, true, true, true);
        gl.BindVertexArray(_godRayQuadVao);
        _taaResolveProgram!.Use();
        var tu = _taaResolveUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, scratchTarget.ColorTextureHandle);
        SetIntOnProgramLoc(_taaResolveProgram, tu.Current, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, historyTarget.ColorTextureHandle);
        SetIntOnProgramLoc(_taaResolveProgram, tu.History, 1);
        if (hasSceneDepth)
        {
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture!.DepthTextureHandle);
            SetIntOnProgramLoc(_taaResolveProgram, tu.SceneDepth, 2);
        }

        if (hasTaaSignal)
        {
            gl.ActiveTexture(TextureUnit.Texture3);
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture!.TaaSignalTextureHandle);
            SetIntOnProgramLoc(_taaResolveProgram, tu.TaaSignal, 3);
        }

        SetIntOnProgramLoc(_taaResolveProgram, tu.HasSceneDepth, hasSceneDepth ? 1 : 0);
        SetIntOnProgramLoc(_taaResolveProgram, tu.HasTaaSignal, hasTaaSignal ? 1 : 0);
        SetIntOnProgramLoc(_taaResolveProgram, tu.HasHistory, _taaHistoryValid ? 1 : 0);
        SetMatrixOnProgramLoc(_taaResolveProgram, tu.InvViewProj, invViewProj);
        SetMatrixOnProgramLoc(_taaResolveProgram, tu.PrevViewProj, _taaPrevViewProj);
        var displayTexelSize = new Vector2(1f / w, 1f / h);
        var captureW = hasSceneDepth && frame.SceneCaptureW > 0 ? frame.SceneCaptureW : w;
        var captureH = hasSceneDepth && frame.SceneCaptureH > 0 ? frame.SceneCaptureH : h;
        var captureTexelSize = new Vector2(1f / captureW, 1f / captureH);
        SetVec2OnProgramLoc(_taaResolveProgram, tu.TexelSize, displayTexelSize);
        SetVec2OnProgramLoc(_taaResolveProgram, tu.CaptureTexelSize, captureTexelSize);
        SetVec2OnProgramLoc(_taaResolveProgram, tu.CurrentJitterPixels,
            new Vector2(frame.PreviewTaaJitterNdc.X * w * 0.5f, frame.PreviewTaaJitterNdc.Y * h * 0.5f));
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        var drawErr = gl.GetError();
        if (drawErr != GLEnum.NoError)
        {
            EmitDiagnostic($"[3D preview] TAA resolve draw GL error: {drawErr}");
        }

        gl.BindVertexArray(0);

        gl.DepthMask(priorDepthMask);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }

        if (priorBlend)
        {
            gl.Enable(EnableCap.Blend);
        }

        if (priorCullFace)
        {
            gl.Enable(EnableCap.CullFace);
        }
        else
        {
            gl.Disable(EnableCap.CullFace);
        }

        var presented = TryPresentPreviewTaaResolveToDefault(ref frame, resolveTarget);
        if (!presented && !frame.Settings.HdrPresentActive)
        {
            presented = resolveTarget.BlitColorToFramebuffer(readFbo, frame.VpX, frame.VpY, w, h);
        }

        if (!presented)
        {
            EmitDiagnostic("[3D preview] TAA resolve present to preview framebuffer failed.");
            BindDefaultFramebuffer(ref frame);
            _taaHistoryValid = false;
            return;
        }

        BindDefaultFramebuffer(ref frame);
        MaybeLogPreviewTaaReadbackDiagnostics(ref frame, scratchTarget, resolveTarget, w, h);
        if (!historyTarget.CopyColorFrom(resolveTarget))
        {
            EmitDiagnostic("[3D preview] TAA resolve history copy failed.");
            _taaHistoryValid = false;
            return;
        }

        _taaPrevViewProj = viewProj;
        _taaPrevSubjectModel = frame.ModelMatrix;
        _taaPrevSubjectModelValid = true;
        CapturePreviousEntitySkinningBones();
        _taaHistoryValid = true;
        _taaFrameIndex++;
    }

    private bool TryPresentPreviewTaaResolveToDefault(ref GlRenderFrame frame, GlColorRenderTarget resolveTarget)
    {
        if (_scenePresentProgram is not { IsValid: true } || _godRayQuadVao == 0 || !resolveTarget.IsValid)
        {
            return false;
        }

        var gl = frame.Gl;
        BindDefaultFramebuffer(ref frame);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorCullFace = gl.IsEnabled(EnableCap.CullFace);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorDepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(false);
        gl.ColorMask(true, true, true, true);
        gl.BindVertexArray(_godRayQuadVao);
        _scenePresentProgram.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, resolveTarget.ColorTextureHandle);
        BindScenePresentUniforms(frame.Settings, sceneIsLinear: frame.Settings.HdrPresentActive);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        var err = gl.GetError();
        gl.BindVertexArray(0);

        gl.DepthMask(priorDepthMask);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }

        if (priorBlend)
        {
            gl.Enable(EnableCap.Blend);
        }

        if (priorCullFace)
        {
            gl.Enable(EnableCap.CullFace);
        }

        if (priorScissor)
        {
            gl.Enable(EnableCap.ScissorTest);
        }

        if (err != GLEnum.NoError)
        {
            EmitDiagnostic($"[3D preview] TAA resolve shader-present GL error: {err}; falling back to blit.");
            return false;
        }

        return true;
    }

    private void EmitPreviewTaaShaderDiagnostic(bool useOpenGlEs)
    {
        if (!_settings.LogPreviewTaaDiagnostics)
        {
            return;
        }

        try
        {
            var fingerprint = GlslPreparedSourceCache.ComputePreparedSourceFingerprint(
                "genesis_taa_resolve.frag",
                ShaderType.FragmentShader,
                useOpenGlEs);
            var origin = GlslPreparedSourceCache.GetShaderSourceOrigin("genesis_taa_resolve.frag");
            EmitDiagnostic(
                $"[3D preview] Preview TAA shader ready: resolveSource={fingerprint} origin={origin}");
        }
        catch (Exception ex)
        {
            EmitDiagnostic("[3D preview] Preview TAA shader ready; source fingerprint unavailable: " + ex.Message);
        }
    }

    private void MaybeLogPreviewTaaReadbackDiagnostics(ref GlRenderFrame frame,
        GlColorRenderTarget scratchTarget, GlColorRenderTarget resolveTarget, int width, int height)
    {
        if (!frame.Settings.LogPreviewTaaDiagnostics || width <= 0 || height <= 0)
        {
            return;
        }

        var hasher = new HashCode();
        hasher.Add(width);
        hasher.Add(height);
        hasher.Add(frame.DefaultFbo);
        hasher.Add(_taaHistoryValid);
        hasher.Add(ComputePreviewTaaSettingsKey(frame.Settings));
        var key = hasher.ToHashCode();
        var nowMs = frame.RenderTime * 1000.0;
        if (key == _lastPreviewTaaReadbackKey && nowMs - _lastPreviewTaaReadbackMs < 3000.0)
        {
            return;
        }

        _lastPreviewTaaReadbackKey = key;
        _lastPreviewTaaReadbackMs = nowMs;

        var readW = Math.Min(width, 320);
        var readH = Math.Min(height, 220);
        var readX = Math.Max(0, (width - readW) / 2);
        var readY = Math.Max(0, (height - readH) / 2);
        var scratchPixels = scratchTarget.TryReadRgb8(readX, readY, readW, readH, out var scratchErr);
        var resolvePixels = resolveTarget.TryReadRgb8(readX, readY, readW, readH, out var resolveErr);

        BindDefaultFramebuffer(ref frame);
        frame.Gl.ReadBuffer(frame.DefaultFbo == 0 ? ReadBufferMode.Back : ReadBufferMode.ColorAttachment0);
        var presentedPixels = GlFramebufferReadback.TryReadRgb8(
            frame.Gl,
            frame.VpX + readX,
            frame.VpY + readY,
            readW,
            readH,
            out var presentedErr);

        var resolveDelta = MeanAbsRgbDelta(scratchPixels, resolvePixels);
        var presentDelta = MeanAbsRgbDelta(resolvePixels, presentedPixels);
        var rawPresentedDelta = MeanAbsRgbDelta(scratchPixels, presentedPixels);
        var resolveMaxDelta = MaxAbsRgbDelta(scratchPixels, resolvePixels);
        var resolveChangedPct = ChangedPixelPercent(scratchPixels, resolvePixels);
        var presentMaxDelta = MaxAbsRgbDelta(resolvePixels, presentedPixels);
        EmitDiagnostic(
            $"[3D preview] TAA readback: crop={readW}x{readH}+{readX},{readY} " +
            $"scratch={PixelHashText(scratchPixels, readW, readH, scratchErr)} " +
            $"resolve={PixelHashText(resolvePixels, readW, readH, resolveErr)} " +
            $"presented={PixelHashText(presentedPixels, readW, readH, presentedErr)} " +
            $"resolveDelta={DeltaText(resolveDelta)} presentDelta={DeltaText(presentDelta)} " +
            $"rawPresentedDelta={DeltaText(rawPresentedDelta)} resolveMax={DeltaText(resolveMaxDelta)} " +
            $"resolveChanged={PercentText(resolveChangedPct)} presentMax={DeltaText(presentMaxDelta)} " +
            $"history={_taaHistoryValid} forceFxaa={frame.Settings.PreviewTaaForceFxaa}");
    }

    private static string PixelHashText(byte[]? pixels, int width, int height, GLEnum error)
    {
        if (pixels is null)
        {
            return $"read-failed({ReadbackErrorText(error)})";
        }

        return PreviewFramebufferFingerprint.Compute(pixels, width, height).ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string ReadbackErrorText(GLEnum error) =>
        error == GLEnum.NoError ? "unknown" : error.ToString();

    private static double MeanAbsRgbDelta(byte[]? lhs, byte[]? rhs)
    {
        if (lhs is null || rhs is null || lhs.Length != rhs.Length || lhs.Length == 0)
        {
            return double.NaN;
        }

        long sum = 0;
        for (var i = 0; i < lhs.Length; i++)
        {
            sum += Math.Abs(lhs[i] - rhs[i]);
        }

        return sum / (lhs.Length * 255.0);
    }

    private static double MaxAbsRgbDelta(byte[]? lhs, byte[]? rhs)
    {
        if (lhs is null || rhs is null || lhs.Length != rhs.Length || lhs.Length == 0)
        {
            return double.NaN;
        }

        var max = 0;
        for (var i = 0; i < lhs.Length; i++)
        {
            max = Math.Max(max, Math.Abs(lhs[i] - rhs[i]));
        }

        return max / 255.0;
    }

    private static double ChangedPixelPercent(byte[]? lhs, byte[]? rhs)
    {
        if (lhs is null || rhs is null || lhs.Length != rhs.Length || lhs.Length == 0)
        {
            return double.NaN;
        }

        var changed = 0;
        var pixelCount = lhs.Length / 3;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var i = pixel * 3;
            if (Math.Abs(lhs[i] - rhs[i]) > 1 ||
                Math.Abs(lhs[i + 1] - rhs[i + 1]) > 1 ||
                Math.Abs(lhs[i + 2] - rhs[i + 2]) > 1)
            {
                changed++;
            }
        }

        return changed * 100.0 / Math.Max(1, pixelCount);
    }

    private static string DeltaText(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("0.00000", CultureInfo.InvariantCulture);

    private static string PercentText(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private void MaybeLogPreviewTaaDiagnostics(ref GlRenderFrame frame, PreviewVolumetricQuality.TaaProfile taa,
        bool hasSceneDepth, bool hasTaaSignal, int width, int height)
    {
        if (!frame.Settings.LogPreviewTaaDiagnostics)
        {
            return;
        }

        var sceneCaptureW = _sceneCapture?.Width ?? 0;
        var sceneCaptureH = _sceneCapture?.Height ?? 0;
        var resolveW = _taaResolveTarget?.Width ?? 0;
        var resolveH = _taaResolveTarget?.Height ?? 0;
        var hasher = new HashCode();
        hasher.Add(width);
        hasher.Add(height);
        hasher.Add(frame.Settings.PreviewTaaMode);
        hasher.Add(hasSceneDepth);
        hasher.Add(hasTaaSignal);
        hasher.Add(_taaHistoryValid);
        hasher.Add(_taaHistoryW);
        hasher.Add(_taaHistoryH);
        hasher.Add(sceneCaptureW);
        hasher.Add(sceneCaptureH);
        hasher.Add(MathF.Round(frame.SceneCaptureScale * 100f));
        hasher.Add(resolveW);
        hasher.Add(resolveH);
        hasher.Add(ComputePreviewTaaSettingsKey(frame.Settings));
        var key = hasher.ToHashCode();
        var nowMs = frame.RenderTime * 1000.0;
        if (key == _lastPreviewTaaDiagnosticKey && nowMs - _lastPreviewTaaDiagnosticMs < 3000.0)
        {
            return;
        }

        _lastPreviewTaaDiagnosticKey = key;
        _lastPreviewTaaDiagnosticMs = nowMs;
        var jitterPixels = new Vector2(frame.PreviewTaaJitterNdc.X * width * 0.5f,
            frame.PreviewTaaJitterNdc.Y * height * 0.5f);
        var sceneCaptureSize = hasSceneDepth ? $"{sceneCaptureW}x{sceneCaptureH}" : "none";
        var resolveSize = _taaResolveTarget is { IsValid: true } ? $"{resolveW}x{resolveH}" : "none";
        EmitDiagnostic(
            $"[3D preview] TAA resolve: view={width}x{height}px texel={1f / width:0.000000},{1f / height:0.000000} " +
            $"mode={Math.Clamp(frame.Settings.PreviewTaaMode, 0, 4)} history={_taaHistoryValid} depth={hasSceneDepth} " +
            $"signal={hasTaaSignal} sceneCapture={sceneCaptureSize} captureScale={frame.SceneCaptureScale:0.##} " +
            $"resolveSize={resolveSize} " +
            $"historySize={_taaHistoryW}x{_taaHistoryH} " +
            $"jitterPx={jitterPixels.X:0.###},{jitterPixels.Y:0.###} profile temporal={taa.TemporalWeight:0.##} " +
            $"jitter={taa.JitterScale:0.##} edge={taa.EdgeAaBlend:0.##} source={taa.SourceFilterStrength:0.##} " +
            $"silhouette={taa.SilhouetteHistoryWeight:0.##} fxaa={taa.FxaaEdgeStrength:0.##} " +
            $"fxaaLuma={Math.Clamp(frame.Settings.PreviewTaaFxaaLumaEdgeScale, 0f, 2f):0.##} " +
            $"fxaaThreshold={Math.Clamp(frame.Settings.PreviewTaaFxaaLumaThreshold, 0.001f, 0.12f):0.###} " +
            $"forceFxaa={frame.Settings.PreviewTaaForceFxaa}");
    }
}
