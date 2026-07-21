using System.Text.Json;
using System.Text.Json.Serialization;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Core;
using AutoPBR.Core.Models;

using DeepBumpInputModeEnum = AutoPBR.Contracts.Ml.DeepBumpInputMode;
using NormalDerivativeEnum = AutoPBR.Core.Models.NormalDerivative;
using NormalOperatorEnum = AutoPBR.Core.Models.NormalOperator;

namespace AutoPBR.App.Models;

public sealed class UserSettings
{
    public string? OutputDirectory { get; set; }

    /// <summary>Folder scanned for batch .zip / .jar resource packs (Scan tab).</summary>
    public string? BatchFolderPath { get; set; }

    /// <summary>When true, input UI targets batch folder; main Convert runs batch for that folder.</summary>
    public bool UseBatchFolderInput { get; set; }
    public double NormalIntensity { get; set; } = AutoPBRDefaults.DefaultNormalIntensity;
    public double HeightIntensity { get; set; } = AutoPBRDefaults.DefaultHeightIntensity;
    public bool BrickHeightMapPostProcessEnabled { get; set; } = AutoPBRDefaults.DefaultBrickHeightMapPostProcessEnabled;
    public double BrickHeightMinStructuralConfidence { get; set; } = AutoPBRDefaults.DefaultBrickHeightMinStructuralConfidence;
    public double BrickHeightInvertDeltaThreshold { get; set; } = AutoPBRDefaults.DefaultBrickHeightInvertDeltaThreshold;
    public double BrickLightGroutDiffuseDeltaMin { get; set; } = AutoPBRDefaults.DefaultBrickLightGroutDiffuseDeltaMin;
    public bool PreviewBrickProbeDebug { get; set; } = AutoPBRDefaults.DefaultBrickProbePreviewDebug;
    public bool FastSpecular { get; set; }
    public string FoliageMode { get; set; } = "No Height";

    /// <summary>Legacy: when loading old settings with "IgnorePlants": false, migrate to FoliageMode "Convert All".</summary>
    [JsonPropertyName("IgnorePlants")]
    public bool? IgnorePlants
    {
        set => FoliageMode = value == false ? "Convert All" : "Ignore All";
    }

    public bool UseLegacyExtractor { get; set; }

    /// <summary>Backward compat: old settings used "ExperimentalExtractor" (true = parallel). Map to UseLegacyExtractor = !value.</summary>
    [JsonPropertyName("ExperimentalExtractor")]
    public bool ExperimentalExtractor
    {
        get => !UseLegacyExtractor;
        set => UseLegacyExtractor = !value;
    }

    public double SmoothnessScale { get; set; } = AutoPBRDefaults.DefaultSmoothnessScale;
    public double MetallicBoost { get; set; } = AutoPBRDefaults.DefaultMetallicBoost;
    public double PorosityBias { get; set; } = AutoPBRDefaults.DefaultPorosityBias;

    /// <summary>Extra porosity B for plant-tagged textures (added to <see cref="PorosityBias"/>). Null = use default on load.</summary>
    public double? PlantMaterialPorosityExtra { get; set; }
    public int MaxThreads { get; set; } // 0 = auto
    public string? TempDirectory { get; set; }

    /// <summary>Optional local Minecraft install/version folder for block model JSON fallback during 3D preview.</summary>
    public string? MinecraftAssetsDirectory { get; set; }

    public bool DebugMode { get; set; }

    /// <summary>When true, request native desktop OpenGL 4.x (WGL) for the 3D preview on next launch. Default false = OpenGL ES 3.0 via ANGLE.</summary>
    public bool PreviewUseOpenGl4 { get; set; }

    /// <summary>Preview HDR present preference: "Auto" or "Sdr".</summary>
    public string PreviewHdrMode { get; set; } = "Auto";

    /// <summary>scRGB paper-white reference luminance in nits (typically 80–2000).</summary>
    public double PreviewHdrPaperWhiteNits { get; set; } = 200.0;

    public string ColorScheme { get; set; } = "Dark";

    /// <summary>Interface scale (typically 0.75–1.75). 1.0 = 100%.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>UI language culture code (e.g. "en", "de").</summary>
    public string Language { get; set; } = "en";

    public bool ProcessBlocks { get; set; } = true;
    public bool ProcessItems { get; set; } = true;
    public bool ProcessArmor { get; set; } = true;
    public bool ProcessEntity { get; set; } = true;
    public bool ProcessParticles { get; set; } = true;
    public bool UseDeepBumpNormals { get; set; }

