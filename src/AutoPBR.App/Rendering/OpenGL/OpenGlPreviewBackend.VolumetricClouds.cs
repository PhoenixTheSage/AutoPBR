using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.App.Services;

using Silk.NET.OpenGL;

using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    // CQ2.5 supplies the matching weather/detail channel semantics. Desktop GL may now
    // select validated v2 assets; GLES/ANGLE deliberately retains the v1 profile.
    private const bool Cq2V2ShaderProfileReady = true;

    private GlShaderProgram? _cloudProgram;
    private GlShaderProgram? _cloudHighProgram;
    private CloudUniformLocs _cloudHighUniformLocs;
    private GlShaderProgram? _cloudTemporalProgram;
    private GlShaderProgram? _cloudUpsampleProgram;
    private GlShaderProgram? _cloudRepairProgram;
    private uint _cloudQuadVao;
    private uint _cloudQuadVbo;
    private GlTexture3D? _cloudNoiseTex;
    private GlTexture3D? _cloudDetailTex;
    private GlTexture3D? _cloudStbnTex;
    private GlTexture2D? _cloudCoverageTex;
    private GlCloudTemporalRenderTarget? _cloudRenderTarget;
    private GlCloudTemporalRenderTarget? _cloudResolveTarget;
    private GlCloudTemporalRenderTarget? _cloudHistoryTarget;
    private GlCloudTemporalRenderTarget? _cloudRepairTarget;
    private GlCloudTemporalRenderTarget? _cloudCompositeTarget;
    private GlCloudRenderFormatProfile _cloudRenderFormatProfile = GlCloudRenderFormatProfile.Compatibility;
    private Matrix4x4 _cloudPrevViewProj = Matrix4x4.Identity;
    private Vector3 _cloudPrevCameraPos;
    private Vector3 _cloudPrevWindOffset;
    private Vector2 _cloudPrevCirrusWindOffset;
    private bool _cloudHistoryValid;
    private int _cloudFrameIndex;
    private int _cloudHistorySettingsHash;
    private int _cloudHistoryW;
    private int _cloudHistoryH;
    private int _cloudHistoryViewportW;
    private int _cloudHistoryViewportH;
    private readonly PreviewCloudDiagnosticGate _cloudProfileDiagnosticGate =
        new(capacity: 8);
    private readonly PreviewCloudDiagnosticGate _cloudLifecycleDiagnosticGate =
        new(capacity: 16);
    private int _cloudDeferredCompositeRetries;
    private int _loggedCloudDeferredCompositeMiss;
    private int _cloudTierReadyWarmupDraws;
    private bool _cloudRuntimeFaulted;
    private bool _loggedCloudFormatFallback;
    private bool _cloudFloatingPointTargetsFaulted;
    private bool _loggedCloudMomentFallback;
    private bool _cloudTemporalMomentsFaulted;
    private bool _cloudEdgeRepairFaulted;
    private bool _loggedCloudEdgeRepairFallback;
    private int _cloudHistoryConfidenceFrames;
    private int _cloudHistoryLowConfidenceFrames;
    private bool _cloudHistoryConfidenceFullLogged;
    private bool _cloudHistoryCopyFailureLogged;
    /// <summary>
    /// Last CA3.1 stability factor (camera/confidence). Consumed by CQ1.8 repair so
    /// the post-temporal STBN retrace does not undo idle denoise on soft edges.
    /// </summary>
    private float _cloudTemporalStability;
    private bool _cloudIdleDenoiseLatched;
    private string _cloudStbnDiagnostic = "not-initialized";
    private string _cloudMomentsDiagnostic = "not-initialized";
    private string _cloudEdgeRepairDiagnostic = "not-initialized";
    private string _cloudDensityAssetDiagnostic = "not-initialized";
    private int _cloudDensityAssetVersion;
    private int _cloudDensityAssetProfileCode;
    private Task<PreviewCloudDensityAssetSet>? _cloudDensityGenerationTask;
    private CancellationTokenSource? _cloudDensityGenerationCancellation;
    private string _cloudDensityGenerationLoadReason = "not-started";
    private bool _cloudDensityGenerationFailureLogged;
    private int _lastCloudDebugViewDiagnostic = -1;
    private PreviewSparseCloudBackendSelection _cloudDensityBackendSelection;
    private bool _sparseCloudResourcesReady;
    private bool _sparseCloudRuntimeFaulted;
    private bool _cloudDensityBackendSelectionInitialized;
    private PreviewSparseCloudTemplateAssetSet? _sparseCloudTemplateAssets;
    private string _sparseCloudTemplateDiagnostic = "not-initialized";
    private GlSparseCloudVolumeResources? _sparseCloudVolumeResources;
    private PreviewSparseCloudBrickAllocator? _sparseCloudBrickAllocator;
    private PreviewSparseCloudClipmapController? _sparseCloudClipmapController;
    private GlShaderProgram? _sparseCloudBrickGenerationProgram;
    private GlSparseCloudBrickGenerator? _sparseCloudBrickGenerator;
    private bool _sparseCloudResourceAllocationAttempted;
    private string _sparseCloudResourceDiagnostic = "not-initialized";
    private bool _sparseCloudTraversalPrepared;
    private string _sparseCloudTraversalDiagnostic = "not-initialized";
    private PreviewSparseCloudSamplingIdentity _sparseCloudPreparedIdentity;
    private PreviewSparseCloudSamplingIdentity _sparseCloudActiveIdentity;
    private string _sparseCloudActivationDiagnostic = "not-initialized";
    private int _sparseCloudControlFrame;
    private int _sparseCloudPreparedLightingChaseCooldown;
    private readonly HashSet<PreviewSparseCloudLogicalBrickKey>
        _sparseCloudPendingRetire = [];
    private readonly PreviewSparseCloudDiagnosticsCounters
        _sparseCloudCounters = new();
    private PreviewSparseCloudFaultInjectPoint _sparseCloudFaultInject =
        PreviewSparseCloudFaultInjectPoint.None;
    private PreviewCloudLightingCachePlan _cloudLightingCachePlan;
    private GlShaderProgram? _cloudLightSliceProgram;
    private GlShaderProgram? _cloudLightComputeProgram;
    private GlShaderProgram? _cloudGroundTransmittanceProgram;
    private GlCloudLightFroxelCache? _cloudLightCache;
    private GlCloudLightFragmentSliceGenerator? _cloudLightSliceGenerator;
    private GlCloudLightComputeGenerator? _cloudLightComputeGenerator;
    private GlCloudGroundTransmittanceTarget? _cloudGroundTransmittanceTarget;
    private GlCloudGroundTransmittancePublisher? _cloudGroundTransmittancePublisher;
    private PreviewCloudLightBasis? _cloudLightBasis;
    private bool _cloudLightCacheReadyLogged;
    private bool _cloudLightGenerationFailureLogged;
    private bool _cloudGroundTransmittanceReadyLogged;
    private bool _cloudGroundTransmittanceFailureLogged;
    private bool _cloudLightComputeSessionFaulted;
    private string _cloudLightComputeFailureReason = "none";
    private string _cloudLightingCacheResourceDiagnostic = "resources=not-initialized";
    private string _cloudGroundTransmittanceDiagnostic = "not-initialized";
    private int _cloudLightFrameSerial;
    private int _cloudGroundTransmittanceLastPublishFrame = -1;
    private int _cloudLightMaterialSettingsHash;
    private bool _cloudLightLifecycleInitialized;
    private Vector3 _cloudLightLifecycleCameraGround;
    private Vector3 _cloudLightObservedSunDirection;
    private Vector3 _cloudLightCurrentWindOffset;
    private float _cloudLightWindPeriod = 1f;
    private string _cloudLightLifecycleDiagnostic = "lifecycle=not-initialized";
    private Vector3 _cloudGroundBounceColorLinear =
        PreviewCloudGroundBounceEstimator.DefaultLinear;

    private void TryInitVolumetricClouds(GL gl, bool useOpenGlEs, int volumetricQuality)
    {
        DestroyVolumetricCloudResources();
        _cloudProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_clouds.frag", out var err);
        if (_cloudProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Volumetric cloud shader: " + (err ?? "link failed"));
            _cloudProgram?.Dispose();
            _cloudProgram = null;
            return;
        }

        _cloudUniformLocs = ResolveCloudUniformLocs(_cloudProgram);
        if (!useOpenGlEs)
        {
            _cloudHighProgram = CreatePreviewProgram(
                "genesis_godrays.vert",
                "genesis_clouds.frag",
                out var highErr,
                "clouds-high-cq3.7",
                new Dictionary<string, int>
                {
                    ["GENESIS_CLOUD_QUALITY"] = PreviewVolumetricQuality.High,
                });
            if (_cloudHighProgram is not { IsValid: true })
            {
                EmitDiagnostic(
                    "[3D preview] CQ3.7 High cloud specialization unavailable; " +
                    "using the generic cloud shader (" +
                    TrimShaderDiagnostic(highErr) + ").");
                _cloudHighProgram?.Dispose();
                _cloudHighProgram = null;
            }
            else
            {
                _cloudHighUniformLocs =
                    ResolveCloudUniformLocs(_cloudHighProgram);
            }
        }

        _cloudTemporalProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_clouds_temporal.frag",
            out var temporalErr, "clouds-temporal");
        if (_cloudTemporalProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Cloud temporal shader: " + (temporalErr ?? "link failed"));
            _cloudTemporalProgram?.Dispose();
            _cloudTemporalProgram = null;
        }
        else
        {
            _cloudTemporalUniformLocs = ResolveCloudTemporalUniformLocs(_cloudTemporalProgram);
        }

        // Depth-aware half-res upsample; non-fatal, falls back to the god-ray composite blit.
        _cloudUpsampleProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_clouds_upsample.frag",
            out var upErr, "clouds-upsample");
        if (_cloudUpsampleProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Cloud upsample shader: " + (upErr ?? "link failed"));
            _cloudUpsampleProgram?.Dispose();
            _cloudUpsampleProgram = null;
        }
        else
        {
            _cloudUpsampleUniformLocs = ResolveCloudUpsampleUniformLocs(_cloudUpsampleProgram);
        }

        // CQ1.8 repair is an optional desktop-only stage. Compile/allocation/runtime failure
        // keeps the CQ1.7 two-thirds trace and ordinary reconstruction path active.
        if (!useOpenGlEs)
        {
            _cloudRepairProgram = CreatePreviewProgram(
                "genesis_godrays.vert",
                "genesis_clouds_repair.frag",
                out var repairErr,
                "clouds-edge-repair");
            if (_cloudRepairProgram is not { IsValid: true })
            {
                _cloudEdgeRepairDiagnostic = "disabled (shader compile/link failed)";
                EmitDiagnostic("[3D preview] CQ1.8 cloud edge repair unavailable: " +
                    (repairErr ?? "link failed"));
                _cloudRepairProgram?.Dispose();
                _cloudRepairProgram = null;
            }
            else
            {
                _cloudRepairUniformLocs = ResolveCloudRepairUniformLocs(_cloudRepairProgram);
                _cloudEdgeRepairDiagnostic = "available";
            }
        }
        else
        {
            _cloudEdgeRepairDiagnostic = "disabled (GLES/ANGLE compatibility policy)";
        }

        // Clouds need the opaque color/depth capture even when god rays and preview TAA are off.
        // Failure is non-fatal: the shader keeps its no-depth fallback for limited GLES drivers.
        if (!TryInitSceneCaptureCore(gl, useOpenGlEs, out var sceneCaptureErr))
        {
            EmitDiagnostic("[3D preview] Cloud scene-depth capture unavailable: " +
                TrimShaderDiagnostic(sceneCaptureErr));
        }

        if (_godRayCompositeProgram is { IsValid: true })
        {
            _cloudCompositeUniformLocs = ResolveCloudCompositeUniformLocs(_godRayCompositeProgram);
        }

        _ = TryInitCloudDensityTextures(
            gl,
            allowV2: CanUseCq2V2DensityProfile(
                useOpenGlEs,
                Cq2V2ShaderProfileReady));
        TryInitCloudSpatiotemporalBlueNoise(gl, useOpenGlEs);
        CreateCloudRenderTargets(
            gl,
            GlCloudRenderFormatProfile.Select(_glCapabilities, volumetricQuality));
        UpdateCloudMomentDiagnostic(volumetricQuality);
        _cloudLightingCachePlan = PreviewCloudLightingCachePlan.Create(
            _glCapabilities,
            volumetricQuality);
        EmitDiagnostic(
            $"[3D preview] CQ1 cloud render format selected: {_cloudRenderFormatProfile.DiagnosticLabel}; " +
            $"moments={_cloudMomentsDiagnostic}; framebuffer completeness is verified on allocation.");

        Span<float> quad =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f
        ];
        _cloudQuadVao = gl.GenVertexArray();
        _cloudQuadVbo = gl.GenBuffer();
        gl.BindVertexArray(_cloudQuadVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _cloudQuadVbo);
        gl.BufferData<float>(GLEnum.ArrayBuffer, quad, GLEnum.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
        TryInitCloudLightingCacheCq33(gl, useOpenGlEs, volumetricQuality);
        TryInitSparseCloudTemplateAssetsCq41();
        TryInitSparseCloudResourcesCq42(gl, volumetricQuality);
        UpdateCloudDensityBackendSelectionCq40(volumetricQuality);
        EmitDiagnostic(
            "[3D preview] CQ3.3 cloud-light froxel cache contract: " +
            _cloudLightingCachePlan.FormatDiagnostic(
                _cloudLightingCacheResourceDiagnostic) + ".");
    }

    private void TryInitCloudLightingCacheCq33(
        GL gl,
        bool useOpenGlEs,
        int volumetricQuality)
    {
        _cloudLightSliceGenerator = null;
        _cloudLightComputeGenerator = null;
        _cloudLightSliceProgram?.Dispose();
        _cloudLightSliceProgram = null;
        _cloudLightComputeProgram?.Dispose();
        _cloudLightComputeProgram = null;
        _cloudGroundTransmittancePublisher = null;
        _cloudGroundTransmittanceProgram?.Dispose();
        _cloudGroundTransmittanceProgram = null;
        _cloudGroundTransmittanceTarget?.Dispose();
        _cloudGroundTransmittanceTarget = null;
        _cloudLightCache?.Dispose();
        _cloudLightCache = null;
        _sparseCloudVolumeResources?.Dispose();
        _sparseCloudVolumeResources = null;
        _sparseCloudBrickGenerator?.Dispose();
        _sparseCloudBrickGenerator = null;
        _sparseCloudBrickGenerationProgram?.Dispose();
        _sparseCloudBrickGenerationProgram = null;
        _sparseCloudBrickAllocator = null;
        _sparseCloudClipmapController = null;
        _sparseCloudPendingRetire.Clear();
        _cloudLightBasis = null;
        _cloudLightCacheReadyLogged = false;
        _cloudLightGenerationFailureLogged = false;
        _cloudGroundTransmittanceReadyLogged = false;
        _cloudGroundTransmittanceFailureLogged = false;
        _cloudGroundTransmittanceDiagnostic = "not-initialized";
        ResetCloudLightingCacheLifecycleCq36();
        _cloudLightingCachePlan = PreviewCloudLightingCachePlan.Create(
            _glCapabilities,
            volumetricQuality);

        if (!_cloudLightingCachePlan.Profile.IsEnabled)
        {
            _cloudLightingCacheResourceDiagnostic = "resources=not-allocated-profile-disabled";
            return;
        }

        if (useOpenGlEs ||
            _glCapabilities?.CanUseFragmentCloudLightingCache != true)
        {
            _cloudLightingCacheResourceDiagnostic =
                "resources=not-allocated-fragment-slices-unavailable";
            return;
        }

        _cloudLightSliceProgram = CreatePreviewProgram(
            "genesis_godrays.vert",
            "genesis_cloud_light_cache_slice.frag",
            out var shaderError,
            "cq3-cloud-light-fragment-slice");
        if (_cloudLightSliceProgram is not { IsValid: true })
        {
            EmitDiagnostic(
                "[3D preview] CQ3.2 cloud-light fragment fallback shader unavailable (" +
                TrimShaderDiagnostic(shaderError) + ").");
            _cloudLightSliceProgram?.Dispose();
            _cloudLightSliceProgram = null;
        }

        if (_cloudLightingCachePlan.PreferredGenerationPath ==
                PreviewCloudLightingCacheGenerationPath.ComputeImageStore &&
            !_cloudLightComputeSessionFaulted)
        {
            _cloudLightComputeProgram = CreatePreviewComputeProgram(
                "genesis_cloud_light_cache.comp",
                out var computeShaderError,
                "cq3.2-cloud-light-compute");
            if (_cloudLightComputeProgram is not { IsValid: true })
            {
                _cloudLightComputeSessionFaulted = true;
                _cloudLightComputeFailureReason =
                    "shader-compile-or-link-" + TrimShaderDiagnostic(computeShaderError);
                EmitDiagnostic(
                    "[3D preview] CQ3.2 cloud-light compute generator unavailable; " +
                    "using fragment slices for this session (" +
                    TrimShaderDiagnostic(computeShaderError) + ").");
                _cloudLightComputeProgram?.Dispose();
                _cloudLightComputeProgram = null;
            }
        }

        var selectedGenerator = PreviewCloudLightingCacheGeneratorFallback.Select(
            _cloudLightingCachePlan,
            _cloudLightComputeProgram is { IsValid: true },
            _cloudLightComputeSessionFaulted,
            _cloudLightSliceProgram is { IsValid: true });
        if (selectedGenerator == PreviewCloudLightingCacheGenerationPath.ShortMarch)
        {
            _cloudLightingCacheResourceDiagnostic =
                "resources=not-allocated-no-cache-generator;" +
                $"computeFailure={_cloudLightComputeFailureReason}";
            return;
        }

        if (!GlCloudLightFroxelCache.TryCreate(
                gl,
                _cloudLightingCachePlan.Profile,
                out var cache,
                out var allocationDiagnostic) ||
            cache is null)
        {
            _cloudLightingCacheResourceDiagnostic =
                "resources=not-allocated-" + allocationDiagnostic;
            EmitDiagnostic(
                "[3D preview] CQ3.2 cloud-light RG16F allocation failed; " +
                "continuing with the short light march (" +
                allocationDiagnostic + ").");
            return;
        }

        _cloudLightCache = cache;
        if (_cloudLightSliceProgram is { IsValid: true } fragmentProgram)
        {
            _cloudLightSliceGenerator = new GlCloudLightFragmentSliceGenerator(
                gl,
                fragmentProgram,
                _cloudQuadVao);
        }

        if (_cloudLightComputeProgram is { IsValid: true } computeProgram)
        {
            _cloudLightComputeGenerator = new GlCloudLightComputeGenerator(
                gl,
                computeProgram);
        }

        TryInitCloudGroundTransmittanceCq35(
            gl,
            volumetricQuality);
        _cloudLightingCacheResourceDiagnostic =
            cache.FormatDiagnostic() +
            $";selectedGenerator={PreviewCloudLightingCachePlan.FormatPath(selectedGenerator)};" +
            $"computeFailure={_cloudLightComputeFailureReason};" +
            _cloudGroundTransmittanceDiagnostic;
    }

    private void TryInitCloudGroundTransmittanceCq35(
        GL gl,
        int volumetricQuality)
    {
        var profile = PreviewCloudGroundTransmittanceProfiles.Resolve(
            volumetricQuality);
        if (!profile.IsEnabled)
        {
            _cloudGroundTransmittanceDiagnostic =
                "groundTransmittance=disabled-by-profile";
            return;
        }

        _cloudGroundTransmittanceProgram = CreatePreviewProgram(
            "genesis_godrays.vert",
            "genesis_cloud_ground_transmittance.frag",
            out var shaderError,
            "cq3.5-ground-transmittance");
        if (_cloudGroundTransmittanceProgram is not { IsValid: true } program)
        {
            _cloudGroundTransmittanceProgram?.Dispose();
            _cloudGroundTransmittanceProgram = null;
            _cloudGroundTransmittanceDiagnostic =
                "groundTransmittance=shader-unavailable-" +
                TrimShaderDiagnostic(shaderError);
            EmitDiagnostic(
                "[3D preview] CQ3.5 ground transmittance publisher unavailable; " +
                "terrain and fog retain full sunlight (" +
                TrimShaderDiagnostic(shaderError) + ").");
            return;
        }

        if (!GlCloudGroundTransmittanceTarget.TryCreate(
                gl,
                profile,
                out var target,
                out var allocationDiagnostic) ||
            target is null)
        {
            _cloudGroundTransmittanceProgram.Dispose();
            _cloudGroundTransmittanceProgram = null;
            _cloudGroundTransmittanceDiagnostic =
                "groundTransmittance=allocation-failed-" +
                allocationDiagnostic;
            EmitDiagnostic(
                "[3D preview] CQ3.5 ground transmittance R16F allocation failed; " +
                "terrain and fog retain full sunlight (" +
                allocationDiagnostic + ").");
            return;
        }

        _cloudGroundTransmittanceTarget = target;
        _cloudGroundTransmittancePublisher =
            new GlCloudGroundTransmittancePublisher(
                gl,
                program,
                _cloudQuadVao);
        _cloudGroundTransmittanceDiagnostic =
            target.FormatDiagnostic();
    }

    private void EnsureCloudLightingCacheProfileCq33(
        GL gl,
        int volumetricQuality)
    {
        var requested = PreviewCloudLightingCacheProfiles.Resolve(volumetricQuality);
        if (_cloudLightingCachePlan.Profile.Equals(requested))
        {
            return;
        }

        TryInitCloudLightingCacheCq33(gl, _useOpenGlEs, volumetricQuality);
        EmitDiagnostic(
            "[3D preview] CQ3.3 cloud-light cache profile changed: " +
            _cloudLightingCachePlan.FormatDiagnostic(
                _cloudLightingCacheResourceDiagnostic) + ".");
    }

    private void ResetCloudLightingCacheLifecycleCq36()
    {
        _cloudLightFrameSerial = 0;
        _cloudGroundTransmittanceLastPublishFrame = -1;
        _cloudLightMaterialSettingsHash = 0;
        _cloudLightLifecycleInitialized = false;
        _cloudLightLifecycleCameraGround = Vector3.Zero;
        _cloudLightObservedSunDirection = Vector3.Zero;
        _cloudLightCurrentWindOffset = Vector3.Zero;
        _cloudLightWindPeriod = 1f;
        _cloudLightLifecycleDiagnostic = "lifecycle=not-initialized";
    }

    private void TryGenerateCloudLightingCacheCq33(
        ref GlRenderFrame frame,
        float layerWorldY,
        Vector3 windOffset,
        Vector2 cirrusWindOffset,
        PreviewVolumetricQuality.Profile qualityProfile)
    {
        EnsureCloudLightingCacheProfileCq33(
            frame.Gl,
            frame.Settings.VolumetricQuality);
        if (_cloudLightCache is not { IsValid: true } cache)
        {
            return;
        }

        var lifecycleFrame = _cloudLightFrameSerial;
        _cloudLightFrameSerial = _cloudLightFrameSerial == int.MaxValue
            ? 0
            : _cloudLightFrameSerial + 1;
        _cloudLightCurrentWindOffset = windOffset;
        _cloudLightWindPeriod =
            Math.Max(frame.Settings.CloudVolumeSize, 8f) * 16f;

        try
        {
            var groundWorldY = PreviewStageConstants.GroundPlaneWorldY;
            var altitudeBounds = PreviewCloudLightAltitudeBounds.Create(
                groundWorldY,
                layerWorldY,
                frame.Settings.CloudVolumeHeight,
                frame.Settings.CloudVolumeSize,
                frame.Settings.CloudCirrusStrength,
                frame.Settings.CloudCumulusLayerCount,
                frame.Settings.CloudInterDeckGap,
                frame.Settings.CloudLayerHeightVariance,
                frame.Settings.CloudUpperThicknessScale,
                frame.Settings.CloudCirrusGap,
                frame.Settings.CloudCirrusThickness);
            var candidateBasis = PreviewCloudLightBasisBuilder.Build(
                frame.LightDir,
                _cloudLightBasis);
            var cameraGround = new Vector3(
                frame.Eye.X,
                groundWorldY,
                frame.Eye.Z);
            var materialSettingsHash =
                ComputeCloudLightMaterialSettingsHashCq36(frame.Settings);
            var materialSettingsChanged =
                _cloudLightLifecycleInitialized &&
                materialSettingsHash != _cloudLightMaterialSettingsHash;
            var largeCameraMovement =
                _cloudLightLifecycleInitialized &&
                Vector3.Distance(
                    cameraGround,
                    _cloudLightLifecycleCameraGround) >
                cache.Profile.Near.WorldSpan * 0.5f;
            var materialSunDirectionChanged =
                _cloudLightLifecycleInitialized &&
                PreviewCloudLightUpdateScheduler.IsMaterialSunDirectionChange(
                    _cloudLightObservedSunDirection,
                    frame.LightDir);
            var lightBasisChanged =
                _cloudLightBasis is { } committedBasis &&
                candidateBasis.ReferenceAxis != committedBasis.ReferenceAxis;
            var decision = PreviewCloudLightUpdateScheduler.Evaluate(
                new PreviewCloudLightUpdateRequest(
                    cache.Profile,
                    lifecycleFrame,
                    cache.Near.IsGenerated,
                    cache.Far.IsGenerated,
                    cache.Near.LastGenerationFrame,
                    cache.Far.LastGenerationFrame,
                    materialSettingsChanged,
                    largeCameraMovement,
                    materialSunDirectionChanged,
                    lightBasisChanged,
                    SpreadHeavyInvalidation:
                        PreviewVolumetricQuality.Clamp(
                            frame.Settings.VolumetricQuality) ==
                        PreviewVolumetricQuality.Cinematic));
            _cloudLightLifecycleInitialized = true;
            _cloudLightMaterialSettingsHash = materialSettingsHash;
            _cloudLightObservedSunDirection = frame.LightDir;

            if (decision.InvalidateBeforeGeneration)
            {
                // Only wipe cascades that this decision will regenerate. The Cinematic
                // large-camera amortisation may request Near alone; invalidating Far
                // anyway left hasFar=0 and produced a hard dark half-screen where the
                // near footprint ended and short-march/missing-far lighting took over.
                if (decision.UpdatesNear)
                {
                    cache.Near.InvalidateGeneration();
                }

                if (decision.UpdatesFar)
                {
                    cache.Far.InvalidateGeneration();
                }
            }

            if (decision.Cascades == PreviewCloudLightCascadeSelection.None)
            {
                UpdateCloudLightingLifecycleDiagnosticCq36(
                    cache,
                    lifecycleFrame,
                    decision,
                    "scheduled-reuse",
                    "none");
                _cloudLightingCacheResourceDiagnostic =
                    cache.FormatDiagnostic() +
                    ";generatedBy=scheduled-reuse;" +
                    $"computeFailure={_cloudLightComputeFailureReason};" +
                    _cloudLightLifecycleDiagnostic;
                _cloudLightingCachePlan = _cloudLightingCachePlan with
                {
                    ActiveRuntimePath =
                        PreviewCloudLightingCacheGenerationPath.CacheSampling,
                };
                return;
            }

            // The shader currently shares one basis across both cascades. Adopt a newly built
            // basis only on a paired refresh; single-cascade cadence updates retain the committed
            // basis so near/far lookup cannot diverge while the sun moves gradually.
            var basis = decision.Cascades == PreviewCloudLightCascadeSelection.Both ||
                        _cloudLightBasis is null
                ? candidateBasis
                : _cloudLightBasis.Value;
            if (decision.Cascades == PreviewCloudLightCascadeSelection.Both ||
                _cloudLightBasis is null)
            {
                _cloudLightBasis = basis;
            }

            var nearInterval = PreviewCloudLightDepthInterval.Create(
                basis,
                cache.Profile.Near,
                cameraGround,
                altitudeBounds,
                groundWorldY);
            var farInterval = PreviewCloudLightDepthInterval.Create(
                basis,
                cache.Profile.Far,
                cameraGround,
                altitudeBounds,
                groundWorldY);
            var nearTransform = PreviewCloudLightCascadeTransform.Create(
                basis,
                cache.Profile.Near,
                cameraGround,
                nearInterval.Minimum,
                nearInterval.Maximum);
            var farTransform = PreviewCloudLightCascadeTransform.Create(
                basis,
                cache.Profile.Far,
                cameraGround,
                farInterval.Minimum,
                farInterval.Maximum);
            var upperWindOffset = ComputeUpperCloudWindOffset(
                frame.Settings.CloudFreezeWind ? 0.0 : frame.RenderTime,
                frame.Settings);
            var inputs = new GlCloudLightSliceGenerationInputs(
                nearTransform,
                farTransform,
                altitudeBounds,
                new Vector3(
                    0f,
                    groundWorldY -
                        PreviewStageConstants.CloudLegacyAltitudeReferenceRadius,
                    0f),
                PreviewStageConstants.CloudLegacyAltitudeReferenceRadius,
                frame.Settings.CloudDensity,
                frame.Settings.CloudCoverageScale,
                frame.Settings.CloudVolumeSize,
                windOffset,
                frame.Settings.CloudCirrusStrength,
                cirrusWindOffset,
                ComputeCirrusWindDirection(frame.Settings),
                qualityProfile.CloudQuality,
                _cloudDensityAssetVersion,
                _cloudNoiseTex?.Id ?? 0,
                _cloudDetailTex?.Id ?? 0,
                _cloudCoverageTex?.Id ?? 0,
                SparseCloud: CreatePreparedSparseCloudBindingsCq46(),
                CumulusLayerCount: frame.Settings.CloudCumulusLayerCount,
                InterDeckGap: frame.Settings.CloudInterDeckGap,
                HeightVariance: frame.Settings.CloudLayerHeightVariance,
                UpperThicknessScale: frame.Settings.CloudUpperThicknessScale,
                UpperCoverageScale: frame.Settings.CloudUpperCoverageScale,
                UpperDensityScale: frame.Settings.CloudUpperDensityScale,
                UpperWindOffset: upperWindOffset,
                CirrusGap: frame.Settings.CloudCirrusGap,
                CirrusThickness: frame.Settings.CloudCirrusThickness,
                StyleBias: frame.Settings.CloudStyleBias);

            var nearScroll = cache.Near.IsGenerated
                ? PreviewCloudLightScrollPlan.Create(
                    cache.Near.Transform,
                    nearTransform)
                : default;
            var farScroll = cache.Far.IsGenerated
                ? PreviewCloudLightScrollPlan.Create(
                    cache.Far.Transform,
                    farTransform)
                : default;
            var generationDiagnostics = new List<string>(2);
            var generatedAny = false;
            var allRequestedGenerated = true;
            var lastGeneratorPath =
                PreviewCloudLightingCacheGenerationPath.ShortMarch;

            if (decision.UpdatesNear)
            {
                var nearGenerated = TryReuseUnchangedCloudLightCascadeCq37(
                    cache.Near,
                    nearTransform,
                    windOffset,
                    lifecycleFrame,
                    inputs.DensityIdentity,
                    out var nearDiagnostic);
                var nearPath =
                    PreviewCloudLightingCacheGenerationPath.CacheSampling;
                if (!nearGenerated)
                {
                    nearGenerated = TryGenerateCloudLightCascadeCq36(
                        ref frame,
                        cache,
                        inputs,
                        PreviewCloudLightCascadeSelection.Near,
                        lifecycleFrame,
                        GlGpuTimerScope.CloudLightNear,
                        out nearPath,
                        out nearDiagnostic);
                }

                generatedAny |= nearGenerated;
                allRequestedGenerated &= nearGenerated;
                lastGeneratorPath = nearPath;
                generationDiagnostics.Add(
                    $"near={nearDiagnostic};{nearScroll.FormatDiagnostic()}");
            }

            if (decision.UpdatesFar)
            {
                var farGenerated = TryReuseUnchangedCloudLightCascadeCq37(
                    cache.Far,
                    farTransform,
                    windOffset,
                    lifecycleFrame,
                    inputs.DensityIdentity,
                    out var farDiagnostic);
                var farPath =
                    PreviewCloudLightingCacheGenerationPath.CacheSampling;
                if (!farGenerated)
                {
                    farGenerated = TryGenerateCloudLightCascadeCq36(
                        ref frame,
                        cache,
                        inputs,
                        PreviewCloudLightCascadeSelection.Far,
                        lifecycleFrame,
                        GlGpuTimerScope.CloudLightFar,
                        out farPath,
                        out farDiagnostic);
                }

                generatedAny |= farGenerated;
                allRequestedGenerated &= farGenerated;
                lastGeneratorPath = farPath;
                generationDiagnostics.Add(
                    $"far={farDiagnostic};{farScroll.FormatDiagnostic()}");
            }

            var generationDiagnostic = string.Join("|", generationDiagnostics);
            if (!allRequestedGenerated)
            {
                _cloudLightingCacheResourceDiagnostic =
                    cache.FormatDiagnostic() + ";generationFailure=" +
                    generationDiagnostic;
                var hasUsableCascade =
                    cache.Near.IsGenerated || cache.Far.IsGenerated;
                _cloudLightingCachePlan = _cloudLightingCachePlan with
                {
                    ActiveRuntimePath = hasUsableCascade
                        ? PreviewCloudLightingCacheGenerationPath.CacheSampling
                        : PreviewCloudLightingCacheGenerationPath.ShortMarch,
                };
                if (!_cloudLightGenerationFailureLogged)
                {
                    _cloudLightGenerationFailureLogged = true;
                    EmitDiagnostic(
                        "[3D preview] CQ3.3 cloud-light cache generation failed; " +
                        (hasUsableCascade
                            ? "retaining valid cascade coverage with per-sample short-march fallback ("
                            : "production lighting remains on the short march (") +
                        generationDiagnostic + ").");
                }
            }

            if (generatedAny &&
                decision.UpdatesFar &&
                cache.Far.IsGenerated)
            {
                TryPublishCloudGroundTransmittanceCq35(
                    ref frame,
                    cache,
                    lifecycleFrame);
            }

            if (decision.UpdatesNear && cache.Near.IsGenerated)
            {
                _cloudLightLifecycleCameraGround = cameraGround;
            }

            var samplingNear = cache.Near.IsGenerated
                ? cache.Near.GetSamplingTransform(
                    windOffset,
                    _cloudLightWindPeriod)
                : nearTransform;
            var samplingFar = cache.Far.IsGenerated
                ? cache.Far.GetSamplingTransform(
                    windOffset,
                    _cloudLightWindPeriod)
                : farTransform;
            var centerWorld = samplingNear.UnitToWorld(
                new Vector3(0.5f, 0.5f, 0.5f));
            var sampleWeights = PreviewCloudLightCascadeBlend.Select(
                samplingNear,
                samplingFar,
                centerWorld,
                cache.Profile.NearOverlapFraction);
            UpdateCloudLightingLifecycleDiagnosticCq36(
                cache,
                lifecycleFrame,
                decision,
                allRequestedGenerated ? "generated" : "partial-fallback",
                generationDiagnostic);
            _cloudLightingCacheResourceDiagnostic =
                cache.FormatDiagnostic() +
                FormattableString.Invariant(
                    $";generatedBy={PreviewCloudLightingCachePlan.FormatPath(lastGeneratorPath)};generation={generationDiagnostic};computeFailure={_cloudLightComputeFailureReason};nearDepth={samplingNear.LightDepthMin:F2}..{samplingNear.LightDepthMin + samplingNear.LightDepthSpan:F2};farDepth={samplingFar.LightDepthMin:F2}..{samplingFar.LightDepthMin + samplingFar.LightDepthSpan:F2};centerWeights={sampleWeights.Near:F2}/{sampleWeights.Far:F2}/{sampleWeights.ShortMarch:F2};{_cloudLightLifecycleDiagnostic}");
            _cloudLightingCachePlan = _cloudLightingCachePlan with
            {
                ActiveRuntimePath = cache.Near.IsGenerated ||
                                    cache.Far.IsGenerated
                    ? PreviewCloudLightingCacheGenerationPath.CacheSampling
                    : PreviewCloudLightingCacheGenerationPath.ShortMarch,
            };
            if (allRequestedGenerated)
            {
                _cloudLightGenerationFailureLogged = false;
            }

            if (!_cloudLightCacheReadyLogged)
            {
                _cloudLightCacheReadyLogged = true;
                EmitDiagnostic(
                    "[3D preview] CQ3.4 cloud-light shading ready; CQ3.6 schedule active: " +
                    _cloudLightingCacheResourceDiagnostic +
                    "; activeRuntime=cache-sampling; " +
                    "lighting=cq3.4-two-octave+sky-visibility+ground-bounce; " +
                    $"localConeTaps={cache.Profile.LocalConeTapCount}; " +
                    _cloudGroundTransmittanceDiagnostic + "; " +
                    "wind-reprojected reuse is capped at four frames; " +
                    "outside/invalid coverage falls back to the short march.");
            }
        }
        catch (Exception ex)
        {
            cache.Near.InvalidateGeneration();
            cache.Far.InvalidateGeneration();
            _cloudLightingCacheResourceDiagnostic =
                cache.FormatDiagnostic() +
                $";generationFailure={ex.GetType().Name}:{ex.Message}";
            _cloudLightingCachePlan = _cloudLightingCachePlan with
            {
                ActiveRuntimePath =
                    PreviewCloudLightingCacheGenerationPath.ShortMarch,
            };
            if (!_cloudLightGenerationFailureLogged)
            {
                _cloudLightGenerationFailureLogged = true;
                EmitDiagnostic(
                    "[3D preview] CQ3.3 cloud-light cache setup failed; " +
                    "production lighting remains on the short march (" +
                    ex.GetType().Name + ": " + ex.Message + ").");
            }
        }
    }

    private static bool TryReuseUnchangedCloudLightCascadeCq37(
        GlCloudLightCascadeTarget target,
        in PreviewCloudLightCascadeTransform desiredTransform,
        Vector3 currentWindOffset,
        int generationFrame,
        int densityIdentity,
        out string diagnostic)
    {
        if (!target.IsGenerated)
        {
            diagnostic = "not-generated";
            return false;
        }

        if (target.DensityIdentity != densityIdentity)
        {
            diagnostic = "density-identity-changed";
            return false;
        }

        var previous = target.Transform;
        var windDelta = currentWindOffset - target.GenerationWindOffset;
        var basisStable =
            Vector3.Dot(previous.Basis.Right, desiredTransform.Basis.Right) > 0.999999f &&
            Vector3.Dot(previous.Basis.Up, desiredTransform.Basis.Up) > 0.999999f &&
            Vector3.Dot(previous.Basis.Forward, desiredTransform.Basis.Forward) > 0.999999f;
        var transformStable =
            previous.Profile.Equals(desiredTransform.Profile) &&
            MathF.Abs(previous.PlaneCenterX - desiredTransform.PlaneCenterX) <= 1e-4f &&
            MathF.Abs(previous.PlaneCenterY - desiredTransform.PlaneCenterY) <= 1e-4f &&
            MathF.Abs(previous.LightDepthMin - desiredTransform.LightDepthMin) <= 1e-4f &&
            MathF.Abs(previous.LightDepthSpan - desiredTransform.LightDepthSpan) <= 1e-4f;
        if (!basisStable ||
            !transformStable ||
            windDelta.LengthSquared() > 1e-8f)
        {
            diagnostic = "state-changed";
            return false;
        }

        // This is a logical scheduled refresh, not an unbounded stale reuse: the cascade's
        // age and generation ID advance transactionally, while identical density/light-space
        // input avoids rewriting an unchanged texture. Ground publication observes the new ID.
        target.CommitGeneration(
            desiredTransform,
            generationFrame,
            currentWindOffset,
            densityIdentity);
        diagnostic = "exact-static-reuse-cq3.7";
        return true;
    }

    private bool TryGenerateCloudLightCascadeCq36(
        ref GlRenderFrame frame,
        GlCloudLightFroxelCache cache,
        in GlCloudLightSliceGenerationInputs inputs,
        PreviewCloudLightCascadeSelection cascade,
        int generationFrame,
        GlGpuTimerScope timerScope,
        out PreviewCloudLightingCacheGenerationPath generatorPath,
        out string diagnostic)
    {
        using var timer = BeginPassTimerScope(timerScope);
        generatorPath = PreviewCloudLightingCacheGeneratorFallback.Select(
            _cloudLightingCachePlan,
            _cloudLightComputeGenerator is not null,
            _cloudLightComputeSessionFaulted,
            _cloudLightSliceGenerator is not null);
        var generated = false;
        diagnostic = "no-generator";
        if (generatorPath ==
                PreviewCloudLightingCacheGenerationPath.ComputeImageStore &&
            _cloudLightComputeGenerator is { } computeGenerator)
        {
            generated = computeGenerator.TryGenerate(
                cache,
                inputs,
                cascade,
                generationFrame,
                out diagnostic);
            if (!generated)
            {
                _cloudLightComputeSessionFaulted = true;
                _cloudLightComputeFailureReason = diagnostic;
                EmitDiagnostic(
                    "[3D preview] CQ3.6 cloud-light compute generation failed; " +
                    "disabling compute for this session and retrying fragment slices (" +
                    diagnostic + ").");
                generatorPath = _cloudLightSliceGenerator is not null
                    ? PreviewCloudLightingCacheGenerationPath.FragmentSlices
                    : PreviewCloudLightingCacheGenerationPath.ShortMarch;
            }
        }

        if (!generated &&
            generatorPath ==
                PreviewCloudLightingCacheGenerationPath.FragmentSlices &&
            _cloudLightSliceGenerator is { } fragmentGenerator)
        {
            generated = fragmentGenerator.TryGenerate(
                cache,
                inputs,
                cascade,
                generationFrame,
                frame.Vw,
                frame.Vh,
                out diagnostic);
        }

        return generated;
    }

    private int ComputeCloudLightMaterialSettingsHashCq36(
        in PreviewRenderSettingsSnapshot settings)
    {
        var hash = new HashCode();
        hash.Add(settings.VolumetricQuality);
        hash.Add(settings.CloudDensity);
        hash.Add(settings.CloudCoverageScale);
        hash.Add(settings.CloudLayerHeight);
        hash.Add(settings.CloudVolumeHeight);
        hash.Add(settings.CloudVolumeSize);
        hash.Add(settings.CloudCirrusStrength);
        hash.Add(settings.CloudCumulusLayerCount);
        hash.Add(settings.CloudInterDeckGap);
        hash.Add(settings.CloudLayerHeightVariance);
        hash.Add(settings.CloudUpperThicknessScale);
        hash.Add(settings.CloudUpperCoverageScale);
        hash.Add(settings.CloudUpperDensityScale);
        hash.Add(settings.CloudUpperWindSpeedScale);
        hash.Add(settings.CloudCirrusGap);
        hash.Add(settings.CloudCirrusThickness);
        hash.Add(settings.CloudStyleBias);
        hash.Add(settings.CloudWindSpeed);
        hash.Add(settings.CloudWindHeadingDegrees);
        hash.Add(settings.CloudFreezeWind);
        hash.Add(settings.CloudDebugView);
        hash.Add(_cloudDensityAssetVersion);
        hash.Add(_cloudDensityAssetProfileCode);
        // Stick to the actively sampleable sparse identity while a newer prepared
        // candidate warms. Hashing every residency revision forces a full Cinematic
        // light-cache rebuild on each brick batch during fast camera motion.
        hash.Add(ResolveSparseCloudLightingDensityIdentity());
        return hash.ToHashCode();
    }

    private int ResolveSparseCloudLightingDensityIdentity()
    {
        if (!IsSparseCloudResidencyReadyForActivation() ||
            !_sparseCloudPreparedIdentity.IsValid)
        {
            return 0;
        }

        return ResolveSparseCloudLightingTargetIdentity().Signature;
    }

    private PreviewSparseCloudSamplingIdentity
        ResolveSparseCloudLightingTargetIdentity()
    {
        if (_sparseCloudResourcesReady &&
            _sparseCloudActiveIdentity.IsValid &&
            _sparseCloudPreparedIdentity != _sparseCloudActiveIdentity &&
            PreviewSparseCloudActivationPolicy.CanSoftHold(
                _sparseCloudActiveIdentity,
                _sparseCloudPreparedIdentity))
        {
            var hasPendingGeneration =
                _sparseCloudBrickGenerator?.HasPendingGeneration ?? false;
            if (hasPendingGeneration ||
                _sparseCloudPreparedLightingChaseCooldown > 0)
            {
                return _sparseCloudActiveIdentity;
            }

            return _sparseCloudPreparedIdentity;
        }

        return _sparseCloudPreparedIdentity;
    }

    private void UpdateCloudLightingLifecycleDiagnosticCq36(
        GlCloudLightFroxelCache cache,
        int frameIndex,
        in PreviewCloudLightUpdateDecision decision,
        string result,
        string generation)
    {
        var nearAge = cache.Near.AgeAt(frameIndex);
        var farAge = cache.Far.AgeAt(frameIndex);
        _cloudLightLifecycleDiagnostic =
            $"lifecycle=cq3.6;frame={frameIndex};requested={decision.Cascades};" +
            $"result={result};invalidation={decision.InvalidationReason};" +
            $"age={FormatCloudLightAge(nearAge)}/{FormatCloudLightAge(farAge)};" +
            $"cadence={cache.Profile.Near.UpdateIntervalFrames}/" +
            $"{cache.Profile.Far.UpdateIntervalFrames};" +
            $"generation={generation}";
    }

    private static string FormatCloudLightAge(int age) =>
        age == int.MaxValue
            ? "invalid"
            : age.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void TryPublishCloudGroundTransmittanceCq35(
        ref GlRenderFrame frame,
        GlCloudLightFroxelCache cache,
        int lifecycleFrame)
    {
        if (_cloudGroundTransmittancePublisher is not { } publisher ||
            _cloudGroundTransmittanceTarget is not { IsAllocated: true } target)
        {
            return;
        }

        if (!publisher.TryPublish(
                cache,
                target,
                PreviewStageConstants.GroundPlaneWorldY,
                _cloudLightCurrentWindOffset,
                _cloudLightWindPeriod,
                out var diagnostic))
        {
            _cloudGroundTransmittanceDiagnostic =
                target.FormatDiagnostic() +
                ";publishFailure=" + diagnostic;
            if (!_cloudGroundTransmittanceFailureLogged)
            {
                _cloudGroundTransmittanceFailureLogged = true;
                EmitDiagnostic(
                    "[3D preview] CQ3.5 ground transmittance publication failed; " +
                    "terrain and fog retain full sunlight (" +
                    diagnostic + ").");
            }

            return;
        }

        _cloudGroundTransmittanceFailureLogged = false;
        _cloudGroundTransmittanceLastPublishFrame = lifecycleFrame;
        _cloudGroundTransmittanceDiagnostic =
            target.FormatDiagnostic() +
            ";" + diagnostic +
            $";publishFrame={_cloudGroundTransmittanceLastPublishFrame};cadence=far-refresh";
        if (_cloudGroundTransmittanceReadyLogged)
        {
            return;
        }

        _cloudGroundTransmittanceReadyLogged = true;
        EmitDiagnostic(
            "[3D preview] CQ3.5 ground transmittance ready: " +
            _cloudGroundTransmittanceDiagnostic +
            "; consumers=terrain-direct+camera-froxel-direct(god-rays); " +
            "ambient/IBL and view-ray cloud depth remain unchanged; " +
            "missing/out-of-range=full-sun.");
    }

    private bool TryInitCloudDensityTextures(GL gl, bool allowV2)
    {
        if (PreviewCloudBakedAssetLoader.TryLoadDensityAssetSet(
                allowV2,
                out var preferred,
                out var preferredLoadReason))
        {
            if (TryCommitCloudDensityTextures(
                    gl,
                    preferred,
                    out var preferredUploadReason))
            {
                UpdateCloudDensityAssetDiagnostic(
                    preferred,
                    $"{preferredLoadReason};{preferredUploadReason}",
                    preferred.AssetVersion >= PreviewCloudDensityAssetContract.AssetVersion
                        ? 1
                        : (allowV2 ? 3 : 2));
                return true;
            }

            if (preferred.AssetVersion == PreviewCloudDensityAssetContract.AssetVersion)
            {
                if (PreviewCloudBakedAssetLoader.TryLoadDensityAssetSet(
                        allowV2: false,
                        out var bundledV1,
                        out var bundledV1LoadReason))
                {
                    if (TryCommitCloudDensityTextures(
                            gl,
                            bundledV1,
                            out var bundledV1UploadReason))
                    {
                        UpdateCloudDensityAssetDiagnostic(
                            bundledV1,
                            $"v2-{preferredUploadReason} -> " +
                            $"{bundledV1LoadReason};{bundledV1UploadReason}",
                            profileCode: 3);
                        return true;
                    }

                    preferredLoadReason +=
                        $";v2-{preferredUploadReason};" +
                        $"v1-{bundledV1LoadReason};{bundledV1UploadReason}";
                }
                else
                {
                    preferredLoadReason +=
                        $";v2-{preferredUploadReason};v1-{bundledV1LoadReason}";
                }
            }
            else
            {
                preferredLoadReason += $";{preferredUploadReason}";
            }
        }

        StartCloudDensityGeneration(preferredLoadReason);
        _cloudDensityAssetDiagnostic =
            $"generated-v1-pending ({preferredLoadReason})";
        _cloudDensityAssetVersion = 0;
        _cloudDensityAssetProfileCode = 5;
        InvalidateCloudTemporalHistory();
        EmitDiagnostic(
            "[3D preview] CQ2 bundled density assets unavailable; " +
            "runtime v1 density generation is running on a CPU worker. " +
            "Detailed cloud shaders retain their procedural hash-density fallback until commit.");
        return false;
    }

    private void StartCloudDensityGeneration(string loadReason)
    {
        CancelCloudDensityGeneration();

        var cancellation = new CancellationTokenSource();
        var task = Task.Run(
            () => CreateGeneratedV1DensityAssetSet(cancellation.Token),
            cancellation.Token);
        _cloudDensityGenerationCancellation = cancellation;
        _cloudDensityGenerationTask = task;
        _cloudDensityGenerationLoadReason = loadReason;
        _cloudDensityGenerationFailureLogged = false;

        _ = task.ContinueWith(
            _ => RequestPreviewFrame(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PumpCloudDensityGeneration(GL gl)
    {
        var task = _cloudDensityGenerationTask;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        var cancellation = _cloudDensityGenerationCancellation;
        var loadReason = _cloudDensityGenerationLoadReason;
        _cloudDensityGenerationTask = null;
        _cloudDensityGenerationCancellation = null;
        cancellation?.Dispose();

        if (task.IsCanceled)
        {
            return;
        }

        if (task.IsFaulted)
        {
            _ = task.Exception;
            _cloudDensityAssetDiagnostic =
                $"procedural-shader-fallback ({loadReason};generated-v1-faulted)";
            _cloudDensityAssetVersion = 0;
            _cloudDensityAssetProfileCode = 5;
            if (!_cloudDensityGenerationFailureLogged)
            {
                _cloudDensityGenerationFailureLogged = true;
                EmitDiagnostic(
                    "[3D preview] CQ2 runtime density generation failed; " +
                    $"{_cloudDensityAssetDiagnostic}.");
            }

            return;
        }

        var generatedV1 = task.Result;
        if (TryCommitCloudDensityTextures(
                gl,
                generatedV1,
                out var generatedUploadReason))
        {
            UpdateCloudDensityAssetDiagnostic(
                generatedV1,
                $"{loadReason} -> generated-v1;{generatedUploadReason}",
                profileCode: 4);
            _cloudDensityGenerationFailureLogged = false;
            return;
        }

        _cloudDensityAssetDiagnostic =
            $"procedural-shader-fallback ({loadReason};{generatedUploadReason})";
        _cloudDensityAssetVersion = 0;
        _cloudDensityAssetProfileCode = 5;
        if (!_cloudDensityGenerationFailureLogged)
        {
            _cloudDensityGenerationFailureLogged = true;
            EmitDiagnostic(
                "[3D preview] CQ2 generated density texture upload failed; " +
                $"{_cloudDensityAssetDiagnostic}.");
        }
    }

    private void CancelCloudDensityGeneration()
    {
        var task = _cloudDensityGenerationTask;
        var cancellation = _cloudDensityGenerationCancellation;
        _cloudDensityGenerationTask = null;
        _cloudDensityGenerationCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (task is { IsCompleted: false })
        {
            _ = task.ContinueWith(
                _ => cancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            cancellation.Dispose();
        }
    }

    private bool TryCommitCloudDensityTextures(
        GL gl,
        in PreviewCloudDensityAssetSet assets,
        out string reason)
    {
        GlTexture3D? shape = null;
        GlTexture3D? detail = null;
        GlTexture2D? weather = null;
        try
        {
            FlushPendingGlErrors(gl);
            shape = new GlTexture3D(gl);
            shape.UploadRgba(assets.ShapeSize, assets.ShapeRgba);
            ThrowIfCloudDensityUploadFailed(gl, "shape");

            detail = new GlTexture3D(gl);
            detail.UploadRgba(assets.DetailSize, assets.DetailRgba);
            ThrowIfCloudDensityUploadFailed(gl, "detail");

            weather = new GlTexture2D(gl, nearestFilter: false, mipmapped: true);
            weather.UploadRgba(
                assets.WeatherWidth,
                assets.WeatherHeight,
                assets.WeatherRgba,
                nearestFilter: false);
            ThrowIfCloudDensityUploadFailed(gl, "weather");

            var priorShape = _cloudNoiseTex;
            var priorDetail = _cloudDetailTex;
            var priorWeather = _cloudCoverageTex;
            var priorVersion = _cloudDensityAssetVersion;

            _cloudNoiseTex = shape;
            _cloudDetailTex = detail;
            _cloudCoverageTex = weather;
            _cloudDensityAssetVersion = assets.AssetVersion;
            shape = null;
            detail = null;
            weather = null;

            priorShape?.Dispose();
            priorDetail?.Dispose();
            priorWeather?.Dispose();
            if (priorVersion != assets.AssetVersion)
            {
                InvalidateCloudTemporalHistory();
            }

            reason = "upload-valid";
            return true;
        }
        catch (Exception exception)
        {
            reason = $"upload-{exception.GetType().Name}";
            return false;
        }
        finally
        {
            shape?.Dispose();
            detail?.Dispose();
            weather?.Dispose();
        }
    }

    private void UpdateCloudDensityAssetDiagnostic(
        in PreviewCloudDensityAssetSet assets,
        string reason,
        int profileCode)
    {
        _cloudDensityAssetDiagnostic =
            $"{assets.ProfileName}/{reason}";
        _cloudDensityAssetProfileCode = profileCode;
        EmitDiagnostic(
            $"[3D preview] CQ2 density asset profile selected: {_cloudDensityAssetDiagnostic}; " +
            $"shape={assets.ShapeSize}^3, detail={assets.DetailSize}^3, " +
            $"weather={assets.WeatherWidth}x{assets.WeatherHeight}, " +
            $"baseBytes={assets.BaseLevelByteLength:N0}.");
    }

    private static PreviewCloudDensityAssetSet CreateGeneratedV1DensityAssetSet(
        CancellationToken cancellationToken) =>
        new(
            AssetVersion: 1,
            ProfileName: "legacy-v1-runtime",
            ShapeSize: PreviewCloudNoiseTextureGenerator.Size,
            ShapeRgba: PreviewCloudNoiseTextureGenerator.GenerateRgba8(cancellationToken),
            DetailSize: PreviewCloudNoiseTextureGenerator.DetailSize,
            DetailRgba: PreviewCloudNoiseTextureGenerator.GenerateDetailRgba8(cancellationToken),
            WeatherWidth: PreviewCloudCoverageMapGenerator.Size,
            WeatherHeight: PreviewCloudCoverageMapGenerator.Size,
            WeatherRgba: PreviewCloudCoverageMapGenerator.GenerateRgba8());

    private static void ThrowIfCloudDensityUploadFailed(GL gl, string asset)
    {
        var error = gl.GetError();
        if (error != GLEnum.NoError)
        {
            throw new InvalidOperationException(
                $"Cloud density {asset} upload produced {error}.");
        }
    }

    private void TryInitCloudSpatiotemporalBlueNoise(GL gl, bool useOpenGlEs)
    {
        _cloudStbnTex?.Dispose();
        _cloudStbnTex = null;

        if (useOpenGlEs)
        {
            _cloudStbnDiagnostic = "fallback-8-frame (GLES policy)";
            return;
        }

        if (!PreviewCloudBakedAssetLoader.TryLoadSpatiotemporalBlueNoise(
                out var stbnR8,
                out var loadReason))
        {
            _cloudStbnDiagnostic = $"fallback-8-frame ({loadReason})";
            EmitDiagnostic(
                "[3D preview] CQ1.5 cloud STBN unavailable; " +
                $"{_cloudStbnDiagnostic}.");
            return;
        }

        GlTexture3D? candidate = null;
        try
        {
            FlushPendingGlErrors(gl);
            candidate = new GlTexture3D(gl);
            candidate.UploadR8(
                PreviewCloudSpatiotemporalBlueNoiseGenerator.Width,
                PreviewCloudSpatiotemporalBlueNoiseGenerator.Height,
                PreviewCloudSpatiotemporalBlueNoiseGenerator.FrameCount,
                stbnR8);
            var uploadError = gl.GetError();
            if (uploadError != GLEnum.NoError)
            {
                throw new InvalidOperationException($"R8 3D texture upload produced {uploadError}.");
            }

            _cloudStbnTex = candidate;
            candidate = null;
            _cloudStbnDiagnostic =
                $"asset-v{PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetVersion}";
        }
        catch (Exception exception)
        {
            _cloudStbnDiagnostic =
                $"fallback-8-frame (upload-{exception.GetType().Name})";
            EmitDiagnostic(
                "[3D preview] CQ1.5 cloud STBN upload failed; " +
                $"{_cloudStbnDiagnostic}.");
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private void TryWarmCloudOffscreenTargets(
        int fullWidth,
        int fullHeight,
        int volumetricQuality)
    {
        var traceSize = PreviewCloudTraceSizing.Resolve(
            fullWidth,
            fullHeight,
            volumetricQuality);
        _cloudHistoryViewportW = Math.Max(1, fullWidth);
        _cloudHistoryViewportH = Math.Max(1, fullHeight);
        _cloudHistoryW = traceSize.Width;
        _cloudHistoryH = traceSize.Height;
        InvalidateCloudTemporalHistory();
        _ = EnsureCloudRenderTargetSetSize(
            traceSize.Width,
            traceSize.Height,
            requireTemporalTargets: true);
    }

    private void CreateCloudRenderTargets(GL gl, GlCloudRenderFormatProfile profile)
    {
        _cloudCompositeTarget = null;
        _cloudRenderTarget?.Dispose();
        _cloudResolveTarget?.Dispose();
        _cloudHistoryTarget?.Dispose();
        if (!profile.UsesDirectMetadata)
        {
            _cloudRepairTarget?.Dispose();
            _cloudRepairTarget = null;
        }

        _cloudRenderFormatProfile = profile;
        _cloudRenderTarget = new GlCloudTemporalRenderTarget(gl, profile);
        _cloudResolveTarget = new GlCloudTemporalRenderTarget(gl, profile);
        _cloudHistoryTarget = new GlCloudTemporalRenderTarget(gl, profile);
    }

    private void EnsureCloudRenderFormatForQuality(GL gl, int volumetricQuality)
    {
        var requested = GlCloudRenderFormatProfile.Select(_glCapabilities, volumetricQuality);
        if (_cloudTemporalMomentsFaulted && requested.UsesTemporalMoments)
        {
            requested = GlCloudRenderFormatProfile.DesktopFloatingPoint;
        }

        if (_cloudFloatingPointTargetsFaulted && requested.UsesDirectMetadata)
        {
            requested = GlCloudRenderFormatProfile.Compatibility;
        }

        if (requested == _cloudRenderFormatProfile)
        {
            return;
        }

        CreateCloudRenderTargets(gl, requested);
        UpdateCloudMomentDiagnostic(volumetricQuality);
        InvalidateCloudTemporalHistory();
        EmitDiagnostic(
            $"[3D preview] CQ1 cloud render format changed with volumetric preset: " +
            $"{_cloudRenderFormatProfile.DiagnosticLabel}.");
    }

    private bool EnsureCloudRenderTargetSetSize(int width, int height, bool requireTemporalTargets)
    {
        bool EnsureSelectedProfile() =>
            _cloudRenderTarget is not null &&
            _cloudRenderTarget.EnsureSize(width, height) &&
            (!requireTemporalTargets ||
                (_cloudResolveTarget is not null &&
                 _cloudResolveTarget.EnsureSize(width, height) &&
                 _cloudHistoryTarget is not null &&
                 _cloudHistoryTarget.EnsureSize(width, height)));

        if (EnsureSelectedProfile())
        {
            return true;
        }

        if (_cloudRenderFormatProfile.UsesTemporalMoments && _gl is not null)
        {
            if (!_loggedCloudMomentFallback)
            {
                _loggedCloudMomentFallback = true;
                EmitDiagnostic(
                    "[3D preview] CQ1.6 RG16F cloud moment attachment was incomplete; " +
                    "continuing with FP16 radiance/direct metadata and neighborhood-only clipping.");
            }

            _cloudTemporalMomentsFaulted = true;
            CreateCloudRenderTargets(_gl, GlCloudRenderFormatProfile.DesktopFloatingPoint);
            _cloudMomentsDiagnostic = "disabled (RG16F MRT incomplete)";
            InvalidateCloudTemporalHistory();
            if (EnsureSelectedProfile())
            {
                return true;
            }
        }

        if (!_cloudRenderFormatProfile.UsesDirectMetadata || _gl is null)
        {
            return false;
        }

        if (!_loggedCloudFormatFallback)
        {
            _loggedCloudFormatFallback = true;
            EmitDiagnostic(
                "[3D preview] CQ1 floating-point cloud MRT allocation was incomplete; " +
                "falling back to the packed RGBA8 compatibility profile for this GPU session.");
        }

        _cloudFloatingPointTargetsFaulted = true;
        CreateCloudRenderTargets(_gl, GlCloudRenderFormatProfile.Compatibility);
        _cloudMomentsDiagnostic = "disabled (floating-point target fallback)";
        InvalidateCloudTemporalHistory();
        return EnsureSelectedProfile();
    }

    private void UpdateCloudMomentDiagnostic(int volumetricQuality)
    {
        if (_cloudRenderFormatProfile.UsesTemporalMoments)
        {
            _cloudMomentsDiagnostic = "RG16F enabled";
            return;
        }

        if (PreviewVolumetricQuality.Clamp(volumetricQuality) < PreviewVolumetricQuality.High)
        {
            _cloudMomentsDiagnostic = "disabled by preset";
            return;
        }

        if (_cloudTemporalMomentsFaulted)
        {
            _cloudMomentsDiagnostic = "disabled after allocation failure";
            return;
        }

        _cloudMomentsDiagnostic = _glCapabilities?.CanUseCloudTemporalMoments == true
            ? "disabled by format fallback"
            : "disabled (fewer than 3 attachments/draw buffers or compatibility backend)";
    }

    private void DestroyVolumetricCloudResources()
    {
        var gl = _gl;
        CancelCloudDensityGeneration();
        _cloudProgram?.Dispose();
        _cloudProgram = null;
        _cloudHighProgram?.Dispose();
        _cloudHighProgram = null;
        _cloudTemporalProgram?.Dispose();
        _cloudTemporalProgram = null;
        _cloudUpsampleProgram?.Dispose();
        _cloudUpsampleProgram = null;
        _cloudRepairProgram?.Dispose();
        _cloudRepairProgram = null;
        _cloudLightSliceGenerator = null;
        _cloudLightComputeGenerator = null;
        _cloudLightSliceProgram?.Dispose();
        _cloudLightSliceProgram = null;
        _cloudLightComputeProgram?.Dispose();
        _cloudLightComputeProgram = null;
        _cloudGroundTransmittancePublisher = null;
        _cloudGroundTransmittanceProgram?.Dispose();
        _cloudGroundTransmittanceProgram = null;
        _cloudGroundTransmittanceTarget?.Dispose();
        _cloudGroundTransmittanceTarget = null;
        _cloudLightCache?.Dispose();
        _cloudLightCache = null;
        _sparseCloudBrickGenerator?.Dispose();
        _sparseCloudBrickGenerator = null;
        _sparseCloudBrickGenerationProgram?.Dispose();
        _sparseCloudBrickGenerationProgram = null;
        _sparseCloudVolumeResources?.Dispose();
        _sparseCloudVolumeResources = null;
        _sparseCloudBrickAllocator = null;
        _sparseCloudClipmapController = null;
        _sparseCloudPendingRetire.Clear();
        _cloudNoiseTex?.Dispose();
        _cloudNoiseTex = null;
        _cloudDetailTex?.Dispose();
        _cloudDetailTex = null;
        _cloudStbnTex?.Dispose();
        _cloudStbnTex = null;
        _cloudCoverageTex?.Dispose();
        _cloudCoverageTex = null;
        _cloudRenderTarget?.Dispose();
        _cloudRenderTarget = null;
        _cloudResolveTarget?.Dispose();
        _cloudResolveTarget = null;
        _cloudHistoryTarget?.Dispose();
        _cloudHistoryTarget = null;
        _cloudRepairTarget?.Dispose();
        _cloudRepairTarget = null;
        _cloudCompositeTarget = null;
        InvalidateCloudTemporalHistory();
        _cloudDeferredCompositeRetries = 0;
        _loggedCloudDeferredCompositeMiss = 0;
        _cloudTierReadyWarmupDraws = 0;
        _cloudRuntimeFaulted = false;
        _cloudRenderFormatProfile = GlCloudRenderFormatProfile.Compatibility;
        _loggedCloudFormatFallback = false;
        _cloudFloatingPointTargetsFaulted = false;
        _loggedCloudMomentFallback = false;
        _cloudTemporalMomentsFaulted = false;
        _cloudEdgeRepairFaulted = false;
        _loggedCloudEdgeRepairFallback = false;
        _cloudHistoryConfidenceFrames = 0;
        _cloudHistoryConfidenceFullLogged = false;
        _cloudHistoryW = 0;
        _cloudHistoryH = 0;
        _cloudHistoryViewportW = 0;
        _cloudHistoryViewportH = 0;
        _cloudStbnDiagnostic = "not-initialized";
        _cloudMomentsDiagnostic = "not-initialized";
        _cloudEdgeRepairDiagnostic = "not-initialized";
        _cloudDensityAssetDiagnostic = "not-initialized";
        _cloudDensityAssetVersion = 0;
        _cloudDensityAssetProfileCode = 0;
        _cloudDensityGenerationLoadReason = "not-started";
        _cloudDensityGenerationFailureLogged = false;
        _lastCloudDebugViewDiagnostic = -1;
        _cloudDensityBackendSelection = default;
        _sparseCloudResourcesReady = false;
        _sparseCloudRuntimeFaulted = false;
        _cloudDensityBackendSelectionInitialized = false;
        _sparseCloudTemplateAssets = null;
        _sparseCloudTemplateDiagnostic = "not-initialized";
        _sparseCloudResourceAllocationAttempted = false;
        _sparseCloudResourceDiagnostic = "not-initialized";
        _sparseCloudTraversalPrepared = false;
        _sparseCloudTraversalDiagnostic = "not-initialized";
        _sparseCloudPreparedIdentity = default;
        _sparseCloudActiveIdentity = default;
        _sparseCloudActivationDiagnostic = "not-initialized";
        _sparseCloudControlFrame = 0;
        _sparseCloudPreparedLightingChaseCooldown = 0;
        _sparseCloudPendingRetire.Clear();
        _cloudLightingCachePlan = default;
        _cloudLightBasis = null;
        _cloudLightCacheReadyLogged = false;
        _cloudLightGenerationFailureLogged = false;
        _cloudGroundTransmittanceReadyLogged = false;
        _cloudGroundTransmittanceFailureLogged = false;
        _cloudLightingCacheResourceDiagnostic = "resources=not-initialized";
        _cloudGroundTransmittanceDiagnostic = "not-initialized";
        ResetCloudLightingCacheLifecycleCq36();
        _cloudFrameIndex = 0;

        if (gl is null)
        {
            _cloudQuadVao = _cloudQuadVbo = 0;
            return;
        }

        if (_cloudQuadVbo != 0)
        {
            gl.DeleteBuffer(_cloudQuadVbo);
            _cloudQuadVbo = 0;
        }

        if (_cloudQuadVao != 0)
        {
            gl.DeleteVertexArray(_cloudQuadVao);
            _cloudQuadVao = 0;
        }
    }

    private bool CanDrawVolumetricClouds(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableVolumetricClouds &&
        !_cloudRuntimeFaulted &&
        _cloudProgram is { IsValid: true } &&
        _cloudQuadVao != 0;

    private GlCloudTemporalRenderTarget? ResolveSharedCloudTransmittanceTarget(
        in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableVolumetricClouds &&
        !_cloudRuntimeFaulted &&
        settings.CloudDebugView == PreviewCloudDebugView.Off &&
        _cloudCompositeTarget is { IsValid: true }
            ? _cloudCompositeTarget
            : null;

    private void InvalidateCloudTemporalHistory()
    {
        _cloudHistoryValid = false;
        _cloudHistoryConfidenceFrames = 0;
        _cloudHistoryLowConfidenceFrames = 0;
        // Keep _cloudHistoryConfidenceFullLogged: the 8/8 success line is once per
        // GPU session. Resetting it here re-spammed the log on every history rebuild.
        _cloudTemporalStability = 0f;
        _cloudIdleDenoiseLatched = false;
    }

    private void NoteCloudTemporalConfidenceProgress()
    {
        if (_cloudHistoryConfidenceFrames >=
            PreviewCloudTemporalMoments.ConfidenceFrameCount)
        {
            _cloudHistoryLowConfidenceFrames = 0;
            if (!_cloudHistoryConfidenceFullLogged)
            {
                _cloudHistoryConfidenceFullLogged = true;
                EmitDiagnostic(
                    "[3D preview] CQ1 cloud temporal history confidence reached " +
                    $"{PreviewCloudTemporalMoments.ConfidenceFrameCount}/" +
                    $"{PreviewCloudTemporalMoments.ConfidenceFrameCount}.");
            }

            return;
        }

        _cloudHistoryLowConfidenceFrames++;
        // ~1s at 60 FPS with confidence stuck in the first accepted bucket.
        if (_cloudHistoryLowConfidenceFrames == 60 &&
            _cloudHistoryConfidenceFrames <= 1)
        {
            EmitDiagnostic(
                "[3D preview] CQ1 cloud temporal confidence is not ramping " +
                $"(live={_cloudHistoryConfidenceFrames}/" +
                $"{PreviewCloudTemporalMoments.ConfidenceFrameCount}, historyValid=" +
                $"{_cloudHistoryValid}). Check history invalidation or copy failures; " +
                "the first-use profile log is not a live confidence meter.");
        }
    }

    /// <summary>
    /// Live CQ1 temporal confidence for HUD/debug. The first-use cloud profile log
    /// freezes whatever value was current at first report and must not be read as a
    /// stuck-confidence signal.
    /// </summary>
    public bool TryGetCloudTemporalHistoryDebug(
        out int confidenceFrames,
        out int confidenceFrameCount,
        out bool historyValid)
    {
        confidenceFrames = _cloudHistoryConfidenceFrames;
        confidenceFrameCount = PreviewCloudTemporalMoments.ConfidenceFrameCount;
        historyValid = _cloudHistoryValid;
        return ShouldUseCloudShaderTemporal(_settings);
    }

    private bool TryApplyCloudEdgeRepair(
        ref GlRenderFrame frame,
        Matrix4x4 invViewProj,
        float layerWorldY,
        PreviewVolumetricQuality.Profile profile,
        bool useSceneDepth,
        Vector3 windOffset,
        Vector2 cirrusWindOffset)
    {
        if (frame.Settings.CloudDebugView != PreviewCloudDebugView.Off)
        {
            _cloudEdgeRepairDiagnostic = "disabled by cloud debug view";
            return false;
        }

        if (PreviewVolumetricQuality.Clamp(frame.Settings.VolumetricQuality) !=
            PreviewVolumetricQuality.Cinematic)
        {
            _cloudEdgeRepairDiagnostic = "disabled by preset";
            return false;
        }

        if (_useOpenGlEs)
        {
            _cloudEdgeRepairDiagnostic = "disabled (GLES/ANGLE compatibility policy)";
            return false;
        }

        if (!_cloudRenderFormatProfile.UsesDirectMetadata)
        {
            _cloudEdgeRepairDiagnostic = "disabled (floating-point target unavailable)";
            return false;
        }

        if (_cloudEdgeRepairFaulted)
        {
            return false;
        }

        var source = _cloudCompositeTarget;
        var program = _cloudRepairProgram;
        if (source is null || program is not { IsValid: true } || _cloudQuadVao == 0)
        {
            if (program is not { IsValid: true } &&
                _cloudEdgeRepairDiagnostic is "available" or "not-initialized")
            {
                _cloudEdgeRepairDiagnostic = "disabled (shader unavailable)";
            }

            return false;
        }

        _cloudRepairTarget ??= new GlCloudTemporalRenderTarget(
            frame.Gl,
            GlCloudRenderFormatProfile.DesktopFloatingPoint);
        if (!_cloudRepairTarget.EnsureSize(frame.Vw, frame.Vh))
        {
            DisableCloudEdgeRepair(
                ref frame,
                "full-resolution RGBA16F/RG32F framebuffer allocation was incomplete");
            return false;
        }

        var gl = frame.Gl;
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorDepth = gl.IsEnabled(EnableCap.DepthTest);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);

        try
        {
            _cloudRepairTarget.Clear();
            _cloudRepairTarget.BindDraw(includeMoments: false);
            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.ScissorTest);
            gl.ColorMask(true, true, true, true);
            FlushPendingGlErrors(gl);

            program.Use();
            var ru = _cloudRepairUniformLocs;
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, source.ColorTextureHandle);
            SetIntOnProgramLoc(program, ru.Clouds, 0);
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
            SetIntOnProgramLoc(program, ru.CloudData, 1);

            var depthFallback = _cloudCoverageTex?.Id ?? source.DataTextureHandle;
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(
                TextureTarget.Texture2D,
                useSceneDepth && _sceneCapture is { IsValid: true }
                    ? _sceneCapture.DepthTextureHandle
                    : depthFallback);
            SetIntOnProgramLoc(program, ru.SceneDepth, 2);

            _cloudNoiseTex?.Bind(3);
            SetIntOnProgramLoc(program, ru.CloudNoise, 3);
            _cloudDetailTex?.Bind(4);
            SetIntOnProgramLoc(program, ru.DetailNoise, 4);
            (_cloudStbnTex ?? _cloudNoiseTex)?.Bind(5);
            SetIntOnProgramLoc(program, ru.CloudStbn, 5);
            _cloudCoverageTex?.Bind(6);
            SetIntOnProgramLoc(program, ru.CoverageMap, 6);
            gl.ActiveTexture(TextureUnit.Texture7);
            gl.BindTexture(
                TextureTarget.Texture2D,
                _atmoLutsValid && _atmoSkyViewTex != 0
                    ? _atmoSkyViewTex
                    : depthFallback);
            SetIntOnProgramLoc(program, ru.SkyViewLut, 7);

            SetVec2OnProgramLoc(program, ru.CloudTexelSize, new Vector2(
                1f / Math.Max(source.Width, 1),
                1f / Math.Max(source.Height, 1)));
            SetMatrixOnProgramLoc(program, ru.InvViewProj, invViewProj);
            SetVec3OnProgramLoc(program, ru.CameraPos, frame.Eye);
            SetVec3OnProgramLoc(program, ru.SunDir, frame.LightDir);
            SetFloatOnProgramLoc(program, ru.SunIntensity, frame.Settings.AtmosphereSunIntensity);
            SetFloatOnProgramLoc(program, ru.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatOnProgramLoc(
                program,
                ru.PlanetRadius,
                PreviewStageConstants.CloudLegacyAltitudeReferenceRadius);
            SetFloatOnProgramLoc(program, ru.LayerHeight, layerWorldY);
            SetFloatOnProgramLoc(program, ru.VolumeHeight, frame.Settings.CloudVolumeHeight);
            SetFloatOnProgramLoc(program, ru.Density, frame.Settings.CloudDensity);
            SetFloatOnProgramLoc(program, ru.CoverageScale, frame.Settings.CloudCoverageScale);
            SetFloatOnProgramLoc(program, ru.VolumeSize, frame.Settings.CloudVolumeSize);
            SetFloatOnProgramLoc(
                program,
                ru.PixelAngularSize,
                PreviewCloudRayFootprint.ComputePixelAngularSize(
                    frame.VerticalFieldOfViewRadians,
                    frame.Vh));
            SetVec3OnProgramLoc(program, ru.WindOffset, windOffset);
            SetFloatOnProgramLoc(program, ru.CirrusStrength, frame.Settings.CloudCirrusStrength);
            SetVec2OnProgramLoc(program, ru.CirrusWindOffset, cirrusWindOffset);
            SetVec2OnProgramLoc(
                program,
                ru.CirrusWindDir,
                ComputeCirrusWindDirection(frame.Settings));
            ApplyCloudLayerEnvelopeUniforms(
                program,
                ru.CumulusLayerCount,
                ru.InterDeckGap,
                ru.HeightVariance,
                ru.UpperThicknessScale,
                ru.UpperCoverageScale,
                ru.UpperDensityScale,
                ru.UpperWindOffset,
                ru.CirrusGap,
                ru.CirrusThickness,
                ru.StyleBias,
                frame.Settings,
                ComputeUpperCloudWindOffset(
                    frame.Settings.CloudFreezeWind ? 0.0 : frame.RenderTime,
                    frame.Settings));
            SetIntOnProgramLoc(
                program,
                ru.MarchSteps,
                Math.Clamp(frame.Settings.CloudMarchStepOverride, 0, 64));
            SetIntOnProgramLoc(program, ru.HasSceneDepth, useSceneDepth ? 1 : 0);
            SetIntOnProgramLoc(program, ru.HasCloudNoise, _cloudNoiseTex is not null ? 1 : 0);
            SetIntOnProgramLoc(program, ru.HasDetailNoise, _cloudDetailTex is not null ? 1 : 0);
            SetIntOnProgramLoc(
                program,
                ru.HasCloudStbn,
                CanUseCloudStbn(
                    _useOpenGlEs,
                    profile.CloudQuality,
                    _cloudStbnTex is not null)
                    ? 1
                    : 0);
            SetIntOnProgramLoc(program, ru.HasCoverageMap, _cloudCoverageTex is not null ? 1 : 0);
            SetIntOnProgramLoc(
                program,
                ru.HasSkyLut,
                _atmoLutsValid && _atmoSkyViewTex != 0 ? 1 : 0);
            SetIntOnProgramLoc(
                program,
                ru.SourceCloudDataDirect,
                source.Profile.UsesDirectMetadata ? 1 : 0);
            SetIntOnProgramLoc(
                program,
                ru.DensityAssetVersion,
                _cloudDensityAssetVersion);
            SetIntOnProgramLoc(program, ru.CloudFrameIndex, _cloudFrameIndex);
            SetFloatOnProgramLoc(program, ru.RepairStability, _cloudTemporalStability);
            BindSparseCloudRepairTraversalCq46(gl, program, ru);

            gl.BindVertexArray(_cloudQuadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            var drawError = gl.GetError();
            if (drawError != GLEnum.NoError)
            {
                throw new InvalidOperationException(
                    $"CQ1.8 cloud edge-repair draw produced GL error {drawError}.");
            }

            _cloudCompositeTarget = _cloudRepairTarget;
            var retraceBlend = PreviewCloudEdgeRepairClassifier.EvaluateRetraceBlend(
                _cloudTemporalStability);
            _cloudEdgeRepairDiagnostic =
                $"active full-res {frame.Vw}x{frame.Vh}, " +
                $"{PreviewCloudEdgeRepairClassifier.RepairStepCount}-step, " +
                $"retraceBlend={retraceBlend:0.00}, stability={_cloudTemporalStability:0.00}";
            return true;
        }
        catch (Exception exception)
        {
            DisableCloudEdgeRepair(
                ref frame,
                $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            gl.BindVertexArray(0);
            gl.ColorMask(
                priorColorMask[0],
                priorColorMask[1],
                priorColorMask[2],
                priorColorMask[3]);
            if (priorBlend)
            {
                gl.Enable(EnableCap.Blend);
            }
            else
            {
                gl.Disable(EnableCap.Blend);
            }

            if (priorDepth)
            {
                gl.Enable(EnableCap.DepthTest);
            }
            else
            {
                gl.Disable(EnableCap.DepthTest);
            }

            if (priorScissor)
            {
                gl.Enable(EnableCap.ScissorTest);
            }
            else
            {
                gl.Disable(EnableCap.ScissorTest);
            }
        }
    }

    private void DisableCloudEdgeRepair(ref GlRenderFrame frame, string reason)
    {
        _cloudEdgeRepairFaulted = true;
        _cloudEdgeRepairDiagnostic = $"disabled after optional failure ({reason})";
        _cloudRepairTarget?.Dispose();
        _cloudRepairTarget = null;
        if (!_loggedCloudEdgeRepairFallback)
        {
            _loggedCloudEdgeRepairFallback = true;
            EmitDiagnostic(
                "[3D preview] CQ1.8 cloud edge repair disabled for this GPU session; " +
                $"continuing with CQ1.7 reconstruction ({reason}).");
        }

        BindDefaultFramebuffer(ref frame);
    }

    private static string FormatCloudAltitudeDiagnostic(
        Vector3 eye,
        in PreviewRenderSettingsSnapshot settings)
    {
        var groundY = PreviewStageConstants.GroundPlaneWorldY;
        var layerWorldY =
            PreviewStageConstants.CloudLayerBaseWorldY(settings.CloudLayerHeight);
        var stack = PreviewCloudLayerEnvelope.Build(
            groundY,
            layerWorldY,
            settings.CloudVolumeHeight,
            settings.CloudVolumeSize,
            settings.CloudCirrusStrength,
            settings.CloudCumulusLayerCount,
            settings.CloudInterDeckGap,
            settings.CloudLayerHeightVariance,
            settings.CloudUpperThicknessScale,
            settings.CloudCirrusGap,
            settings.CloudCirrusThickness);
        var altitude = PreviewCloudLayerGeometry.Altitude(eye, groundY);
        return FormattableString.Invariant(
            $"worldAltitude={altitude:F5}; cumulusSigned={altitude - stack.CumulusSupportBase:F5}/{altitude - stack.CumulusSupportTop:F5}; cirrusSigned={altitude - stack.CirrusBaseAltitude:F5}/{altitude - stack.CirrusTopAltitude:F5}; decks={stack.DeckCount}");
    }

    private void HandleCloudRuntimeFailure(ref GlRenderFrame frame, string stage, Exception exception)
    {
        _cloudRuntimeFaulted = true;
        _cloudCompositeTarget = null;
        InvalidateCloudTemporalHistory();
        _volumeFroxelHistoryValid = false;
        _volumeIntegrateHistoryValid = false;
        _godRayHistoryValid = false;

        try
        {
            BindDefaultFramebuffer(ref frame);
            frame.Gl.Disable(EnableCap.ScissorTest);
            frame.Gl.Disable(EnableCap.Blend);
            frame.Gl.Enable(EnableCap.DepthTest);
            frame.Gl.DepthMask(true);
        }
        catch (Exception recoveryException)
        {
            LogService.AppendEmergencyDiagnostic(
                "Cloud render-state recovery failure",
                recoveryException.ToString());
        }

        var altitudeDiagnostic =
            FormatCloudAltitudeDiagnostic(frame.Eye, frame.Settings);
        var detail = FormattableString.Invariant($"Cloud stage: {stage}\nEye: ({frame.Eye.X:R}, {frame.Eye.Y:R}, {frame.Eye.Z:R})\nContinuous altitude: {altitudeDiagnostic}\nViewport: {frame.Vw}x{frame.Vh}; cloud quality: {frame.Settings.CloudQuality}; volumetric quality: {frame.Settings.VolumetricQuality}; temporal: {ShouldUseCloudShaderTemporal(frame.Settings)}\nContext: {_glCapabilities?.FormatContextSuffix() ?? "unavailable"}\n{exception}");
        LogService.AppendEmergencyDiagnostic("Volumetric cloud render exception", detail);
        EmitDiagnostic(
            $"[3D preview] Volumetric cloud {stage} failed ({exception.GetType().Name}: {exception.Message}). " +
            "Detailed clouds are disabled for this GPU session; analytic fog/cloud fallback remains active. " +
            $"Emergency log: {LogService.EmergencyLogPath}");
    }

    private bool DrawVolumetricClouds(
        ref GlRenderFrame frame,
        bool deferComposite = false,
        bool? forceTemporal = null,
        bool updateHistory = true)
    {
        PumpCloudDensityGeneration(frame.Gl);
        if (!CanDrawVolumetricClouds(frame.Settings))
        {
            return false;
        }

        BindDefaultFramebuffer(ref frame);
        return DrawVolumetricCloudsInternal(ref frame, deferComposite, forceTemporal, updateHistory);
    }

    /// <summary>
    /// Cloud temporal reconstruction owns a separate history from god rays and final preview TAA.
    /// Representative-distance rejection prevents the old sky-frustum history leak, so it remains
    /// useful when either of those later passes is active.
    /// </summary>
    private static bool ShouldUseCloudShaderTemporal(in PreviewRenderSettingsSnapshot settings)
    {
        if (settings.CloudDisableTemporal || settings.CloudDebugView != PreviewCloudDebugView.Off)
        {
            return false;
        }

        var profile = PreviewVolumetricQuality.Resolve(settings.VolumetricQuality);
        return profile.CloudTemporalWeight > 0f;
    }

    private static bool CanUseCloudTemporalReproject(in PreviewRenderSettingsSnapshot settings)
    {
        return ShouldUseCloudShaderTemporal(settings);
    }

    private bool DrawVolumetricCloudsInternal(
        ref GlRenderFrame frame,
        bool deferComposite = false,
        bool? forceTemporal = null,
        bool updateHistory = true)
    {
        var settings = frame.Settings;
        var debugViewCode = (int)settings.CloudDebugView;
        if (_lastCloudDebugViewDiagnostic != debugViewCode)
        {
            _lastCloudDebugViewDiagnostic = debugViewCode;
            if (settings.CloudDebugView != PreviewCloudDebugView.Off)
            {
                EmitDiagnostic(
                    $"[3D preview] Cloud debug view active: {settings.CloudDebugView}; " +
                    $"densityAssetProfileCode={_cloudDensityAssetProfileCode}, " +
                    $"densityAssets={_cloudDensityAssetDiagnostic}. " +
                    "Temporal reconstruction and Cinematic edge repair are bypassed.");
            }
        }

        var viewProj = frame.Proj * frame.View;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
        {
            return false;
        }

        _cloudCompositeTarget = null;

        var gl = frame.Gl;
        var profile = PreviewVolumetricQuality.Resolve(settings.VolumetricQuality);
        TryInitSparseCloudResourcesCq42(
            gl,
            settings.VolumetricQuality);
        var layerWorldY =
            PreviewStageConstants.CloudLayerBaseWorldY(
                settings.CloudLayerHeight);
        UpdateSparseCloudClipmapsCq43(
            ref frame,
            layerWorldY);
        PrepareSparseCloudActivationCq46();
        UpdateCloudDensityBackendSelectionCq40(settings.VolumetricQuality);
        EnsureCloudRenderFormatForQuality(gl, settings.VolumetricQuality);
        var useSceneDepth = frame.GodRayCaptureActive && _sceneCapture is { IsValid: true };
        var windTime = settings.CloudFreezeWind ? 0.0 : frame.RenderTime;
        var windOffset = ComputeCloudWindOffset(windTime, settings);
        var cirrusWindOffset = ComputeCirrusWindOffset(windTime, settings);
        TryGenerateCloudLightingCacheCq33(
            ref frame,
            layerWorldY,
            windOffset,
            cirrusWindOffset,
            profile);
        FinalizeSparseCloudActivationCq46(settings.VolumetricQuality);
        UpdateCloudDensityBackendSelectionCq40(settings.VolumetricQuality);
        var settingsHash = ComputeCloudHistorySettingsHash(
            settings,
            frame.VerticalFieldOfViewRadians);
        if (_cloudHistoryValid && (_cloudHistorySettingsHash != settingsHash ||
            Vector3.Distance(frame.Eye, _cloudPrevCameraPos) > Math.Max(settings.CloudVolumeSize, 80f)))
        {
            InvalidateCloudTemporalHistory();
        }

        var temporalAvailable = _cloudTemporalProgram is { IsValid: true } &&
            _cloudRenderTarget is not null && _cloudResolveTarget is not null && _cloudHistoryTarget is not null;
        var useTemporalReproject = (forceTemporal ?? CanUseCloudTemporalReproject(settings)) && temporalAvailable;
        var traceSize = PreviewCloudTraceSizing.Resolve(
            frame.Vw,
            frame.Vh,
            settings.VolumetricQuality);
        var useOffscreen = true;

        if (useOffscreen)
        {
            var w = traceSize.Width;
            var h = traceSize.Height;
            if (_cloudHistoryViewportW != frame.Vw || _cloudHistoryViewportH != frame.Vh)
            {
                InvalidateCloudTemporalHistory();
                _cloudHistoryViewportW = frame.Vw;
                _cloudHistoryViewportH = frame.Vh;
            }

            if (_cloudHistoryW != w || _cloudHistoryH != h)
            {
                InvalidateCloudTemporalHistory();
                _cloudHistoryW = w;
                _cloudHistoryH = h;
            }

            if (!EnsureCloudRenderTargetSetSize(w, h, requireTemporalTargets: useTemporalReproject))
            {
                if (deferComposite)
                {
                    if (_cloudDeferredCompositeRetries > 0)
                    {
                        _cloudDeferredCompositeRetries--;
                    }

                    return false;
                }

                useOffscreen = false;
                useTemporalReproject = false;
            }
            else if (deferComposite)
            {
                _cloudDeferredCompositeRetries = 0;
            }

            if (useTemporalReproject &&
                (_cloudResolveTarget is null || _cloudHistoryTarget is null))
            {
                useTemporalReproject = false;
                InvalidateCloudTemporalHistory();
            }
        }

        if (useOffscreen)
        {
            // Transparent black, not the scene clear color: discarded pixels must stay
            // alpha 0 or the composite stamps opaque near-black over the sky between clouds.
            // RG32F metadata uses a negative cloud-type sentinel in place of alpha validity.
            _cloudRenderTarget!.Clear();
            _cloudRenderTarget.BindDraw(includeMoments: false);
        }

        var traceTargetHeight = useOffscreen ? traceSize.Height : frame.Vh;
        var pixelAngularSize = PreviewCloudRayFootprint.ComputePixelAngularSize(
            frame.VerticalFieldOfViewRadians,
            traceTargetHeight);
        var jitterPhase = PreviewCloudTemporalJitter.Sample(_cloudFrameIndex);
        BindCloudShaderUniforms(frame, invViewProj, layerWorldY, profile, useSceneDepth,
            windOffset, cirrusWindOffset, jitterPhase, pixelAngularSize);

        GLEnum cloudDrawErr;
        using (BeginPassTimerScope(GlGpuTimerScope.CloudTrace))
        {
            var priorBlend = gl.IsEnabled(EnableCap.Blend);
            var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
            var priorColorMask = new bool[4];
            gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);
            if (useOffscreen)
            {
                gl.Disable(EnableCap.Blend);
            }
            else
            {
                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            }

            gl.Disable(EnableCap.DepthTest);
            gl.DepthMask(false);
            gl.Disable(EnableCap.ScissorTest);
            gl.ColorMask(true, true, true, true);
            FlushPendingGlErrors(gl);
            gl.BindVertexArray(_cloudQuadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            cloudDrawErr = gl.GetError();
            gl.BindVertexArray(0);
            gl.DepthMask(true);
            gl.Enable(EnableCap.DepthTest);
            gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
            if (priorScissor)
            {
                gl.Enable(EnableCap.ScissorTest);
            }

            if (priorBlend)
            {
                gl.Enable(EnableCap.Blend);
            }
            else
            {
                gl.Disable(EnableCap.Blend);
            }
        }

        if (cloudDrawErr != GLEnum.NoError)
        {
            throw new InvalidOperationException($"Volumetric cloud draw produced GL error {cloudDrawErr}.");
        }

        if (useOffscreen)
        {
            _cloudCompositeTarget = _cloudRenderTarget;
            if (useTemporalReproject)
            {
                bool temporalOk;
                using (BeginPassTimerScope(GlGpuTimerScope.CloudTemporal))
                {
                    temporalOk = ResolveCloudTemporal(
                        frame, invViewProj, windOffset, cirrusWindOffset, profile);
                }

                if (temporalOk)
                {
                    _cloudCompositeTarget = _cloudResolveTarget;
                    if (updateHistory && _cloudHistoryTarget!.CopyFrom(_cloudResolveTarget!))
                    {
                        _cloudPrevViewProj = viewProj;
                        _cloudPrevCameraPos = frame.Eye;
                        _cloudPrevWindOffset = windOffset;
                        _cloudPrevCirrusWindOffset = cirrusWindOffset;
                        _cloudHistorySettingsHash = settingsHash;
                        _cloudHistoryValid = true;
                        _cloudHistoryConfidenceFrames =
                            PreviewCloudTemporalMoments.AdvanceConfidence(_cloudHistoryConfidenceFrames);
                        NoteCloudTemporalConfidenceProgress();
                    }
                    else if (updateHistory)
                    {
                        if (!_cloudHistoryCopyFailureLogged)
                        {
                            _cloudHistoryCopyFailureLogged = true;
                            EmitDiagnostic(
                                "[3D preview] CQ1 cloud temporal history copy failed; " +
                                "confidence was reset. Color/data blit is required; " +
                                "moment blit failures are now non-fatal.");
                        }

                        InvalidateCloudTemporalHistory();
                    }
                }
                else
                {
                    // Keep CQ1.8 retrace fully armed when temporal did not resolve.
                    _cloudTemporalStability = 0f;
                }
            }
            else
            {
                InvalidateCloudTemporalHistory();
            }

            using (BeginPassTimerScope(GlGpuTimerScope.CloudRepair))
            {
                _ = TryApplyCloudEdgeRepair(
                    ref frame,
                    invViewProj,
                    layerWorldY,
                    profile,
                    useSceneDepth,
                    windOffset,
                    cirrusWindOffset);
            }

            if (!deferComposite)
            {
                using (BeginPassTimerScope(GlGpuTimerScope.CloudUpsample))
                {
                    CompositeCloudRenderTargetToDefault(ref frame);
                }
            }
        }

        _cloudFrameIndex = PreviewCloudTemporalJitter.AdvanceFrame(
            _cloudFrameIndex,
            settings.CloudDisableTemporal,
            PreviewCloudSpatiotemporalBlueNoiseGenerator.FrameCount);

        if (useOffscreen && deferComposite)
        {
            BindDefaultFramebuffer(ref frame);
        }

        var cloudProfileDiagnosticIdentity =
            $"{PreviewVolumetricQuality.Clamp(frame.Settings.VolumetricQuality)}|" +
            $"{profile.CloudQuality}|{_cloudRenderFormatProfile.Name}|" +
            $"density-v{_cloudDensityAssetVersion}|" +
            $"{_cloudDensityBackendSelection.ActiveBackend}";
        if (_cloudProfileDiagnosticGate.TryReport(
                cloudProfileDiagnosticIdentity))
        {
            var godRays = frame.GodRayCaptureActive && _sceneCapture is { IsValid: true };
            EmitDiagnostic($"[3D preview] Flat continuous-world volumetric clouds active (sceneDepth={useSceneDepth}, " +
                $"temporalResolve={useTemporalReproject}, cloudDepthHistory={useTemporalReproject}, godRays={godRays}, " +
                $"volumetricPreset={PreviewVolumetricQuality.GetName(frame.Settings.VolumetricQuality)}, " +
                $"cloudQuality={PreviewVolumetricQuality.Resolve(frame.Settings.VolumetricQuality).CloudQuality}, " +
                $"traceProgram={(profile.CloudQuality == PreviewVolumetricQuality.High && _cloudHighProgram is { IsValid: true } ? "high-specialized-cq3.7" : "generic")}, " +
                $"cloudFormat={_cloudRenderFormatProfile.Name}, " +
                $"trace={traceSize.Width}x{traceSize.Height}@{traceSize.Scale:0.###}, " +
                $"pixelAngle={pixelAngularSize:0.000000}, " +
                "cloudColor=linear-trace-history/final-composite-encode, " +
                $"densityAssets={_cloudDensityAssetDiagnostic}, " +
                $"densityBackend={_cloudDensityBackendSelection.FormatDiagnostic()}, " +
                $"sparseTemplates={_sparseCloudTemplateDiagnostic}, " +
                $"sparseResources={_sparseCloudResourceDiagnostic}, " +
                $"sparseTraversal={_sparseCloudTraversalDiagnostic}, " +
                $"sparseActivation={_sparseCloudActivationDiagnostic}, " +
                $"densitySemantics=v{_cloudDensityAssetVersion}, " +
                $"densityDetail={(_cloudDensityAssetVersion >= 2 ? "single-low-medium/ca1-broad-boundary-high-cinematic" : "legacy-single")}, " +
                $"cloudPopulation={(_cloudDensityAssetVersion >= 2 ? "ca2-dual-scale-asymmetric-v2-templates" : "legacy-continuous")}, " +
                $"weatherAddressing={(_cloudDensityAssetVersion >= 2 ? "dual-world" : "legacy")}, " +
                "directDiscOcclusion=post-temporal-full-res, " +
                $"stbn={_cloudStbnDiagnostic}, " +
                $"stbnActive={CanUseCloudStbn(_useOpenGlEs, profile.CloudQuality, _cloudStbnTex is not null)}, " +
                $"moments={_cloudMomentsDiagnostic}, " +
                $"edgeRepair={_cloudEdgeRepairDiagnostic}, " +
                $"thinFeaturePreservation={PreviewCloudTemporalLowAlphaWeight.FormatDiagnostic()};" +
                $"{PreviewCloudEdgeRepairClassifier.FormatDiagnostic()};" +
                $"{PreviewCloudLightingShadingProfiles.FormatDiagnostic()}, " +
                $"cloudLightCache={_cloudLightingCachePlan.FormatDiagnostic(_cloudLightingCacheResourceDiagnostic)}, " +
                // First-use gate freezes this line; do not treat it as live confidence.
                $"historyConfidence=live-via-overlay (first-report-snapshot=" +
                $"{_cloudHistoryConfidenceFrames}/{PreviewCloudTemporalMoments.ConfidenceFrameCount}), " +
                $"previewTaa={frame.Settings.EnablePreviewTaa}, warmupDraws={_cloudTierReadyWarmupDraws}, " +
                $"noiseTex={_cloudNoiseTex is not null}, coverageMap={_cloudCoverageTex is not null}).");
        }

        return true;
    }

    private void BindCloudShaderUniforms(
        GlRenderFrame frame,
        Matrix4x4 invViewProj,
        float layerWorldY,
        PreviewVolumetricQuality.Profile profile,
        bool useSceneDepth,
        Vector3 windOffset,
        Vector2 cirrusWindOffset,
        float jitterPhase,
        float pixelAngularSize)
    {
        var useHighSpecialization =
            profile.CloudQuality == PreviewVolumetricQuality.High &&
            _cloudHighProgram is { IsValid: true };
        var program = useHighSpecialization
            ? _cloudHighProgram
            : _cloudProgram;
        if (program is null)
        {
            return;
        }

        var gl = frame.Gl;
        var settings = frame.Settings;
        var cu = useHighSpecialization
            ? _cloudHighUniformLocs
            : _cloudUniformLocs;

        // GLES/ANGLE: sampler uniforms default to texture unit 0, and draw validation
        // rejects a program whose samplers of different types (sampler3D uCloudNoise vs
        // the sampler2D uniforms) reference the same unit — the whole cloud quad is then
        // silently dropped with GL_INVALID_OPERATION. On cold start with god rays active,
        // uSceneDepth on the warmup path could otherwise sit on unit 0 alongside uCloudNoise.
        // Pin every sampler to its own unit unconditionally; the uHas* flags keep
        // unbound units from being sampled.
        SetIntOnProgramLoc(program, cu.CloudNoise, 0);
        SetIntOnProgramLoc(program, cu.CoverageMap, 1);
        SetIntOnProgramLoc(program, cu.SkyViewLut, 2);
        SetIntOnProgramLoc(program, cu.DetailNoise, 3);
        SetIntOnProgramLoc(program, cu.CloudStbn, 4);
        SetIntOnProgramLoc(program, cu.SceneDepth, 5);
        SetIntOnProgramLoc(program, cu.CloudLightNear, 6);
        SetIntOnProgramLoc(program, cu.CloudLightFar, 7);
        SetIntOnProgramLoc(program, cu.SparseCloudAtlas, 8);
        SetIntOnProgramLoc(program, cu.SparseCloudPageL0, 9);
        SetIntOnProgramLoc(program, cu.SparseCloudPageL1, 10);
        SetIntOnProgramLoc(program, cu.SparseCloudPageL2, 11);

        SetFloatOnProgramLoc(program, cu.SunIntensity, settings.AtmosphereSunIntensity);
        SetFloatOnProgramLoc(program, cu.LayerHeight, layerWorldY);
        SetFloatOnProgramLoc(program, cu.VolumeHeight, settings.CloudVolumeHeight);
        SetFloatOnProgramLoc(program, cu.Density, settings.CloudDensity);
        SetFloatOnProgramLoc(program, cu.CoverageScale, settings.CloudCoverageScale);
        SetFloatOnProgramLoc(program, cu.VolumeSize, settings.CloudVolumeSize);
        SetFloatOnProgramLoc(program, cu.PixelAngularSize, pixelAngularSize);
        SetIntOnProgramLoc(program, cu.Quality, profile.CloudQuality);
        SetIntOnProgramLoc(program, cu.MarchSteps, Math.Clamp(settings.CloudMarchStepOverride, 0, 64));
        SetIntOnProgramLoc(program, cu.DebugView, (int)settings.CloudDebugView);
        SetMatrixOnProgramLoc(program, cu.InvViewProj, invViewProj);
        SetVec3OnProgramLoc(program, cu.CameraPos, frame.Eye);
        SetFloatOnProgramLoc(program, cu.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
        SetFloatOnProgramLoc(
            program,
            cu.PlanetRadius,
            PreviewStageConstants.CloudLegacyAltitudeReferenceRadius);
        SetVec3OnProgramLoc(program, cu.SunDir, frame.LightDir);
        SetVec3OnProgramLoc(program, cu.WindOffset, windOffset);
        SetFloatOnProgramLoc(program, cu.CirrusStrength, settings.CloudCirrusStrength);
        SetVec2OnProgramLoc(program, cu.CirrusWindOffset, cirrusWindOffset);
        SetVec2OnProgramLoc(program, cu.CirrusWindDir, ComputeCirrusWindDirection(settings));
        ApplyCloudLayerEnvelopeUniforms(
            program,
            cu.CumulusLayerCount,
            cu.InterDeckGap,
            cu.HeightVariance,
            cu.UpperThicknessScale,
            cu.UpperCoverageScale,
            cu.UpperDensityScale,
            cu.UpperWindOffset,
            cu.CirrusGap,
            cu.CirrusThickness,
            cu.StyleBias,
            settings,
            ComputeUpperCloudWindOffset(
                settings.CloudFreezeWind ? 0.0 : frame.RenderTime,
                settings));
        SetIntOnProgramLoc(program, cu.HasSceneDepth, useSceneDepth ? 1 : 0);
        SetFloatOnProgramLoc(program, cu.FramePhase, jitterPhase);
        SetIntOnProgramLoc(program, cu.CloudFrameIndex, _cloudFrameIndex);
        SetIntOnProgramLoc(program, cu.HasCloudNoise, _cloudNoiseTex is not null ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HasDetailNoise, _cloudDetailTex is not null ? 1 : 0);
        SetIntOnProgramLoc(
            program,
            cu.HasCloudStbn,
            CanUseCloudStbn(_useOpenGlEs, profile.CloudQuality, _cloudStbnTex is not null) ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HasCoverageMap, _cloudCoverageTex is not null ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HasSkyLut, _atmoLutsValid && _atmoSkyViewTex != 0 ? 1 : 0);
        SetIntOnProgramLoc(program, cu.CloudDataDirect,
            _cloudRenderFormatProfile.UsesDirectMetadata ? 1 : 0);
        SetIntOnProgramLoc(
            program,
            cu.DensityAssetVersion,
            _cloudDensityAssetVersion);
        SetIntOnProgramLoc(
            program,
            cu.DensityAssetProfileCode,
            _cloudDensityAssetProfileCode);
        BindSparseCloudTraversalCq45(gl, program, cu);
        BindCloudLightCacheUniforms(gl, program, cu, profile.CloudQuality);

        if (_cloudNoiseTex is not null)
        {
            _cloudNoiseTex.Bind(0);
        }

        if (_cloudCoverageTex is not null)
        {
            _cloudCoverageTex.Bind(1);
        }

        if (_cloudDetailTex is not null)
        {
            _cloudDetailTex.Bind(3);
        }

        if (_cloudStbnTex is not null)
        {
            _cloudStbnTex.Bind(4);
        }

        if (_atmoLutsValid && _atmoSkyViewTex != 0)
        {
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, _atmoSkyViewTex);
        }

        if (useSceneDepth && _sceneCapture is not null)
        {
            gl.ActiveTexture(TextureUnit.Texture5);
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        }
    }

    private void BindSparseCloudTraversalCq45(
        GL gl,
        GlShaderProgram program,
        in CloudUniformLocs cu)
    {
        var bindings = CreateActiveSparseCloudBindingsCq46();
        var active = bindings is { IsValid: true };
        var sparse = active ? bindings!.Value : default;
        SetIntOnProgramLoc(
            program,
            cu.HasSparseCloudTraversal,
            active ? 1 : 0);
        for (var level = 0;
             level < PreviewSparseCloudVolumeContract.ClipmapCount;
             level++)
        {
            var origin = level switch
            {
                0 => sparse.OriginL0,
                1 => sparse.OriginL1,
                _ => sparse.OriginL2,
            };
            var location = level switch
            {
                0 => cu.SparseCloudOriginL0,
                1 => cu.SparseCloudOriginL1,
                _ => cu.SparseCloudOriginL2,
            };
            if (location >= 0)
            {
                gl.Uniform3(location, origin.X, origin.Y, origin.Z);
            }
        }

        gl.ActiveTexture(TextureUnit.Texture8);
        gl.BindTexture(
            TextureTarget.Texture3D,
            sparse.AtlasTexture);
        gl.ActiveTexture(TextureUnit.Texture9);
        gl.BindTexture(
            TextureTarget.Texture3D,
            sparse.PageTableL0);
        gl.ActiveTexture(TextureUnit.Texture10);
        gl.BindTexture(
            TextureTarget.Texture3D,
            sparse.PageTableL1);
        gl.ActiveTexture(TextureUnit.Texture11);
        gl.BindTexture(
            TextureTarget.Texture3D,
            sparse.PageTableL2);
    }

    private void BindSparseCloudRepairTraversalCq46(
        GL gl,
        GlShaderProgram program,
        in CloudRepairUniformLocs ru)
    {
        var bindings = CreateActiveSparseCloudBindingsCq46();
        var active = bindings is { IsValid: true };
        var sparse = active ? bindings!.Value : default;
        SetIntOnProgramLoc(
            program,
            ru.HasSparseCloudTraversal,
            active ? 1 : 0);
        SetIntOnProgramLoc(program, ru.SparseCloudAtlas, 8);
        SetIntOnProgramLoc(program, ru.SparseCloudPageL0, 9);
        SetIntOnProgramLoc(program, ru.SparseCloudPageL1, 10);
        SetIntOnProgramLoc(program, ru.SparseCloudPageL2, 11);
        SetInt3OnProgramLoc(
            program,
            ru.SparseCloudOriginL0,
            sparse.OriginL0.X,
            sparse.OriginL0.Y,
            sparse.OriginL0.Z);
        SetInt3OnProgramLoc(
            program,
            ru.SparseCloudOriginL1,
            sparse.OriginL1.X,
            sparse.OriginL1.Y,
            sparse.OriginL1.Z);
        SetInt3OnProgramLoc(
            program,
            ru.SparseCloudOriginL2,
            sparse.OriginL2.X,
            sparse.OriginL2.Y,
            sparse.OriginL2.Z);
        gl.ActiveTexture(TextureUnit.Texture8);
        gl.BindTexture(TextureTarget.Texture3D, sparse.AtlasTexture);
        gl.ActiveTexture(TextureUnit.Texture9);
        gl.BindTexture(TextureTarget.Texture3D, sparse.PageTableL0);
        gl.ActiveTexture(TextureUnit.Texture10);
        gl.BindTexture(TextureTarget.Texture3D, sparse.PageTableL1);
        gl.ActiveTexture(TextureUnit.Texture11);
        gl.BindTexture(TextureTarget.Texture3D, sparse.PageTableL2);
    }

    private void BindCloudLightCacheUniforms(
        GL gl,
        GlShaderProgram program,
        in CloudUniformLocs cu,
        int cloudQuality)
    {
        var cache = _cloudLightCache;
        var cachePermitted = cloudQuality >= 2 &&
            cache is { IsValid: true };
        var hasNear = cachePermitted && cache!.Near.IsGenerated;
        var hasFar = cachePermitted && cache!.Far.IsGenerated;
        var hasAny = hasNear || hasFar;

        var basis = hasNear
            ? cache!.Near.Transform.Basis
            : hasFar
                ? cache!.Far.Transform.Basis
                : new PreviewCloudLightBasis(
                    Vector3.UnitX,
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    PreviewCloudLightReferenceAxis.WorldUp);
        var nearTransform = hasNear
            ? cache!.Near.GetSamplingTransform(
                _cloudLightCurrentWindOffset,
                _cloudLightWindPeriod)
            : default;
        var farTransform = hasFar
            ? cache!.Far.GetSamplingTransform(
                _cloudLightCurrentWindOffset,
                _cloudLightWindPeriod)
            : default;

        SetVec3OnProgramLoc(program, cu.CloudLightBasisRight, basis.Right);
        SetVec3OnProgramLoc(program, cu.CloudLightBasisUp, basis.Up);
        SetVec3OnProgramLoc(program, cu.CloudLightBasisForward, basis.Forward);
        SetVec2OnProgramLoc(
            program,
            cu.CloudLightNearPlaneCenter,
            hasNear
                ? new Vector2(
                    nearTransform.PlaneCenterX,
                    nearTransform.PlaneCenterY)
                : Vector2.Zero);
        SetVec2OnProgramLoc(
            program,
            cu.CloudLightFarPlaneCenter,
            hasFar
                ? new Vector2(
                    farTransform.PlaneCenterX,
                    farTransform.PlaneCenterY)
                : Vector2.Zero);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLightNearWorldSpan,
            hasNear ? nearTransform.Profile.WorldSpan : 1f);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLightFarWorldSpan,
            hasFar ? farTransform.Profile.WorldSpan : 1f);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLightNearDepthMin,
            hasNear ? nearTransform.LightDepthMin : 0f);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLightFarDepthMin,
            hasFar ? farTransform.LightDepthMin : 0f);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLightNearDepthSpan,
            hasNear ? nearTransform.LightDepthSpan : 1f);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLightFarDepthSpan,
            hasFar ? farTransform.LightDepthSpan : 1f);
        SetIntOnProgramLoc(
            program,
            cu.CloudLightNearDepth,
            hasNear ? nearTransform.Profile.Depth : 1);
        SetIntOnProgramLoc(
            program,
            cu.CloudLightFarDepth,
            hasFar ? farTransform.Profile.Depth : 1);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLightNearOverlap,
            cachePermitted
                ? cache!.Profile.NearOverlapFraction
                : PreviewCloudLightingCacheProfiles.NearOverlapFraction);
        SetIntOnProgramLoc(program, cu.HasCloudLightNear, hasNear ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HasCloudLightFar, hasFar ? 1 : 0);
        var shading = PreviewCloudLightingShadingProfiles.Default;
        SetVec3OnProgramLoc(
            program,
            cu.CloudScatterOctave1,
            shading.Octave1);
        SetVec3OnProgramLoc(
            program,
            cu.CloudScatterOctave2,
            shading.Octave2);
        SetFloatOnProgramLoc(
            program,
            cu.CloudScatterEnergyClamp,
            shading.ScatteredEnergyClamp);
        SetFloatOnProgramLoc(
            program,
            cu.CloudCachedSkyVisibilityFloor,
            shading.CachedSkyVisibilityFloor);
        SetVec3OnProgramLoc(
            program,
            cu.CloudGroundBounceColor,
            _cloudGroundBounceColorLinear);
        SetFloatOnProgramLoc(
            program,
            cu.CloudGroundBounceStrength,
            shading.GroundBounceStrength);
        SetIntOnProgramLoc(
            program,
            cu.CloudLocalConeTapCount,
            hasAny ? cache!.Profile.LocalConeTapCount : 0);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLocalConeRange,
            hasAny ? cache!.Profile.Near.TexelWorldSize : 0f);
        SetFloatOnProgramLoc(
            program,
            cu.CloudLocalConeOpticalDepthScale,
            shading.LocalConeOpticalDepthScale);

        gl.ActiveTexture(TextureUnit.Texture6);
        gl.BindTexture(
            TextureTarget.Texture2DArray,
            hasNear ? cache!.Near.ArrayTextureHandle : 0);
        gl.ActiveTexture(TextureUnit.Texture7);
        gl.BindTexture(
            TextureTarget.Texture2DArray,
            hasFar ? cache!.Far.ArrayTextureHandle : 0);

        if (_cloudLightingCachePlan.Profile.IsEnabled)
        {
            _cloudLightingCachePlan = _cloudLightingCachePlan with
            {
                ActiveRuntimePath = hasAny
                    ? PreviewCloudLightingCacheGenerationPath.CacheSampling
                    : PreviewCloudLightingCacheGenerationPath.ShortMarch,
            };
        }
    }

    private void BindCloudGroundTransmittanceUniforms(
        GL gl,
        GlShaderProgram program,
        in PreviewRenderSettingsSnapshot settings,
        int textureUnit,
        int textureLocation,
        int hasLocation,
        int basisRightLocation,
        int basisUpLocation,
        int planeCenterLocation,
        int worldSpanLocation,
        int texelSizeLocation)
    {
        var target = _cloudGroundTransmittanceTarget;
        var available = settings.EnableVolumetricClouds &&
            settings.CloudDebugView == PreviewCloudDebugView.Off &&
            target is { IsPublished: true } &&
            target.IsCurrent(_cloudLightCache);
        var transform = available
            ? target!.GetSamplingTransform(
                _cloudLightCurrentWindOffset,
                _cloudLightWindPeriod)
            : default;
        var basis = available
            ? transform.Basis
            : new PreviewCloudLightBasis(
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                PreviewCloudLightReferenceAxis.WorldUp);
        var texelSize = available
            ? target!.Profile.TexelSize
            : Vector2.One;

        SetIntOnProgramLoc(program, hasLocation, available ? 1 : 0);
        SetVec3OnProgramLoc(program, basisRightLocation, basis.Right);
        SetVec3OnProgramLoc(program, basisUpLocation, basis.Up);
        SetVec2OnProgramLoc(
            program,
            planeCenterLocation,
            available
                ? new Vector2(
                    transform.PlaneCenterX,
                    transform.PlaneCenterY)
                : Vector2.Zero);
        SetFloatOnProgramLoc(
            program,
            worldSpanLocation,
            available ? transform.Profile.WorldSpan : 1f);
        SetVec2OnProgramLoc(
            program,
            texelSizeLocation,
            texelSize);
        SetIntOnProgramLoc(
            program,
            textureLocation,
            textureUnit);
        gl.ActiveTexture(TextureUnit.Texture0 + textureUnit);
        gl.BindTexture(
            TextureTarget.Texture2D,
            available ? target!.TextureHandle : 0);
    }

    private bool ResolveCloudTemporal(
        GlRenderFrame frame,
        Matrix4x4 invViewProj,
        Vector3 windOffset,
        Vector2 cirrusWindOffset,
        PreviewVolumetricQuality.Profile profile)
    {
        if (_cloudTemporalProgram is not { IsValid: true } program ||
            _cloudRenderTarget is not { IsValid: true } current ||
            _cloudResolveTarget is not { IsValid: true } resolve ||
            _cloudHistoryTarget is not { IsValid: true } history)
        {
            return false;
        }

        var gl = frame.Gl;
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorDepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);

        resolve.BindDraw();
        resolve.Clear();
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(false);
        gl.ColorMask(true, true, true, true);
        program.Use();

        var tu = _cloudTemporalUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, current.ColorTextureHandle);
        SetIntOnProgramLoc(program, tu.CurrentClouds, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, current.DataTextureHandle);
        SetIntOnProgramLoc(program, tu.CurrentCloudData, 1);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, history.ColorTextureHandle);
        SetIntOnProgramLoc(program, tu.HistoryClouds, 2);
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, history.DataTextureHandle);
        SetIntOnProgramLoc(program, tu.HistoryCloudData, 3);
        gl.ActiveTexture(TextureUnit.Texture4);
        gl.BindTexture(TextureTarget.Texture2D, history.MomentTextureHandle);
        SetIntOnProgramLoc(program, tu.HistoryCloudMoments, 4);

        SetMatrixOnProgramLoc(program, tu.InvViewProj, invViewProj);
        SetMatrixOnProgramLoc(program, tu.PrevViewProj, _cloudPrevViewProj);
        SetVec3OnProgramLoc(program, tu.CameraPos, frame.Eye);
        SetVec3OnProgramLoc(program, tu.PrevCameraPos, _cloudPrevCameraPos);
        var windPeriod = Math.Max(frame.Settings.CloudVolumeSize, 8f) * 16f;
        var windDelta = ComputeWrappedCloudWindDelta(windOffset, _cloudPrevWindOffset, windPeriod);
        SetVec2OnProgramLoc(program, tu.WindDelta, new Vector2(windDelta.X, windDelta.Z));
        SetVec2OnProgramLoc(program, tu.CirrusWindDelta, cirrusWindOffset - _cloudPrevCirrusWindOffset);
        SetVec2OnProgramLoc(program, tu.TexelSize,
            new Vector2(1f / Math.Max(current.Width, 1), 1f / Math.Max(current.Height, 1)));
        SetFloatOnProgramLoc(program, tu.TemporalWeight,
            PreviewVolumetricQuality.EffectivePassTemporalWeight(profile.CloudTemporalWeight, frame.Settings));
        var momentProfile = PreviewCloudTemporalMoments.Resolve(profile.CloudQuality);
        var useMoments = momentProfile.Enabled &&
            _cloudRenderFormatProfile.UsesTemporalMoments &&
            resolve.MomentTextureHandle != 0 &&
            history.MomentTextureHandle != 0;
        SetFloatOnProgramLoc(program, tu.MomentSigma, momentProfile.Sigma);
        SetFloatOnProgramLoc(program, tu.MomentMinBand, momentProfile.MinimumBand);
        var historyConfidence = useMoments
            ? PreviewCloudTemporalMoments.ResolveConfidence(_cloudHistoryConfidenceFrames)
            : 1f;
        SetFloatOnProgramLoc(program, tu.HistoryConfidence, historyConfidence);
        var windDelta2 = new Vector2(windDelta.X, windDelta.Z);
        var cameraDelta = Vector3.Distance(frame.Eye, _cloudPrevCameraPos);
        _cloudIdleDenoiseLatched = PreviewCloudTemporalLowAlphaWeight.UpdateIdleLatch(
            _cloudIdleDenoiseLatched,
            cameraDelta,
            historyConfidence);
        var stabilityTarget = _cloudIdleDenoiseLatched
            ? 1f
            : PreviewCloudTemporalLowAlphaWeight.EvaluateStability(
                cameraDelta,
                windDelta2.Length(),
                historyConfidence);
        _cloudTemporalStability = PreviewCloudTemporalLowAlphaWeight.EaseStability(
            _cloudTemporalStability,
            stabilityTarget);
        SetFloatOnProgramLoc(program, tu.TemporalStability, _cloudTemporalStability);
        SetIntOnProgramLoc(program, tu.HasHistory, _cloudHistoryValid ? 1 : 0);
        SetIntOnProgramLoc(program, tu.HasMoments, useMoments ? 1 : 0);
        SetIntOnProgramLoc(program, tu.CloudDataDirect,
            _cloudRenderFormatProfile.UsesDirectMetadata ? 1 : 0);

        FlushPendingGlErrors(gl);
        gl.BindVertexArray(_cloudQuadVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        var resolveError = gl.GetError();
        gl.BindVertexArray(0);

        gl.DepthMask(priorDepthMask);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorBlend) { gl.Enable(EnableCap.Blend); } else { gl.Disable(EnableCap.Blend); }
        if (priorDepthTest) { gl.Enable(EnableCap.DepthTest); } else { gl.Disable(EnableCap.DepthTest); }
        if (priorScissor) { gl.Enable(EnableCap.ScissorTest); } else { gl.Disable(EnableCap.ScissorTest); }

        if (resolveError != GLEnum.NoError)
        {
            throw new InvalidOperationException($"Cloud temporal resolve produced GL error {resolveError}.");
        }

        return true;
    }

    private static Vector3 ComputeWrappedCloudWindDelta(Vector3 current, Vector3 previous, float period)
    {
        static float ShortestDelta(float value, float range)
        {
            var half = range * 0.5f;
            if (value > half) { return value - range; }
            if (value < -half) { return value + range; }
            return value;
        }

        var delta = current - previous;
        return new Vector3(ShortestDelta(delta.X, period), 0f, ShortestDelta(delta.Z, period));
    }

    private int ComputeCloudHistorySettingsHash(
        in PreviewRenderSettingsSnapshot settings,
        float verticalFieldOfViewRadians)
    {
        var hash = new HashCode();
        hash.Add(settings.VolumetricQuality);
        hash.Add(settings.CloudDensity);
        hash.Add(settings.CloudVolumeSize);
        hash.Add(settings.CloudLayerHeight);
        hash.Add(settings.CloudVolumeHeight);
        hash.Add(settings.CloudCoverageScale);
        hash.Add(settings.CloudWindSpeed);
        hash.Add(settings.CloudWindHeadingDegrees);
        hash.Add(settings.CloudCirrusStrength);
        hash.Add(settings.CloudCumulusLayerCount);
        hash.Add(settings.CloudInterDeckGap);
        hash.Add(settings.CloudLayerHeightVariance);
        hash.Add(settings.CloudUpperThicknessScale);
        hash.Add(settings.CloudUpperCoverageScale);
        hash.Add(settings.CloudUpperDensityScale);
        hash.Add(settings.CloudUpperWindSpeedScale);
        hash.Add(settings.CloudCirrusGap);
        hash.Add(settings.CloudCirrusThickness);
        hash.Add(settings.CloudStyleBias);
        hash.Add(settings.CloudMarchStepOverride);
        hash.Add(settings.CloudFreezeWind);
        hash.Add(settings.AtmosphereSunIntensity);
        hash.Add(verticalFieldOfViewRadians);
        hash.Add(PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetVersion);
        hash.Add(_cloudDensityAssetVersion);
        hash.Add((int)_cloudDensityBackendSelection.ActiveBackend);
        hash.Add(
            _cloudDensityBackendSelection.ActiveBackend ==
                PreviewCloudDensityBackend.SparseVoxel
                ? _sparseCloudActiveIdentity.Signature
                : 0);
        return hash.ToHashCode();
    }

    private void UpdateCloudDensityBackendSelectionCq40(int volumetricQuality)
    {
        var selection = PreviewSparseCloudBackendPolicy.Select(
            volumetricQuality,
            _glCapabilities,
            PreviewSparseCloudBackendPolicy.IsForceProceduralLayerEnabled(),
            _sparseCloudResourcesReady,
            _sparseCloudRuntimeFaulted);
        if (_cloudDensityBackendSelectionInitialized &&
            selection == _cloudDensityBackendSelection)
        {
            return;
        }

        var activeBackendChanged =
            _cloudDensityBackendSelectionInitialized &&
            selection.ActiveBackend !=
            _cloudDensityBackendSelection.ActiveBackend;
        _cloudDensityBackendSelection = selection;
        _cloudDensityBackendSelectionInitialized = true;
        if (activeBackendChanged)
        {
            InvalidateCloudTemporalHistory();
            var sparseLightingAlreadyPrepared =
                selection.ActiveBackend ==
                    PreviewCloudDensityBackend.SparseVoxel &&
                IsSparseCloudLightingPreparedCq46(
                    _sparseCloudActiveIdentity);
            if (!sparseLightingAlreadyPrepared)
            {
                _cloudLightCache?.Near.InvalidateGeneration();
                _cloudLightCache?.Far.InvalidateGeneration();
                ResetCloudLightingCacheLifecycleCq36();
            }
        }

        var backendDiagnostic = selection.FormatDiagnostic();
        if (_cloudLifecycleDiagnosticGate.TryReport(
                "backend|" + backendDiagnostic))
        {
            EmitDiagnostic(
                "[3D preview] CQ4.0 cloud density backend: " +
                backendDiagnostic + ".");
        }
    }

    private void PrepareSparseCloudActivationCq46()
    {
        if (!_sparseCloudPreparedIdentity.IsValid)
        {
            DemoteSparseCloudActivationCq46();
            return;
        }

        if (_sparseCloudPreparedIdentity == _sparseCloudActiveIdentity)
        {
            return;
        }

        // Soft-hold: keep the prior active sparse view sampleable while a newer
        // same-origin prepared identity catches lighting. Hard-demoting on every
        // residency revision causes procedural↔sparse thrash and light-cache spikes.
        if (_sparseCloudResourcesReady &&
            PreviewSparseCloudActivationPolicy.CanSoftHold(
                _sparseCloudActiveIdentity,
                _sparseCloudPreparedIdentity))
        {
            _sparseCloudActivationDiagnostic =
                "soft-hold-cq4.6/" +
                _sparseCloudActiveIdentity.FormatDiagnostic() +
                "/prepared-" +
                _sparseCloudPreparedIdentity.FormatDiagnostic();
            return;
        }

        DemoteSparseCloudActivationCq46();
    }

    private void DemoteSparseCloudActivationCq46()
    {
        if (_sparseCloudResourcesReady ||
            _sparseCloudActiveIdentity.IsValid)
        {
            _sparseCloudCounters.NoteIdentityDemotion();
        }

        _sparseCloudResourcesReady = false;
        _sparseCloudVolumeResources?.SetSamplingReadyCq46(false);
    }

    private void FinalizeSparseCloudActivationCq46(int volumetricQuality)
    {
        if (PreviewVolumetricQuality.Clamp(volumetricQuality) !=
                PreviewVolumetricQuality.Cinematic ||
            PreviewSparseCloudBackendPolicy.IsForceProceduralLayerEnabled() ||
            _sparseCloudRuntimeFaulted ||
            !_sparseCloudPreparedIdentity.IsValid)
        {
            DemoteSparseCloudActivationCq46();
            return;
        }

        if (!IsSparseCloudResidencyReadyForActivation(
                out var residencyDiagnostic))
        {
            if (!_sparseCloudResourcesReady)
            {
                _sparseCloudVolumeResources?.SetSamplingReadyCq46(false);
                _sparseCloudActivationDiagnostic =
                    residencyDiagnostic + "/" +
                    _sparseCloudPreparedIdentity.FormatDiagnostic();
            }

            return;
        }

        if (!IsSparseCloudLightingPreparedCq46(
                _sparseCloudPreparedIdentity))
        {
            if (!_sparseCloudResourcesReady)
            {
                _sparseCloudVolumeResources?.SetSamplingReadyCq46(false);
                _sparseCloudCounters.NoteCq3PreparationStall();
                _sparseCloudActivationDiagnostic =
                    "waiting-for-cq3-lighting-" +
                    _sparseCloudPreparedIdentity.FormatDiagnostic();
            }
            else
            {
                _sparseCloudCounters.NoteCq3PreparationStall();
                _sparseCloudActivationDiagnostic =
                    "soft-hold-waiting-lighting/" +
                    _sparseCloudActiveIdentity.FormatDiagnostic() +
                    "/prepared-" +
                    _sparseCloudPreparedIdentity.FormatDiagnostic();
            }

            return;
        }

        var identityChanged =
            _sparseCloudActiveIdentity !=
            _sparseCloudPreparedIdentity;
        _sparseCloudActiveIdentity = _sparseCloudPreparedIdentity;
        _sparseCloudResourcesReady = true;
        _sparseCloudVolumeResources?.SetSamplingReadyCq46(true);
        _sparseCloudPreparedLightingChaseCooldown = 0;
        _sparseCloudActivationDiagnostic =
            "active-cq4.6/" +
            _sparseCloudActiveIdentity.FormatDiagnostic() +
            "/cq1-history+repair/cq3-near+far";
        if (identityChanged)
        {
            _sparseCloudCounters.NoteIdentityPromotion();
            InvalidateCloudTemporalHistory();
            if (_cloudLifecycleDiagnosticGate.TryReport(
                    "sparse-activation|active"))
            {
                EmitDiagnostic(
                    "[3D preview] CQ4.6 sparse cloud density activated transactionally: " +
                    _sparseCloudActivationDiagnostic + ".");
            }
        }
    }

    private bool IsSparseCloudResidencyReadyForActivation() =>
        IsSparseCloudResidencyReadyForActivation(out _);

    private bool IsSparseCloudResidencyReadyForActivation(
        out string diagnostic)
    {
        var residentCount = _sparseCloudPreparedIdentity.IsValid
            ? _sparseCloudPreparedIdentity.ResidentCount
            : 0;
        var requestedCount = Math.Max(
            _sparseCloudClipmapController?.RequestedCount ?? 0,
            residentCount);
        var hasPendingGeneration =
            _sparseCloudBrickGenerator?.HasPendingGeneration ?? false;
        diagnostic = PreviewSparseCloudActivationPolicy
            .FormatResidencyWarmupDiagnostic(
                residentCount,
                requestedCount,
                hasPendingGeneration);
        return _sparseCloudPreparedIdentity.IsValid &&
               PreviewSparseCloudActivationPolicy.IsResidencyReady(
                   residentCount,
                   requestedCount,
                   hasPendingGeneration);
    }

    private bool IsSparseCloudLightingPreparedCq46(
        in PreviewSparseCloudSamplingIdentity identity)
    {
        if (_cloudLightCache is not { IsValid: true } cache)
        {
            return false;
        }

        var residentCount = identity.ResidentCount;
        var requestedCount = Math.Max(
            _sparseCloudClipmapController?.RequestedCount ?? 0,
            residentCount);
        return PreviewSparseCloudActivationPolicy.CanActivate(
            identity,
            cache.Near.IsGenerated,
            cache.Near.DensityIdentity,
            cache.Far.IsGenerated,
            cache.Far.DensityIdentity,
            residentCount,
            requestedCount,
            _sparseCloudBrickGenerator?.HasPendingGeneration ?? false);
    }

    private GlSparseCloudSamplingBindings?
        CreatePreparedSparseCloudBindingsCq46()
    {
        // Do not feed incomplete sparse residency into CQ3 light-cache generation.
        // Warmup keeps High-equivalent procedural density for both the view and the
        // cache until the residency gate opens, then both switch together.
        if (!IsSparseCloudResidencyReadyForActivation())
        {
            return null;
        }

        var target = ResolveSparseCloudLightingTargetIdentity();
        if (target == _sparseCloudPreparedIdentity &&
            _sparseCloudResourcesReady &&
            _sparseCloudActiveIdentity.IsValid &&
            target != _sparseCloudActiveIdentity)
        {
            // Amortize prepared-identity light refreshes across motion.
            _sparseCloudPreparedLightingChaseCooldown = 4;
        }

        return CreateSparseCloudBindingsCq46(
            target,
            requireActiveBackend: false);
    }

    private GlSparseCloudSamplingBindings?
        CreateActiveSparseCloudBindingsCq46() =>
        CreateSparseCloudBindingsCq46(
            _sparseCloudActiveIdentity,
            requireActiveBackend: true);

    private GlSparseCloudSamplingBindings?
        CreateSparseCloudBindingsCq46(
            in PreviewSparseCloudSamplingIdentity identity,
            bool requireActiveBackend)
    {
        var resources = _sparseCloudVolumeResources;
        if (!identity.IsValid ||
            resources is not { IsAllocated: true } ||
            _sparseCloudRuntimeFaulted ||
            (requireActiveBackend &&
             (!_sparseCloudResourcesReady ||
              _cloudDensityBackendSelection.ActiveBackend !=
                  PreviewCloudDensityBackend.SparseVoxel ||
              identity != _sparseCloudActiveIdentity)))
        {
            return null;
        }

        return new GlSparseCloudSamplingBindings(
            resources.AtlasTextureHandle,
            resources.GetActivePageTableHandle(0),
            resources.GetActivePageTableHandle(1),
            resources.GetActivePageTableHandle(2),
            identity.OriginL0,
            identity.OriginL1,
            identity.OriginL2,
            identity.Signature);
    }

    private void TryInitSparseCloudTemplateAssetsCq41()
    {
        _sparseCloudTemplateAssets = null;
        if (_glCapabilities?.CanUseSparseCloudVolumes != true)
        {
            _sparseCloudTemplateDiagnostic = "not-loaded-capability-unavailable";
            return;
        }

        if (!PreviewSparseCloudTemplateAssetLoader.TryLoad(
                out var assets,
                out var reason))
        {
            _sparseCloudTemplateDiagnostic = "invalid-" + reason;
            EmitDiagnostic(
                "[3D preview] CQ4.1 sparse cloud template library unavailable; " +
                "retaining the accepted CQ3.9 procedural layer (" + reason + ").");
            return;
        }

        _sparseCloudTemplateAssets = assets;
        _sparseCloudTemplateDiagnostic =
            $"ready-v{assets.AssetVersion}/{assets.Templates.Count}-templates/" +
            $"{assets.ByteLength}-bytes";
        EmitDiagnostic(
            "[3D preview] CQ4.1 sparse cloud template library: " +
            _sparseCloudTemplateDiagnostic +
            "; CQ4.2 resource allocation is independently gated by Cinematic quality.");
    }

    private void TryInitSparseCloudResourcesCq42(
        GL gl,
        int volumetricQuality)
    {
        if (_sparseCloudVolumeResources is { IsAllocated: true } &&
            _sparseCloudBrickGenerator is not null)
        {
            return;
        }

        _sparseCloudResourcesReady = false;
        if (PreviewVolumetricQuality.Clamp(volumetricQuality) !=
            PreviewVolumetricQuality.Cinematic)
        {
            _sparseCloudResourceDiagnostic =
                "not-allocated-quality-not-cinematic";
            return;
        }

        if (PreviewSparseCloudBackendPolicy
            .IsForceProceduralLayerEnabled())
        {
            _sparseCloudResourceDiagnostic =
                "not-allocated-debug-force-procedural";
            return;
        }

        if (_glCapabilities?.CanUseSparseCloudVolumes != true)
        {
            _sparseCloudResourceDiagnostic =
                "not-allocated-capability-unavailable";
            return;
        }

        if (_sparseCloudTemplateAssets is null)
        {
            _sparseCloudResourceDiagnostic =
                "not-allocated-template-library-unavailable";
            return;
        }

        if (_sparseCloudRuntimeFaulted ||
            _sparseCloudResourceAllocationAttempted)
        {
            return;
        }

        _sparseCloudResourceAllocationAttempted = true;
        if (!GlSparseCloudVolumeResources.TryCreate(
                gl,
                _glCapabilities,
                out var resources,
                out var reason))
        {
            _sparseCloudRuntimeFaulted = true;
            _sparseCloudResourceDiagnostic =
                "allocation-failed-" + reason;
            EmitDiagnostic(
                "[3D preview] CQ4.2 sparse cloud resource allocation failed; " +
                "retaining the accepted CQ3.9 procedural layer (" +
                reason + ").");
            return;
        }

        _sparseCloudBrickGenerationProgram = CreatePreviewComputeProgram(
            "genesis_sparse_cloud_brick_generate.comp",
            out var generationShaderError,
            "cq4.4-sparse-cloud-brick-generation");
        if (_sparseCloudBrickGenerationProgram is not { IsValid: true })
        {
            resources!.Dispose();
            _sparseCloudBrickGenerationProgram?.Dispose();
            _sparseCloudBrickGenerationProgram = null;
            _sparseCloudRuntimeFaulted = true;
            _sparseCloudResourceDiagnostic =
                "generation-shader-failed-" +
                TrimShaderDiagnostic(generationShaderError);
            EmitDiagnostic(
                "[3D preview] CQ4.4 sparse brick generator unavailable; " +
                "retaining the accepted CQ3.9 procedural layer (" +
                _sparseCloudResourceDiagnostic + ").");
            return;
        }

        if (!GlSparseCloudBrickGenerator.TryCreate(
                gl,
                _sparseCloudBrickGenerationProgram,
                _sparseCloudTemplateAssets.Value,
                out var generator,
                out var generatorReason) ||
            generator is null)
        {
            resources!.Dispose();
            _sparseCloudBrickGenerationProgram?.Dispose();
            _sparseCloudBrickGenerationProgram = null;
            _sparseCloudRuntimeFaulted = true;
            _sparseCloudResourceDiagnostic =
                "generation-init-failed-" + generatorReason;
            EmitDiagnostic(
                "[3D preview] CQ4.4 sparse brick generator unavailable; " +
                "retaining the accepted CQ3.9 procedural layer (" +
                _sparseCloudResourceDiagnostic + ").");
            return;
        }

        _sparseCloudVolumeResources = resources;
        _sparseCloudBrickGenerator = generator;
        // The validated CPU blobs are no longer needed after their RG8 array upload.
        // The accounting reserves one persistent template copy, now owned by the GPU.
        _sparseCloudTemplateAssets = null;
        _sparseCloudBrickAllocator =
            new PreviewSparseCloudBrickAllocator();
        _sparseCloudClipmapController =
            new PreviewSparseCloudClipmapController();
        _sparseCloudResourceDiagnostic =
            resources!.FormatDiagnostic() + ";" +
            generator.FormatDiagnostic() + ";" +
            _sparseCloudBrickAllocator.FormatDiagnostic() +
            ";backendActivation=warming-cq4.6-transaction";
        EmitDiagnostic(
            "[3D preview] CQ4.2 sparse cloud resources allocated: " +
            _sparseCloudResourceDiagnostic + ".");
    }

    private void UpdateSparseCloudClipmapsCq43(
        ref GlRenderFrame frame,
        float layerWorldY)
    {
        if (PreviewVolumetricQuality.Clamp(
                frame.Settings.VolumetricQuality) !=
            PreviewVolumetricQuality.Cinematic)
        {
            return;
        }

        var resources = _sparseCloudVolumeResources;
        var controller = _sparseCloudClipmapController;
        var allocator = _sparseCloudBrickAllocator;
        var generator = _sparseCloudBrickGenerator;
        if (resources is null ||
            controller is null ||
            allocator is null ||
            generator is null ||
            !resources.IsAllocated ||
            _sparseCloudRuntimeFaulted)
        {
            return;
        }

        if (!resources.TryPublishCompleted(
                out var published,
                out var publicationReason))
        {
            FaultSparseCloudRuntimeCq43(
                "page-publication-" + publicationReason);
            return;
        }

        if (!generator.TryCollectCompleted(
                out var completed,
                out var generationCompletionReason))
        {
            FaultSparseCloudRuntimeCq43(
                "brick-generation-completion-" +
                generationCompletionReason);
            return;
        }

        var residentPublished = 0;
        var residentOrphanRecycled = 0;
        foreach (var record in completed)
        {
            if (controller.TryMarkResident(
                    record.Key,
                    record.PhysicalBrickIndex))
            {
                if (!allocator.MarkResident(
                        record.Key,
                        generator.GenerationId,
                        _sparseCloudControlFrame))
                {
                    FaultSparseCloudRuntimeCq43(
                        "brick-residency-transition-" + record.Key);
                    return;
                }

                residentPublished++;
                _sparseCloudPendingRetire.Remove(record.Key);
                continue;
            }

            // CQ4.7: the clipmap retired this page while generation was in flight.
            // The fence already completed, so recycle the physical slot immediately.
            if (!allocator.TryRecycleOrphanedGeneration(
                    record.Key,
                    generator.GenerationId,
                    _sparseCloudControlFrame))
            {
                FaultSparseCloudRuntimeCq43(
                    "brick-orphan-recycle-" + record.Key);
                return;
            }

            residentOrphanRecycled++;
            _sparseCloudPendingRetire.Remove(record.Key);
        }

        if (residentOrphanRecycled > 0)
        {
            _sparseCloudCounters.NoteOrphanedGenerationRecycled(
                residentOrphanRecycled);
        }

        var viewDirection = frame.LookTarget - frame.Eye;
        ReadOnlySpan<Vector4> frustumPlanes =
            frame.CameraFrustumValid
                ? frame.CameraFrustumPlanes
                : ReadOnlySpan<Vector4>.Empty;
        var update = controller.Update(
            frame.Eye,
            viewDirection,
            layerWorldY +
            Math.Max(frame.Settings.CloudVolumeHeight, 0.01f) * 0.5f,
            frustumPlanes,
            _sparseCloudControlFrame,
            generator.HasPendingGeneration
                ? 0
                : PreviewSparseCloudVolumeContract
                    .MaximumEnteringBricksPerFrame);
        var generationFrame = _sparseCloudControlFrame;
        _sparseCloudControlFrame =
            _sparseCloudControlFrame == int.MaxValue
                ? 0
                : _sparseCloudControlFrame + 1;

        foreach (var retiredKey in update.Retired)
        {
            _sparseCloudPendingRetire.Add(retiredKey);
        }

        if (published)
        {
            allocator.SyncActiveReferences(
                resources.PublishedPhysicalBrickIndices);
            allocator.ReleaseEligibleRetired();
        }

        var pendingRetireReleased = 0;
        if (_sparseCloudPendingRetire.Count > 0)
        {
            var pendingKeys = _sparseCloudPendingRetire.ToArray();
            foreach (var key in pendingKeys)
            {
                if (!allocator.TryGet(key, out _))
                {
                    _sparseCloudPendingRetire.Remove(key);
                    continue;
                }

                if (allocator.TryRecycleUnreferenced(key))
                {
                    _sparseCloudPendingRetire.Remove(key);
                    pendingRetireReleased++;
                }
            }
        }

        var dispatched = 0;
        var generationDispatchReason = "no-entering-bricks";
        if (update.Entering.Count > 0)
        {
            var windTime =
                frame.Settings.CloudFreezeWind ? 0.0 : frame.RenderTime;
            var generationInputs =
                new PreviewSparseCloudBrickGenerationInputs(
                    generationFrame,
                    layerWorldY,
                    layerWorldY +
                    Math.Max(frame.Settings.CloudVolumeHeight, 0.01f),
                    frame.Settings.CloudDensity,
                    frame.Settings.CloudCoverageScale,
                    frame.Settings.CloudVolumeSize,
                    ComputeCloudWindOffset(windTime, frame.Settings),
                    _cloudCoverageTex?.Id ?? 0,
                    frame.Settings.CloudStyleBias);
            using (BeginPassTimerScope(GlGpuTimerScope.SparseBrickGen))
            {
                if (!generator.TryDispatch(
                        resources,
                        allocator,
                        update.Entering,
                        generationInputs,
                        out dispatched,
                        out generationDispatchReason))
                {
                    FaultSparseCloudRuntimeCq43(
                        "brick-generation-dispatch-" +
                        generationDispatchReason);
                    return;
                }
            }
        }

        if (!resources.HasPendingPublication &&
            controller.TableRevision != resources.PublishedPlanRevision &&
            !resources.TryStagePageTables(
                controller,
                generator.GenerationId,
                out var stagingReason))
        {
            FaultSparseCloudRuntimeCq43(
                "page-staging-" + stagingReason);
            return;
        }

        _sparseCloudTraversalPrepared =
            PreviewSparseCloudSamplingIdentity.TryCreate(
                resources.PublishedAtlasGenerationId,
                generator.HasPendingGeneration,
                resources.PublishedGenerationId,
                resources.PublishedPlanRevision,
                resources.HasPendingPublication,
                controller.TableRevision,
                resources.PublishedResidentCount,
                resources.GetActiveOrigin(0),
                resources.GetActiveOrigin(1),
                resources.GetActiveOrigin(2),
                out _sparseCloudPreparedIdentity,
                out var identityReason);
        _sparseCloudTraversalDiagnostic =
            _sparseCloudTraversalPrepared
                ? "prepared-cq4.6/" +
                  _sparseCloudPreparedIdentity.FormatDiagnostic()
                : $"warming/generation-{generator.GenerationId}/" +
                  $"publication-{resources.PublishedGenerationId}/" +
                  $"resident-{controller.ResidentCount}/" +
                  $"identity={identityReason}";
        _sparseCloudCounters.CaptureResidency(
            allocator,
            controller,
            _sparseCloudPendingRetire.Count);
        _sparseCloudResourceDiagnostic =
            resources.FormatDiagnostic() + ";" +
            controller.FormatDiagnostic() + ";" +
            allocator.FormatDiagnostic() + ";" +
            generator.FormatDiagnostic() + ";" +
            $"lastUpdate=frame-{update.Frame}/entering-{update.Entering.Count}/" +
            $"retired-{update.Retired.Count}/originChanged-{update.OriginChanged}/" +
            $"teleport-{update.Teleport};" +
            $"lastGeneration=completed-{completed.Count}/mapped-{residentPublished}/" +
            $"orphanRecycled-{residentOrphanRecycled}/" +
            $"pendingRetireReleased-{pendingRetireReleased}/" +
            $"dispatched-{dispatched}/{generationDispatchReason}/" +
            $"{generationCompletionReason};" +
            $"lastPublication={(published ? publicationReason : "none")};" +
            $"counters={_sparseCloudCounters.FormatDiagnostic()};" +
            $"backendActivation={_sparseCloudActivationDiagnostic}";

        if (_sparseCloudPreparedLightingChaseCooldown > 0)
        {
            _sparseCloudPreparedLightingChaseCooldown--;
        }
    }

    internal void InjectSparseCloudFaultForTests(
        PreviewSparseCloudFaultInjectPoint point)
    {
        _sparseCloudFaultInject = point;
        _sparseCloudVolumeResources?.SetFaultInjectForTests(point);
        _sparseCloudBrickGenerator?.SetFaultInjectForTests(point);
        if (point == PreviewSparseCloudFaultInjectPoint.ContextLoss)
        {
            FaultSparseCloudRuntimeCq43("injected-context-loss");
        }
    }

    internal string FormatSparseCloudCountersForTests() =>
        _sparseCloudCounters.FormatDiagnostic();

    private void FaultSparseCloudRuntimeCq43(string reason)
    {
        _sparseCloudRuntimeFaulted = true;
        _sparseCloudResourcesReady = false;
        _sparseCloudTraversalPrepared = false;
        _sparseCloudPreparedIdentity = default;
        _sparseCloudActiveIdentity = default;
        _sparseCloudPreparedLightingChaseCooldown = 0;
        _sparseCloudActivationDiagnostic =
            "runtime-failed-" + reason;
        _sparseCloudTraversalDiagnostic = "runtime-failed-" + reason;
        _sparseCloudResourceDiagnostic = "runtime-failed-" + reason;
        _sparseCloudPendingRetire.Clear();
        _sparseCloudVolumeResources?.Dispose();
        _sparseCloudVolumeResources = null;
        _sparseCloudBrickGenerator?.Dispose();
        _sparseCloudBrickGenerator = null;
        _sparseCloudBrickGenerationProgram?.Dispose();
        _sparseCloudBrickGenerationProgram = null;
        _sparseCloudBrickAllocator = null;
        _sparseCloudClipmapController = null;
        EmitDiagnostic(
            "[3D preview] CQ4 sparse control/generation failed; " +
            "retaining the accepted CQ3.9 procedural layer (" +
            reason + ").");
    }

    internal static bool CanUseCloudStbn(
        bool useOpenGlEs,
        int cloudQuality,
        bool assetAvailable) =>
        !useOpenGlEs &&
        cloudQuality >= PreviewVolumetricQuality.High &&
        assetAvailable;

    internal static bool CanUseCq2V2DensityProfile(
        bool useOpenGlEs,
        bool shaderProfileReady) =>
        !useOpenGlEs && shaderProfileReady;

    private static float ComputeCloudDirectDiscCosEdge(in GlRenderFrame frame)
    {
        PreviewSunScreenProjection.Compute(
            frame.Eye,
            frame.WorldLightDir,
            frame.View,
            frame.Proj,
            frame.Vw / (float)Math.Max(frame.Vh, 1),
            frame.Settings.GodRayConeScale,
            frame.Settings.AtmosphereSunDiscSize,
            out _,
            out _,
            out _,
            out var cosDiscEdge);
        return cosDiscEdge;
    }

    private static float ComputeCloudSunDiscVisibility(in GlRenderFrame frame)
    {
        if (frame.Settings.AtmosphereSunDiscStrength <= 1e-4f ||
            frame.Settings.AtmosphereSunDiscBrightness <= 1e-4f)
        {
            return 0f;
        }

        var lightLengthSquared = frame.WorldLightDir.LengthSquared();
        if (lightLengthSquared <= 1e-12f)
        {
            return 0f;
        }

        static float Smoothstep(float edge0, float edge1, float value)
        {
            var t = Math.Clamp(
                (value - edge0) / Math.Max(edge1 - edge0, 1e-6f),
                0f,
                1f);
            return t * t * (3f - 2f * t);
        }

        var towardSun = -frame.WorldLightDir / MathF.Sqrt(lightLengthSquared);
        var dayAmount =
            Smoothstep(-0.04f, 0.22f, towardSun.Y) *
            Smoothstep(0.08f, 2f, frame.Settings.AtmosphereSunIntensity);
        return Smoothstep(0f, 0.06f, dayAmount) *
            (0.35f + 0.65f * dayAmount);
    }

    /// <summary>
    /// World-space wind drift for the cloud field. Components wrap at the v2 primary
    /// weather period (volumeSize * 16). That is also an integer multiple of the v1
    /// weather and detail periods, so profile fallback cannot introduce a wrap snap.
    /// </summary>
    private static Vector3 ComputeCloudWindOffset(double renderTime, in PreviewRenderSettingsSnapshot settings)
    {
        var period = Math.Max(settings.CloudVolumeSize, 8f) * 16f;
        var heading = settings.CloudWindHeadingDegrees * (MathF.PI / 180f);
        var travel = renderTime * settings.CloudWindSpeed;
        var wx = (float)((MathF.Cos(heading) * travel) % period);
        var wz = (float)((MathF.Sin(heading) * travel) % period);
        return new Vector3(wx, 0f, wz);
    }

    private static Vector3 ComputeUpperCloudWindOffset(
        double renderTime,
        in PreviewRenderSettingsSnapshot settings)
    {
        var period = Math.Max(settings.CloudVolumeSize, 8f) * 16f;
        var heading =
            (settings.CloudWindHeadingDegrees +
             PreviewCloudLayerEnvelope.UpperDeckHeadingVeerDegrees) *
            (MathF.PI / 180f);
        var travel = renderTime *
            settings.CloudWindSpeed *
            Math.Max(settings.CloudUpperWindSpeedScale, 0f);
        var wx = (float)((MathF.Cos(heading) * travel) % period);
        var wz = (float)((MathF.Sin(heading) * travel) % period);
        return new Vector3(wx, 0f, wz);
    }

    private void ApplyCloudLayerEnvelopeUniforms(
        GlShaderProgram program,
        int cumulusLayerCountLoc,
        int interDeckGapLoc,
        int heightVarianceLoc,
        int upperThicknessScaleLoc,
        int upperCoverageScaleLoc,
        int upperDensityScaleLoc,
        int upperWindOffsetLoc,
        int cirrusGapLoc,
        int cirrusThicknessLoc,
        int styleBiasLoc,
        in PreviewRenderSettingsSnapshot settings,
        Vector3 upperWindOffset)
    {
        SetIntOnProgramLoc(
            program,
            cumulusLayerCountLoc,
            PreviewCloudLayerEnvelope.ClampDeckCount(settings.CloudCumulusLayerCount));
        SetFloatOnProgramLoc(program, interDeckGapLoc, settings.CloudInterDeckGap);
        SetFloatOnProgramLoc(program, heightVarianceLoc, settings.CloudLayerHeightVariance);
        SetFloatOnProgramLoc(program, upperThicknessScaleLoc, settings.CloudUpperThicknessScale);
        SetFloatOnProgramLoc(program, upperCoverageScaleLoc, settings.CloudUpperCoverageScale);
        SetFloatOnProgramLoc(program, upperDensityScaleLoc, settings.CloudUpperDensityScale);
        SetVec3OnProgramLoc(program, upperWindOffsetLoc, upperWindOffset);
        SetFloatOnProgramLoc(program, cirrusGapLoc, settings.CloudCirrusGap);
        SetFloatOnProgramLoc(program, cirrusThicknessLoc, settings.CloudCirrusThickness);
        SetIntOnProgramLoc(program, styleBiasLoc, Math.Clamp(settings.CloudStyleBias, 0, 4));
    }

    /// <summary>
    /// High-altitude wind for the cirrus sheet: faster than the cumulus layer and slightly
    /// veered, as real upper winds are. The cirrus noise is procedural (non-tiling), so the
    /// offset stays unwrapped; float precision is ample for multi-hour preview sessions.
    /// </summary>
    private static Vector2 ComputeCirrusWindOffset(double renderTime, in PreviewRenderSettingsSnapshot settings)
    {
        var direction = ComputeCirrusWindDirection(settings);
        var travel = (float)(renderTime * settings.CloudWindSpeed * 2.4);
        return direction * travel;
    }

    private static Vector2 ComputeCirrusWindDirection(in PreviewRenderSettingsSnapshot settings)
    {
        var heading = (settings.CloudWindHeadingDegrees + 18f) * (MathF.PI / 180f);
        return new Vector2(MathF.Cos(heading), MathF.Sin(heading));
    }

    private void CompositeCloudRenderTargetToDefault(ref GlRenderFrame frame)
    {
        var useUpsample = _cloudUpsampleProgram is { IsValid: true };
        var program = useUpsample ? _cloudUpsampleProgram : _godRayCompositeProgram;
        var source = _cloudCompositeTarget ?? _cloudRenderTarget;
        if (source is null || program is not { IsValid: true } || _cloudQuadVao == 0)
        {
            BindDefaultFramebuffer(ref frame);
            return;
        }

        var gl = frame.Gl;
        BindDefaultFramebuffer(ref frame);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.ScissorTest);
        gl.ColorMask(true, true, true, true);
        FlushPendingGlErrors(gl);
        gl.BindVertexArray(_cloudQuadVao);
        program.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, source.ColorTextureHandle);
        if (useUpsample)
        {
            var upu = _cloudUpsampleUniformLocs;
            SetIntOnProgramLoc(program, upu.Clouds, 0);
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
            SetIntOnProgramLoc(program, upu.CloudData, 2);
            SetVec2OnProgramLoc(program, upu.CloudTexelSize, new Vector2(
                1f / Math.Max(source.Width, 1),
                1f / Math.Max(source.Height, 1)));
            var hasDepth = _sceneCapture is { IsValid: true };
            SetIntOnProgramLoc(program, upu.HasSceneDepth, hasDepth ? 1 : 0);
            var viewProj = frame.Proj * frame.View;
            if (Matrix4x4.Invert(viewProj, out var invViewProj))
            {
                SetMatrixOnProgramLoc(program, upu.InvViewProj, invViewProj);
            }
            SetVec3OnProgramLoc(program, upu.CameraPos, frame.Eye);
            SetVec3OnProgramLoc(program, upu.SunDir, frame.WorldLightDir);
            SetFloatOnProgramLoc(
                program,
                upu.SunCosDiscEdge,
                ComputeCloudDirectDiscCosEdge(in frame));
            SetFloatOnProgramLoc(
                program,
                upu.SunDiscVisibility,
                ComputeCloudSunDiscVisibility(in frame));
            SetIntOnProgramLoc(program, upu.CloudDataDirect,
                source.Profile.UsesDirectMetadata ? 1 : 0);
            // Match atmo_sky.frag / procedural sky: exposure * 1.4 before present encode.
            SetFloatOnProgramLoc(
                program,
                upu.CloudExposure,
                frame.Settings.AtmosphereSkyExposure * 1.4f);
            SetIntOnProgramLoc(program, upu.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
            SetFloatOnProgramLoc(program, upu.HdrPaperWhiteNits, frame.Settings.HdrPaperWhiteNits);
            SetFloatOnProgramLoc(program, upu.HdrPeakNits, frame.Settings.HdrPeakNits);
            SetIntOnProgramLoc(program, upu.ApplyCloudEncoding,
                frame.Settings.CloudDebugView == PreviewCloudDebugView.Off ? 1 : 0);
            SetIntOnProgramLoc(
                program,
                upu.CloudSourceFullResolution,
                ReferenceEquals(source, _cloudRepairTarget) ? 1 : 0);
            if (hasDepth)
            {
                gl.ActiveTexture(TextureUnit.Texture1);
                gl.BindTexture(TextureTarget.Texture2D, _sceneCapture!.DepthTextureHandle);
                SetIntOnProgramLoc(program, upu.SceneDepth, 1);
            }
        }
        else
        {
            SetIntOnProgramLoc(program, _cloudCompositeUniformLocs.Rays, 0);
            SetIntOnProgramLoc(program, _cloudCompositeUniformLocs.CloudPresent, 1);
            SetFloatOnProgramLoc(
                program,
                _cloudCompositeUniformLocs.CloudExposure,
                frame.Settings.AtmosphereSkyExposure * 1.4f);
            SetIntOnProgramLoc(
                program,
                _cloudCompositeUniformLocs.HdrPresent,
                frame.Settings.HdrPresentActive ? 1 : 0);
            SetFloatOnProgramLoc(
                program,
                _cloudCompositeUniformLocs.HdrPaperWhiteNits,
                frame.Settings.HdrPaperWhiteNits);
            SetFloatOnProgramLoc(
                program,
                _cloudCompositeUniformLocs.HdrPeakNits,
                frame.Settings.HdrPeakNits);
            SetIntOnProgramLoc(
                program,
                _cloudCompositeUniformLocs.ApplyCloudEncoding,
                frame.Settings.CloudDebugView == PreviewCloudDebugView.Off ? 1 : 0);
        }

        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorScissor)
        {
            gl.Enable(EnableCap.ScissorTest);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }
    }
}
