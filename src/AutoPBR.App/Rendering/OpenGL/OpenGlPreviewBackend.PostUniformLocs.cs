namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private readonly record struct TaaResolveUniformLocs(
        int Current,
        int History,
        int SceneDepth,
        int TaaSignal,
        int HasSceneDepth,
        int HasTaaSignal,
        int HasHistory,
        int InvViewProj,
        int PrevViewProj,
        int TexelSize,
        int CaptureTexelSize,
        int CurrentJitterPixels,
        int TemporalWeight,
        int StableTemporalBoost,
        int MaxStableTemporal,
        int TaaSharpenStrength,
        int DepthEdgeHistoryFloor,
        int EdgeAaBlend,
        int SourceFilterStrength,
        int SilhouetteHistoryWeight,
        int FxaaEdgeStrength,
        int FxaaLumaEdgeStrength,
        int FxaaLumaThreshold,
        int ForceFxaa,
        int HdrPresent);

    private readonly record struct ScenePresentUniformLocs(
        int SceneColor,
        int HdrPresent,
        int SceneIsLinear,
        int HdrPaperWhiteNits,
        int HdrPeakNits);

    private readonly record struct ScreenSpaceGodRayUniformLocs(
        int SceneDepth,
        int SunUv,
        int SunDiscRadius,
        int SunConeRadius,
        int Strength);

    private readonly record struct ShadowAwareGodRayUniformLocs(
        int SceneDepth,
        int ShadowMap,
        int ShadowMapNear,
        int ShadowMapMid,
        int InvViewProj,
        int LightViewProj,
        int LightViewProjNear,
        int LightViewProjMid,
        int CameraPos,
        int SunUv,
        int SunDiscRadius,
        int SunConeRadius,
        int Strength,
        int LayerHeight,
        int VolumeHeight,
        int CloudDensity,
        int VolumeSize,
        int GroundWorldY,
        int FogSlabHeight,
        int HeightFogStrength,
        int ShadowTexelSize,
        int ShadowTexelSizeNear,
        int ShadowTexelSizeMid,
        int ShadowMinBias,
        int EnableShadowMap,
        int EnableShadowCascades,
        int CascadeSplitDistance,
        int CascadeMidSplitDistance,
        int CascadeBlendWidth,
        int ShadowDistance,
        int ShadowFadeStart,
        int EnableCloudAttenuation);

    private readonly record struct GodRayUpsampleUniformLocs(
        int HalfResRays,
        int SceneDepth,
        int History,
        int InvViewProj,
        int PrevViewProj,
        int HalfResTexelSize,
        int TemporalWeight,
        int HasHistory);

    private readonly record struct GodRayCompositeUniformLocs(
        int Rays,
        int HasCloudMask,
        int CloudMask);

    private readonly record struct VolumeInjectUniformLocs(
        int CameraPos,
        int CamRight,
        int CamUp,
        int CamForward,
        int LightDir,
        int LightColor,
        int HalfExtent,
        int SliceCount,
        int DepthDistribution,
        int LayerHeight,
        int VolumeHeight,
        int CloudDensity,
        int VolumeSize,
        int GroundWorldY,
        int FogSlabHeight,
        int HeightFogStrength,
        int DebugDensity,
        int LightViewProj,
        int LightViewProjNear,
        int LightViewProjMid,
        int ShadowTexelSize,
        int ShadowTexelSizeNear,
        int ShadowTexelSizeMid,
        int ShadowMinBias,
        int EnableShadowMap,
        int EnableShadowCascades,
        int CascadeSplitDistance,
        int CascadeMidSplitDistance,
        int CascadeBlendWidth,
        int ShadowDistance,
        int ShadowFadeStart,
        int ShadowMap,
        int ShadowMapNear,
        int ShadowMapMid,
        int SliceIndex);

    private readonly record struct VolumeInjectComputeUniformLocs(
        int CameraPos,
        int CamRight,
        int CamUp,
        int CamForward,
        int LightDir,
        int LightColor,
        int HalfExtent,
        int FroxelSize,
        int SliceCount,
        int DepthDistribution,
        int LayerHeight,
        int VolumeHeight,
        int CloudDensity,
        int VolumeSize,
        int GroundWorldY,
        int FogSlabHeight,
        int HeightFogStrength,
        int DebugDensity,
        int LightViewProj,
        int LightViewProjNear,
        int LightViewProjMid,
        int ShadowTexelSize,
        int ShadowTexelSizeNear,
        int ShadowTexelSizeMid,
        int ShadowMinBias,
        int EnableShadowMap,
        int EnableShadowCascades,
        int CascadeSplitDistance,
        int CascadeMidSplitDistance,
        int CascadeBlendWidth,
        int ShadowDistance,
        int ShadowFadeStart,
        int ShadowMap,
        int ShadowMapNear,
        int ShadowMapMid);

    private readonly record struct VolumeIntegrateUniformLocs(
        int FroxelVolume,
        int FroxelOccupancy,
        int SceneDepth,
        int InvViewProj,
        int CameraPos,
        int CamRight,
        int CamUp,
        int CamForward,
        int LightDir,
        int HalfExtent,
        int SliceCount,
        int FroxelTexelSize,
        int Strength,
        int Jitter,
        int ScatterGain,
        int Extinction,
        int DepthDistribution,
        int PrevIntegrate,
        int HasPrevIntegrate,
        int PrevFroxelVolume,
        int HasPrevFroxel,
        int PrevViewProj,
        int PrevCameraPos,
        int PrevCamRight,
        int PrevCamUp,
        int PrevCamForward,
        int PrevHalfExtent,
        int TemporalWeight,
        int FroxelTemporalWeight,
        int CloudTransmittance,
        int CloudData,
        int HasCloudTransmittance);

    private readonly record struct CloudUniformLocs(
        int CloudNoise,
        int CoverageMap,
        int SkyViewLut,
        int DetailNoise,
        int SceneDepth,
        int InvViewProj,
        int CameraPos,
        int GroundWorldY,
        int PlanetRadius,
        int SunDir,
        int SunIntensity,
        int SkyExposure,
        int LayerHeight,
        int VolumeHeight,
        int Density,
        int CoverageScale,
        int VolumeSize,
        int WindOffset,
        int CirrusStrength,
        int CirrusWindOffset,
        int CirrusWindDir,
        int Quality,
        int MarchSteps,
        int DebugView,
        int HasSceneDepth,
        int FramePhase,
        int HasCloudNoise,
        int HasDetailNoise,
        int HasCoverageMap,
        int HasSkyLut,
        int HdrPresent);

    private readonly record struct CloudTemporalUniformLocs(
        int CurrentClouds,
        int CurrentCloudData,
        int HistoryClouds,
        int HistoryCloudData,
        int InvViewProj,
        int PrevViewProj,
        int CameraPos,
        int PrevCameraPos,
        int WindDelta,
        int CirrusWindDelta,
        int TexelSize,
        int TemporalWeight,
        int HasHistory);

    private readonly record struct CloudUpsampleUniformLocs(
        int Clouds,
        int CloudData,
        int CloudTexelSize,
        int HasSceneDepth,
        int SceneDepth,
        int InvViewProj,
        int CameraPos,
        int GroundWorldY,
        int PlanetRadius);

    private readonly record struct CloudCompositeUniformLocs(
        int Clouds,
        int HasSceneDepth,
        int SceneDepth,
        int Rays);

    private readonly record struct AtmoTransUniformLocs(
        int Turbidity,
        int HorizonFalloff);

    private readonly record struct AtmoSkyViewUniformLocs(
        int TransmittanceLut,
        int SunDir,
        int Turbidity,
        int SunIntensity,
        int HorizonFalloff);

    private readonly record struct AtmoSkyUniformLocs(
        int SkyViewLut,
        int Turbidity,
        int HorizonFalloff,
        int InvViewProj,
        int CameraPos,
        int LightDir,
        int SunIntensity,
        int HorizonFogStrength,
        int GroundWorldY,
        int SkyExposure,
        int SunDiscStrength,
        int SunDiscBrightness,
        int SunCosDiscEdge,
        int MoonCosDiscEdge,
        int RenderTime,
        int ViewportAspect,
        int SunDiscRadiusUv,
        int HdrPresent);

    private readonly record struct ProceduralSkyUniformLocs(
        int InvViewProj,
        int CameraPos,
        int LightDir,
        int SunIntensity,
        int SkyExposure,
        int RenderTime,
        int Turbidity,
        int HorizonFalloff,
        int HorizonFogStrength,
        int GroundWorldY,
        int SunDiscStrength,
        int SunDiscBrightness,
        int SunCosDiscEdge,
        int MoonCosDiscEdge,
        int ViewportAspect,
        int SunDiscRadiusUv,
        int SunElevation);

    private TaaResolveUniformLocs _taaResolveUniformLocs;
    private ScenePresentUniformLocs _scenePresentUniformLocs;
    private ScreenSpaceGodRayUniformLocs _screenSpaceGodRayUniformLocs;
    private ShadowAwareGodRayUniformLocs _shadowAwareGodRayUniformLocs;
    private GodRayUpsampleUniformLocs _godRayUpsampleUniformLocs;
    private GodRayCompositeUniformLocs _godRayCompositeUniformLocs;
    private VolumeInjectUniformLocs _volumeInjectUniformLocs;
    private VolumeInjectComputeUniformLocs _volumeInjectComputeUniformLocs;
    private VolumeIntegrateUniformLocs _volumeIntegrateUniformLocs;
    private CloudUniformLocs _cloudUniformLocs;
    private CloudTemporalUniformLocs _cloudTemporalUniformLocs;
    private CloudUpsampleUniformLocs _cloudUpsampleUniformLocs;
    private CloudCompositeUniformLocs _cloudCompositeUniformLocs;
    private AtmoTransUniformLocs _atmoTransUniformLocs;
    private AtmoSkyViewUniformLocs _atmoSkyViewUniformLocs;
    private AtmoSkyUniformLocs _atmoSkyUniformLocs;
    private ProceduralSkyUniformLocs _proceduralSkyUniformLocs;

    private static TaaResolveUniformLocs ResolveTaaResolveUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uCurrent"),
            program.GetUniformLocation("uHistory"),
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uTaaSignal"),
            program.GetUniformLocation("uHasSceneDepth"),
            program.GetUniformLocation("uHasTaaSignal"),
            program.GetUniformLocation("uHasHistory"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uPrevViewProj"),
            program.GetUniformLocation("uTexelSize"),
            program.GetUniformLocation("uCaptureTexelSize"),
            program.GetUniformLocation("uCurrentJitterPixels"),
            program.GetUniformLocation("uTemporalWeight"),
            program.GetUniformLocation("uStableTemporalBoost"),
            program.GetUniformLocation("uMaxStableTemporal"),
            program.GetUniformLocation("uTaaSharpenStrength"),
            program.GetUniformLocation("uDepthEdgeHistoryFloor"),
            program.GetUniformLocation("uEdgeAaBlend"),
            program.GetUniformLocation("uSourceFilterStrength"),
            program.GetUniformLocation("uSilhouetteHistoryWeight"),
            program.GetUniformLocation("uFxaaEdgeStrength"),
            program.GetUniformLocation("uFxaaLumaEdgeStrength"),
            program.GetUniformLocation("uFxaaLumaThreshold"),
            program.GetUniformLocation("uForceFxaa"),
            program.GetUniformLocation("uHdrPresent"));

    private static ScenePresentUniformLocs ResolveScenePresentUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uSceneColor"),
            program.GetUniformLocation("uHdrPresent"),
            program.GetUniformLocation("uSceneIsLinear"),
            program.GetUniformLocation("uHdrPaperWhiteNits"),
            program.GetUniformLocation("uHdrPeakNits"));

    private static ScreenSpaceGodRayUniformLocs ResolveScreenSpaceGodRayUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uSunUv"),
            program.GetUniformLocation("uSunDiscRadius"),
            program.GetUniformLocation("uSunConeRadius"),
            program.GetUniformLocation("uStrength"));

    private static ShadowAwareGodRayUniformLocs ResolveShadowAwareGodRayUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uShadowMap"),
            program.GetUniformLocation("uShadowMapNear"),
            program.GetUniformLocation("uShadowMapMid"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uLightViewProj"),
            program.GetUniformLocation("uLightViewProjNear"),
            program.GetUniformLocation("uLightViewProjMid"),
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uSunUv"),
            program.GetUniformLocation("uSunDiscRadius"),
            program.GetUniformLocation("uSunConeRadius"),
            program.GetUniformLocation("uStrength"),
            program.GetUniformLocation("uLayerHeight"),
            program.GetUniformLocation("uVolumeHeight"),
            program.GetUniformLocation("uCloudDensity"),
            program.GetUniformLocation("uVolumeSize"),
            program.GetUniformLocation("uGroundWorldY"),
            program.GetUniformLocation("uFogSlabHeight"),
            program.GetUniformLocation("uHeightFogStrength"),
            program.GetUniformLocation("uShadowTexelSize"),
            program.GetUniformLocation("uShadowTexelSizeNear"),
            program.GetUniformLocation("uShadowTexelSizeMid"),
            program.GetUniformLocation("uShadowMinBias"),
            program.GetUniformLocation("uEnableShadowMap"),
            program.GetUniformLocation("uEnableShadowCascades"),
            program.GetUniformLocation("uCascadeSplitDistance"),
            program.GetUniformLocation("uCascadeMidSplitDistance"),
            program.GetUniformLocation("uCascadeBlendWidth"),
            program.GetUniformLocation("uShadowDistance"),
            program.GetUniformLocation("uShadowFadeStart"),
            program.GetUniformLocation("uEnableCloudAttenuation"));

    private static GodRayUpsampleUniformLocs ResolveGodRayUpsampleUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uHalfResRays"),
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uHistory"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uPrevViewProj"),
            program.GetUniformLocation("uHalfResTexelSize"),
            program.GetUniformLocation("uTemporalWeight"),
            program.GetUniformLocation("uHasHistory"));

    private static GodRayCompositeUniformLocs ResolveGodRayCompositeUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uRays"),
            program.GetUniformLocation("uHasCloudMask"),
            program.GetUniformLocation("uCloudMask"));

    private static VolumeInjectUniformLocs ResolveVolumeInjectUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uCamRight"),
            program.GetUniformLocation("uCamUp"),
            program.GetUniformLocation("uCamForward"),
            program.GetUniformLocation("uLightDir"),
            program.GetUniformLocation("uLightColor"),
            program.GetUniformLocation("uHalfExtent"),
            program.GetUniformLocation("uSliceCount"),
            program.GetUniformLocation("uDepthDistribution"),
            program.GetUniformLocation("uLayerHeight"),
            program.GetUniformLocation("uVolumeHeight"),
            program.GetUniformLocation("uCloudDensity"),
            program.GetUniformLocation("uVolumeSize"),
            program.GetUniformLocation("uGroundWorldY"),
            program.GetUniformLocation("uFogSlabHeight"),
            program.GetUniformLocation("uHeightFogStrength"),
            program.GetUniformLocation("uDebugDensity"),
            program.GetUniformLocation("uLightViewProj"),
            program.GetUniformLocation("uLightViewProjNear"),
            program.GetUniformLocation("uLightViewProjMid"),
            program.GetUniformLocation("uShadowTexelSize"),
            program.GetUniformLocation("uShadowTexelSizeNear"),
            program.GetUniformLocation("uShadowTexelSizeMid"),
            program.GetUniformLocation("uShadowMinBias"),
            program.GetUniformLocation("uEnableShadowMap"),
            program.GetUniformLocation("uEnableShadowCascades"),
            program.GetUniformLocation("uCascadeSplitDistance"),
            program.GetUniformLocation("uCascadeMidSplitDistance"),
            program.GetUniformLocation("uCascadeBlendWidth"),
            program.GetUniformLocation("uShadowDistance"),
            program.GetUniformLocation("uShadowFadeStart"),
            program.GetUniformLocation("uShadowMap"),
            program.GetUniformLocation("uShadowMapNear"),
            program.GetUniformLocation("uShadowMapMid"),
            program.GetUniformLocation("uSliceIndex"));

    private static VolumeInjectComputeUniformLocs ResolveVolumeInjectComputeUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uCamRight"),
            program.GetUniformLocation("uCamUp"),
            program.GetUniformLocation("uCamForward"),
            program.GetUniformLocation("uLightDir"),
            program.GetUniformLocation("uLightColor"),
            program.GetUniformLocation("uHalfExtent"),
            program.GetUniformLocation("uFroxelSize"),
            program.GetUniformLocation("uSliceCount"),
            program.GetUniformLocation("uDepthDistribution"),
            program.GetUniformLocation("uLayerHeight"),
            program.GetUniformLocation("uVolumeHeight"),
            program.GetUniformLocation("uCloudDensity"),
            program.GetUniformLocation("uVolumeSize"),
            program.GetUniformLocation("uGroundWorldY"),
            program.GetUniformLocation("uFogSlabHeight"),
            program.GetUniformLocation("uHeightFogStrength"),
            program.GetUniformLocation("uDebugDensity"),
            program.GetUniformLocation("uLightViewProj"),
            program.GetUniformLocation("uLightViewProjNear"),
            program.GetUniformLocation("uLightViewProjMid"),
            program.GetUniformLocation("uShadowTexelSize"),
            program.GetUniformLocation("uShadowTexelSizeNear"),
            program.GetUniformLocation("uShadowTexelSizeMid"),
            program.GetUniformLocation("uShadowMinBias"),
            program.GetUniformLocation("uEnableShadowMap"),
            program.GetUniformLocation("uEnableShadowCascades"),
            program.GetUniformLocation("uCascadeSplitDistance"),
            program.GetUniformLocation("uCascadeMidSplitDistance"),
            program.GetUniformLocation("uCascadeBlendWidth"),
            program.GetUniformLocation("uShadowDistance"),
            program.GetUniformLocation("uShadowFadeStart"),
            program.GetUniformLocation("uShadowMap"),
            program.GetUniformLocation("uShadowMapNear"),
            program.GetUniformLocation("uShadowMapMid"));

    private static VolumeIntegrateUniformLocs ResolveVolumeIntegrateUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uFroxelVolume"),
            program.GetUniformLocation("uFroxelOccupancy"),
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uCamRight"),
            program.GetUniformLocation("uCamUp"),
            program.GetUniformLocation("uCamForward"),
            program.GetUniformLocation("uLightDir"),
            program.GetUniformLocation("uHalfExtent"),
            program.GetUniformLocation("uSliceCount"),
            program.GetUniformLocation("uFroxelTexelSize"),
            program.GetUniformLocation("uStrength"),
            program.GetUniformLocation("uJitter"),
            program.GetUniformLocation("uScatterGain"),
            program.GetUniformLocation("uExtinction"),
            program.GetUniformLocation("uDepthDistribution"),
            program.GetUniformLocation("uPrevIntegrate"),
            program.GetUniformLocation("uHasPrevIntegrate"),
            program.GetUniformLocation("uPrevFroxelVolume"),
            program.GetUniformLocation("uHasPrevFroxel"),
            program.GetUniformLocation("uPrevViewProj"),
            program.GetUniformLocation("uPrevCameraPos"),
            program.GetUniformLocation("uPrevCamRight"),
            program.GetUniformLocation("uPrevCamUp"),
            program.GetUniformLocation("uPrevCamForward"),
            program.GetUniformLocation("uPrevHalfExtent"),
            program.GetUniformLocation("uTemporalWeight"),
            program.GetUniformLocation("uFroxelTemporalWeight"),
            program.GetUniformLocation("uCloudTransmittance"),
            program.GetUniformLocation("uCloudData"),
            program.GetUniformLocation("uHasCloudTransmittance"));

    private static CloudUniformLocs ResolveCloudUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uCloudNoise"),
            program.GetUniformLocation("uCoverageMap"),
            program.GetUniformLocation("uSkyViewLut"),
            program.GetUniformLocation("uDetailNoise"),
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uGroundWorldY"),
            program.GetUniformLocation("uPlanetRadius"),
            program.GetUniformLocation("uSunDir"),
            program.GetUniformLocation("uSunIntensity"),
            program.GetUniformLocation("uSkyExposure"),
            program.GetUniformLocation("uLayerHeight"),
            program.GetUniformLocation("uVolumeHeight"),
            program.GetUniformLocation("uDensity"),
            program.GetUniformLocation("uCoverageScale"),
            program.GetUniformLocation("uVolumeSize"),
            program.GetUniformLocation("uWindOffset"),
            program.GetUniformLocation("uCirrusStrength"),
            program.GetUniformLocation("uCirrusWindOffset"),
            program.GetUniformLocation("uCirrusWindDir"),
            program.GetUniformLocation("uQuality"),
            program.GetUniformLocation("uMarchSteps"),
            program.GetUniformLocation("uDebugView"),
            program.GetUniformLocation("uHasSceneDepth"),
            program.GetUniformLocation("uFramePhase"),
            program.GetUniformLocation("uHasCloudNoise"),
            program.GetUniformLocation("uHasDetailNoise"),
            program.GetUniformLocation("uHasCoverageMap"),
            program.GetUniformLocation("uHasSkyLut"),
            program.GetUniformLocation("uHdrPresent"));

    private static CloudTemporalUniformLocs ResolveCloudTemporalUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uCurrentClouds"),
            program.GetUniformLocation("uCurrentCloudData"),
            program.GetUniformLocation("uHistoryClouds"),
            program.GetUniformLocation("uHistoryCloudData"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uPrevViewProj"),
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uPrevCameraPos"),
            program.GetUniformLocation("uWindDelta"),
            program.GetUniformLocation("uCirrusWindDelta"),
            program.GetUniformLocation("uTexelSize"),
            program.GetUniformLocation("uTemporalWeight"),
            program.GetUniformLocation("uHasHistory"));

    private static CloudUpsampleUniformLocs ResolveCloudUpsampleUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uClouds"),
            program.GetUniformLocation("uCloudData"),
            program.GetUniformLocation("uCloudTexelSize"),
            program.GetUniformLocation("uHasSceneDepth"),
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uGroundWorldY"),
            program.GetUniformLocation("uPlanetRadius"));

    private static CloudCompositeUniformLocs ResolveCloudCompositeUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uClouds"),
            program.GetUniformLocation("uHasSceneDepth"),
            program.GetUniformLocation("uSceneDepth"),
            program.GetUniformLocation("uRays"));

    private static AtmoTransUniformLocs ResolveAtmoTransUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uTurbidity"),
            program.GetUniformLocation("uHorizonFalloff"));

    private static AtmoSkyViewUniformLocs ResolveAtmoSkyViewUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uTransmittanceLut"),
            program.GetUniformLocation("uSunDir"),
            program.GetUniformLocation("uTurbidity"),
            program.GetUniformLocation("uSunIntensity"),
            program.GetUniformLocation("uHorizonFalloff"));

    private static AtmoSkyUniformLocs ResolveAtmoSkyUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uSkyViewLut"),
            program.GetUniformLocation("uTurbidity"),
            program.GetUniformLocation("uHorizonFalloff"),
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uLightDir"),
            program.GetUniformLocation("uSunIntensity"),
            program.GetUniformLocation("uHorizonFogStrength"),
            program.GetUniformLocation("uGroundWorldY"),
            program.GetUniformLocation("uSkyExposure"),
            program.GetUniformLocation("uSunDiscStrength"),
            program.GetUniformLocation("uSunDiscBrightness"),
            program.GetUniformLocation("uSunCosDiscEdge"),
            program.GetUniformLocation("uMoonCosDiscEdge"),
            program.GetUniformLocation("uRenderTime"),
            program.GetUniformLocation("uViewportAspect"),
            program.GetUniformLocation("uSunDiscRadiusUv"),
            program.GetUniformLocation("uHdrPresent"));

    private static ProceduralSkyUniformLocs ResolveProceduralSkyUniformLocs(GlProceduralSkyProgram program) =>
        new(
            program.GetUniformLocation("uInvViewProj"),
            program.GetUniformLocation("uCameraPos"),
            program.GetUniformLocation("uLightDir"),
            program.GetUniformLocation("uSunIntensity"),
            program.GetUniformLocation("uSkyExposure"),
            program.GetUniformLocation("uRenderTime"),
            program.GetUniformLocation("uTurbidity"),
            program.GetUniformLocation("uHorizonFalloff"),
            program.GetUniformLocation("uHorizonFogStrength"),
            program.GetUniformLocation("uGroundWorldY"),
            program.GetUniformLocation("uSunDiscStrength"),
            program.GetUniformLocation("uSunDiscBrightness"),
            program.GetUniformLocation("uSunCosDiscEdge"),
            program.GetUniformLocation("uMoonCosDiscEdge"),
            program.GetUniformLocation("uViewportAspect"),
            program.GetUniformLocation("uSunDiscRadiusUv"),
            program.GetUniformLocation("uSunElevation"));
}