    /// <summary>DeepBump tile overlap: "Small", "Medium", or "Large" (default Large = best quality).</summary>
    public string DeepBumpOverlap { get; set; } = "Large";

    public string DeepBumpInputMode { get; set; } = nameof(DeepBumpInputModeEnum.Auto);
    public bool DeepBumpForceBlue255 { get; set; }
    public double DeepBumpNormalIntensity { get; set; } = AutoPBRDefaults.DefaultNormalIntensity;
    public double DeepBumpNormalSoftClamp { get; set; }
    public bool DeepBumpEdgeGuidedEnhance { get; set; }
    public double DeepBumpEdgeGuidedStrength { get; set; } = 1.0;
    public double DeepBumpEdgeGuidedGamma { get; set; } = 1.0;
    public double DeepBumpEdgeGuidedDirectionMix { get; set; } = 0.35;
    public int NormalHeightTransparentAlphaClampMax { get; set; }

    /// <summary>Normal operator when not using DeepBump. \"SobelVc\" or \"ScharrVc\".</summary>
    public string NormalOperator { get; set; } = nameof(NormalOperatorEnum.SobelVc);

    /// <summary>Normal kernel size for Sobel/Scharr when not using DeepBump. \"3\", \"5\", or \"7\" (7 only for Sobel).</summary>
    public string NormalKernelSize { get; set; } = "3";

    /// <summary>What to derive normals from: Luminance, Color, ColorLuminanceBlend, or ColorLuminanceMax.</summary>
    public string NormalDerivative { get; set; } = nameof(NormalDerivativeEnum.Luminance);

    public bool PreprocessLinearize { get; set; }
    public int PreprocessDenoiseRadius { get; set; }
    public double PreprocessDenoiseBlend { get; set; } = 0.5;
    public bool PreprocessFrequencySplit { get; set; }
    public int PreprocessFrequencyRadius { get; set; } = 2;
    public double PreprocessFrequencyDetailStrength { get; set; } = 1.0;

    public bool SpecularUsePercentileRemap { get; set; } = true;
    public double SpecularRemapLowPercentile { get; set; } = 0.02;
    public double SpecularRemapHighPercentile { get; set; } = 0.98;
    public bool SpecularForceNoEmissive { get; set; }
    public bool UseMlSpecularPredictor { get; set; }
    public string? MlSpecularModelPath { get; set; }

    /// <summary>Optional override paths for bundled per-resolution specular models (edge length in pixels).</summary>
    public string? MlSpecularModelPath16 { get; set; }
    public string? MlSpecularModelPath32 { get; set; }
    public string? MlSpecularModelPath64 { get; set; }
    public string? MlSpecularModelPath128 { get; set; }
    public string? MlSpecularModelPath256 { get; set; }

    /// <summary>Blend toward ML specular (0 = heuristic, 1 = full ML). Null = use default on load.</summary>
    public double? MlSpecularHeuristicBlend { get; set; }

    /// <summary>Specular ML blend mode enum name (SmoothnessOnly, AiMetalAndEmissive, or Full); null defaults to SmoothnessOnly.</summary>
    public string? MlSpecularHeuristicBlendMode { get; set; }
    /// <summary>Specular ML blend math enum name (Linear, Additive, Multiplicative); null defaults to Linear.</summary>
    public string? MlSpecularBlendMath { get; set; }

    public bool MlSpecularUseEdgeChannel { get; set; } = true;
    public int MlSpecularTransparentAlphaClampMax { get; set; }
    public bool SpecularDebugDisableHeuristicSpecular { get; set; }
    public bool SpecularDebugSkipSpecularRemap { get; set; }
    public bool SpecularDebugVerboseSpecularMl { get; set; }

    public bool GenerateAo { get; set; }
    public int AoRadius { get; set; } = 4;
    public double AoStrength { get; set; } = 1.0;

    /// <summary>When true, ONNX GPU sessions prefer TensorRT (CUDA fallback). Default false = CUDA only.</summary>
    public bool PreferOnnxTensorRtExecutionProvider { get; set; }

    /// <summary>User-defined material tag rules (keywords + optional semantic hints).</summary>
    public List<CustomTagRuleEntry> CustomTagRules { get; set; } = [];

