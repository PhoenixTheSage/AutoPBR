namespace AutoPBR.App.Rendering.Abstractions;

public readonly struct PreviewRenderSettingsSnapshot
{
    public float NormalStrength { get; init; }
    public float HeightStrength { get; init; }
    public float SpecularStrength { get; init; }
    public float RoughnessScale { get; init; }
    public float AmbientStrength { get; init; }
    public float Exposure { get; init; }
    public bool EnableParallax { get; init; }
    public bool EnableNormalMap { get; init; }
    public bool EnableSpecularMap { get; init; }
    public bool AutoRotate { get; init; }
    public float LightYawDegrees { get; init; }
    public float LightPitchDegrees { get; init; }
    public bool NearestTextureFilter { get; init; }
    public float AlphaCutoff { get; init; }
    public bool ItemUseAlphaBlend { get; init; }
    public PreviewEntityAlphaMode EntityAlphaMode { get; init; }
    public bool EnableEntityLabPbrShading { get; init; }
    public bool EnableEntityParallax { get; init; }
    public int SpritePlaneCount { get; init; }
    public float SpriteThickness { get; init; }
    public bool ItemFlatSpritePreview { get; init; }
    public bool ShowBackgroundGrid { get; init; }
    public float GridColorR { get; init; }
    public float GridColorG { get; init; }
    public float GridColorB { get; init; }
    public float GridColorA { get; init; }
    public bool ShowGroundMesh { get; init; }
    public int ChunkViewDistance { get; init; }
    public int TerrainWorldSeed { get; init; }
    public float TerrainBiomeSize { get; init; }
    public float TerrainAmplification { get; init; }
    public float TerrainErosionStrength { get; init; }
    public float TerrainContinentalness { get; init; }
    public bool ShowCornerAxes { get; init; }
    public bool DrawPreviewSubject { get; init; }
    public bool EnableSss { get; init; }
    public bool EnableParallaxShadow { get; init; }
    public int ParallaxTraceLayers { get; init; }
    public int ParallaxRefineSteps { get; init; }
    public int ParallaxShadowSamples { get; init; }
    public float ParallaxShadowSoftness { get; init; }
    public float ParallaxMaxUvShift { get; init; }
    public bool EnableTessellationDisplacement { get; init; }
    public float TessellationLevel { get; init; }
    public float TessellationDisplacementStrength { get; init; }
    public bool EnableParallaxAo { get; init; }
    public float ParallaxAoStrength { get; init; }
    public bool EnableIbl { get; init; }
    public bool EnableAtmosphericSky { get; init; }
    public float AtmosphereTurbidity { get; init; }
    public float AtmosphereSunIntensity { get; init; }
    public float AtmosphereHorizonFalloff { get; init; }
    public float AtmosphereSkyExposure { get; init; }
    public float AtmosphereSunDiscStrength { get; init; }
    public float AtmosphereSunDiscBrightness { get; init; }
    public float AtmosphereSunDiscSize { get; init; }
    public float AtmosphereMoonDiscStrength { get; init; }
    public float AtmosphereMoonDiscSize { get; init; }
    public float AtmosphereMoonGlowStrength { get; init; }
    public float AtmosphereMoonTextureSharpness { get; init; }
    public float MoonWorldLightIntensity { get; init; }
    public float AerialFogStrength { get; init; }
    public float TimeOfDayHours { get; init; }
    public bool AnimateTimeOfDay { get; init; }
    public float TimeOfDaySpeed { get; init; }
    public bool CapturePreviewFingerprint { get; init; }
    public float SssStrength { get; init; }
    public float IblStrength { get; init; }
    public float EmissionStrength { get; init; }
    public float EntityAnimationSpeed { get; init; }
    public float EntityAnimationAmplitude { get; init; }
    public bool EnableEntityAnimation { get; init; }
    public bool PauseEntityIdleAnimation { get; init; }
    public bool EnableLegacyEntityWobble { get; init; }
    public bool ForceEntityCpuSkinning { get; init; }
    public bool EnableShadows { get; init; }
    public int ShadowMapResolution { get; init; }
    public float ShadowMinBias { get; init; }
    public float ShadowMaxBias { get; init; }
    public float ShadowSoftnessTexels { get; init; }
    public float ShadowDistance { get; init; }
    public bool EnableShadowCascades { get; init; }
    public bool EnableGodRays { get; init; }
    public bool EnableVolumeGodRays { get; init; }
    public bool EnableVolumetricClouds { get; init; }
    public int VolumetricQuality { get; init; }
    public float GodRayStrength { get; init; }
    public float GodRayConeScale { get; init; }
    public float GodRayScatterGain { get; init; }
    public float GodRayExtinction { get; init; }
    public float GodRayDebugDensity { get; init; }
    public bool GodRayStabilizeDebug { get; init; }
    public bool GodRaySparseMarch { get; init; }
    public float CloudDensity { get; init; }
    public float CloudVolumeSize { get; init; }
    public float CloudLayerHeight { get; init; }
    public float CloudVolumeHeight { get; init; }
    public int CloudQuality { get; init; }
    public float CloudCoverageScale { get; init; }
    public float CloudWindSpeed { get; init; }
    public float CloudWindHeadingDegrees { get; init; }
    public float CloudCirrusStrength { get; init; }
    public PreviewCloudDebugView CloudDebugView { get; init; }
    public bool CloudDisableTemporal { get; init; }
    public int CloudMarchStepOverride { get; init; }
    public bool CloudFreezeWind { get; init; }
    public bool LogVolumetricTiming { get; init; }
    public bool LogPreviewTaaDiagnostics { get; init; }
    public bool LogGpuPassTimings { get; init; }
    public bool ShowExpandedGpuTimingHud { get; init; }
    public int OcclusionDebugMode { get; init; }
    public bool EnablePreviewTaa { get; init; }
    public int PreviewTaaMode { get; init; }
    public float PreviewTaaTemporalScale { get; init; }
    public float PreviewTaaJitterScale { get; init; }
    public float PreviewTaaSourceFilterScale { get; init; }
    public float PreviewTaaEdgeBlendScale { get; init; }
    public float PreviewTaaFxaaStrengthScale { get; init; }
    public float PreviewTaaFxaaLumaEdgeScale { get; init; }
    public float PreviewTaaFxaaLumaThreshold { get; init; }
    public bool PreviewTaaForceFxaa { get; init; }
    public bool ShowSunProjectionDebug { get; init; }
    public bool HdrPresentActive { get; init; }
    public float HdrPaperWhiteNits { get; init; }
    public float HdrPeakNits { get; init; }

    public static PreviewRenderSettingsSnapshot From(PreviewRenderSettings s) => new()
    {
        NormalStrength = s.NormalStrength,
        HeightStrength = s.HeightStrength,
        SpecularStrength = s.SpecularStrength,
        RoughnessScale = s.RoughnessScale,
        AmbientStrength = s.AmbientStrength,
        Exposure = s.Exposure,
        EnableParallax = s.EnableParallax,
        EnableNormalMap = s.EnableNormalMap,
        EnableSpecularMap = s.EnableSpecularMap,
        AutoRotate = s.AutoRotate,
        LightYawDegrees = s.LightYawDegrees,
        LightPitchDegrees = s.LightPitchDegrees,
        NearestTextureFilter = s.NearestTextureFilter,
        AlphaCutoff = s.AlphaCutoff,
        ItemUseAlphaBlend = s.ItemUseAlphaBlend,
        EntityAlphaMode = s.EntityAlphaMode,
        EnableEntityLabPbrShading = s.EnableEntityLabPbrShading,
        EnableEntityParallax = s.EnableEntityParallax,
        SpritePlaneCount = s.SpritePlaneCount,
        SpriteThickness = s.SpriteThickness,
        ItemFlatSpritePreview = s.ItemFlatSpritePreview,
        ShowBackgroundGrid = s.ShowBackgroundGrid,
        GridColorR = s.GridColorR,
        GridColorG = s.GridColorG,
        GridColorB = s.GridColorB,
        GridColorA = s.GridColorA,
        ShowGroundMesh = s.ShowGroundMesh,
        ChunkViewDistance = s.ChunkViewDistance,
        TerrainWorldSeed = s.TerrainWorldSeed,
        TerrainBiomeSize = s.TerrainBiomeSize,
        TerrainAmplification = s.TerrainAmplification,
        TerrainErosionStrength = s.TerrainErosionStrength,
        TerrainContinentalness = s.TerrainContinentalness,
        ShowCornerAxes = s.ShowCornerAxes,
        DrawPreviewSubject = s.DrawPreviewSubject,
        EnableSss = s.EnableSss,
        EnableParallaxShadow = s.EnableParallaxShadow,
        ParallaxTraceLayers = s.ParallaxTraceLayers,
        ParallaxRefineSteps = s.ParallaxRefineSteps,
        ParallaxShadowSamples = s.ParallaxShadowSamples,
        ParallaxShadowSoftness = s.ParallaxShadowSoftness,
        ParallaxMaxUvShift = s.ParallaxMaxUvShift,
        EnableTessellationDisplacement = s.EnableTessellationDisplacement,
        TessellationLevel = s.TessellationLevel,
        TessellationDisplacementStrength = s.TessellationDisplacementStrength,
        EnableParallaxAo = s.EnableParallaxAo,
        ParallaxAoStrength = s.ParallaxAoStrength,
        EnableIbl = s.EnableIbl,
        EnableAtmosphericSky = s.EnableAtmosphericSky,
        AtmosphereTurbidity = s.AtmosphereTurbidity,
        AtmosphereSunIntensity = s.AtmosphereSunIntensity,
        AtmosphereHorizonFalloff = s.AtmosphereHorizonFalloff,
        AtmosphereSkyExposure = s.AtmosphereSkyExposure,
        AtmosphereSunDiscStrength = s.AtmosphereSunDiscStrength,
        AtmosphereSunDiscBrightness = s.AtmosphereSunDiscBrightness,
        AtmosphereSunDiscSize = s.AtmosphereSunDiscSize,
        AtmosphereMoonDiscStrength = s.AtmosphereMoonDiscStrength,
        AtmosphereMoonDiscSize = s.AtmosphereMoonDiscSize,
        AtmosphereMoonGlowStrength = s.AtmosphereMoonGlowStrength,
        AtmosphereMoonTextureSharpness = s.AtmosphereMoonTextureSharpness,
        MoonWorldLightIntensity = s.MoonWorldLightIntensity,
        AerialFogStrength = s.AerialFogStrength,
        TimeOfDayHours = s.TimeOfDayHours,
        AnimateTimeOfDay = s.AnimateTimeOfDay,
        TimeOfDaySpeed = s.TimeOfDaySpeed,
        CapturePreviewFingerprint = s.CapturePreviewFingerprint,
        SssStrength = s.SssStrength,
        IblStrength = s.IblStrength,
        EmissionStrength = s.EmissionStrength,
        EntityAnimationSpeed = s.EntityAnimationSpeed,
        EntityAnimationAmplitude = s.EntityAnimationAmplitude,
        EnableEntityAnimation = s.EnableEntityAnimation,
        PauseEntityIdleAnimation = s.PauseEntityIdleAnimation,
        EnableLegacyEntityWobble = s.EnableLegacyEntityWobble,
        ForceEntityCpuSkinning = s.ForceEntityCpuSkinning,
        EnableShadows = s.EnableShadows,
        ShadowMapResolution = s.ShadowMapResolution,
        ShadowMinBias = s.ShadowMinBias,
        ShadowMaxBias = s.ShadowMaxBias,
        ShadowSoftnessTexels = s.ShadowSoftnessTexels,
        ShadowDistance = s.ShadowDistance,
        EnableShadowCascades = s.EnableShadowCascades,
        EnableGodRays = s.EnableGodRays,
        EnableVolumeGodRays = s.EnableVolumeGodRays,
        EnableVolumetricClouds = s.EnableVolumetricClouds,
        VolumetricQuality = s.VolumetricQuality,
        GodRayStrength = s.GodRayStrength,
        GodRayConeScale = s.GodRayConeScale,
        GodRayScatterGain = s.GodRayScatterGain,
        GodRayExtinction = s.GodRayExtinction,
        GodRayDebugDensity = s.GodRayDebugDensity,
        GodRayStabilizeDebug = s.GodRayStabilizeDebug,
        GodRaySparseMarch = s.GodRaySparseMarch,
        CloudDensity = s.CloudDensity,
        CloudVolumeSize = s.CloudVolumeSize,
        CloudLayerHeight = s.CloudLayerHeight,
        CloudVolumeHeight = s.CloudVolumeHeight,
        CloudQuality = s.CloudQuality,
        CloudCoverageScale = s.CloudCoverageScale,
        CloudWindSpeed = s.CloudWindSpeed,
        CloudWindHeadingDegrees = s.CloudWindHeadingDegrees,
        CloudCirrusStrength = s.CloudCirrusStrength,
        CloudDebugView = s.CloudDebugView,
        CloudDisableTemporal = s.CloudDisableTemporal,
        CloudMarchStepOverride = s.CloudMarchStepOverride,
        CloudFreezeWind = s.CloudFreezeWind,
        LogVolumetricTiming = s.LogVolumetricTiming,
        LogPreviewTaaDiagnostics = s.LogPreviewTaaDiagnostics,
        LogGpuPassTimings = s.LogGpuPassTimings,
        ShowExpandedGpuTimingHud = s.ShowExpandedGpuTimingHud,
        OcclusionDebugMode = Math.Clamp(s.OcclusionDebugMode, 0, 2),
        EnablePreviewTaa = s.EnablePreviewTaa,
        PreviewTaaMode = s.PreviewTaaMode,
        PreviewTaaTemporalScale = s.PreviewTaaTemporalScale,
        PreviewTaaJitterScale = s.PreviewTaaJitterScale,
        PreviewTaaSourceFilterScale = s.PreviewTaaSourceFilterScale,
        PreviewTaaEdgeBlendScale = s.PreviewTaaEdgeBlendScale,
        PreviewTaaFxaaStrengthScale = s.PreviewTaaFxaaStrengthScale,
        PreviewTaaFxaaLumaEdgeScale = s.PreviewTaaFxaaLumaEdgeScale,
        PreviewTaaFxaaLumaThreshold = s.PreviewTaaFxaaLumaThreshold,
        PreviewTaaForceFxaa = s.PreviewTaaForceFxaa,
        ShowSunProjectionDebug = s.ShowSunProjectionDebug,
        HdrPresentActive = s.HdrPresentActive,
        HdrPaperWhiteNits = s.HdrPaperWhiteNits,
        HdrPeakNits = s.HdrPeakNits,
    };
}
