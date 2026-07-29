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
    private bool _loggedCloudDraw;
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
    private PreviewCloudCameraRegion? _cloudCameraRegion;
    private string _cloudStbnDiagnostic = "not-initialized";
    private string _cloudMomentsDiagnostic = "not-initialized";
    private string _cloudEdgeRepairDiagnostic = "not-initialized";
    private string _cloudDensityAssetDiagnostic = "not-initialized";
    private int _cloudDensityAssetVersion;
    private int _cloudDensityAssetProfileCode;
    private int _lastCloudDebugViewDiagnostic = -1;
    private PreviewCloudLightingCachePlan _cloudLightingCachePlan;
    private GlShaderProgram? _cloudLightSliceProgram;
    private GlCloudLightFroxelCache? _cloudLightCache;
    private GlCloudLightFragmentSliceGenerator? _cloudLightSliceGenerator;
    private PreviewCloudLightBasis? _cloudLightBasis;
    private bool _cloudLightReferenceGenerationAttempted;
    private string _cloudLightingCacheResourceDiagnostic = "resources=not-initialized";

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
        TryInitCloudLightingCacheCq31(gl, useOpenGlEs, volumetricQuality);
        EmitDiagnostic(
            "[3D preview] CQ3.1 cloud-light froxel cache contract: " +
            _cloudLightingCachePlan.FormatDiagnostic(
                _cloudLightingCacheResourceDiagnostic) + ".");
    }

    private void TryInitCloudLightingCacheCq31(
        GL gl,
        bool useOpenGlEs,
        int volumetricQuality)
    {
        _cloudLightSliceGenerator = null;
        _cloudLightSliceProgram?.Dispose();
        _cloudLightSliceProgram = null;
        _cloudLightCache?.Dispose();
        _cloudLightCache = null;
        _cloudLightBasis = null;
        _cloudLightReferenceGenerationAttempted = false;
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
            "cq3.1-cloud-light-fragment-slice");
        if (_cloudLightSliceProgram is not { IsValid: true })
        {
            _cloudLightingCacheResourceDiagnostic =
                "resources=not-allocated-fragment-shader-failed";
            EmitDiagnostic(
                "[3D preview] CQ3.1 cloud-light fragment-slice shader unavailable; " +
                "continuing with the short light march (" +
                TrimShaderDiagnostic(shaderError) + ").");
            _cloudLightSliceProgram?.Dispose();
            _cloudLightSliceProgram = null;
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
                "[3D preview] CQ3.1 cloud-light RG16F allocation failed; " +
                "continuing with the short light march (" +
                allocationDiagnostic + ").");
            return;
        }

        _cloudLightCache = cache;
        _cloudLightSliceGenerator = new GlCloudLightFragmentSliceGenerator(
            gl,
            _cloudLightSliceProgram,
            _cloudQuadVao);
        _cloudLightingCacheResourceDiagnostic = cache.FormatDiagnostic();
    }

    private void EnsureCloudLightingCacheProfileCq31(
        GL gl,
        int volumetricQuality)
    {
        var requested = PreviewCloudLightingCacheProfiles.Resolve(volumetricQuality);
        if (_cloudLightingCachePlan.Profile.Equals(requested))
        {
            return;
        }

        TryInitCloudLightingCacheCq31(gl, _useOpenGlEs, volumetricQuality);
        EmitDiagnostic(
            "[3D preview] CQ3.1 cloud-light cache profile changed: " +
            _cloudLightingCachePlan.FormatDiagnostic(
                _cloudLightingCacheResourceDiagnostic) + ".");
    }

    private void TryGenerateCloudLightingReferenceCq31(
        ref GlRenderFrame frame,
        float layerWorldY,
        Vector3 windOffset,
        Vector2 cirrusWindOffset,
        PreviewVolumetricQuality.Profile qualityProfile)
    {
        EnsureCloudLightingCacheProfileCq31(
            frame.Gl,
            frame.Settings.VolumetricQuality);
        if (_cloudLightReferenceGenerationAttempted ||
            _cloudLightCache is not { IsValid: true } cache ||
            _cloudLightSliceGenerator is not { } generator)
        {
            return;
        }

        _cloudLightReferenceGenerationAttempted = true;
        try
        {
            var groundWorldY = PreviewStageConstants.GroundPlaneWorldY;
            var altitudeBounds = PreviewCloudLightAltitudeBounds.Create(
                groundWorldY,
                layerWorldY,
                frame.Settings.CloudVolumeHeight,
                frame.Settings.CloudVolumeSize,
                frame.Settings.CloudCirrusStrength);
            var basis = PreviewCloudLightBasisBuilder.Build(
                frame.LightDir,
                _cloudLightBasis);
            _cloudLightBasis = basis;
            var cameraGround = new Vector3(
                frame.Eye.X,
                groundWorldY,
                frame.Eye.Z);
            var nearInterval = PreviewCloudLightDepthInterval.Create(
                basis,
                cache.Profile.Near,
                cameraGround,
                altitudeBounds,
                groundWorldY,
                PreviewStageConstants.CloudPlanetRadius);
            var farInterval = PreviewCloudLightDepthInterval.Create(
                basis,
                cache.Profile.Far,
                cameraGround,
                altitudeBounds,
                groundWorldY,
                PreviewStageConstants.CloudPlanetRadius);
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
            var inputs = new GlCloudLightSliceGenerationInputs(
                nearTransform,
                farTransform,
                altitudeBounds,
                PreviewCloudShellGeometry.PlanetCenter(groundWorldY),
                PreviewStageConstants.CloudPlanetRadius,
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
                _cloudCoverageTex?.Id ?? 0);

            if (!generator.TryGenerate(
                    cache,
                    inputs,
                    frame.Vw,
                    frame.Vh,
                    out var generationDiagnostic))
            {
                _cloudLightingCacheResourceDiagnostic =
                    cache.FormatDiagnostic() + ";generationFailure=" +
                    generationDiagnostic;
                EmitDiagnostic(
                    "[3D preview] CQ3.1 cloud-light fragment reference generation failed; " +
                    "production lighting remains on the short march (" +
                    generationDiagnostic + ").");
                return;
            }

            var centerWorld = nearTransform.UnitToWorld(
                new Vector3(0.5f, 0.5f, 0.5f));
            var sampleWeights = PreviewCloudLightCascadeBlend.Select(
                nearTransform,
                farTransform,
                centerWorld,
                cache.Profile.NearOverlapFraction);
            _cloudLightingCacheResourceDiagnostic =
                cache.FormatDiagnostic() +
                FormattableString.Invariant(
                    $";reference={generationDiagnostic};nearDepth={nearTransform.LightDepthMin:F2}..{nearTransform.LightDepthMin + nearTransform.LightDepthSpan:F2};farDepth={farTransform.LightDepthMin:F2}..{farTransform.LightDepthMin + farTransform.LightDepthSpan:F2};centerWeights={sampleWeights.Near:F2}/{sampleWeights.Far:F2}/{sampleWeights.ShortMarch:F2}");
            EmitDiagnostic(
                "[3D preview] CQ3.1 cloud-light fragment reference ready: " +
                _cloudLightingCacheResourceDiagnostic +
                "; activeRuntime=short-march.");
        }
        catch (Exception ex)
        {
            cache.Near.InvalidateGeneration();
            cache.Far.InvalidateGeneration();
            _cloudLightingCacheResourceDiagnostic =
                cache.FormatDiagnostic() +
                $";generationFailure={ex.GetType().Name}:{ex.Message}";
            EmitDiagnostic(
                "[3D preview] CQ3.1 cloud-light reference setup failed; " +
                "production lighting remains on the short march (" +
                ex.GetType().Name + ": " + ex.Message + ").");
        }
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

        var generatedV1 = CreateGeneratedV1DensityAssetSet();
        if (TryCommitCloudDensityTextures(
                gl,
                generatedV1,
                out var generatedUploadReason))
        {
            UpdateCloudDensityAssetDiagnostic(
                generatedV1,
                $"{preferredLoadReason} -> generated-v1;{generatedUploadReason}",
                profileCode: 4);
            return true;
        }

        _cloudDensityAssetDiagnostic =
            $"procedural-shader-fallback ({preferredLoadReason};{generatedUploadReason})";
        _cloudDensityAssetVersion = 0;
        _cloudDensityAssetProfileCode = 5;
        InvalidateCloudTemporalHistory();
        EmitDiagnostic(
            "[3D preview] CQ2 density texture initialization failed; " +
            $"{_cloudDensityAssetDiagnostic}. Detailed cloud shaders retain their " +
            "procedural hash-density fallback.");
        return false;
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

    private static PreviewCloudDensityAssetSet CreateGeneratedV1DensityAssetSet() =>
        new(
            AssetVersion: 1,
            ProfileName: "legacy-v1-runtime",
            ShapeSize: PreviewCloudNoiseTextureGenerator.Size,
            ShapeRgba: PreviewCloudNoiseTextureGenerator.GenerateRgba8(),
            DetailSize: PreviewCloudNoiseTextureGenerator.DetailSize,
            DetailRgba: PreviewCloudNoiseTextureGenerator.GenerateDetailRgba8(),
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
        _loggedCloudDraw = false;
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
        _cloudProgram?.Dispose();
        _cloudProgram = null;
        _cloudTemporalProgram?.Dispose();
        _cloudTemporalProgram = null;
        _cloudUpsampleProgram?.Dispose();
        _cloudUpsampleProgram = null;
        _cloudRepairProgram?.Dispose();
        _cloudRepairProgram = null;
        _cloudLightSliceGenerator = null;
        _cloudLightSliceProgram?.Dispose();
        _cloudLightSliceProgram = null;
        _cloudLightCache?.Dispose();
        _cloudLightCache = null;
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
        _loggedCloudDraw = false;
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
        _cloudHistoryW = 0;
        _cloudHistoryH = 0;
        _cloudHistoryViewportW = 0;
        _cloudHistoryViewportH = 0;
        _cloudCameraRegion = null;
        _cloudStbnDiagnostic = "not-initialized";
        _cloudMomentsDiagnostic = "not-initialized";
        _cloudEdgeRepairDiagnostic = "not-initialized";
        _cloudDensityAssetDiagnostic = "not-initialized";
        _cloudDensityAssetVersion = 0;
        _cloudDensityAssetProfileCode = 0;
        _lastCloudDebugViewDiagnostic = -1;
        _cloudLightingCachePlan = default;
        _cloudLightBasis = null;
        _cloudLightReferenceGenerationAttempted = false;
        _cloudLightingCacheResourceDiagnostic = "resources=not-initialized";
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
            SetFloatOnProgramLoc(program, ru.PlanetRadius, PreviewStageConstants.CloudPlanetRadius);
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
            _cloudEdgeRepairDiagnostic =
                $"active full-res {frame.Vw}x{frame.Vh}, {PreviewCloudEdgeRepairClassifier.RepairStepCount}-step";
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

    private void ObserveCloudCameraRegion(ref GlRenderFrame frame)
    {
        var groundY = PreviewStageConstants.GroundPlaneWorldY;
        var center = PreviewCloudShellGeometry.PlanetCenter(groundY);
        var layerBase = PreviewStageConstants.CloudLayerBaseWorldY(frame.Settings.CloudLayerHeight) - groundY;
        var layerTop = layerBase + Math.Max(frame.Settings.CloudVolumeHeight, 0.01f);
        var region = PreviewCloudShellGeometry.ClassifyCamera(frame.Eye, center, layerBase, layerTop);
        if (_cloudCameraRegion == region)
        {
            return;
        }

        var previous = _cloudCameraRegion?.ToString() ?? "Unknown";
        _cloudCameraRegion = region;
        var radialAltitude = (frame.Eye - center).Length() - PreviewCloudShellGeometry.PlanetRadius;
        var transition = FormattableString.Invariant($"[3D preview] Cloud camera region transition: {previous} -> {region} (radialAltitude={radialAltitude:F3}, layer={layerBase:F3}..{layerTop:F3}, eye=({frame.Eye.X:F3},{frame.Eye.Y:F3},{frame.Eye.Z:F3})).");
        EmitDiagnostic(transition);
        // Persist before issuing the cloud draw. A native driver fault can bypass every managed catch,
        // so this marker records the last shell boundary crossed even in that failure mode.
        LogService.AppendEmergencyDiagnostic("Cloud camera transition", transition);
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

        var groundY = PreviewStageConstants.GroundPlaneWorldY;
        var center = PreviewCloudShellGeometry.PlanetCenter(groundY);
        var layerBase = PreviewStageConstants.CloudLayerBaseWorldY(frame.Settings.CloudLayerHeight) - groundY;
        var layerTop = layerBase + Math.Max(frame.Settings.CloudVolumeHeight, 0.01f);
        var radialAltitude = (frame.Eye - center).Length() - PreviewCloudShellGeometry.PlanetRadius;
        var detail = FormattableString.Invariant($"Cloud stage: {stage}\nCamera region: {_cloudCameraRegion?.ToString() ?? "Unknown"}\nEye: ({frame.Eye.X:R}, {frame.Eye.Y:R}, {frame.Eye.Z:R})\nRadial altitude: {radialAltitude:R}; layer: {layerBase:R}..{layerTop:R}\nViewport: {frame.Vw}x{frame.Vh}; cloud quality: {frame.Settings.CloudQuality}; volumetric quality: {frame.Settings.VolumetricQuality}; temporal: {ShouldUseCloudShaderTemporal(frame.Settings)}\nContext: {_glCapabilities?.FormatContextSuffix() ?? "unavailable"}\n{exception}");
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
        EnsureCloudRenderFormatForQuality(gl, settings.VolumetricQuality);
        var layerWorldY = PreviewStageConstants.CloudLayerBaseWorldY(settings.CloudLayerHeight);
        var useSceneDepth = frame.GodRayCaptureActive && _sceneCapture is { IsValid: true };
        var windTime = settings.CloudFreezeWind ? 0.0 : frame.RenderTime;
        var windOffset = ComputeCloudWindOffset(windTime, settings);
        var cirrusWindOffset = ComputeCirrusWindOffset(windTime, settings);
        TryGenerateCloudLightingReferenceCq31(
            ref frame,
            layerWorldY,
            windOffset,
            cirrusWindOffset,
            profile);
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
                    }
                    else if (updateHistory)
                    {
                        InvalidateCloudTemporalHistory();
                    }
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

        if (!_loggedCloudDraw)
        {
            _loggedCloudDraw = true;
            var godRays = frame.GodRayCaptureActive && _sceneCapture is { IsValid: true };
            EmitDiagnostic($"[3D preview] Curved-shell volumetric clouds active (sceneDepth={useSceneDepth}, " +
                $"temporalResolve={useTemporalReproject}, cloudDepthHistory={useTemporalReproject}, godRays={godRays}, " +
                $"volumetricPreset={PreviewVolumetricQuality.GetName(frame.Settings.VolumetricQuality)}, " +
                $"cloudQuality={PreviewVolumetricQuality.Resolve(frame.Settings.VolumetricQuality).CloudQuality}, " +
                $"cloudFormat={_cloudRenderFormatProfile.Name}, " +
                $"trace={traceSize.Width}x{traceSize.Height}@{traceSize.Scale:0.###}, " +
                $"pixelAngle={pixelAngularSize:0.000000}, " +
                "cloudColor=linear-trace-history/final-composite-encode, " +
                $"densityAssets={_cloudDensityAssetDiagnostic}, " +
                $"densitySemantics=v{_cloudDensityAssetVersion}, " +
                $"densityDetail={(_cloudDensityAssetVersion >= 2 ? "single-low-medium/rotated-edge-high-cinematic" : "legacy-single")}, " +
                $"weatherAddressing={(_cloudDensityAssetVersion >= 2 ? "dual-world" : "legacy")}, " +
                "directDiscOcclusion=post-temporal-full-res, " +
                $"stbn={_cloudStbnDiagnostic}, " +
                $"stbnActive={CanUseCloudStbn(_useOpenGlEs, profile.CloudQuality, _cloudStbnTex is not null)}, " +
                $"moments={_cloudMomentsDiagnostic}, " +
                $"edgeRepair={_cloudEdgeRepairDiagnostic}, " +
                $"cloudLightCache={_cloudLightingCachePlan.FormatDiagnostic(_cloudLightingCacheResourceDiagnostic)}, " +
                $"historyConfidence={_cloudHistoryConfidenceFrames}/{PreviewCloudTemporalMoments.ConfidenceFrameCount}, " +
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
        if (_cloudProgram is not { } program)
        {
            return;
        }

        var gl = frame.Gl;
        var settings = frame.Settings;
        var cu = _cloudUniformLocs;

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
        SetFloatOnProgramLoc(program, cu.PlanetRadius, PreviewStageConstants.CloudPlanetRadius);
        SetVec3OnProgramLoc(program, cu.SunDir, frame.LightDir);
        SetVec3OnProgramLoc(program, cu.WindOffset, windOffset);
        SetFloatOnProgramLoc(program, cu.CirrusStrength, settings.CloudCirrusStrength);
        SetVec2OnProgramLoc(program, cu.CirrusWindOffset, cirrusWindOffset);
        SetVec2OnProgramLoc(program, cu.CirrusWindDir, ComputeCirrusWindDirection(settings));
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
        SetFloatOnProgramLoc(program, tu.HistoryConfidence,
            useMoments
                ? PreviewCloudTemporalMoments.ResolveConfidence(_cloudHistoryConfidenceFrames)
                : 1f);
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
        hash.Add(settings.CloudMarchStepOverride);
        hash.Add(settings.CloudFreezeWind);
        hash.Add(settings.AtmosphereSunIntensity);
        hash.Add(verticalFieldOfViewRadians);
        hash.Add(PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetVersion);
        hash.Add(_cloudDensityAssetVersion);
        return hash.ToHashCode();
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
            SetFloatOnProgramLoc(program, upu.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatOnProgramLoc(program, upu.PlanetRadius, PreviewStageConstants.CloudPlanetRadius);
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
            SetFloatOnProgramLoc(program, upu.CloudExposure, frame.Settings.AtmosphereSkyExposure);
            SetIntOnProgramLoc(program, upu.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
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
                frame.Settings.AtmosphereSkyExposure);
            SetIntOnProgramLoc(
                program,
                _cloudCompositeUniformLocs.HdrPresent,
                frame.Settings.HdrPresentActive ? 1 : 0);
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