    /// <summary>When true, Explore suggests extra tags using MiniLM embeddings (requires ONNX model under Data).</summary>
    public bool UseSemanticMaterialTags { get; set; } = true;

    /// <summary>Minimum cosine similarity (0–1) for a semantic tag suggestion.</summary>
    public double MaterialTagMinSimilarity { get; set; } = 0.25;

    /// <summary>When the best ML score is below this (0–1), only the Unknown material tag is suggested.</summary>
    public double MaterialTagCertaintyThreshold { get; set; } = 0.35;

    /// <summary>Maximum number of ML-suggested tags per texture.</summary>
    public int MaterialTagMaxCount { get; set; } = 3;

    /// <summary>When true, semantic matching augments MiniLM using dictionary definition evidence.</summary>
    public bool DictionaryEvidenceEnabled { get; set; }

    /// <summary>Dictionary evidence blend weight (0..1) for fused semantic score.</summary>
    public double DictionaryEvidenceWeight { get; set; } = 0.35;

    /// <summary>Minimum dictionary evidence cosine score used for fusion.</summary>
    public double DictionaryMinEvidenceScore { get; set; } = 0.18;

    /// <summary>Dictionary request timeout in milliseconds.</summary>
    public int DictionaryRequestTimeoutMs { get; set; } = 900;

    /// <summary>0 = 2D preview, 1 = 3D preview.</summary>
    public int PreviewDisplayMode { get; set; }

    /// <summary>When in 3D preview mode, continuously rotate the block preview.</summary>
    public bool Preview3DAutoRotate { get; set; } = true;

    /// <summary>3D preview: speed multiplier for emulated entity idle animation.</summary>
    public double Preview3DEntityAnimationSpeed { get; set; } = 1.0;

    /// <summary>3D preview: amplitude multiplier for emulated entity idle animation.</summary>
    public double Preview3DEntityAnimationAmplitude { get; set; } = 1.0;

    /// <summary>3D preview: enable/disable emulated entity idle animation.</summary>
    public bool Preview3DEnableEntityAnimation { get; set; } = true;

    /// <summary>Legacy preview bob/yaw on emulated entities (independent of setupAnim IR motion).</summary>
    public bool Preview3DEnableLegacyEntityWobble { get; set; }

    /// <summary>3D preview: freeze vertex-baked emulated entity idle motion at the current clock.</summary>
    public bool Preview3DPauseEntityIdleAnimation { get; set; }

    /// <summary>When previewing sprite-style foliage in 3D, blend translucent pixels instead of alpha cutout.</summary>
    public bool Preview3DItemUseAlphaBlend { get; set; }

    /// <summary>Emulated entity 3D preview diffuse alpha handling: 0 opaque, 1 cutout (default), 2 blend.</summary>
    public int Preview3DEntityAlphaMode { get; set; } = 1;

    /// <summary>3D preview: use LabPBR normal and specular maps on runtime entity rigs.</summary>
    public bool Preview3DEnableEntityLabPbrShading { get; set; } = true;

    /// <summary>3D preview: enable POM / parallax AO / parallax self-shadow on runtime entity rigs.</summary>
    public bool Preview3DEnableEntityParallax { get; set; }

    /// <summary>Draw a ground grid in 3D preview.</summary>
    public bool Preview3DShowGrid { get; set; } = true;

    /// <summary>Draw the textured grass ground plane in 3D preview.</summary>
    public bool Preview3DShowGroundMesh { get; set; } = true;

    /// <summary>Hard Full-detail terrain chunk radius (Chebyshev).</summary>
    public int Preview3DChunkViewDistance { get; set; } = 8;

    /// <summary>Grass colormap temperature slider (0–1) for 3D preview biome tint.</summary>
    public double? Preview3DGrassColormapTemperature { get; set; }

    /// <summary>Grass colormap downfall/rain slider (0–1) for 3D preview biome tint.</summary>
    public double? Preview3DGrassColormapDownfall { get; set; }

    /// <summary>Draw X/Y/Z axis reference in the corner of the 3D preview.</summary>
    public bool Preview3DShowAxes { get; set; } = true;

    /// <summary>Show a frames-per-second readout in the top-right corner of the 3D preview.</summary>
    public bool Preview3DShowFpsCounter { get; set; }

    /// <summary>When true, write periodic P8 GPU pass timings to the preview log (Debug tab).</summary>
    public bool Preview3DLogGpuPassTimings { get; set; }

