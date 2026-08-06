using System.Diagnostics;
using System.Runtime.InteropServices;

using AutoPBR.App.Lang;
using AutoPBR.App.Rendering.Abstractions;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private GlShaderCompileContext? _shaderCtx;
    private PreviewGpuInitTier _gpuInitTier = PreviewGpuInitTier.None;
    private readonly Stopwatch _gpuInitStopwatch = new();
    private PreviewGpuInitProgress _gpuInitProgress = PreviewGpuInitProgress.Starting;
    private bool _shadowAwareGodRayInitAttempted;

    public PreviewGpuInitProgress GpuInitProgress
    {
        get
        {
            lock (_sync)
            {
                return _gpuInitProgress;
            }
        }
    }

    public event Action<PreviewGpuInitProgress>? GpuInitProgressChanged;

    private void RaiseGpuInitProgress(string phase, in PreviewRenderSettingsSnapshot settings)
    {
        var desired = ComputeDesiredGpuTier(settings);
        var bootstrapFrac = _gpuBootstrap?.Fraction ?? (_gpuAlive ? 1.0 : 0.0);
        var tierFrac = ComputeTierProgressFraction(desired);
        var prewarmFrac = PreviewShaderPrewarm.Fraction;
        // Terrain first-fill + post-core tiers own a large share so the bar does not jump to
        // ~near-complete at CoreReady while the viewport is still black.
        var terrainFrac = _terrainStartupReadyLatched
            ? 1.0
            : ResolveTerrainInitProgressFraction();
        var progressFraction = Math.Clamp(
            prewarmFrac * 0.10 + bootstrapFrac * 0.30 + tierFrac * 0.30 + terrainFrac * 0.30,
            0.0,
            1.0);
        var fullyReady = PreviewShaderPrewarm.IsComplete &&
                         _gpuAlive &&
                         _gpuBootstrap is null &&
                         _gpuInitTier.HasAll(desired) &&
                         terrainFrac >= 1.0;
        var progress = new PreviewGpuInitProgress
        {
            ShaderSourcesReady = PreviewShaderPrewarm.IsComplete,
            CoreReady = _gpuInitTier.HasAll(PreviewGpuInitTier.Core),
            GodRaysReady = (desired & PreviewGpuInitTier.GodRays) == 0 ||
                           _gpuInitTier.HasAll(PreviewGpuInitTier.GodRays),
            CloudsReady = (desired & PreviewGpuInitTier.Clouds) == 0 ||
                          _gpuInitTier.HasAll(PreviewGpuInitTier.Clouds),
            PreviewTaaReady = (desired & PreviewGpuInitTier.PreviewTaa) == 0 ||
                              _gpuInitTier.HasAll(PreviewGpuInitTier.PreviewTaa),
            ScreenSpaceAoReady = (desired & PreviewGpuInitTier.ScreenSpaceAo) == 0 ||
                                 _gpuInitTier.HasAll(PreviewGpuInitTier.ScreenSpaceAo),
            IsFullyReady = fullyReady,
            Phase = phase,
            ProgressFraction = fullyReady ? 1.0 : progressFraction,
            ElapsedMs = _gpuInitStopwatch.Elapsed.TotalMilliseconds,
        };

        // EnsureGpuTier raises every frame once Core is up; only notify when readiness/phase/fraction change.
        // ElapsedMs alone must not fire — subscribers re-push the 3D scene and flood the UI log.
        var prev = _gpuInitProgress;
        var changed = prev.ShaderSourcesReady != progress.ShaderSourcesReady ||
                      prev.CoreReady != progress.CoreReady ||
                      prev.GodRaysReady != progress.GodRaysReady ||
                      prev.CloudsReady != progress.CloudsReady ||
                      prev.PreviewTaaReady != progress.PreviewTaaReady ||
                      prev.ScreenSpaceAoReady != progress.ScreenSpaceAoReady ||
                      prev.IsFullyReady != progress.IsFullyReady ||
                      !string.Equals(prev.Phase, progress.Phase, StringComparison.Ordinal) ||
                      Math.Abs(prev.ProgressFraction - progress.ProgressFraction) > 1e-4;
        _gpuInitProgress = progress;
        if (changed)
        {
            GpuInitProgressChanged?.Invoke(progress);
        }
    }

    private double ComputeTierProgressFraction(PreviewGpuInitTier desired)
    {
        var total = 0;
        var ready = 0;
        if ((desired & PreviewGpuInitTier.Core) != 0)
        {
            total++;
            if (_gpuInitTier.HasAll(PreviewGpuInitTier.Core))
            {
                ready++;
            }
        }

        if ((desired & PreviewGpuInitTier.GodRays) != 0)
        {
            total++;
            if (_gpuInitTier.HasAll(PreviewGpuInitTier.GodRays))
            {
                ready++;
            }
        }

        if ((desired & PreviewGpuInitTier.Clouds) != 0)
        {
            total++;
            if (_gpuInitTier.HasAll(PreviewGpuInitTier.Clouds))
            {
                ready++;
            }
        }

        if ((desired & PreviewGpuInitTier.PreviewTaa) != 0)
        {
            total++;
            if (_gpuInitTier.HasAll(PreviewGpuInitTier.PreviewTaa))
            {
                ready++;
            }
        }

        if ((desired & PreviewGpuInitTier.ScreenSpaceAo) != 0)
        {
            total++;
            if (_gpuInitTier.HasAll(PreviewGpuInitTier.ScreenSpaceAo))
            {
                ready++;
            }
        }

        return total == 0 ? 1.0 : (double)ready / total;
    }

    /// <summary>
    /// 0..1 for first Full-disk fill after CoreReady. Overlay stays up until a paintable set of
    /// Full chunks is GPU-resident (avoids dismissing into a black/empty viewport).
    /// </summary>
    private double ResolveTerrainInitProgressFraction()
    {
        if (!_settings.ShowGroundMesh)
        {
            return 1.0;
        }

        if (!_gpuInitTier.HasAll(PreviewGpuInitTier.Core) || _terrainStreamer is null)
        {
            return 0.0;
        }

        // Paintable near pad — not the whole hard disk. Overlay should hide once the viewport
        // is no longer empty/black; Full catch-up continues without blocking "ready".
        var hard = Math.Max(0, _terrainStreamer.HardRadiusChunks);
        var near = Math.Min(hard, 2);
        var target = Math.Max(9, (2 * near + 1) * (2 * near + 1));
        var fullResident = 0;
        var cameraChunk = _terrainStreamer.CameraChunk;
        foreach (var key in _terrainGpuChunks.Keys)
        {
            // Distant Full uploads do not make the viewport usable. Only hide the overlay after
            // the camera-local pad is present; the old global count could report ready while
            // cameraChunkResident was still false and the preview showed only sky.
            if (key.IsFull &&
                key.ChebyshevDistanceToChunk(cameraChunk) <= near)
            {
                fullResident++;
            }
        }

        if (fullResident >= target)
        {
            return 1.0;
        }

        return Math.Clamp(fullResident / (double)target, 0.0, 0.99);
    }

    private static PreviewGpuInitTier ComputeDesiredGpuTier(in PreviewRenderSettingsSnapshot settings)
    {
        var tier = PreviewGpuInitTier.Core;
        if (settings.EnableGodRays || settings.EnableScreenSpaceGodRays)
        {
            tier |= PreviewGpuInitTier.GodRays;
        }

        if (settings.EnableVolumetricClouds)
        {
            tier |= PreviewGpuInitTier.Clouds;
        }

        if (settings.EnablePreviewTaa &&
            (PreviewVolumetricQuality.ResolvePreviewTaa(settings.VolumetricQuality, settings.PreviewTaaMode).TemporalWeight > 0f ||
             settings.PreviewTaaForceFxaa))
        {
            tier |= PreviewGpuInitTier.PreviewTaa;
        }

        if (settings.EnableScreenSpaceAo)
        {
            tier |= PreviewGpuInitTier.ScreenSpaceAo;
        }

        return tier;
    }

    private void EnsureGpuTier(in PreviewRenderSettingsSnapshot settings)
    {
        if (_gl is null || _shaderCtx is null || !_gpuInitTier.HasAll(PreviewGpuInitTier.Core))
        {
            return;
        }

        var desired = ComputeDesiredGpuTier(settings);
        // God rays compile first; when clouds are also enabled, load them in the same frame so
        // the first combined draw never runs with capture ready but cloud shaders/textures missing.
        if ((desired & PreviewGpuInitTier.GodRays) != 0 && !_gpuInitTier.HasAll(PreviewGpuInitTier.GodRays))
        {
            RaiseGpuInitProgress(PreviewGpuInitPhases.LoadingGodRays, settings);
            TryInitGodRaysCore(_gl, _useOpenGlEs);
            TryInitVolume(_gl, _useOpenGlEs);
            _gpuInitTier |= PreviewGpuInitTier.GodRays;
            if ((desired & PreviewGpuInitTier.Clouds) != 0 && !_gpuInitTier.HasAll(PreviewGpuInitTier.Clouds))
            {
                TryInitCloudGpuTierIfNeeded(settings, _previewPixelWidth, _previewPixelHeight);
            }

            RaiseGpuInitProgress(ResolvePostCoreInitPhase(desired), settings);
            return;
        }

        if ((desired & PreviewGpuInitTier.Clouds) != 0 && !_gpuInitTier.HasAll(PreviewGpuInitTier.Clouds))
        {
            TryInitCloudGpuTierIfNeeded(settings, _previewPixelWidth, _previewPixelHeight);
            RaiseGpuInitProgress(ResolvePostCoreInitPhase(desired), settings);
            return;
        }

        if ((desired & PreviewGpuInitTier.PreviewTaa) != 0 && !_gpuInitTier.HasAll(PreviewGpuInitTier.PreviewTaa))
        {
            RaiseGpuInitProgress(PreviewGpuInitPhases.LoadingTaa, settings);
            TryInitPreviewTaa(_gl, _useOpenGlEs);
            _gpuInitTier |= PreviewGpuInitTier.PreviewTaa;
            RaiseGpuInitProgress(ResolvePostCoreInitPhase(desired), settings);
            return;
        }

        if ((desired & PreviewGpuInitTier.ScreenSpaceAo) != 0 && !_gpuInitTier.HasAll(PreviewGpuInitTier.ScreenSpaceAo))
        {
            RaiseGpuInitProgress(PreviewGpuInitPhases.LoadingScreenSpaceAo, settings);
            TryInitScreenSpaceAo(_gl, _useOpenGlEs);
            // Only mark ready when programs linked; otherwise retry next frame.
            if (_ssaoProgram is { IsValid: true } &&
                _gtaoProgram is { IsValid: true } &&
                _aoBilateralProgram is { IsValid: true } &&
                _aoTemporalProgram is { IsValid: true } &&
                _aoCompositeProgram is { IsValid: true } &&
                _aoResources is not null)
            {
                _gpuInitTier |= PreviewGpuInitTier.ScreenSpaceAo;
            }
        }

        RaiseGpuInitProgress(ResolvePostCoreInitPhase(desired), settings);
    }

    /// <summary>
    /// Prefer UploadingMeshes over Ready/PreviewReady while the Full pad is still empty so the
    /// overlay text matches a still-black viewport after shader tiers finish.
    /// </summary>
    private string ResolvePostCoreInitPhase(PreviewGpuInitTier desired)
    {
        if (!_gpuInitTier.HasAll(desired))
        {
            return PreviewGpuInitPhases.PreviewReady;
        }

        if (!_terrainStartupReadyLatched &&
            ResolveTerrainInitProgressFraction() < 1.0)
        {
            return PreviewGpuInitPhases.UploadingMeshes;
        }

        return PreviewGpuInitPhases.Ready;
    }

    private void InitShaderCompileContext(GL gl, bool useOpenGlEs)
    {
        unsafe
        {
            var vendorPtr = gl.GetString(StringName.Vendor);
            var rendererPtr = gl.GetString(StringName.Renderer);
            var vendor = vendorPtr is null ? "unknown" : Marshal.PtrToStringUTF8((nint)vendorPtr) ?? "unknown";
            var renderer = rendererPtr is null ? "unknown" : Marshal.PtrToStringUTF8((nint)rendererPtr) ?? "unknown";
            _shaderCtx = new GlShaderCompileContext(gl, useOpenGlEs, vendor, renderer);
        }
    }

    private GlShaderProgram CreatePreviewProgram(string vertexFile, string fragmentFile, out string? error,
        string? debugLabel = null, IReadOnlyDictionary<string, int>? defines = null) =>
        _shaderCtx!.CreateProgram(vertexFile, fragmentFile, out error, debugLabel, defines);

    private GlShaderProgram CreatePreviewProgram(
        string vertexFile,
        string tessControlFile,
        string tessEvaluationFile,
        string fragmentFile,
        out string? error,
        string? debugLabel = null,
        IReadOnlyDictionary<string, int>? defines = null) =>
        _shaderCtx!.CreateProgram(vertexFile, tessControlFile, tessEvaluationFile, fragmentFile, out error, debugLabel, defines);

    private GlShaderProgram CreatePreviewComputeProgram(
        string computeFile,
        out string? error,
        string? debugLabel = null,
        IReadOnlyDictionary<string, int>? defines = null) =>
        _shaderCtx!.CreateComputeProgram(computeFile, out error, debugLabel, defines);

    private bool TryEnsureProceduralSkyProgram()
    {
        if (_proceduralSkyProgram is { IsValid: true } || _gl is null)
        {
            return _proceduralSkyProgram is { IsValid: true };
        }

        _proceduralSkyProgram = new GlProceduralSkyProgram(_gl, _useOpenGlEs, out var procErr);
        if (_proceduralSkyProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Procedural sky fallback: " + (procErr ?? "link failed"));
            _proceduralSkyProgram?.Dispose();
            _proceduralSkyProgram = null;
            return false;
        }

        EmitDiagnostic("[3D preview] Using embedded procedural sky (LUT sky shader unavailable).");
        _proceduralSkyUniformLocs = ResolveProceduralSkyUniformLocs(_proceduralSkyProgram);
        return true;
    }

    private void TryEnsureShadowAwareGodRayProgram()
    {
        if (_shadowAwareGodRayProgram is { IsValid: true } || _shaderCtx is null || _shadowAwareGodRayInitAttempted)
        {
            return;
        }

        _shadowAwareGodRayInitAttempted = true;
        var godRayDefines = BuildGodRaySparseMarchDefines(_settings.GodRaySparseMarch);
        _shadowAwareGodRayProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_godrays_shadow.frag",
            out var shErr, defines: godRayDefines);
        if (_shadowAwareGodRayProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Shadow-aware god-ray shader: " + TrimShaderDiagnostic(shErr));
            _shadowAwareGodRayProgram?.Dispose();
            _shadowAwareGodRayProgram = null;
        }
        else
        {
            _shadowAwareGodRayUniformLocs = ResolveShadowAwareGodRayUniformLocs(_shadowAwareGodRayProgram);
        }
    }
}
