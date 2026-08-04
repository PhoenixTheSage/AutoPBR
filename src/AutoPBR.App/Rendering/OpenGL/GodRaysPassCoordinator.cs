using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Toggle-driven history invalidation for god-ray and volumetric passes.</summary>
internal readonly struct GodRaysPassInvalidation
{
    public bool GodRayHistory { get; init; }
    public bool VolumeFroxelHistory { get; init; }
    public bool VolumeIntegrateHistory { get; init; }
    public bool CloudHistory { get; init; }
    public bool TaaHistory { get; init; }
    public bool ResetGodRayLogs { get; init; }
}

/// <summary>Coordinates god-ray toggle sync, scene-capture sizing, and diagnostic throttling.</summary>
internal sealed class GodRaysPassCoordinator
{
    public const int PreviewTaaSsaaMaxDimension = 3072;

    private long _lastVolumetricTimingLogMs;
    private int _lastSceneCaptureAaLogKey;
    private bool _prevEnableGodRays;
    private bool _prevEnableVolumetricClouds;
    private bool _prevGodRayStabilizeDebug = true;
    private bool _prevCloudDisableTemporal;

    public GodRaysPassInvalidation SyncGodRayToggleState(in PreviewRenderSettingsSnapshot settings)
    {
        var godRaysChanged = _prevEnableGodRays != settings.EnableGodRays;
        var stabilizeChanged = _prevGodRayStabilizeDebug != settings.GodRayStabilizeDebug;
        if (!godRaysChanged && !stabilizeChanged)
        {
            return default;
        }

        _prevEnableGodRays = settings.EnableGodRays;
        _prevGodRayStabilizeDebug = settings.GodRayStabilizeDebug;
        return new GodRaysPassInvalidation
        {
            GodRayHistory = true,
            VolumeFroxelHistory = true,
            VolumeIntegrateHistory = true,
            CloudHistory = true,
            TaaHistory = true,
            ResetGodRayLogs = true,
        };
    }

    public GodRaysPassInvalidation SyncVolumetricToggleState(in PreviewRenderSettingsSnapshot settings)
    {
        var cloudsChanged = _prevEnableVolumetricClouds != settings.EnableVolumetricClouds;
        var temporalChanged = _prevCloudDisableTemporal != settings.CloudDisableTemporal;
        if (!cloudsChanged && !temporalChanged)
        {
            return default;
        }

        _prevEnableVolumetricClouds = settings.EnableVolumetricClouds;
        _prevCloudDisableTemporal = settings.CloudDisableTemporal;
        return new GodRaysPassInvalidation
        {
            GodRayHistory = true,
            VolumeFroxelHistory = true,
            CloudHistory = true,
            TaaHistory = true,
        };
    }

    public static float ResolveSceneCaptureScale(
        in PreviewRenderSettingsSnapshot settings,
        Func<PreviewRenderSettingsSnapshot, bool> isTaaActive,
        Func<PreviewRenderSettingsSnapshot, PreviewVolumetricQuality.TaaProfile> resolveEffectiveTaa)
    {
        if (!isTaaActive(settings))
        {
            return 1f;
        }

        var taa = resolveEffectiveTaa(settings);
        // Cap at 1.5x. A former 2x tier (EdgeAA / high FXAA) still produced a hard bottom-half
        // cutoff on some GL/ANGLE paths after the odd-height fix; EdgeAA quality comes from the
        // resolve profile (edge blend / FXAA), not from a second SSAA octave.
        if (settings.PreviewTaaForceFxaa ||
            Math.Clamp(settings.PreviewTaaMode, 0, 4) == 2 ||
            taa.EdgeAaBlend > 0.05f ||
            taa.FxaaEdgeStrength > 0.20f)
        {
            return 1.5f;
        }

        return 1f;
    }

    public static void ResolveSceneCaptureSize(
        in GlRenderFrame frame,
        Func<PreviewRenderSettingsSnapshot, bool> isTaaActive,
        Func<PreviewRenderSettingsSnapshot, PreviewVolumetricQuality.TaaProfile> resolveEffectiveTaa,
        out int captureW,
        out int captureH,
        out float captureScale)
    {
        captureScale = ResolveSceneCaptureScale(frame.Settings, isTaaActive, resolveEffectiveTaa);
        if (captureScale > 1f)
        {
            var maxOutputDimension = Math.Max(frame.Vw, frame.Vh);
            var maxAllowedScale = PreviewTaaSsaaMaxDimension / (float)Math.Max(1, maxOutputDimension);
            captureScale = Math.Clamp(captureScale, 1f, Math.Max(1f, maxAllowedScale));
        }

        // Keep capture dimensions even. Odd heights (e.g. 683 @ 1.5x → 1025) combined with
        // half-res AO / Y-sensitive blits produced a hard horizontal lighting split.
        captureW = AlignEvenCaptureDimension((int)MathF.Ceiling(frame.Vw * captureScale));
        captureH = AlignEvenCaptureDimension((int)MathF.Ceiling(frame.Vh * captureScale));
    }

    internal static int AlignEvenCaptureDimension(int size)
    {
        size = Math.Max(2, size);
        return (size & 1) == 0 ? size : size + 1;
    }

    public bool TryLogSceneCaptureAaScale(
        in GlRenderFrame frame,
        Action<string> emitDiagnostic)
    {
        if (!frame.Settings.LogPreviewTaaDiagnostics || frame.SceneCaptureScale <= 1f)
        {
            return false;
        }

        var key = HashCode.Combine(
            frame.Vw,
            frame.Vh,
            frame.SceneCaptureW,
            frame.SceneCaptureH,
            MathF.Round(frame.SceneCaptureScale * 100f),
            Math.Clamp(frame.Settings.PreviewTaaMode, 0, 4),
            frame.Settings.PreviewTaaForceFxaa);
        if (_lastSceneCaptureAaLogKey == key)
        {
            return false;
        }

        _lastSceneCaptureAaLogKey = key;
        emitDiagnostic(
            $"[3D preview] Scene capture AA scale: {frame.SceneCaptureScale:0.##}x " +
            $"({frame.Vw}x{frame.Vh} -> {frame.SceneCaptureW}x{frame.SceneCaptureH}, " +
            $"taaMode={Math.Clamp(frame.Settings.PreviewTaaMode, 0, 4)} forceFxaa={frame.Settings.PreviewTaaForceFxaa}).");
        return true;
    }

    public bool TryLogVolumetricTiming(
        in PreviewRenderSettingsSnapshot settings,
        double injectMs,
        double integrateMs,
        Action<string> emitDiagnostic)
    {
        if (!settings.LogVolumetricTiming)
        {
            return false;
        }

        var totalMs = injectMs + integrateMs;
        if (totalMs < 2.5)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now - _lastVolumetricTimingLogMs < 8000)
        {
            return false;
        }

        _lastVolumetricTimingLogMs = now;
        emitDiagnostic(
            $"[3D preview] Volumetric pass timing: inject {injectMs:F2} ms, integrate {integrateMs:F2} ms " +
            $"(budget ~2.5 ms @1080p; quality={settings.VolumetricQuality}).");
        return true;
    }

    public void SeedToggleBaseline(in PreviewRenderSettingsSnapshot settings)
    {
        _prevEnableGodRays = settings.EnableGodRays;
        _prevGodRayStabilizeDebug = settings.GodRayStabilizeDebug;
        _prevEnableVolumetricClouds = settings.EnableVolumetricClouds;
        _prevCloudDisableTemporal = settings.CloudDisableTemporal;
    }
}