    /// <summary>When true, FPS overlay shows per-pass GPU timings instead of total GPU ms only.</summary>
    public bool Preview3DShowExpandedGpuTimingHud { get; set; }

    /// <summary>Synchronize native WGL preview presentation to the current display refresh rate.</summary>
    public bool? Preview3DVSyncEnabled { get; set; }

    /// <summary>Legacy setting migrated to <see cref="Preview3DVSyncEnabled"/>.</summary>
    public bool Preview3DCapFpsAt60 { get; set; }

    /// <summary>When true, 3D preview uses parallax occlusion mapping (POM) from the height map.</summary>
    public bool Preview3DEnableParallax { get; set; } = true;

    /// <summary>When true, 3D preview applies LabPBR-style normal mapping (_n).</summary>
    public bool Preview3DEnableNormalMap { get; set; } = true;

    /// <summary>When true, 3D preview applies LabPBR-style specular / metal interpretation (_s).</summary>
    public bool Preview3DEnableSpecularMap { get; set; } = true;

    /// <summary>Genesis: shader-side parallax displacement scalar (height strength, 0..1).</summary>
    public double Preview3DParallaxHeightStrength { get; set; } = 0.05;

    /// <summary>Genesis POM: primary height-field march layers (8..128).</summary>
    public double Preview3DParallaxTraceLayers { get; set; } = 64;

    /// <summary>Genesis POM: binary/secant refinement steps after primary hit (0..8).</summary>
    public double Preview3DParallaxRefineSteps { get; set; } = 5;

    /// <summary>Genesis POM: self-shadow ray samples from hit point toward light (4..64).</summary>
    public double Preview3DParallaxShadowSamples { get; set; } = 24;

    /// <summary>Genesis POM: receiver-side self-shadow softness (0..4).</summary>
    public double Preview3DParallaxShadowSoftness { get; set; } = 1.25;

    /// <summary>Genesis POM: maximum UV travel allowed for one trace (0.05..0.75).</summary>
    public double Preview3DParallaxMaxUvShift { get; set; } = 0.45;

    /// <summary>Genesis high-end preview: tessellate triangles and displace height-map protrusions outward.</summary>
    public bool Preview3DEnableTessellationDisplacement { get; set; } = true;

    /// <summary>Genesis tessellation: fixed triangle tessellation level (1..16).</summary>
    public double Preview3DTessellationLevel { get; set; } = 8;

    /// <summary>Genesis tessellation: maximum outward protrusion in preview world units (0..0.20).</summary>
    public double Preview3DTessellationDisplacementStrength { get; set; } = 0.06;

    /// <summary>Genesis: enable cheap subsurface scattering approximation (LabPBR _s.b >= 65).</summary>
    public bool Preview3DEnableSss { get; set; } = true;

    /// <summary>Genesis: enable parallax self-shadow trace toward the light.</summary>
    public bool Preview3DEnableParallaxShadow { get; set; } = true;

    /// <summary>Genesis: toggle POM-derived contact AO in the 3D preview shader.</summary>
    public bool Preview3DEnableParallaxAo { get; set; } = true;

    /// <summary>Genesis: strength multiplier for POM-derived contact AO (0..2).</summary>
    public double Preview3DParallaxAoStrength { get; set; } = 1.0;

    /// <summary>Genesis: enable environment IBL (LUT-based when atmospheric sky is on; procedural hemisphere otherwise).</summary>
    public bool Preview3DEnableIbl { get; set; } = true;

    /// <summary>Genesis: indirect (IBL probe) intensity scalar.</summary>
    public double Preview3DIblStrength { get; set; } = 0.6;

    /// <summary>Atmospheric sky: render LUT-driven sky background and ambient probes.</summary>
    public bool Preview3DEnableAtmosphericSky { get; set; } = true;

    /// <summary>Atmospheric sky turbidity (haze amount).</summary>
    public double Preview3DAtmosphereTurbidity { get; set; } = 2.6;

    /// <summary>Atmospheric sky sun intensity multiplier.</summary>
    public double Preview3DAtmosphereSunIntensity { get; set; } = 10.0;

    /// <summary>Atmospheric sky horizon falloff scalar.</summary>
    public double Preview3DAtmosphereHorizonFalloff { get; set; } = 1.35;

