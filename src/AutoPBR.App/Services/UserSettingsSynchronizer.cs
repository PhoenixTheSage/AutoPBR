using AutoPBR.App.Models;
using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.ViewModels;
using AutoPBR.Core;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

namespace AutoPBR.App.Services;

/// <summary>Two-way sync between MainWindowViewModel and UserSettings persistence.</summary>
internal static class UserSettingsSynchronizer
{
    private const int CurrentPersistedSettingsGeneration = 2;
    private const int DefaultPreview3DTaaMode = 0;

    public static void LoadInto(MainWindowViewModel vm, UserSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            vm.OutputDirectory = settings.OutputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(settings.BatchFolderPath))
        {
            vm.BatchFolderPath = settings.BatchFolderPath;
        }

        vm.UseBatchFolderInput = settings.UseBatchFolderInput;

        vm.NormalIntensity = settings.NormalIntensity;
        vm.HeightIntensity = settings.HeightIntensity;
        vm.BrickHeightMapPostProcessEnabled = settings.BrickHeightMapPostProcessEnabled;
        vm.BrickHeightMinStructuralConfidence = settings.BrickHeightMinStructuralConfidence;
        vm.BrickHeightInvertDeltaThreshold = settings.BrickHeightInvertDeltaThreshold;
        vm.BrickLightGroutDiffuseDeltaMin = settings.BrickLightGroutDiffuseDeltaMin;
        vm.PreviewBrickProbeDebug = settings.PreviewBrickProbeDebug;
        vm.PreviewDisplayMode = Math.Clamp(settings.PreviewDisplayMode, 0, 1);
        vm.Preview3DAutoRotate = settings.Preview3DAutoRotate;
        vm.Preview3DEntityAnimationSpeed = settings.Preview3DEntityAnimationSpeed <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DEntityAnimationSpeed, 0.0, 4.0);
        vm.Preview3DEntityAnimationAmplitude = settings.Preview3DEntityAnimationAmplitude < 0
            ? 1.0
            : Math.Clamp(settings.Preview3DEntityAnimationAmplitude, 0.0, 2.0);
        vm.Preview3DEnableEntityAnimation = settings.Preview3DEnableEntityAnimation;
        vm.Preview3DEnableLegacyEntityWobble = settings.Preview3DEnableLegacyEntityWobble;
        vm.Preview3DPauseEntityIdleAnimation = settings.Preview3DPauseEntityIdleAnimation;
        vm.Preview3DShowGrid = settings.Preview3DShowGrid;
        vm.Preview3DGridColor = Avalonia.Media.Color.FromUInt32(
            settings.Preview3DGridColorArgb == 0
                ? PreviewStageConstants.DefaultGridColorArgb
                : settings.Preview3DGridColorArgb);
        vm.Preview3DShowGroundMesh = settings.Preview3DShowGroundMesh;
        vm.Preview3DChunkViewDistance = Math.Clamp(
            settings.Preview3DChunkViewDistance, 2, 24);
        vm.Preview3DWorldSeed = Math.Clamp(settings.Preview3DWorldSeed, 0, int.MaxValue);
        vm.Preview3DTerrainBiomeSize = Math.Clamp(
            settings.Preview3DTerrainBiomeSize,
            PreviewStageConstants.TerrainMinBiomeSize,
            PreviewStageConstants.TerrainMaxBiomeSize);
        vm.Preview3DTerrainAmplification = Math.Clamp(
            settings.Preview3DTerrainAmplification,
            PreviewStageConstants.TerrainMinAmplification,
            PreviewStageConstants.TerrainMaxAmplification);
        vm.Preview3DTerrainErosionStrength = Math.Clamp(
            settings.Preview3DTerrainErosionStrength,
            PreviewStageConstants.TerrainMinErosionStrength,
            PreviewStageConstants.TerrainMaxErosionStrength);
        vm.Preview3DTerrainContinentalness = Math.Clamp(
            settings.Preview3DTerrainContinentalness,
            PreviewStageConstants.TerrainMinContinentalness,
            PreviewStageConstants.TerrainMaxContinentalness);
        vm.Preview3DGrassColormapTemperature = Math.Clamp(
            settings.Preview3DGrassColormapTemperature ?? PreviewStageConstants.DefaultGrassColormapTemperature,
            0.0,
            1.0);
        vm.Preview3DGrassColormapDownfall = Math.Clamp(
            settings.Preview3DGrassColormapDownfall ?? PreviewStageConstants.DefaultGrassColormapDownfall,
            0.0,
            1.0);
        vm.Preview3DShowAxes = settings.Preview3DShowAxes;
        vm.Preview3DShowFpsCounter = settings.Preview3DShowFpsCounter;
        vm.Preview3DLogGpuPassTimings = settings.Preview3DLogGpuPassTimings;
        vm.Preview3DLogVerbosePreviewDiagnostics = settings.Preview3DLogVerbosePreviewDiagnostics;
        vm.Preview3DShowExpandedGpuTimingHud = settings.Preview3DShowExpandedGpuTimingHud;
        vm.Preview3DOcclusionDebugMode = Math.Clamp(settings.Preview3DOcclusionDebugMode, 0, 2);
        vm.Preview3DVSyncEnabled = settings.Preview3DVSyncEnabled ?? settings.Preview3DCapFpsAt60;
        vm.Preview3DEnableParallax = settings.Preview3DEnableParallax;
        vm.Preview3DEnableNormalMap = settings.Preview3DEnableNormalMap;
        vm.Preview3DEnableSpecularMap = settings.Preview3DEnableSpecularMap;
        vm.Preview3DParallaxHeightStrength = settings.Preview3DParallaxHeightStrength <= 0
            ? 0.05
            : Math.Clamp(settings.Preview3DParallaxHeightStrength, 0.0, 1.0);
        vm.Preview3DParallaxTraceLayers = settings.Preview3DParallaxTraceLayers <= 0
            ? 64
            : Math.Clamp(settings.Preview3DParallaxTraceLayers, 8.0, 128.0);
        vm.Preview3DParallaxRefineSteps = settings.Preview3DParallaxRefineSteps < 0
            ? 5
            : Math.Clamp(settings.Preview3DParallaxRefineSteps, 0.0, 8.0);
        vm.Preview3DParallaxShadowSamples = settings.Preview3DParallaxShadowSamples <= 0
            ? 24
            : Math.Clamp(settings.Preview3DParallaxShadowSamples, 4.0, 64.0);
        vm.Preview3DParallaxShadowSoftness = settings.Preview3DParallaxShadowSoftness < 0
            ? 1.25
            : Math.Clamp(settings.Preview3DParallaxShadowSoftness, 0.0, 4.0);
        vm.Preview3DParallaxMaxUvShift = settings.Preview3DParallaxMaxUvShift <= 0
            ? 0.45
            : Math.Clamp(settings.Preview3DParallaxMaxUvShift, 0.05, 0.75);
        vm.Preview3DEnableTessellationDisplacement = settings.Preview3DEnableTessellationDisplacement;
        vm.Preview3DTessellationLevel = settings.Preview3DTessellationLevel <= 0
            ? 8.0
            : Math.Clamp(settings.Preview3DTessellationLevel, 1.0, 16.0);
        vm.Preview3DTessellationDisplacementStrength = settings.Preview3DTessellationDisplacementStrength < 0
            ? 0.06
            : Math.Clamp(settings.Preview3DTessellationDisplacementStrength, 0.0, 0.20);
        vm.Preview3DEnableSss = settings.Preview3DEnableSss;
        vm.Preview3DEnableParallaxShadow = settings.Preview3DEnableParallaxShadow;
        vm.Preview3DEnableParallaxAo = settings.Preview3DEnableParallaxAo;
        vm.Preview3DParallaxAoStrength = settings.Preview3DParallaxAoStrength <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DParallaxAoStrength, 0.0, 2.0);
        vm.Preview3DEnableIbl = settings.Preview3DEnableIbl;
        vm.Preview3DIblStrength = settings.Preview3DIblStrength <= 0
            ? 0.6
            : Math.Clamp(settings.Preview3DIblStrength, 0.0, 2.0);
        vm.Preview3DEnableAtmosphericSky = settings.Preview3DEnableAtmosphericSky;
        vm.Preview3DAtmosphereTurbidity = settings.Preview3DAtmosphereTurbidity <= 0
            ? 2.6
            : Math.Clamp(settings.Preview3DAtmosphereTurbidity, 1.2, 10.0);
        vm.Preview3DAtmosphereSunIntensity = settings.Preview3DAtmosphereSunIntensity <= 0
            ? 10.0
            : Math.Clamp(settings.Preview3DAtmosphereSunIntensity, 0.2, 64.0);
        vm.Preview3DAtmosphereHorizonFalloff = settings.Preview3DAtmosphereHorizonFalloff <= 0
            ? 1.35
            : Math.Clamp(settings.Preview3DAtmosphereHorizonFalloff, 0.25, 4.0);
        vm.Preview3DAtmosphereSkyExposure = settings.Preview3DAtmosphereSkyExposure <= 0
            ? 0.85
            : Math.Clamp(settings.Preview3DAtmosphereSkyExposure, 0.1, 3.0);
        vm.Preview3DAtmosphereSunDiscStrength = settings.Preview3DAtmosphereSunDiscStrength < 0
            ? 0.35
            : Math.Clamp(settings.Preview3DAtmosphereSunDiscStrength, 0.0, 2.0);
        vm.Preview3DAtmosphereSunDiscBrightness = settings.Preview3DAtmosphereSunDiscBrightness <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DAtmosphereSunDiscBrightness, 0.0, 4.0);
        vm.Preview3DAtmosphereSunDiscSize = settings.Preview3DAtmosphereSunDiscSize <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DAtmosphereSunDiscSize, 0.05, 2.0);
        vm.Preview3DAtmosphereMoonDiscStrength = settings.Preview3DAtmosphereMoonDiscStrength <= 0
            ? 1.35
            : Math.Clamp(settings.Preview3DAtmosphereMoonDiscStrength, 0.0, 4.0);
        vm.Preview3DAtmosphereMoonDiscSize = settings.Preview3DAtmosphereMoonDiscSize <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DAtmosphereMoonDiscSize, 0.05, 3.0);
        vm.Preview3DAtmosphereMoonGlowStrength = settings.Preview3DAtmosphereMoonGlowStrength < 0
            ? 0.7
            : Math.Clamp(settings.Preview3DAtmosphereMoonGlowStrength, 0.0, 4.0);
        vm.Preview3DAtmosphereMoonTextureSharpness = settings.Preview3DAtmosphereMoonTextureSharpness <= 0
            ? 1.25
            : Math.Clamp(settings.Preview3DAtmosphereMoonTextureSharpness, 0.0, 4.0);
        vm.Preview3DMoonWorldLightIntensity = settings.Preview3DMoonWorldLightIntensity <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DMoonWorldLightIntensity, 0.0, 8.0);
        vm.Preview3DShowCelestialDebug = settings.Preview3DShowCelestialDebug;
        vm.Preview3DEnableShadows = settings.Preview3DEnableShadows;
        vm.Preview3DEnableShadowCascades = settings.Preview3DEnableShadowCascades;
        vm.Preview3DShadowDistance = settings.Preview3DShadowDistance <= 0
            ? 128.0
            : Math.Clamp(settings.Preview3DShadowDistance, 32.0, 256.0);
        vm.Preview3DLightYawDegrees = Math.Clamp(settings.Preview3DLightYawDegrees, -180.0, 180.0);
        vm.Preview3DLightPitchDegrees = Math.Clamp(settings.Preview3DLightPitchDegrees, -89.0, 89.0);
        vm.Preview3DTimeOfDayHours = settings.Preview3DTimeOfDayHours is > 0 and <= 24
            ? settings.Preview3DTimeOfDayHours
            : PreviewLightMath.TimeOfDayFromLightYawPitch(
                settings.Preview3DLightYawDegrees,
                settings.Preview3DLightPitchDegrees);
        vm.Preview3DAnimateTimeOfDay = settings.Preview3DAnimateTimeOfDay;
        vm.Preview3DTimeOfDaySpeed = settings.Preview3DTimeOfDaySpeed <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DTimeOfDaySpeed, 0.1, 4.0);
        vm.Preview3DHorizonFogStrength = settings.Preview3DHorizonFogStrength < 0
            ? 0
            : Math.Clamp(settings.Preview3DHorizonFogStrength, 0.0, 2.0);
        vm.Preview3DEnableGodRays = settings.Preview3DEnableGodRays;
        vm.Preview3DEnableVolumetricClouds = settings.Preview3DEnableVolumetricClouds;
        vm.Preview3DVolumetricQuality = Math.Clamp(settings.Preview3DVolumetricQuality, 0, 2);
        vm.Preview3DGodRayStrength = settings.Preview3DGodRayStrength <= 0
            ? 0.45
            : Math.Clamp(settings.Preview3DGodRayStrength, 0.0, 2.0);
        vm.Preview3DGodRayScatterGain = settings.Preview3DGodRayScatterGain <= 0
            ? 3.4
            : Math.Clamp(settings.Preview3DGodRayScatterGain, 0.0, 20.0);
        vm.Preview3DGodRayExtinction = settings.Preview3DGodRayExtinction <= 0
            ? 1.15
            : Math.Clamp(settings.Preview3DGodRayExtinction, 0.01, 8.0);
        vm.Preview3DGodRayDebugDensity = Math.Clamp(settings.Preview3DGodRayDebugDensity, 0.0, 2.0);
        vm.Preview3DGodRayStabilizeDebug = settings.Preview3DGodRayStabilizeDebug;
        vm.Preview3DCloudDensity = Math.Clamp(settings.Preview3DCloudDensity, 0.0, 2.0);
        vm.Preview3DCloudCoverageScale = Math.Clamp(settings.Preview3DCloudCoverageScale, 0.0, 2.0);
        vm.Preview3DCloudLayerHeight = Math.Clamp(settings.Preview3DCloudLayerHeight, -12.0, 48.0);
        vm.Preview3DCloudVolumeHeight = settings.Preview3DCloudVolumeHeight <= 0
            ? 24.0
            : Math.Clamp(settings.Preview3DCloudVolumeHeight, 4.0, 96.0);
        vm.Preview3DCloudVolumeSize = settings.Preview3DCloudVolumeSize <= 0
            ? 48.0
            : Math.Clamp(settings.Preview3DCloudVolumeSize, 8.0, 256.0);
        vm.Preview3DCloudWindSpeed = Math.Clamp(settings.Preview3DCloudWindSpeed, 0.0, 12.0);
        vm.Preview3DCloudWindHeadingDegrees = Math.Clamp(settings.Preview3DCloudWindHeadingDegrees, -180.0, 180.0);
        vm.Preview3DCloudCirrusStrength = Math.Clamp(settings.Preview3DCloudCirrusStrength, 0.0, 2.0);
        vm.Preview3DCloudDebugView = Math.Clamp(settings.Preview3DCloudDebugView, 0, 2);
        vm.Preview3DCloudDisableTemporal = settings.Preview3DCloudDisableTemporal;
        vm.Preview3DCloudMarchStepOverride = Math.Clamp(settings.Preview3DCloudMarchStepOverride, 0.0, 64.0);
        vm.Preview3DCloudFreezeWind = settings.Preview3DCloudFreezeWind;
        vm.Preview3DEnablePreviewTaa = settings.Preview3DEnablePreviewTaa;
        vm.Preview3DTaaMode = ResolvePreview3DTaaMode(settings);
        vm.Preview3DTaaTemporalScale = Math.Clamp(settings.Preview3DTaaTemporalScale, 0.0, 1.25);
        vm.Preview3DTaaJitterScale = Math.Clamp(settings.Preview3DTaaJitterScale, 0.0, 2.0);
        vm.Preview3DTaaSourceFilterScale = Math.Clamp(settings.Preview3DTaaSourceFilterScale, 0.0, 2.0);
        vm.Preview3DTaaEdgeBlendScale = Math.Clamp(settings.Preview3DTaaEdgeBlendScale, 0.0, 2.0);
        vm.Preview3DTaaFxaaStrengthScale = Math.Clamp(settings.Preview3DTaaFxaaStrengthScale, 0.0, 5.0);
        vm.Preview3DTaaFxaaLumaEdgeScale = Math.Clamp(settings.Preview3DTaaFxaaLumaEdgeScale, 0.0, 2.0);
        vm.Preview3DTaaFxaaLumaThreshold = Math.Clamp(settings.Preview3DTaaFxaaLumaThreshold, 0.001, 0.12);
        vm.Preview3DTaaForceFxaa = settings.Preview3DTaaForceFxaa;
        vm.Preview3DSpritePlaneCount = settings.Preview3DSpritePlaneCount <= 0
            ? 2
            : Math.Clamp(settings.Preview3DSpritePlaneCount, 1, 8);
        vm.Preview3DSpriteThickness = Math.Clamp(
            settings.Preview3DSpriteThickness,
            PreviewStageConstants.SpriteThicknessMin,
            PreviewStageConstants.SpriteThicknessMax);
        vm.Preview3DCameraOrbitSensitivity = settings.Preview3DCameraOrbitSensitivity <= 0
            ? 0.006
            : Math.Clamp(settings.Preview3DCameraOrbitSensitivity, 0.0008, 0.04);
        vm.Preview3DCameraPanSensitivity = settings.Preview3DCameraPanSensitivity <= 0
            ? 0.0022
            : Math.Clamp(settings.Preview3DCameraPanSensitivity, 0.0003, 0.02);
        vm.Preview3DCameraZoomSensitivity = settings.Preview3DCameraZoomSensitivity <= 0
            ? 0.12
            : Math.Clamp(settings.Preview3DCameraZoomSensitivity, 0.02, 0.5);
        var boomDefault = (double)PreviewCamera.DefaultOrbitBoomArmDistance;
        vm.Preview3DCameraOrbitBoomDistance = settings.Preview3DCameraOrbitBoomDistance <= 0
            ? boomDefault
            : Math.Clamp(settings.Preview3DCameraOrbitBoomDistance, 1.05, 120.0);
        vm.Preview3DCameraResetKey = string.IsNullOrWhiteSpace(settings.Preview3DCameraResetKey)
            ? "R"
            : settings.Preview3DCameraResetKey.Trim();
        vm.Preview3DCameraFlyLookSensitivity = settings.Preview3DCameraFlyLookSensitivity <= 0
            ? 0.006
            : Math.Clamp(settings.Preview3DCameraFlyLookSensitivity, 0.0008, 0.04);
        vm.Preview3DCameraInvertLookY = settings.Preview3DCameraInvertLookY;
        vm.Preview3DCameraFlyMoveSpeed = settings.Preview3DCameraFlyMoveSpeed <= 0
            ? 1.0
            : Math.Clamp(settings.Preview3DCameraFlyMoveSpeed, 0.25, 4.0);
        vm.Preview3DCameraFlySmoothAcceleration = settings.Preview3DCameraFlySmoothAcceleration;
        vm.Preview3DItemUseAlphaBlend = settings.Preview3DItemUseAlphaBlend;
        vm.Preview3DEntityAlphaMode = Math.Clamp(settings.Preview3DEntityAlphaMode, 0, 2);
        vm.Preview3DEnableEntityLabPbrShading = settings.Preview3DEnableEntityLabPbrShading;
        vm.Preview3DEnableEntityParallax = settings.Preview3DEnableEntityParallax;
        vm.FastSpecular = settings.FastSpecular;
        vm.FoliageMode = string.IsNullOrWhiteSpace(settings.FoliageMode) ? "No Height" : settings.FoliageMode;
        vm.UseLegacyExtractor = settings.UseLegacyExtractor;
        vm.SmoothnessScale = settings.SmoothnessScale;
        vm.MetallicBoost = settings.MetallicBoost;
        vm.PorosityBias = settings.PorosityBias;
        vm.PlantMaterialPorosityExtra = Math.Clamp(
            settings.PlantMaterialPorosityExtra ?? AutoPBRDefaults.DefaultPlantMaterialPorosityExtra,
            -128,
            128);
        vm.MaxThreads = settings.MaxThreads;
        vm.TempDirectory = settings.TempDirectory;
        if (string.IsNullOrWhiteSpace(settings.MinecraftAssetsDirectory))
        {
            vm.MinecraftAssetsDirectory = MinecraftInstallPathDetector.TryDetectDefaultAssetsRoot();
        }
        else
        {
            vm.MinecraftAssetsDirectory = settings.MinecraftAssetsDirectory;
        }
        vm.DebugMode = settings.DebugMode;
        vm.PreviewUseOpenGl4 = settings.PreviewUseOpenGl4;
        vm.PreviewHdrMode = PreviewHdrPresentPolicy.FormatMode(
            PreviewHdrPresentPolicy.ParseMode(settings.PreviewHdrMode));
        vm.PreviewHdrPaperWhiteNits = PreviewHdrPresentPolicy.ClampPaperWhiteNits(
            (float)(settings.PreviewHdrPaperWhiteNits <= 0
                ? PreviewHdrPresentPolicy.DefaultPaperWhiteNits
                : settings.PreviewHdrPaperWhiteNits));
        vm.ColorScheme = string.IsNullOrWhiteSpace(settings.ColorScheme) ? "Dark" : settings.ColorScheme;
        vm.UiScale = settings.UiScale <= 0
            ? 1.0
            : Math.Clamp(settings.UiScale, MainWindowViewModel.MinUiScale, MainWindowViewModel.MaxUiScale);
        vm.ProcessBlocks = settings.ProcessBlocks;
        vm.ProcessItems = settings.ProcessItems;
        vm.ProcessArmor = settings.ProcessArmor;
        vm.ProcessEntity = settings.ProcessEntity;
        vm.ProcessParticles = settings.ProcessParticles;
        vm.UseDeepBumpNormals = settings.UseDeepBumpNormals;
        vm.DeepBumpOverlap = string.IsNullOrWhiteSpace(settings.DeepBumpOverlap) ? "Large" : settings.DeepBumpOverlap;
        vm.DeepBumpInputMode = string.IsNullOrWhiteSpace(settings.DeepBumpInputMode)
            ? nameof(DeepBumpInputMode.Auto)
            : settings.DeepBumpInputMode;
        vm.DeepBumpForceBlue255 = settings.DeepBumpForceBlue255;
        vm.DeepBumpNormalIntensity = settings.DeepBumpNormalIntensity <= 0
            ? AutoPBRDefaults.DefaultNormalIntensity
            : settings.DeepBumpNormalIntensity;
        vm.DeepBumpNormalSoftClamp = Math.Clamp(settings.DeepBumpNormalSoftClamp, 0.0, 2.0);
        vm.DeepBumpEdgeGuidedEnhance = settings.DeepBumpEdgeGuidedEnhance;
        vm.DeepBumpEdgeGuidedStrength = Math.Clamp(settings.DeepBumpEdgeGuidedStrength, 0.0, 6.0);
        vm.DeepBumpEdgeGuidedGamma = Math.Clamp(settings.DeepBumpEdgeGuidedGamma, 0.1, 8.0);
        vm.DeepBumpEdgeGuidedDirectionMix = Math.Clamp(settings.DeepBumpEdgeGuidedDirectionMix, 0.0, 1.0);
        vm.NormalHeightTransparentAlphaClampMax = Math.Clamp(settings.NormalHeightTransparentAlphaClampMax, 0, 255);
        vm.NormalOperator = string.IsNullOrWhiteSpace(settings.NormalOperator)
            ? nameof(NormalOperator.SobelVc)
            : settings.NormalOperator;
        vm.NormalKernelSize = string.IsNullOrWhiteSpace(settings.NormalKernelSize) ? "3" : settings.NormalKernelSize;
        vm.NormalDerivative = string.IsNullOrWhiteSpace(settings.NormalDerivative)
            ? nameof(NormalDerivative.Luminance)
            : settings.NormalDerivative;

        vm.PreprocessLinearize = settings.PreprocessLinearize;
        vm.PreprocessDenoiseRadius = settings.PreprocessDenoiseRadius;
        vm.PreprocessDenoiseBlend = settings.PreprocessDenoiseBlend;
        vm.PreprocessFrequencySplit = settings.PreprocessFrequencySplit;
        vm.PreprocessFrequencyRadius = settings.PreprocessFrequencyRadius;
        vm.PreprocessFrequencyDetailStrength = settings.PreprocessFrequencyDetailStrength;

        vm.SpecularUsePercentileRemap = settings.SpecularUsePercentileRemap;
        vm.SpecularRemapLowPercentile = settings.SpecularRemapLowPercentile;
        vm.SpecularRemapHighPercentile = settings.SpecularRemapHighPercentile;
        vm.SpecularForceNoEmissive = settings.SpecularForceNoEmissive;
        vm.UseMlSpecularPredictor = settings.UseMlSpecularPredictor;
        vm.MlSpecularModelPath = settings.MlSpecularModelPath;
        vm.MlSpecularModelPath16 = settings.MlSpecularModelPath16;
        vm.MlSpecularModelPath32 = settings.MlSpecularModelPath32;
        vm.MlSpecularModelPath64 = settings.MlSpecularModelPath64;
        vm.MlSpecularModelPath128 = settings.MlSpecularModelPath128;
        vm.MlSpecularModelPath256 = settings.MlSpecularModelPath256;
        vm.MlSpecularHeuristicBlend = Math.Clamp(
            settings.MlSpecularHeuristicBlend ?? AutoPBRDefaults.DefaultMlSpecularHeuristicBlend,
            0.0,
            1.0);
        {
            var mode = string.IsNullOrWhiteSpace(settings.MlSpecularHeuristicBlendMode)
                ? nameof(MlSpecularHeuristicBlendMode.SmoothnessOnly)
                : settings.MlSpecularHeuristicBlendMode.Trim();
            if (!Enum.TryParse<MlSpecularHeuristicBlendMode>(mode, ignoreCase: true, out _))
            {
                mode = nameof(MlSpecularHeuristicBlendMode.SmoothnessOnly);
            }

            vm.MlSpecularHeuristicBlendMode = mode;
        }
        {
            var math = string.IsNullOrWhiteSpace(settings.MlSpecularBlendMath)
                ? nameof(MlSpecularBlendMath.Linear)
                : settings.MlSpecularBlendMath.Trim();
            if (!Enum.TryParse<MlSpecularBlendMath>(math, ignoreCase: true, out _))
            {
                math = nameof(MlSpecularBlendMath.Linear);
            }

            vm.MlSpecularBlendMath = math;
        }
        vm.MlSpecularUseEdgeChannel = settings.MlSpecularUseEdgeChannel;
        vm.MlSpecularTransparentAlphaClampMax = Math.Clamp(settings.MlSpecularTransparentAlphaClampMax, 0, 255);
        vm.SpecularDebugDisableHeuristicSpecular = settings.SpecularDebugDisableHeuristicSpecular;
        vm.SpecularDebugSkipSpecularRemap = settings.SpecularDebugSkipSpecularRemap;
        vm.SpecularDebugVerboseSpecularMl = settings.SpecularDebugVerboseSpecularMl;

        vm.GenerateAo = settings.GenerateAo;
        vm.AoRadius = settings.AoRadius;
        vm.AoStrength = settings.AoStrength;
        vm.PreferOnnxTensorRtExecutionProvider = settings.PreferOnnxTensorRtExecutionProvider;

        vm.UseSemanticMaterialTags = settings.UseSemanticMaterialTags;
        vm.MaterialTagMinSimilarity = settings.MaterialTagMinSimilarity <= 0 || settings.MaterialTagMinSimilarity > 1
            ? 0.25
            : settings.MaterialTagMinSimilarity;
        vm.MaterialTagCertaintyThreshold = settings.MaterialTagCertaintyThreshold <= 0 || settings.MaterialTagCertaintyThreshold > 1
            ? 0.35
            : settings.MaterialTagCertaintyThreshold;
        vm.MaterialTagMaxCount = Math.Clamp(settings.MaterialTagMaxCount <= 0 ? 3 : settings.MaterialTagMaxCount, 1, 16);
        vm.DictionaryEvidenceEnabled = settings.DictionaryEvidenceEnabled;
        vm.DictionaryEvidenceWeight = Math.Clamp(settings.DictionaryEvidenceWeight, 0.0, 1.0);
        vm.DictionaryMinEvidenceScore = Math.Clamp(settings.DictionaryMinEvidenceScore, -1.0, 1.0);
        vm.DictionaryRequestTimeoutMs = Math.Clamp(settings.DictionaryRequestTimeoutMs <= 0 ? 900 : settings.DictionaryRequestTimeoutMs, 100, 5000);

        vm.CustomTagRules.Clear();
        foreach (var entry in settings.CustomTagRules)
        {
            vm.CustomTagRules.Add(entry);
        }
    }

    public static void SaveFrom(MainWindowViewModel vm, UserSettings settings)
    {
        settings.OutputDirectory = vm.OutputDirectory;
        settings.BatchFolderPath = vm.BatchFolderPath;
        settings.UseBatchFolderInput = vm.UseBatchFolderInput;
        settings.NormalIntensity = vm.NormalIntensity;
        settings.HeightIntensity = vm.HeightIntensity;
        settings.BrickHeightMapPostProcessEnabled = vm.BrickHeightMapPostProcessEnabled;
        settings.BrickHeightMinStructuralConfidence = vm.BrickHeightMinStructuralConfidence;
        settings.BrickHeightInvertDeltaThreshold = vm.BrickHeightInvertDeltaThreshold;
        settings.BrickLightGroutDiffuseDeltaMin = vm.BrickLightGroutDiffuseDeltaMin;
        settings.PreviewBrickProbeDebug = vm.PreviewBrickProbeDebug;
        settings.PreviewDisplayMode = Math.Clamp(vm.PreviewDisplayMode, 0, 1);
        settings.Preview3DAutoRotate = vm.Preview3DAutoRotate;
        settings.Preview3DEntityAnimationSpeed = Math.Clamp(vm.Preview3DEntityAnimationSpeed, 0.0, 4.0);
        settings.Preview3DEntityAnimationAmplitude = Math.Clamp(vm.Preview3DEntityAnimationAmplitude, 0.0, 2.0);
        settings.Preview3DEnableEntityAnimation = vm.Preview3DEnableEntityAnimation;
        settings.Preview3DEnableLegacyEntityWobble = vm.Preview3DEnableLegacyEntityWobble;
        settings.Preview3DPauseEntityIdleAnimation = vm.Preview3DPauseEntityIdleAnimation;
        settings.Preview3DShowGrid = vm.Preview3DShowGrid;
        settings.Preview3DGridColorArgb = vm.Preview3DGridColor.ToUInt32();
        settings.Preview3DShowGroundMesh = vm.Preview3DShowGroundMesh;
        settings.Preview3DChunkViewDistance = (int)Math.Round(Math.Clamp(vm.Preview3DChunkViewDistance, 2, 24));
        settings.Preview3DWorldSeed = (int)Math.Clamp(Math.Round(vm.Preview3DWorldSeed), 0, int.MaxValue);
        settings.Preview3DTerrainBiomeSize = Math.Clamp(
            vm.Preview3DTerrainBiomeSize,
            PreviewStageConstants.TerrainMinBiomeSize,
            PreviewStageConstants.TerrainMaxBiomeSize);
        settings.Preview3DTerrainAmplification = Math.Clamp(
            vm.Preview3DTerrainAmplification,
            PreviewStageConstants.TerrainMinAmplification,
            PreviewStageConstants.TerrainMaxAmplification);
        settings.Preview3DTerrainErosionStrength = Math.Clamp(
            vm.Preview3DTerrainErosionStrength,
            PreviewStageConstants.TerrainMinErosionStrength,
            PreviewStageConstants.TerrainMaxErosionStrength);
        settings.Preview3DTerrainContinentalness = Math.Clamp(
            vm.Preview3DTerrainContinentalness,
            PreviewStageConstants.TerrainMinContinentalness,
            PreviewStageConstants.TerrainMaxContinentalness);
        settings.Preview3DGrassColormapTemperature = Math.Clamp(vm.Preview3DGrassColormapTemperature, 0.0, 1.0);
        settings.Preview3DGrassColormapDownfall = Math.Clamp(vm.Preview3DGrassColormapDownfall, 0.0, 1.0);
        settings.Preview3DShowAxes = vm.Preview3DShowAxes;
        settings.Preview3DShowFpsCounter = vm.Preview3DShowFpsCounter;
        settings.Preview3DLogGpuPassTimings = vm.Preview3DLogGpuPassTimings;
        settings.Preview3DLogVerbosePreviewDiagnostics = vm.Preview3DLogVerbosePreviewDiagnostics;
        settings.Preview3DShowExpandedGpuTimingHud = vm.Preview3DShowExpandedGpuTimingHud;
        settings.Preview3DOcclusionDebugMode = Math.Clamp(vm.Preview3DOcclusionDebugMode, 0, 2);
        settings.Preview3DVSyncEnabled = vm.Preview3DVSyncEnabled;
        settings.Preview3DCapFpsAt60 = false;
        settings.Preview3DEnableParallax = vm.Preview3DEnableParallax;
        settings.Preview3DEnableNormalMap = vm.Preview3DEnableNormalMap;
        settings.Preview3DEnableSpecularMap = vm.Preview3DEnableSpecularMap;
        settings.Preview3DParallaxHeightStrength = Math.Clamp(vm.Preview3DParallaxHeightStrength, 0.0, 1.0);
        settings.Preview3DParallaxTraceLayers = Math.Clamp(vm.Preview3DParallaxTraceLayers, 8.0, 128.0);
        settings.Preview3DParallaxRefineSteps = Math.Clamp(vm.Preview3DParallaxRefineSteps, 0.0, 8.0);
        settings.Preview3DParallaxShadowSamples = Math.Clamp(vm.Preview3DParallaxShadowSamples, 4.0, 64.0);
        settings.Preview3DParallaxShadowSoftness = Math.Clamp(vm.Preview3DParallaxShadowSoftness, 0.0, 4.0);
        settings.Preview3DParallaxMaxUvShift = Math.Clamp(vm.Preview3DParallaxMaxUvShift, 0.05, 0.75);
        settings.Preview3DEnableTessellationDisplacement = vm.Preview3DEnableTessellationDisplacement;
        settings.Preview3DTessellationLevel = Math.Clamp(vm.Preview3DTessellationLevel, 1.0, 16.0);
        settings.Preview3DTessellationDisplacementStrength = Math.Clamp(vm.Preview3DTessellationDisplacementStrength, 0.0, 0.20);
        settings.Preview3DEnableSss = vm.Preview3DEnableSss;
        settings.Preview3DEnableParallaxShadow = vm.Preview3DEnableParallaxShadow;
        settings.Preview3DEnableParallaxAo = vm.Preview3DEnableParallaxAo;
        settings.Preview3DParallaxAoStrength = Math.Clamp(vm.Preview3DParallaxAoStrength, 0.0, 2.0);
        settings.Preview3DEnableIbl = vm.Preview3DEnableIbl;
        settings.Preview3DIblStrength = Math.Clamp(vm.Preview3DIblStrength, 0.0, 2.0);
        settings.Preview3DEnableAtmosphericSky = vm.Preview3DEnableAtmosphericSky;
        settings.Preview3DAtmosphereTurbidity = Math.Clamp(vm.Preview3DAtmosphereTurbidity, 1.2, 10.0);
        settings.Preview3DAtmosphereSunIntensity = Math.Clamp(vm.Preview3DAtmosphereSunIntensity, 0.2, 64.0);
        settings.Preview3DAtmosphereHorizonFalloff = Math.Clamp(vm.Preview3DAtmosphereHorizonFalloff, 0.25, 4.0);
        settings.Preview3DAtmosphereSkyExposure = Math.Clamp(vm.Preview3DAtmosphereSkyExposure, 0.1, 3.0);
        settings.Preview3DAtmosphereSunDiscStrength = Math.Clamp(vm.Preview3DAtmosphereSunDiscStrength, 0.0, 2.0);
        settings.Preview3DAtmosphereSunDiscBrightness = Math.Clamp(vm.Preview3DAtmosphereSunDiscBrightness, 0.0, 4.0);
        settings.Preview3DAtmosphereSunDiscSize = Math.Clamp(vm.Preview3DAtmosphereSunDiscSize, 0.05, 2.0);
        settings.Preview3DAtmosphereMoonDiscStrength = Math.Clamp(vm.Preview3DAtmosphereMoonDiscStrength, 0.0, 4.0);
        settings.Preview3DAtmosphereMoonDiscSize = Math.Clamp(vm.Preview3DAtmosphereMoonDiscSize, 0.05, 3.0);
        settings.Preview3DAtmosphereMoonGlowStrength = Math.Clamp(vm.Preview3DAtmosphereMoonGlowStrength, 0.0, 4.0);
        settings.Preview3DAtmosphereMoonTextureSharpness = Math.Clamp(vm.Preview3DAtmosphereMoonTextureSharpness, 0.0, 4.0);
        settings.Preview3DMoonWorldLightIntensity = Math.Clamp(vm.Preview3DMoonWorldLightIntensity, 0.0, 8.0);
        settings.Preview3DShowCelestialDebug = vm.Preview3DShowCelestialDebug;
        settings.Preview3DTimeOfDayHours = Math.Clamp(vm.Preview3DTimeOfDayHours, 0.0, 24.0);
        settings.Preview3DAnimateTimeOfDay = vm.Preview3DAnimateTimeOfDay;
        settings.Preview3DTimeOfDaySpeed = Math.Clamp(vm.Preview3DTimeOfDaySpeed, 0.1, 4.0);
        settings.Preview3DHorizonFogStrength = Math.Clamp(vm.Preview3DHorizonFogStrength, 0.0, 2.0);
        settings.Preview3DEnableGodRays = vm.Preview3DEnableGodRays;
        settings.Preview3DEnableVolumetricClouds = vm.Preview3DEnableVolumetricClouds;
        settings.Preview3DVolumetricQuality = Math.Clamp(vm.Preview3DVolumetricQuality, 0, 2);
        settings.Preview3DGodRayStrength = Math.Clamp(vm.Preview3DGodRayStrength, 0.0, 2.0);
        settings.Preview3DGodRayScatterGain = Math.Clamp(vm.Preview3DGodRayScatterGain, 0.0, 20.0);
        settings.Preview3DGodRayExtinction = Math.Clamp(vm.Preview3DGodRayExtinction, 0.01, 8.0);
        settings.Preview3DGodRayDebugDensity = Math.Clamp(vm.Preview3DGodRayDebugDensity, 0.0, 2.0);
        settings.Preview3DGodRayStabilizeDebug = vm.Preview3DGodRayStabilizeDebug;
        settings.Preview3DCloudDensity = Math.Clamp(vm.Preview3DCloudDensity, 0.0, 2.0);
        settings.Preview3DCloudCoverageScale = Math.Clamp(vm.Preview3DCloudCoverageScale, 0.0, 2.0);
        settings.Preview3DCloudLayerHeight = Math.Clamp(vm.Preview3DCloudLayerHeight, -12.0, 48.0);
        settings.Preview3DCloudVolumeHeight = Math.Clamp(vm.Preview3DCloudVolumeHeight, 4.0, 96.0);
        settings.Preview3DCloudVolumeSize = Math.Clamp(vm.Preview3DCloudVolumeSize, 8.0, 256.0);
        settings.Preview3DCloudWindSpeed = Math.Clamp(vm.Preview3DCloudWindSpeed, 0.0, 12.0);
        settings.Preview3DCloudWindHeadingDegrees = Math.Clamp(vm.Preview3DCloudWindHeadingDegrees, -180.0, 180.0);
        settings.Preview3DCloudCirrusStrength = Math.Clamp(vm.Preview3DCloudCirrusStrength, 0.0, 2.0);
        settings.Preview3DCloudDebugView = Math.Clamp(vm.Preview3DCloudDebugView, 0, 2);
        settings.Preview3DCloudDisableTemporal = vm.Preview3DCloudDisableTemporal;
        settings.Preview3DCloudMarchStepOverride = Math.Clamp(vm.Preview3DCloudMarchStepOverride, 0.0, 64.0);
        settings.Preview3DCloudFreezeWind = vm.Preview3DCloudFreezeWind;
        settings.Preview3DEnablePreviewTaa = vm.Preview3DEnablePreviewTaa;
        settings.Preview3DTaaMode = Math.Clamp(vm.Preview3DTaaMode, 0, 4);
        settings.PersistedSettingsGeneration = CurrentPersistedSettingsGeneration;
        settings.Preview3DTaaTemporalScale = Math.Clamp(vm.Preview3DTaaTemporalScale, 0.0, 1.25);
        settings.Preview3DTaaJitterScale = Math.Clamp(vm.Preview3DTaaJitterScale, 0.0, 2.0);
        settings.Preview3DTaaSourceFilterScale = Math.Clamp(vm.Preview3DTaaSourceFilterScale, 0.0, 2.0);
        settings.Preview3DTaaEdgeBlendScale = Math.Clamp(vm.Preview3DTaaEdgeBlendScale, 0.0, 2.0);
        settings.Preview3DTaaFxaaStrengthScale = Math.Clamp(vm.Preview3DTaaFxaaStrengthScale, 0.0, 5.0);
        settings.Preview3DTaaFxaaLumaEdgeScale = Math.Clamp(vm.Preview3DTaaFxaaLumaEdgeScale, 0.0, 2.0);
        settings.Preview3DTaaFxaaLumaThreshold = Math.Clamp(vm.Preview3DTaaFxaaLumaThreshold, 0.001, 0.12);
        settings.Preview3DTaaForceFxaa = vm.Preview3DTaaForceFxaa;
        settings.Preview3DEnableShadows = vm.Preview3DEnableShadows;
        settings.Preview3DLightYawDegrees = Math.Clamp(vm.Preview3DLightYawDegrees, -180.0, 180.0);
        settings.Preview3DLightPitchDegrees = Math.Clamp(vm.Preview3DLightPitchDegrees, -89.0, 89.0);
        settings.Preview3DEnableShadowCascades = vm.Preview3DEnableShadowCascades;
        settings.Preview3DShadowDistance = Math.Clamp(vm.Preview3DShadowDistance, 32.0, 256.0);
        settings.Preview3DSpritePlaneCount = Math.Clamp(vm.Preview3DSpritePlaneCount, 1, 8);
        settings.Preview3DSpriteThickness = Math.Clamp(
            vm.Preview3DSpriteThickness,
            PreviewStageConstants.SpriteThicknessMin,
            PreviewStageConstants.SpriteThicknessMax);
        settings.Preview3DCameraOrbitSensitivity = Math.Clamp(vm.Preview3DCameraOrbitSensitivity, 0.0008, 0.04);
        settings.Preview3DCameraPanSensitivity = Math.Clamp(vm.Preview3DCameraPanSensitivity, 0.0003, 0.02);
        settings.Preview3DCameraZoomSensitivity = Math.Clamp(vm.Preview3DCameraZoomSensitivity, 0.02, 0.5);
        settings.Preview3DCameraOrbitBoomDistance = Math.Clamp(vm.Preview3DCameraOrbitBoomDistance, 1.05, 120.0);
        settings.Preview3DCameraResetKey = string.IsNullOrWhiteSpace(vm.Preview3DCameraResetKey)
            ? "R"
            : vm.Preview3DCameraResetKey.Trim();
        settings.Preview3DCameraFlyLookSensitivity = Math.Clamp(vm.Preview3DCameraFlyLookSensitivity, 0.0008, 0.04);
        settings.Preview3DCameraInvertLookY = vm.Preview3DCameraInvertLookY;
        settings.Preview3DCameraFlyMoveSpeed = Math.Clamp(vm.Preview3DCameraFlyMoveSpeed, 0.25, 4.0);
        settings.Preview3DCameraFlySmoothAcceleration = vm.Preview3DCameraFlySmoothAcceleration;
        settings.Preview3DItemUseAlphaBlend = vm.Preview3DItemUseAlphaBlend;
        settings.Preview3DEntityAlphaMode = Math.Clamp(vm.Preview3DEntityAlphaMode, 0, 2);
        settings.Preview3DEnableEntityLabPbrShading = vm.Preview3DEnableEntityLabPbrShading;
        settings.Preview3DEnableEntityParallax = vm.Preview3DEnableEntityParallax;
        settings.FastSpecular = vm.FastSpecular;
        settings.FoliageMode = vm.FoliageMode;
        settings.UseLegacyExtractor = vm.UseLegacyExtractor;
        settings.SmoothnessScale = vm.SmoothnessScale;
        settings.MetallicBoost = vm.MetallicBoost;
        settings.PorosityBias = vm.PorosityBias;
        settings.PlantMaterialPorosityExtra = vm.PlantMaterialPorosityExtra;
        settings.MaxThreads = vm.MaxThreads;
        settings.TempDirectory = vm.TempDirectory;
        settings.MinecraftAssetsDirectory = vm.MinecraftAssetsDirectory;
        settings.DebugMode = vm.DebugMode;
        settings.PreviewUseOpenGl4 = vm.PreviewUseOpenGl4;
        settings.PreviewHdrMode = PreviewHdrPresentPolicy.FormatMode(
            PreviewHdrPresentPolicy.ParseMode(vm.PreviewHdrMode));
        settings.PreviewHdrPaperWhiteNits = PreviewHdrPresentPolicy.ClampPaperWhiteNits(
            (float)vm.PreviewHdrPaperWhiteNits);
        settings.ColorScheme = vm.ColorScheme;
        settings.UiScale = Math.Clamp(vm.UiScale, MainWindowViewModel.MinUiScale, MainWindowViewModel.MaxUiScale);
        settings.Language = vm.SelectedLanguage?.CultureCode ?? "en";
        settings.ProcessBlocks = vm.ProcessBlocks;
        settings.ProcessItems = vm.ProcessItems;
        settings.ProcessArmor = vm.ProcessArmor;
        settings.ProcessEntity = vm.ProcessEntity;
        settings.ProcessParticles = vm.ProcessParticles;
        settings.UseDeepBumpNormals = vm.UseDeepBumpNormals;
        settings.DeepBumpOverlap = vm.DeepBumpOverlap;
        settings.DeepBumpInputMode = vm.DeepBumpInputMode;
        settings.DeepBumpForceBlue255 = vm.DeepBumpForceBlue255;
        settings.DeepBumpNormalIntensity = vm.DeepBumpNormalIntensity;
        settings.DeepBumpNormalSoftClamp = Math.Clamp(vm.DeepBumpNormalSoftClamp, 0.0, 2.0);
        settings.DeepBumpEdgeGuidedEnhance = vm.DeepBumpEdgeGuidedEnhance;
        settings.DeepBumpEdgeGuidedStrength = Math.Clamp(vm.DeepBumpEdgeGuidedStrength, 0.0, 6.0);
        settings.DeepBumpEdgeGuidedGamma = Math.Clamp(vm.DeepBumpEdgeGuidedGamma, 0.1, 8.0);
        settings.DeepBumpEdgeGuidedDirectionMix = Math.Clamp(vm.DeepBumpEdgeGuidedDirectionMix, 0.0, 1.0);
        settings.NormalHeightTransparentAlphaClampMax = Math.Clamp(vm.NormalHeightTransparentAlphaClampMax, 0, 255);
        settings.NormalOperator = vm.NormalOperator;
        settings.NormalKernelSize = vm.NormalKernelSize;
        settings.NormalDerivative = vm.NormalDerivative;

        settings.PreprocessLinearize = vm.PreprocessLinearize;
        settings.PreprocessDenoiseRadius = vm.PreprocessDenoiseRadius;
        settings.PreprocessDenoiseBlend = vm.PreprocessDenoiseBlend;
        settings.PreprocessFrequencySplit = vm.PreprocessFrequencySplit;
        settings.PreprocessFrequencyRadius = vm.PreprocessFrequencyRadius;
        settings.PreprocessFrequencyDetailStrength = vm.PreprocessFrequencyDetailStrength;

        settings.SpecularUsePercentileRemap = vm.SpecularUsePercentileRemap;
        settings.SpecularRemapLowPercentile = vm.SpecularRemapLowPercentile;
        settings.SpecularRemapHighPercentile = vm.SpecularRemapHighPercentile;
        settings.SpecularForceNoEmissive = vm.SpecularForceNoEmissive;
        settings.UseMlSpecularPredictor = vm.UseMlSpecularPredictor;
        settings.MlSpecularModelPath = vm.MlSpecularModelPath;
        settings.MlSpecularModelPath16 = vm.MlSpecularModelPath16;
        settings.MlSpecularModelPath32 = vm.MlSpecularModelPath32;
        settings.MlSpecularModelPath64 = vm.MlSpecularModelPath64;
        settings.MlSpecularModelPath128 = vm.MlSpecularModelPath128;
        settings.MlSpecularModelPath256 = vm.MlSpecularModelPath256;
        settings.MlSpecularHeuristicBlend = Math.Clamp(vm.MlSpecularHeuristicBlend, 0.0, 1.0);
        settings.MlSpecularHeuristicBlendMode = vm.MlSpecularHeuristicBlendMode;
        settings.MlSpecularBlendMath = vm.MlSpecularBlendMath;
        settings.MlSpecularUseEdgeChannel = vm.MlSpecularUseEdgeChannel;
        settings.MlSpecularTransparentAlphaClampMax = Math.Clamp(vm.MlSpecularTransparentAlphaClampMax, 0, 255);
        settings.SpecularDebugDisableHeuristicSpecular = vm.SpecularDebugDisableHeuristicSpecular;
        settings.SpecularDebugSkipSpecularRemap = vm.SpecularDebugSkipSpecularRemap;
        settings.SpecularDebugVerboseSpecularMl = vm.SpecularDebugVerboseSpecularMl;

        settings.GenerateAo = vm.GenerateAo;
        settings.AoRadius = vm.AoRadius;
        settings.AoStrength = vm.AoStrength;
        settings.PreferOnnxTensorRtExecutionProvider = vm.PreferOnnxTensorRtExecutionProvider;
        settings.UseSemanticMaterialTags = vm.UseSemanticMaterialTags;
        settings.MaterialTagMinSimilarity = Math.Clamp(vm.MaterialTagMinSimilarity, 0.05, 0.99);
        settings.MaterialTagCertaintyThreshold = Math.Clamp(vm.MaterialTagCertaintyThreshold, 0.05, 0.99);
        settings.MaterialTagMaxCount = Math.Clamp(vm.MaterialTagMaxCount, 1, 16);
        settings.DictionaryEvidenceEnabled = vm.DictionaryEvidenceEnabled;
        settings.DictionaryEvidenceWeight = Math.Clamp(vm.DictionaryEvidenceWeight, 0.0, 1.0);
        settings.DictionaryMinEvidenceScore = Math.Clamp(vm.DictionaryMinEvidenceScore, -1.0, 1.0);
        settings.DictionaryRequestTimeoutMs = Math.Clamp(vm.DictionaryRequestTimeoutMs, 100, 5000);
        settings.CustomTagRules = vm.CustomTagRules.ToList();
        settings.Save();
    }

    private static int ResolvePreview3DTaaMode(UserSettings settings)
    {
        if (settings.PersistedSettingsGeneration >= CurrentPersistedSettingsGeneration)
        {
            return Math.Clamp(settings.Preview3DTaaMode ?? DefaultPreview3DTaaMode, 0, 4);
        }

        if (settings.Preview3DTaaMode is null)
        {
            return DefaultPreview3DTaaMode;
        }

        return Math.Clamp(settings.Preview3DTaaMode.Value, 0, 4) switch
        {
            0 => 1,
            1 => 0,
            var mode => mode,
        };
    }
}
