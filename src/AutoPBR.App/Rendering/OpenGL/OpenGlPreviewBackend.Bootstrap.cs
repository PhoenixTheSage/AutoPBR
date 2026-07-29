
using AutoPBR.App.Lang;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private GpuBootstrapRunner? _gpuBootstrap;
    private bool _gpuBootstrapAborted;
    private bool _pendingShaderReload;
    private int _gpuGenesisPrewarmIndex;
    private string _glVersionString = "(unknown)";

    public string? ActiveContextSummary { get; private set; }

    private sealed class GpuBootstrapRunner
    {
        private int _step;
        private const int StepCount = 8;

        public bool IsComplete => _step >= StepCount;

        public double Fraction => Math.Clamp((double)_step / StepCount, 0.0, 1.0);

        public string Phase => PreviewGpuInitPhases.BootstrapPhase(_step);

        public void Advance(OpenGlPreviewBackend backend, double maxMilliseconds)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_step < StepCount && sw.Elapsed.TotalMilliseconds < maxMilliseconds)
            {
                if (!backend.RunGpuBootstrapStep(_step))
                {
                    return;
                }

                _step++;
                // Terrain streaming (step 3) starts the occluder bake; pump uploads while later
                // steps run so DDA can be valid on the first real scene frame.
                if (_step > 3)
                {
                    backend.PumpTerrainOccluderAtlasBootstrap();
                }
            }
        }
    }

    public void InvalidateShaderCachesAndReload()
    {
        GlProgramBinaryCache.ClearAll();
        PreviewShaderPrewarm.ClearAndRestart();
        lock (_sync)
        {
            if (_gl is null)
            {
                return;
            }

            _pendingShaderReload = true;
            _gpuInitStopwatch.Restart();
            RaiseGpuInitProgress(PreviewGpuInitPhases.ClearingShaderCache, _settings);
        }
    }

    private void HandlePendingShaderReloadLocked()
    {
        if (!_pendingShaderReload)
        {
            return;
        }

        ReleaseGpuResourceObjectsLocked();
        _gpuBootstrap = new GpuBootstrapRunner();
        _gpuGenesisPrewarmIndex = 0;
        _gpuBootstrapAborted = false;
        _pendingShaderReload = false;
    }

    private void ReleaseGpuResourceObjectsLocked()
    {
        _gpuAlive = false;
        _mesh?.Dispose();
        _mesh = null;
        _groundMesh?.Dispose();
        _groundMesh = null;
        DisposeTerrainGpuChunks();
        DisposeTerrainMeshPool();
        DisposeGroundTextureArrays();
        _terrainStreamer?.Dispose();
        _terrainStreamer = null;
        // Streamer recreate starts with BuiltIn defaults; re-apply cached bake rules on next PassSetup.
        _terrainGrassBakeSettingsDirty = true;
        _terrainVegetationBakePlanDirty = true;
        _terrainWorldGenSettingsDirty = true;
        DisposeGroundGpuResources();
        _neutralNormal?.Dispose();
        _neutralNormal = null;
        _neutralSpec?.Dispose();
        _neutralSpec = null;
        _neutralHeight?.Dispose();
        _neutralHeight = null;
        AbandonPendingMaterialUpload();
        _albedo?.Dispose();
        _albedo = null;
        _normal?.Dispose();
        _normal = null;
        _spec?.Dispose();
        _spec = null;
        _height?.Dispose();
        _height = null;
        DisposeMaterialTextureArrays();
        DisposeGpuTimerProfiler();
        _program?.Dispose();
        _program = null;
        _mainProgramUsesTessellation = false;
        _shadowProgram?.Dispose();
        _shadowProgram = null;
        _shadowTarget?.Dispose();
        _shadowTarget = null;
        _shadowTargetCascadeNear?.Dispose();
        _shadowTargetCascadeNear = null;
        _shadowTargetCascadeMid?.Dispose();
        _shadowTargetCascadeMid = null;
        _shadowTargetsNearRes = 0;
        _shadowTargetsMidRes = 0;
        _shadowTargetsFarRes = 0;
        _shadowTargetsWantCascades = false;
        _shadowTargetsDirty = false;
        _grassGroundReady = false;
        DestroyAtmosphereResources();
        DestroyImageHistogramResources();
        DestroyGodRayResources();
        DestroyVolumeResources();
        DestroyVolumetricCloudResources();
        DestroyPreviewTaaResources();
        DestroyMoonBillboard();
        DestroyLineOverlay();
        DestroySunDebugOverlay();
        _proceduralSkyProgram?.Dispose();
        _proceduralSkyProgram = null;
        DisposeEntitySkinningUploadBuffers();
        DisposeGenesisMaterialDrawRecordBuffer();
        DisposeGenesisIndirectDrawCommands();
        _shaderCtx = null;
        _shaderToolchainPlan = null;
        _gpuInitTier = PreviewGpuInitTier.None;
        _gpuGenesisPrewarmIndex = 0;
        _shadowAwareGodRayInitAttempted = false;
        _atmoLutsValid = false;
        // Atlas may survive reload; re-emit the enable diagnostic when DDA comes back.
        _loggedVoxelDdaOcclusionEnabled = false;
        _loggedVoxelDdaOcclusionPending = false;
        _loggedVoxelDdaSlowBake = false;
        _loggedVoxelDdaFailure = "none";
        _loggedHiZOcclusionEnabled = false;
        _voxelDdaReadyThisFrame = false;
        _hiZReadyThisFrame = false;
    }

    private bool RunGpuBootstrapStep(int step)
    {
        var gl = _gl!;
        switch (step)
        {
            case 0:
                InitShaderCompileContext(gl, _useOpenGlEs);
                EmitDiagnostic(_useOpenGlEs
                    ? $"[3D preview] Context: {_glVersionString} (Genesis shader path, GLSL ES 3.0)."
                    : $"[3D preview] Context: {_glVersionString} (Genesis shader path, GLSL 330 core).");
                if (_glCapabilities is not null)
                {
                    EmitDiagnostic(_glCapabilities.FormatDiagnostic());
                }

                if (_shaderToolchainPlan is not null)
                {
                    EmitDiagnostic(_shaderToolchainPlan.FormatDiagnostic());
                }

                RecordActiveContextSummary();
                _mainProgramUsesTessellation = false;
                string? err = null;
                var bootMask = GenesisShaderFeatureMaskBuilder.Build(_settings, entityEmulatedPreview: false);
                var bootUseEntitySkinningSsbo = ShouldUseEntitySkinningSsbo();
                var bootUseMaterialDrawRecordSsbo = ShouldUseMaterialDrawRecordSsbo();
                var bootUseDrawRecordBaseInstance = bootUseMaterialDrawRecordSsbo && ShouldUseDrawRecordBaseInstance();
                var bootDefines = BuildGenesisProgramDefines(
                    bootMask,
                    bootUseEntitySkinningSsbo,
                    bootUseMaterialDrawRecordSsbo,
                    materialTextureArrays: false,
                    drawRecordBaseInstance: bootUseDrawRecordBaseInstance);
                if (!_useOpenGlEs)
                {
                    _program = CreatePreviewProgram(
                        "genesis.vert",
                        "genesis.tcs",
                        "genesis.tes",
                        "genesis.frag",
                        out err,
                        "genesis+tessellation",
                        bootDefines);
                    if (_program.IsValid)
                    {
                        _mainProgramUsesTessellation = true;
                        EmitDiagnostic("[3D preview] Genesis tessellation program ready (triangle patches).");
                    }
                    else
                    {
                        DisableGenesisTessellationCompile(err ?? "link failed");
                        _program.Dispose();
                        _program = null;
                    }
                }

                var bootUseMaterialTextureArrays = ShouldUseMaterialTextureArrays();
                bootDefines = BuildGenesisProgramDefines(
                    bootMask,
                    bootUseEntitySkinningSsbo,
                    bootUseMaterialDrawRecordSsbo,
                    bootUseMaterialTextureArrays,
                    bootUseDrawRecordBaseInstance);
                if (_mainProgramUsesTessellation && _program is { IsValid: true })
                {
                    // Upgrade the boot tessellation program to the full desktop feature set
                    // (arrays + base-instance compose with TCS/TES on WGL).
                    var tessWithFeatures = CreatePreviewProgram(
                        "genesis.vert",
                        "genesis.tcs",
                        "genesis.tes",
                        "genesis.frag",
                        out err,
                        $"genesis+tess+{(byte)bootMask:X2}",
                        bootDefines);
                    if (tessWithFeatures.IsValid)
                    {
                        _program.Dispose();
                        _program = tessWithFeatures;
                    }
                    else
                    {
                        tessWithFeatures.Dispose();
                        // Keep the working tessellation program; frame selection can still
                        // promote arrays on a later non-tess or tess+array cache entry.
                        bootUseMaterialTextureArrays = false;
                        bootUseDrawRecordBaseInstance = false;
                        EmitDiagnostic(
                            "[3D preview] Genesis tessellation+texture-array boot upgrade deferred. " +
                            (err ?? "link failed"));
                    }
                }

                _program ??= CreatePreviewProgram("genesis.vert", "genesis.frag", out err, defines: bootDefines);
                if (_program is { IsValid: false } && bootUseDrawRecordBaseInstance)
                {
                    _program.Dispose();
                    _program = null;
                    DisableDrawRecordBaseInstanceCompile(err);
                    bootUseDrawRecordBaseInstance = false;
                    bootDefines = BuildGenesisProgramDefines(
                        bootMask,
                        bootUseEntitySkinningSsbo,
                        bootUseMaterialDrawRecordSsbo,
                        bootUseMaterialTextureArrays,
                        drawRecordBaseInstance: false);
                    _program = CreatePreviewProgram("genesis.vert", "genesis.frag", out err, defines: bootDefines);
                }

                if (_program is { IsValid: false } && bootUseMaterialTextureArrays)
                {
                    _program.Dispose();
                    _program = null;
                    DisableMaterialTextureArraysCompile(err);
                    bootUseMaterialTextureArrays = false;
                    bootDefines = BuildGenesisProgramDefines(
                        bootMask,
                        bootUseEntitySkinningSsbo,
                        bootUseMaterialDrawRecordSsbo,
                        materialTextureArrays: false,
                        drawRecordBaseInstance: bootUseDrawRecordBaseInstance);
                    _program = CreatePreviewProgram("genesis.vert", "genesis.frag", out err, defines: bootDefines);
                }

                if (_program is { IsValid: false } && bootUseMaterialDrawRecordSsbo)
                {
                    _program.Dispose();
                    _program = null;
                    DisableMaterialDrawRecordSsboCompile(err);
                    bootUseMaterialDrawRecordSsbo = false;
                    bootUseDrawRecordBaseInstance = false;
                    bootDefines = BuildGenesisProgramDefines(
                        bootMask,
                        bootUseEntitySkinningSsbo,
                        materialDrawRecordSsbo: false,
                        materialTextureArrays: false);
                    _program = CreatePreviewProgram("genesis.vert", "genesis.frag", out err, defines: bootDefines);
                }

                if (_program is { IsValid: false } && bootUseEntitySkinningSsbo)
                {
                    _program.Dispose();
                    _program = null;
                    DisableEntitySkinningSsboCompile(err);
                    bootUseEntitySkinningSsbo = false;
                    bootDefines = BuildGenesisProgramDefines(
                        bootMask,
                        entitySkinningSsbo: false,
                        materialDrawRecordSsbo: bootUseMaterialDrawRecordSsbo,
                        materialTextureArrays: bootUseMaterialTextureArrays,
                        drawRecordBaseInstance: bootUseDrawRecordBaseInstance);
                    _program = CreatePreviewProgram("genesis.vert", "genesis.frag", out err, defines: bootDefines);
                }

                if (!_program.IsValid)
                {
                    _lastError = err ?? "Shader link failed.";
                    EmitDiagnostic("[3D preview] " + _lastError);
                    _program.Dispose();
                    _program = null;
                    _gpuBootstrapAborted = true;
                    return false;
                }

                _mainEntityUniformLocs = ResolveEntitySkinningUniformLocs(_program);
                _mainUniformLocs = ResolveMainProgramUniformLocs(_program);
                _activeGenesisProgramKey = new GenesisProgramCacheKey(
                    bootMask,
                    _mainProgramUsesTessellation,
                    bootUseEntitySkinningSsbo,
                    bootUseMaterialDrawRecordSsbo,
                    bootUseMaterialTextureArrays,
                    bootUseMaterialDrawRecordSsbo && bootUseDrawRecordBaseInstance);
                _genesisPrograms[_activeGenesisProgramKey] = _program;
                _genesisProgramLru.AddFirst(_activeGenesisProgramKey);
                return true;

            case 1:
                var shadowUseEntitySkinningSsbo = ShouldUseEntitySkinningSsbo();
                var shadowUseMaterialDrawRecordSsbo = ShouldUseMaterialDrawRecordSsbo();
                var shadowUseMaterialTextureArrays = ShouldUseMaterialTextureArrays();
                var shadowUseDrawRecordBaseInstance =
                    shadowUseMaterialDrawRecordSsbo && ShouldUseDrawRecordBaseInstance();
                var shadowDefines = BuildGenesisProgramDefines(
                    GenesisShaderFeatureMask.None,
                    shadowUseEntitySkinningSsbo,
                    shadowUseMaterialDrawRecordSsbo,
                    shadowUseMaterialTextureArrays,
                    shadowUseDrawRecordBaseInstance);
                _shadowProgram = CreatePreviewProgram(
                    "genesis_shadow.vert",
                    "genesis_shadow.frag",
                    out var shadowErr,
                    defines: shadowDefines);
                if (_shadowProgram is { IsValid: false } && shadowUseDrawRecordBaseInstance)
                {
                    var fallbackMainKey = _activeGenesisProgramKey with { DrawRecordBaseInstance = false };
                    if (TryGetOrCreateGenesisProgram(fallbackMainKey, out var fallbackMainProgram, out var fallbackMainErr))
                    {
                        _shadowProgram.Dispose();
                        _shadowProgram = null;
                        DisableDrawRecordBaseInstanceCompile(shadowErr);
                        if (_program is not null && !_genesisPrograms.ContainsValue(_program))
                        {
                            _program.Dispose();
                        }

                        _program = fallbackMainProgram;
                        _activeGenesisProgramKey = fallbackMainKey;
                        _mainProgramUsesTessellation = fallbackMainKey.Tessellation;
                        _mainEntityUniformLocs = ResolveEntitySkinningUniformLocs(_program);
                        _mainUniformLocs = ResolveMainProgramUniformLocs(_program);
                        shadowUseDrawRecordBaseInstance = false;
                        shadowDefines = BuildGenesisProgramDefines(
                            GenesisShaderFeatureMask.None,
                            shadowUseEntitySkinningSsbo,
                            shadowUseMaterialDrawRecordSsbo,
                            shadowUseMaterialTextureArrays,
                            drawRecordBaseInstance: false);
                        _shadowProgram = CreatePreviewProgram(
                            "genesis_shadow.vert",
                            "genesis_shadow.frag",
                            out shadowErr,
                            defines: shadowDefines);
                    }
                    else
                    {
                        EmitDiagnostic(
                            "[3D preview] Multi-draw draw-record fallback main program failed: " +
                            (fallbackMainErr ?? "link failed"));
                    }
                }

                if (_shadowProgram is { IsValid: false } && shadowUseMaterialTextureArrays)
                {
                    var fallbackMainKey = _activeGenesisProgramKey with { MaterialTextureArrays = false };
                    if (TryGetOrCreateGenesisProgram(fallbackMainKey, out var fallbackMainProgram, out var fallbackMainErr))
                    {
                        _shadowProgram.Dispose();
                        _shadowProgram = null;
                        DisableMaterialTextureArraysCompile(shadowErr);
                        if (_program is not null && !_genesisPrograms.ContainsValue(_program))
                        {
                            _program.Dispose();
                        }

                        _program = fallbackMainProgram;
                        _activeGenesisProgramKey = fallbackMainKey;
                        _mainProgramUsesTessellation = fallbackMainKey.Tessellation;
                        _mainEntityUniformLocs = ResolveEntitySkinningUniformLocs(_program);
                        _mainUniformLocs = ResolveMainProgramUniformLocs(_program);
                        shadowUseMaterialTextureArrays = false;
                        shadowDefines = BuildGenesisProgramDefines(
                            GenesisShaderFeatureMask.None,
                            shadowUseEntitySkinningSsbo,
                            shadowUseMaterialDrawRecordSsbo,
                            materialTextureArrays: false,
                            drawRecordBaseInstance: shadowUseDrawRecordBaseInstance);
                        _shadowProgram = CreatePreviewProgram(
                            "genesis_shadow.vert",
                            "genesis_shadow.frag",
                            out shadowErr,
                            defines: shadowDefines);
                    }
                    else
                    {
                        EmitDiagnostic(
                            "[3D preview] Material texture-array fallback main program failed: " +
                            (fallbackMainErr ?? "link failed"));
                    }
                }

                if (_shadowProgram is { IsValid: false } && shadowUseMaterialDrawRecordSsbo)
                {
                    var fallbackMainKey = _activeGenesisProgramKey with
                    {
                        MaterialDrawRecordSsbo = false,
                        MaterialTextureArrays = false,
                        DrawRecordBaseInstance = false
                    };
                    if (TryGetOrCreateGenesisProgram(fallbackMainKey, out var fallbackMainProgram, out var fallbackMainErr))
                    {
                        _shadowProgram.Dispose();
                        _shadowProgram = null;
                        DisableMaterialDrawRecordSsboCompile(shadowErr);
                        if (_program is not null && !_genesisPrograms.ContainsValue(_program))
                        {
                            _program.Dispose();
                        }

                        _program = fallbackMainProgram;
                        _activeGenesisProgramKey = fallbackMainKey;
                        _mainProgramUsesTessellation = fallbackMainKey.Tessellation;
                        _mainEntityUniformLocs = ResolveEntitySkinningUniformLocs(_program);
                        _mainUniformLocs = ResolveMainProgramUniformLocs(_program);
                        shadowUseMaterialDrawRecordSsbo = false;
                        shadowUseDrawRecordBaseInstance = false;
                        shadowDefines = BuildGenesisProgramDefines(
                            GenesisShaderFeatureMask.None,
                            shadowUseEntitySkinningSsbo,
                            materialDrawRecordSsbo: false,
                            materialTextureArrays: false);
                        _shadowProgram = CreatePreviewProgram(
                            "genesis_shadow.vert",
                            "genesis_shadow.frag",
                            out shadowErr,
                            defines: shadowDefines);
                    }
                    else
                    {
                        EmitDiagnostic(
                            "[3D preview] Material/draw record SSBO fallback main program failed: " +
                            (fallbackMainErr ?? "link failed"));
                    }
                }

                if (_shadowProgram is { IsValid: false } && shadowUseEntitySkinningSsbo)
                {
                    var fallbackMainKey = _activeGenesisProgramKey with { EntitySkinningSsbo = false };
                    if (TryGetOrCreateGenesisProgram(fallbackMainKey, out var fallbackMainProgram, out var fallbackMainErr))
                    {
                        _shadowProgram.Dispose();
                        _shadowProgram = null;
                        DisableEntitySkinningSsboCompile(shadowErr);
                        if (_program is not null && !_genesisPrograms.ContainsValue(_program))
                        {
                            _program.Dispose();
                        }

                        _program = fallbackMainProgram;
                        _activeGenesisProgramKey = fallbackMainKey;
                        _mainProgramUsesTessellation = fallbackMainKey.Tessellation;
                        _mainEntityUniformLocs = ResolveEntitySkinningUniformLocs(_program);
                        _mainUniformLocs = ResolveMainProgramUniformLocs(_program);
                        shadowDefines = BuildGenesisProgramDefines(
                            GenesisShaderFeatureMask.None,
                            entitySkinningSsbo: false,
                            materialDrawRecordSsbo: shadowUseMaterialDrawRecordSsbo,
                            materialTextureArrays: shadowUseMaterialTextureArrays,
                            drawRecordBaseInstance: shadowUseDrawRecordBaseInstance);
                        _shadowProgram = CreatePreviewProgram(
                            "genesis_shadow.vert",
                            "genesis_shadow.frag",
                            out shadowErr,
                            defines: shadowDefines);
                    }
                    else
                    {
                        EmitDiagnostic(
                            "[3D preview] Entity skinning SSBO fallback main program failed: " +
                            (fallbackMainErr ?? "link failed"));
                    }
                }

                if (!_shadowProgram.IsValid)
                {
                    EmitDiagnostic("[3D preview] Shadow program: " + (shadowErr ?? "link failed"));
                    _shadowProgram.Dispose();
                    _shadowProgram = null;
                }
                else
                {
                    _shadowEntityUniformLocs = ResolveEntitySkinningUniformLocs(_shadowProgram);
                    _shadowUniformLocs = ResolveShadowProgramUniformLocs(_shadowProgram);
                }

                InitEntitySkinningBoneUbo(gl);
                InitGenesisMaterialDrawRecordSsbo(gl);
                LogEntityShaderInitDiagnosticsOnce();
                return true;

            case 2:
                EnsureShadowMapTargets(gl, _settings);
                return true;

            case 3:
                if (!PreviewBundledGpuAssetPrewarm.IsGroundReady)
                {
                    return false;
                }

                _albedo = new GlTexture2D(gl);
                _normal = new GlTexture2D(gl);
                _spec = new GlTexture2D(gl);
                _height = new GlTexture2D(gl);
                _mesh = new GlMeshBuffer(gl);
                _groundMesh = new GlMeshBuffer(gl);
                // Keep a cheap depth-writing pad under streamed terrain. Residency only proves
                // that a chunk was uploaded, not that its current material/draw path produced
                // visible samples. The pad therefore remains a two-triangle safety underlay;
                // true terrain sits above it and replaces it without z-fighting.
                var groundFallback = PreviewMeshFactory.CreatePreviewGroundPlane(
                    name: "terrain_streaming_fallback",
                    halfExtent: PreviewStageConstants.TerrainFlatPadHalfExtent,
                    worldY: PreviewStageConstants.GroundPlaneWorldY - 0.015f);
                _groundMesh.Upload(
                    groundFallback.InterleavedVertices,
                    groundFallback.Indices);
                InitTerrainStreaming(gl);
                _neutralNormal = new GlTexture2D(gl);
                _neutralNormal.UploadRgba(1, 1, [128, 128, 255, 255]);
                _neutralSpec = new GlTexture2D(gl);
                _neutralSpec.UploadRgba(1, 1, [120, 60, 40, 255]);
                _neutralHeight = new GlTexture2D(gl);
                _neutralHeight.UploadRgba(1, 1, [128, 128, 128, 255]);
                _grassGroundReady = TryUploadBundledGroundFallback(gl);
                return true;

            case 4:
                TryInitLineOverlay(gl, _useOpenGlEs);
                return true;

            case 5:
                return TryInitMoonBillboard(gl, _useOpenGlEs);

            case 6:
                TryInitAtmosphere(gl);
                return true;

            case 7:
                if (!PrewarmNextCommonGenesisProgramOnGpu(
                        ref _gpuGenesisPrewarmIndex))
                {
                    return false;
                }

                _gpuInitTier = PreviewGpuInitTier.Core;
                EmitDiagnostic(
                    "[3D preview] Core GPU init: " +
                    $"{_gpuInitStopwatch.Elapsed.TotalMilliseconds:F0} ms, " +
                    $"sky={(_atmoSkyProgram is { IsValid: true } ? "lut" : "lazy-procedural")}, " +
                    $"atmoLut={(_atmoTransProgram is { IsValid: true } && _atmoSkyViewProgram is { IsValid: true } ? "yes" : "no")}.");
                _gpuAlive = true;
                _materialDirty = true;
                _meshDirty = true;
                if (_scene is not null)
                {
                    SyncOrbitFromSceneLocked(_scene);
                    _orbitSyncedKey = ResolveOrbitSyncKey(_scene, _blockModelSubject);
                }

                _loggedMeshReady = false;
                _loggedZeroIndex = false;
                return true;

            default:
                return true;
        }
    }

    private void RecordActiveContextSummary()
    {
        var capabilitySuffix = (_glCapabilities?.FormatContextSuffix() ?? string.Empty) +
                               (_shaderToolchainPlan?.FormatContextSuffix() ?? string.Empty);
        if (_nativeWglPresenterActive)
        {
            ActiveContextSummary = $"{_glVersionString} · GLSL 330 core (WGL native child){capabilitySuffix}";
        }
        else if (_desktopWglSidecar is not null)
        {
            ActiveContextSummary = _desktopWglSidecar.UsesDxInteropPresentation
                ? $"{_glVersionString} · GLSL 330 core (WGL sidecar · D3D11 interop){capabilitySuffix}"
                : $"{_glVersionString} · GLSL 330 core (WGL sidecar){capabilitySuffix}";
        }
        else
        {
            ActiveContextSummary = _useOpenGlEs
                ? $"{_glVersionString} · GLSL ES 3.0{capabilitySuffix}"
                : $"{_glVersionString} · GLSL 330 core{capabilitySuffix}";
        }

        if (PreviewOpenGlSession.RequestedDesktopGl4 && _useOpenGlEs)
        {
            EmitDiagnostic("[3D preview] " + Resources.PreviewOpenGlFallbackWarning);
        }
    }
}