    /// <summary>3D preview: sky composite exposure multiplier.</summary>
    public double Preview3DAtmosphereSkyExposure { get; set; } = 0.85;

    /// <summary>3D preview: additive sun disc bloom on sky composite.</summary>
    public double Preview3DAtmosphereSunDiscStrength { get; set; } = 0.35;

    /// <summary>3D preview: limb-darkened sun disc surface brightness (bloom is separate).</summary>
    public double Preview3DAtmosphereSunDiscBrightness { get; set; } = 1.0;

    /// <summary>3D preview: sun angular-size multiplier (1 = stylized default; ~0.07 = real sun).</summary>
    public double Preview3DAtmosphereSunDiscSize { get; set; } = 1.0;

    /// <summary>3D preview: moon surface brightness multiplier.</summary>
    public double Preview3DAtmosphereMoonDiscStrength { get; set; } = 1.35;

    /// <summary>3D preview: moon angular-size multiplier.</summary>
    public double Preview3DAtmosphereMoonDiscSize { get; set; } = 1.0;

    /// <summary>3D preview: moon glow/aureole multiplier.</summary>
    public double Preview3DAtmosphereMoonGlowStrength { get; set; } = 0.7;

    /// <summary>3D preview: moon texture sharpen amount.</summary>
    public double Preview3DAtmosphereMoonTextureSharpness { get; set; } = 1.25;

    /// <summary>3D preview: moon direct world-light multiplier; does not affect atmospheric sky fill.</summary>
    public double Preview3DMoonWorldLightIntensity { get; set; } = 1.0;

    /// <summary>3D preview: always draw square/cross markers for sun and moon projection.</summary>
    public bool Preview3DShowCelestialDebug { get; set; }

    /// <summary>3D preview: clock time (0–24 h) for sun/moon cycle.</summary>
    public double Preview3DTimeOfDayHours { get; set; } = 12.0;

    public bool Preview3DAnimateTimeOfDay { get; set; }

    public double Preview3DTimeOfDaySpeed { get; set; } = 1.0;

    /// <summary>3D preview: below-horizon sky fog and aerial perspective on geometry.</summary>
    public double Preview3DHorizonFogStrength { get; set; } = 1.0;

    public bool Preview3DEnableGodRays { get; set; } = true;

    public bool Preview3DEnableVolumetricClouds { get; set; }

    /// <summary>0 = low, 1 = medium, 2 = high volumetric cost preset.</summary>
    public int Preview3DVolumetricQuality { get; set; } = 1;

    public double Preview3DGodRayStrength { get; set; } = 0.45;

    /// <summary>Debug: froxel god-ray inscatter gain (shader default 3.4).</summary>
    public double Preview3DGodRayScatterGain { get; set; } = 3.4;

    /// <summary>Debug: froxel god-ray Beer-Lambert extinction coefficient (shader default 1.15).</summary>
    public double Preview3DGodRayExtinction { get; set; } = 1.15;

    /// <summary>Debug: uniform froxel medium density so god rays show without fog/clouds (0 = off).</summary>
    public double Preview3DGodRayDebugDensity { get; set; }

    /// <summary>Debug: disable temporal god-ray reuse and freeze march jitter (reduces pulsing).</summary>
    public bool Preview3DGodRayStabilizeDebug { get; set; } = true;

    public double Preview3DCloudDensity { get; set; } = 0.35;

    /// <summary>0 = clear sky, 1 = weather map as baked.</summary>
    public double Preview3DCloudCoverageScale { get; set; } = 1.0;

    /// <summary>Added to the default cloud slab base height (world Y).</summary>
    public double Preview3DCloudLayerHeight { get; set; }

    public double Preview3DCloudVolumeHeight { get; set; } = 24.0;

    public double Preview3DCloudVolumeSize { get; set; } = 48.0;

    public double Preview3DCloudWindSpeed { get; set; } = 1.5;

    public double Preview3DCloudWindHeadingDegrees { get; set; } = 35.0;

    /// <summary>High thin cirrus sheet opacity (0 = off).</summary>
    public double Preview3DCloudCirrusStrength { get; set; } = 0.45;

    /// <summary>0 = off, 1 = coverage map, 2 = mid-layer density slice.</summary>
    public int Preview3DCloudDebugView { get; set; }

    public bool Preview3DCloudDisableTemporal { get; set; }

