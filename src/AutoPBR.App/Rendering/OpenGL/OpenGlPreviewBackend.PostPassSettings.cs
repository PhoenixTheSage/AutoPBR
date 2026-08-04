using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private int _lastPostPassAppliedSettingsRevision = -1;

    private void EnsurePostPassPerSettingsUniforms(ref GlRenderFrame frame)
    {
        if (frame.SettingsRevision == _lastPostPassAppliedSettingsRevision)
        {
            return;
        }

        ApplyTaaPerSettingsUniforms(frame.Settings);
        ApplyGodRayPerSettingsUniforms(frame.Settings);
        ApplyVolumePerSettingsUniforms(frame.Settings);
        ApplyCloudPerSettingsUniforms(frame.Settings);
        _lastPostPassAppliedSettingsRevision = frame.SettingsRevision;
    }

    private void ApplyTaaPerSettingsUniforms(in PreviewRenderSettingsSnapshot settings)
    {
        if (_taaResolveProgram is not { IsValid: true })
        {
            return;
        }

        var taa = ResolveEffectivePreviewTaa(settings);
        var tu = _taaResolveUniformLocs;
        SetFloatOnProgramLoc(_taaResolveProgram, tu.TemporalWeight, taa.TemporalWeight);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.StableTemporalBoost, taa.StableTemporalBoost);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.MaxStableTemporal, taa.MaxStableTemporal);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.TaaSharpenStrength, taa.SharpenStrength);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.DepthEdgeHistoryFloor, taa.DepthEdgeHistoryFloor);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.EdgeAaBlend, taa.EdgeAaBlend);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.SourceFilterStrength, taa.SourceFilterStrength);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.SilhouetteHistoryWeight, taa.SilhouetteHistoryWeight);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.FxaaEdgeStrength, taa.FxaaEdgeStrength);
        SetFloatOnProgramLoc(_taaResolveProgram, tu.FxaaLumaEdgeStrength,
            Math.Clamp(settings.PreviewTaaFxaaLumaEdgeScale, 0f, 2f));
        SetFloatOnProgramLoc(_taaResolveProgram, tu.FxaaLumaThreshold,
            Math.Clamp(settings.PreviewTaaFxaaLumaThreshold, 0.001f, 0.12f));
        SetIntOnProgramLoc(_taaResolveProgram, tu.ForceFxaa, settings.PreviewTaaForceFxaa ? 1 : 0);
        SetIntOnProgramLoc(_taaResolveProgram, tu.HdrPresent, settings.HdrPresentActive ? 1 : 0);
    }

    private void BindScenePresentUniforms(
        in PreviewRenderSettingsSnapshot settings,
        bool sceneIsLinear,
        bool encodeHdr = true)
    {
        if (_scenePresentProgram is not { IsValid: true })
        {
            return;
        }

        var pu = _scenePresentUniformLocs;
        SetIntOnProgramLoc(_scenePresentProgram, pu.SceneColor, 0);
        // TAA resolves an already present-encoded default FBO; re-running presentEncodeScRgb
        // double-compresses midtones and re-crushes paper-scaled post layers (clouds / rays).
        SetIntOnProgramLoc(
            _scenePresentProgram,
            pu.HdrPresent,
            encodeHdr && settings.HdrPresentActive ? 1 : 0);
        SetIntOnProgramLoc(_scenePresentProgram, pu.SceneIsLinear, sceneIsLinear ? 1 : 0);
        SetFloatOnProgramLoc(_scenePresentProgram, pu.HdrPaperWhiteNits, settings.HdrPaperWhiteNits);
        SetFloatOnProgramLoc(_scenePresentProgram, pu.HdrPeakNits, settings.HdrPeakNits);
    }

    private void ApplyGodRayPerSettingsUniforms(in PreviewRenderSettingsSnapshot settings)
    {
        var layerWorldY = PreviewStageConstants.CloudLayerBaseWorldY(settings.CloudLayerHeight);
        if (_screenSpaceGodRayProgram is { IsValid: true })
        {
            var ssu = _screenSpaceGodRayUniformLocs;
            SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.Strength, settings.ScreenSpaceGodRayStrength);
        }

        if (_shadowAwareGodRayProgram is { IsValid: true })
        {
            var shu = _shadowAwareGodRayUniformLocs;
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.Strength, settings.ScreenSpaceGodRayStrength);
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.LayerHeight, layerWorldY);
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.VolumeHeight, settings.CloudVolumeHeight);
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.CloudDensity, settings.CloudDensity);
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.VolumeSize, settings.CloudVolumeSize);
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.FogSlabHeight, PreviewStageConstants.GroundFogSlabHeight);
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.HeightFogStrength,
                ResolveVolumeHeightFogStrength(settings));
            SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowMinBias, settings.ShadowMinBias);
            // SS beams are leaf/gap detail; never bake procedural cloud density into them.
            SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.EnableCloudAttenuation, 0);
        }
    }

    private void ApplyVolumePerSettingsUniforms(in PreviewRenderSettingsSnapshot settings)
    {
        var layerWorldY = PreviewStageConstants.CloudLayerBaseWorldY(settings.CloudLayerHeight);
        var quality = PreviewVolumetricQuality.Resolve(settings.VolumetricQuality);

        if (_volumeInjectProgram is { IsValid: true })
        {
            var vi = _volumeInjectUniformLocs;
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.DepthDistribution, quality.FroxelDepthExp);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.LayerHeight, layerWorldY);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.VolumeHeight, settings.CloudVolumeHeight);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.CloudDensity, settings.CloudDensity);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.VolumeSize, settings.CloudVolumeSize);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.FogSlabHeight, PreviewStageConstants.GroundFogSlabHeight);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.HeightFogStrength, ResolveVolumeHeightFogStrength(settings));
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.DebugDensity, Math.Max(0f, settings.GodRayDebugDensity));
            // Volume path: tighter bias than opaque lighting so cutout canopy holes stay open in shafts.
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.ShadowMinBias, settings.ShadowMinBias * 0.45f);
        }

        if (_volumeInjectComputeProgram is { IsValid: true })
        {
            var vci = _volumeInjectComputeUniformLocs;
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.DepthDistribution, quality.FroxelDepthExp);
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.LayerHeight, layerWorldY);
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.VolumeHeight, settings.CloudVolumeHeight);
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.CloudDensity, settings.CloudDensity);
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.VolumeSize, settings.CloudVolumeSize);
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.FogSlabHeight, PreviewStageConstants.GroundFogSlabHeight);
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.HeightFogStrength, ResolveVolumeHeightFogStrength(settings));
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.DebugDensity, Math.Max(0f, settings.GodRayDebugDensity));
            SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.ShadowMinBias, settings.ShadowMinBias * 0.45f);
        }

        if (_volumeIntegrateProgram is { IsValid: true })
        {
            var iu = _volumeIntegrateUniformLocs;
            SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.ScatterGain, settings.GodRayScatterGain);
            SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.Extinction, Math.Max(1e-3f, settings.GodRayExtinction));
            SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.DepthDistribution, quality.FroxelDepthExp);
            // Ambient fill scales with horizon fog so one slider drives valley haze visibility.
            SetFloatOnProgramLoc(
                _volumeIntegrateProgram,
                iu.AmbientFillGain,
                ResolveVolumeAmbientFillGain(settings));
            SetFloatOnProgramLoc(
                _volumeIntegrateProgram,
                iu.PhaseDirectivity,
                Math.Clamp(settings.GodRayPhaseDirectivity, 0f, 1f));
        }
    }

    private void ApplyCloudPerSettingsUniforms(in PreviewRenderSettingsSnapshot settings)
    {
        if (_cloudProgram is not { IsValid: true })
        {
            return;
        }

        var profile = PreviewVolumetricQuality.Resolve(settings.VolumetricQuality);
        var layerWorldY = PreviewStageConstants.CloudLayerBaseWorldY(settings.CloudLayerHeight);
        var cu = _cloudUniformLocs;
        SetFloatOnProgramLoc(_cloudProgram, cu.SunIntensity, settings.AtmosphereSunIntensity);
        SetFloatOnProgramLoc(_cloudProgram, cu.LayerHeight, layerWorldY);
        SetFloatOnProgramLoc(_cloudProgram, cu.VolumeHeight, settings.CloudVolumeHeight);
        SetFloatOnProgramLoc(_cloudProgram, cu.Density, settings.CloudDensity);
        SetFloatOnProgramLoc(_cloudProgram, cu.CoverageScale, settings.CloudCoverageScale);
        SetFloatOnProgramLoc(_cloudProgram, cu.VolumeSize, settings.CloudVolumeSize);
        SetFloatOnProgramLoc(_cloudProgram, cu.CirrusStrength, settings.CloudCirrusStrength);
        SetIntOnProgramLoc(_cloudProgram, cu.Quality, profile.CloudQuality);
        SetIntOnProgramLoc(_cloudProgram, cu.MarchSteps, Math.Clamp(settings.CloudMarchStepOverride, 0, 64));
        SetIntOnProgramLoc(_cloudProgram, cu.DebugView, (int)settings.CloudDebugView);
        ApplyCloudLayerEnvelopeUniforms(
            _cloudProgram,
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
            upperWindOffset: default);
        if (_cloudHighProgram is { IsValid: true })
        {
            var hu = _cloudHighUniformLocs;
            ApplyCloudLayerEnvelopeUniforms(
                _cloudHighProgram,
                hu.CumulusLayerCount,
                hu.InterDeckGap,
                hu.HeightVariance,
                hu.UpperThicknessScale,
                hu.UpperCoverageScale,
                hu.UpperDensityScale,
                hu.UpperWindOffset,
                hu.CirrusGap,
                hu.CirrusThickness,
                hu.StyleBias,
                settings,
                upperWindOffset: default);
        }
    }
}