    /// <summary>Ray-march step override (0 = quality preset).</summary>
    public double Preview3DCloudMarchStepOverride { get; set; }

    public bool Preview3DCloudFreezeWind { get; set; }

    /// <summary>Final preview TAA on the composited RGB frame.</summary>
    public bool Preview3DEnablePreviewTaa { get; set; } = true;

    /// <summary>Final preview TAA tuning preset: 0 = less jitter, 1 = stable, 2 = edge AA, 3 = sharp, 4 = no projection jitter.</summary>
    public int? Preview3DTaaMode { get; set; }

    /// <summary>Bumped when persisted preview settings change encoding; used for one-time migrations on load.</summary>
    public int PersistedSettingsGeneration { get; set; }

    public double Preview3DTaaTemporalScale { get; set; } = 1.0;

    public double Preview3DTaaJitterScale { get; set; } = 1.0;

    public double Preview3DTaaSourceFilterScale { get; set; } = 1.0;

    public double Preview3DTaaEdgeBlendScale { get; set; } = 1.0;

    public double Preview3DTaaFxaaStrengthScale { get; set; } = 1.0;

    public double Preview3DTaaFxaaLumaEdgeScale { get; set; } = 1.0;

    public double Preview3DTaaFxaaLumaThreshold { get; set; } = 0.018;

    public bool Preview3DTaaForceFxaa { get; set; }

    /// <summary>Genesis Shadows Phase 2: master toggle for the directional shadow map pass.</summary>
    public bool Preview3DEnableShadows { get; set; } = true;

    /// <summary>Genesis Shadows Phase 2: light yaw in degrees (-180..180); drives the shadow ortho frustum.</summary>
    public double Preview3DLightYawDegrees { get; set; } = -35;

    /// <summary>Genesis Shadows Phase 2: light pitch in degrees (-89..89, negative = sun above horizon).</summary>
    public double Preview3DLightPitchDegrees { get; set; } = -55;

    /// <summary>Two-cascade directional shadows (near + far) for volume inject and geometry.</summary>
    public bool Preview3DEnableShadowCascades { get; set; }

    /// <summary>Number of crossed sprite planes to build for 2D Sprite flagged textures in 3D preview.</summary>
    public int Preview3DSpritePlaneCount { get; set; } = 1;

    /// <summary>Flat sprite preview depth in world units (0 = single-sided quad). Max ~25% of the 1×1 card.</summary>
    public double Preview3DSpriteThickness { get; set; }

    /// <summary>3D preview: orbit sensitivity in radians per pixel (Alt + middle mouse).</summary>
    public double Preview3DCameraOrbitSensitivity { get; set; } = 0.006;

    /// <summary>3D preview: pan sensitivity (middle mouse without Alt); scaled by camera distance.</summary>
    public double Preview3DCameraPanSensitivity { get; set; } = 0.0022;

    /// <summary>3D preview: mouse wheel zoom step strength.</summary>
    public double Preview3DCameraZoomSensitivity { get; set; } = 0.12;

    /// <summary>3D preview: orbit boom arm length (world units), pivot-to-eye distance for default framing.</summary>
    public double Preview3DCameraOrbitBoomDistance { get; set; } = PreviewCamera.DefaultOrbitBoomArmDistance;

    /// <summary>3D preview: keyboard key name to reset camera (Avalonia Key enum name, e.g. R, Home, Escape).</summary>
    public string Preview3DCameraResetKey { get; set; } = "R";

    /// <summary>3D preview: fly-camera look sensitivity in radians per pixel (right-drag while flying).</summary>
    public double Preview3DCameraFlyLookSensitivity { get; set; } = 0.006;

    /// <summary>3D preview: invert vertical look for orbit and fly cameras.</summary>
    public bool Preview3DCameraInvertLookY { get; set; }

    /// <summary>3D preview: fly move speed multiplier (WASD while right mouse held).</summary>
    public double Preview3DCameraFlyMoveSpeed { get; set; } = 1.0;

    /// <summary>3D preview: ease WASD fly movement in/out instead of instant starts and stops.</summary>
    public bool Preview3DCameraFlySmoothAcceleration { get; set; } = true;

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPBR");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    private static readonly JsonSerializerOptions SaveJsonSerializerOptions = new() { WriteIndented = true };

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {

                return new UserSettings();
            }


            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
            return settings ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, SaveJsonSerializerOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort; ignore persistence errors.
        }
    }
}
