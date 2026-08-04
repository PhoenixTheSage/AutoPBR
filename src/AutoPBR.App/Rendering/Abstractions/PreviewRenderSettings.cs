namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>Debug visualization modes for the screen-space cloud pass (preview only).</summary>
public enum PreviewCloudDebugView
{
    Off = 0,
    WeatherCoverage = 1,
    FinalDensity = 2,
    WeatherCloudType = 3,
    WeatherDensityPotential = 4,
    WeatherConvection = 5,
    ShapeCoherentBody = 6,
    ShapeBroadBillow = 7,
    ShapeMediumBreakup = 8,
    ShapeFineErosion = 9,
    DetailBroadBillow = 10,
    DetailFineBillow = 11,
    DetailWispyErosion = 12,
    DetailCurlDistortion = 13,
    SelectedLod = 14,
    BaseDensity = 15,
    AssetProfile = 16,
    SparseClipmapLevel = 17,
    SparsePageState = 18,
    SparsePhysicalBrick = 19,
    SparseBaseDensity = 20,
    SparseConservativeDistance = 21,
    SparseTraversalSteps = 22,
    SparseFallbackContribution = 23,
    SparseTemplateFamily = 24,
    SparseCascadeBlend = 25
}

public sealed class PreviewRenderSettings
{
    public float NormalStrength { get; init; } = 1f;
    public float HeightStrength { get; init; } = 0.05f;
    public float SpecularStrength { get; init; } = 1f;
    public float RoughnessScale { get; init; } = 1f;
    public float AmbientStrength { get; init; } = 0.35f;
    public float Exposure { get; init; } = 1f;
    public bool EnableParallax { get; init; } = true;
    public bool EnableNormalMap { get; init; } = true;
    public bool EnableSpecularMap { get; init; } = true;
    public bool AutoRotate { get; init; } = true;
    public float LightYawDegrees { get; init; } = -35f;
    public float LightPitchDegrees { get; init; } = -55f;
    public bool NearestTextureFilter { get; init; } = true;
    /// <summary>Item plane: alpha test threshold in linear 0..1.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;
    public bool ItemUseAlphaBlend { get; init; }

    /// <summary>Emulated entity meshes: alpha test / blend for diffuse and shadow passes.</summary>
    public PreviewEntityAlphaMode EntityAlphaMode { get; init; } = PreviewEntityAlphaMode.Cutout;

    /// <summary>
    /// When true (default), runtime entity preview uses normal + specular maps like block previews.
    /// Turn off if a rig shows tangent mismatch artifacts.
    /// </summary>
    public bool EnableEntityLabPbrShading { get; init; } = true;

    /// <summary>
    /// When true, POM / parallax AO / parallax self-shadow apply to emulated entity meshes (off by default; sensitive to UV seams).
    /// </summary>
    public bool EnableEntityParallax { get; init; }

    public int SpritePlaneCount { get; init; } = 1;

    /// <summary>Flat sprite preview: depth of the item plane in world units (0 = single-sided quad).</summary>
    public float SpriteThickness { get; init; }

    /// <summary>When true, crossed-plane count is ignored and a single textured sprite plane/cuboid is used.</summary>
    public bool ItemFlatSpritePreview { get; init; }

    /// <summary>Draw a subtle XZ grid under the preview object in 3D mode.</summary>
    public bool ShowBackgroundGrid { get; init; } = true;

    /// <summary>Ground-grid tint (linear 0–1 RGBA applied as a shader color multiply).</summary>
    public float GridColorR { get; init; } = 0.949f;
    public float GridColorG { get; init; } = 0.949f;
    public float GridColorB { get; init; } = 0.973f;
    public float GridColorA { get; init; } = 0.910f;

    /// <summary>Draw the textured grass ground plane under the preview object in 3D mode.</summary>
    public bool ShowGroundMesh { get; init; } = true;

    /// <summary>
    /// Hard Full-detail Chebyshev radius in chunks for streamed terrain (LOD + fog extend beyond).
    /// </summary>
    public int ChunkViewDistance { get; init; } = 8;

    /// <summary>
    /// Extra Chebyshev chunks beyond <see cref="ChunkViewDistance"/> kept as combined distant LOD
    /// sections (up to 1024 for extreme horizon ranges).
    /// </summary>
    public int LodRingChunks { get; init; } = 128;

    /// <summary>Deterministic world seed for streamed terrain (pad under subject stays flat).</summary>
    public int TerrainWorldSeed { get; init; } = 0x41504252;

    /// <summary>Biome region scale (1 = default; larger = bigger biomes).</summary>
    public float TerrainBiomeSize { get; init; } = 1f;

    /// <summary>Height amplification multiplier for all biomes.</summary>
    public float TerrainAmplification { get; init; } = 1f;

    /// <summary>Mountain analytical erosion strength (1 = default).</summary>
    public float TerrainErosionStrength { get; init; } = 1f;

    /// <summary>Continental climate stretch (higher = more inland/mountain mass).</summary>
    public float TerrainContinentalness { get; init; } = 1f;

    /// <summary>Draw RGB world-axis lines in a corner (matches block Y-rotation).</summary>
    public bool ShowCornerAxes { get; init; } = true;

    /// <summary>
    /// When false, only the shared environment is drawn (sky, sun, ground plane, grid/axes); the preview cube/item mesh is omitted until maps exist.
    /// </summary>
    public bool DrawPreviewSubject { get; init; } = true;

    /// <summary>Genesis: cheap subsurface scattering approximation when LabPBR _s.b >= 65.</summary>
    public bool EnableSss { get; init; } = true;

    /// <summary>Genesis: parallax self-shadow trace toward the light from the POM hit point.</summary>
    public bool EnableParallaxShadow { get; init; } = true;

    /// <summary>Genesis POM: primary height-field march layers. Higher is more accurate at grazing angles.</summary>
    public int ParallaxTraceLayers { get; init; } = 64;

    /// <summary>Genesis POM: binary/secant refinement steps after the primary layer hit.</summary>
    public int ParallaxRefineSteps { get; init; } = 5;

    /// <summary>Genesis POM: self-shadow ray samples from the POM hit point toward the light.</summary>
    public int ParallaxShadowSamples { get; init; } = 24;

    /// <summary>Genesis POM: receiver-side softness for parallax self-shadow visibility.</summary>
    public float ParallaxShadowSoftness { get; init; } = 1.25f;

    /// <summary>Genesis POM: maximum UV travel allowed for one parallax trace (see PreviewStageConstants).</summary>
    public float ParallaxMaxUvShift { get; init; } = 0.45f;

    /// <summary>Genesis high-end preview: tessellate triangles and displace high height-map regions outward.</summary>
    public bool EnableTessellationDisplacement { get; init; }

    /// <summary>Genesis tessellation: fixed triangle tessellation level for the preview path.</summary>
    public float TessellationLevel { get; init; } = 8f;

    /// <summary>Genesis tessellation: maximum world-space outward protrusion at full height.</summary>
    public float TessellationDisplacementStrength { get; init; } = 0.06f;

    /// <summary>Genesis: toggle for POM-derived contact ambient occlusion.</summary>
    public bool EnableParallaxAo { get; init; } = true;

    /// <summary>Genesis: contact ambient occlusion strength derived from the POM hit neighborhood.</summary>
    public float ParallaxAoStrength { get; init; } = 1f;

    /// <summary>Genesis: environment IBL — split-sum sky LUT specular + diffuse hemisphere when atmospheric sky is on.</summary>
    public bool EnableIbl { get; init; } = true;

    /// <summary>Atmospheric sky: render LUT-driven sky background and ambient probes.</summary>
    public bool EnableAtmosphericSky { get; init; } = true;

    /// <summary>Atmospheric sky: aerosol concentration proxy (higher = hazier horizon).</summary>
    public float AtmosphereTurbidity { get; init; } = 2.6f;

    /// <summary>Atmospheric sky: sun radiance multiplier for sky/background lighting.</summary>
    public float AtmosphereSunIntensity { get; init; } = 10f;

    /// <summary>Atmospheric sky: controls horizon extinction rolloff.</summary>
    public float AtmosphereHorizonFalloff { get; init; } = 1.35f;

    /// <summary>Atmospheric sky: master brightness on the composite sky pass.</summary>
    public float AtmosphereSkyExposure { get; init; } = 0.85f;

    /// <summary>Atmospheric sky: additive glare around the sun disc (separate from scatter intensity).</summary>
    public float AtmosphereSunDiscStrength { get; init; } = 0.35f;

    /// <summary>Atmospheric sky: limb-darkened sun disc surface brightness (bloom is separate).</summary>
    public float AtmosphereSunDiscBrightness { get; init; } = 1f;

    /// <summary>Atmospheric sky: sun angular-size multiplier (1 = legacy stylized size; ~0.07 = real sun).</summary>
    public float AtmosphereSunDiscSize { get; init; } = 1f;

    /// <summary>Moon billboard: surface brightness multiplier.</summary>
    public float AtmosphereMoonDiscStrength { get; init; } = 1.35f;

    /// <summary>Moon billboard: angular-size multiplier.</summary>
    public float AtmosphereMoonDiscSize { get; init; } = 1f;

    /// <summary>Moon billboard: outer glow/aureole multiplier.</summary>
    public float AtmosphereMoonGlowStrength { get; init; } = 0.7f;

    /// <summary>Moon billboard: shader texture sharpen amount.</summary>
    public float AtmosphereMoonTextureSharpness { get; init; } = 1.25f;

    /// <summary>Moonlight: multiplier for direct world lighting at night; does not affect sky/atmosphere fill.</summary>
    public float MoonWorldLightIntensity { get; init; } = 1f;

    /// <summary>Horizon fog on sky dome below horizon and aerial perspective on geometry.</summary>
    public float AerialFogStrength { get; init; } = 1f;

    /// <summary>Clock time (0–24 h) for sun/moon cycle UI; drives yaw/pitch when edited.</summary>
    public float TimeOfDayHours { get; init; } = 12f;

    /// <summary>When true, advance <see cref="TimeOfDayHours"/> from render time (full cycle in 24 / speed seconds).</summary>
    public bool AnimateTimeOfDay { get; init; }

    /// <summary>Game-hours advanced per real second when <see cref="AnimateTimeOfDay"/> is on.</summary>
    public float TimeOfDaySpeed { get; init; } = 1f;

    /// <summary>Debug tab: downsample and hash the default framebuffer after the preview frame (off by default; expensive readback).</summary>
    public bool CapturePreviewFingerprint { get; init; }

    /// <summary>Genesis: SSS contribution scalar (multiplier on wrap + transmission lobes).</summary>
    public float SssStrength { get; init; } = 1f;

    /// <summary>Genesis: indirect environment IBL contribution scalar.</summary>
    public float IblStrength { get; init; } = 0.6f;

    /// <summary>Genesis: emission scalar (LabPBR _s.a additive).</summary>
    public float EmissionStrength { get; init; } = 1f;

    /// <summary>Render-time animation speed multiplier for emulated entity preview rigs.</summary>
    public float EntityAnimationSpeed { get; init; } = 1f;

    /// <summary>Render-time animation amplitude multiplier for emulated entity preview rigs.</summary>
    public float EntityAnimationAmplitude { get; init; } = 1f;

    /// <summary>When false, render-time animation for emulated entity preview rigs is disabled.</summary>
    public bool EnableEntityAnimation { get; init; } = true;

    /// <summary>When true, vertex-baked idle motion for emulated entities holds at the clock value from when this was enabled.</summary>
    public bool PauseEntityIdleAnimation { get; init; }

    /// <summary>
    /// Legacy whole-mesh bob/yaw/roll on the model matrix (pre–setupAnim IR). Off by default; use lifted IR / GPU bones instead.
    /// </summary>
    public bool EnableLegacyEntityWobble { get; init; }

    /// <summary>
    /// When true, skin entity preview on the CPU (<c>SkinAndBakeToPreviewLayout</c>) and upload a 12-float preview-space VBO
    /// instead of the GPU bind mesh + entity shader path. Debug / parity fallback only.
    /// </summary>
    public bool ForceEntityCpuSkinning { get; init; }

    /// <summary>Genesis Shadows Phase 2: master toggle for the directional shadow map pass.</summary>
    public bool EnableShadows { get; init; } = true;

    /// <summary>Genesis Shadows Phase 2: shadow map resolution per side (256-4096, square). Near cascade uses this when cascades are on.</summary>
    public int ShadowMapResolution { get; init; } = 4096;

    /// <summary>Genesis Shadows Phase 2: minimum slope-scaled depth bias to combat acne.</summary>
    public float ShadowMinBias { get; init; } = 0.002f;

    /// <summary>Genesis Shadows Phase 2: maximum slope-scaled depth bias (used at grazing N.L).</summary>
    public float ShadowMaxBias { get; init; } = 0.012f;

    /// <summary>Genesis Shadows: receiver-side PCF disk radius in shadow-map texels.</summary>
    public float ShadowSoftnessTexels { get; init; } = 1.0f;

    /// <summary>
    /// Max world-space radius from the camera for directional shadow casting/receiving.
    /// Far cascade coverage and distance fade use this (clamped 32..256).
    /// </summary>
    public float ShadowDistance { get; init; } = 128f;

    /// <summary>
    /// Shadow tone lightness/darkness (0 = none, 1 = as sampled, &gt;1 deeper umbra via ambient).
    /// Preserves sampled footprint; clamped 0..3 in the preview settings snapshot.
    /// </summary>
    public float ShadowStrength { get; init; } = 1f;

    /// <summary>
    /// Cascaded directional shadows (near/mid/far distance LODs with stepped map resolutions).
    /// </summary>
    public bool EnableShadowCascades { get; init; }

    /// <summary>Genesis: froxel volume god rays toward the sun.</summary>
    public bool EnableGodRays { get; init; } = true;

    /// <summary>Genesis: froxel volume inject + integrate god-ray path.</summary>
    public bool EnableVolumeGodRays { get; init; } = true;

    /// <summary>Genesis: procedural volumetric cloud layer in the sky pass.</summary>
    public bool EnableVolumetricClouds { get; init; }

    /// <summary>Volumetric cost preset: 0 = low, 1 = medium, 2 = high.</summary>
    public int VolumetricQuality { get; init; } = 1;

    public float GodRayStrength { get; init; } = 0.45f;
    public float GodRayConeScale { get; init; } = 1.25f;

    /// <summary>
    /// Layer shadow-aware / screen-space god rays for fine beams (leaf gaps) the froxel grid misses.
    /// When froxel volumetric fog is off, this is the standalone shaft path.
    /// </summary>
    public bool EnableScreenSpaceGodRays { get; init; } = true;

    /// <summary>Intensity of the screen-space / shadow-map god-ray layer.</summary>
    public float ScreenSpaceGodRayStrength { get; init; } = 0.85f;

    /// <summary>
    /// Multiplier on froxel XY/slice resolution (1 = quality preset, higher = finer less-blocky shafts).
    /// </summary>
    public float GodRayFroxelResolution { get; init; } = 1.35f;

    /// <summary>
    /// 0 = sun-lit fog visible from any camera angle (isotropic), 1 = pure Mie shafts only when looking toward the sun.
    /// </summary>
    public float GodRayPhaseDirectivity { get; init; } = 0.42f;

    /// <summary>Debug: inscatter accumulation gain in the froxel integrate march (sun shafts only).</summary>
    public float GodRayScatterGain { get; init; } = 3.6f;

    /// <summary>Debug: extinction coefficient for Beer-Lambert transmittance in the integrate march.</summary>
    public float GodRayExtinction { get; init; } = 0.70f;

    /// <summary>Debug: uniform participating-medium density injected into the froxel volume so god rays are
    /// visible without height fog or clouds (0 = off / production behaviour).</summary>
    public float GodRayDebugDensity { get; init; }

    /// <summary>Debug: disable froxel/integrate/upsample temporal reuse and freeze march jitter to stop pulsing.</summary>
    public bool GodRayStabilizeDebug { get; init; } = true;

    /// <summary>When true, screen-space god-ray shaders skip odd march steps in empty/occluded segments.</summary>
    public bool GodRaySparseMarch { get; init; } = true;

    public float CloudDensity { get; init; } = 0.35f;
    public float CloudVolumeSize { get; init; } = 48f;
    public float CloudLayerHeight { get; init; }
    public float CloudVolumeHeight { get; init; } = 24f;
    public int CloudQuality { get; init; } = 1;

    /// <summary>Scales weather-map coverage before the density remap (0 = clear sky, 1 = map as baked).</summary>
    public float CloudCoverageScale { get; init; } = 1f;

    /// <summary>Cloud field drift speed in world units per second.</summary>
    public float CloudWindSpeed { get; init; } = 1.5f;

    /// <summary>Wind heading in degrees on the XZ plane (0 = +X).</summary>
    public float CloudWindHeadingDegrees { get; init; } = 35f;

    /// <summary>Opacity of the high thin cirrus sheet above the main cloud layer (0 = disabled).</summary>
    public float CloudCirrusStrength { get; init; } = 0.45f;

    /// <summary>Number of stacked cumulus altitude decks (1–3).</summary>
    public int CloudCumulusLayerCount { get; init; } = 2;

    /// <summary>Clear-air gap between successive cumulus decks.</summary>
    public float CloudInterDeckGap { get; init; } = 12f;

    /// <summary>Procedural XZ base-height variance amplitude inside each cumulus deck.</summary>
    public float CloudLayerHeightVariance { get; init; } = 6f;

    /// <summary>Thickness scale applied to upper cumulus decks relative to the primary deck.</summary>
    public float CloudUpperThicknessScale { get; init; } = 0.65f;

    /// <summary>Coverage scale multiplier for upper cumulus decks.</summary>
    public float CloudUpperCoverageScale { get; init; } = 0.70f;

    /// <summary>Density scale multiplier for upper cumulus decks.</summary>
    public float CloudUpperDensityScale { get; init; } = 0.85f;

    /// <summary>Wind speed scale for upper cumulus decks relative to the primary deck.</summary>
    public float CloudUpperWindSpeedScale { get; init; } = 1.35f;

    /// <summary>Clear-air gap from the top cumulus deck to the cirrus sheet.</summary>
    public float CloudCirrusGap { get; init; } = 120f;

    /// <summary>Thickness of the cirrus sheet.</summary>
    public float CloudCirrusThickness { get; init; } = 2.5f;

    /// <summary>
    /// Cloud style bias: 0 = Auto (weather-driven), 1 = Humilis, 2 = Mediocris,
    /// 3 = Congestus, 4 = Stratus.
    /// </summary>
    public int CloudStyleBias { get; init; }

    /// <summary>Debug overlay replacing the lit cloud composite (coverage map or mid-layer density slice).</summary>
    public PreviewCloudDebugView CloudDebugView { get; init; }

    /// <summary>Debug: disable cloud temporal reprojection even when the quality preset enables it.</summary>
    public bool CloudDisableTemporal { get; init; }

    /// <summary>Debug: override ray-march step count (0 = follow quality preset).</summary>
    public int CloudMarchStepOverride { get; init; }

    /// <summary>Debug: hold wind advection at time zero so cloud shapes stay fixed while tuning.</summary>
    public bool CloudFreezeWind { get; init; }

    /// <summary>When true, log froxel inject/integrate timings that exceed the documented budget (Debug tab; off by default).</summary>
    public bool LogVolumetricTiming { get; init; }

    /// <summary>When true, emit periodic TXAA resolve/readback diagnostics (Debug tab; off by default; expensive on the render thread).</summary>
    public bool LogPreviewTaaDiagnostics { get; init; }

    /// <summary>When true, emit periodic P8 GPU pass timings to the preview log (Debug tab; off by default).</summary>
    public bool LogGpuPassTimings { get; init; }

    /// <summary>When true, FPS overlay shows per-pass GPU timings; otherwise only total GPU ms.</summary>
    public bool ShowExpandedGpuTimingHud { get; init; }

    /// <summary>
    /// Occlusion culling debug: 0 = off, 1 = stats (HUD/log counter readback), 2 = tint/keep Hi-Z for inspection.
    /// See <see cref="PreviewOcclusionDebugMode"/>.
    /// </summary>
    public int OcclusionDebugMode { get; init; }

    /// <summary>Final full-res TAA on the composited preview frame (uses shared temporal reprojection).</summary>
    public bool EnablePreviewTaa { get; init; } = true;

    /// <summary>Screen-space ambient occlusion (SSAO / GTAO) on lit opaque scene color.</summary>
    public bool EnableScreenSpaceAo { get; init; }

    /// <summary><see cref="PreviewAoMode"/>: Auto / SSAO / GTAO.</summary>
    public int PreviewAoMode { get; init; }

    /// <summary>How strongly AO multiplies lit opaque color (0 = off, 1 = full).</summary>
    public float AoStrength { get; init; } = 0.85f;

    /// <summary>AO sample radius in view-space units.</summary>
    public float AoRadius { get; init; } = 0.55f;

    /// <summary>Power curve applied to occlusion visibility.</summary>
    public float AoPower { get; init; } = 1.15f;

    /// <summary>0 = off, 1 = raw AO, 2 = strength-applied AO.</summary>
    public int AoDebugView { get; init; }

    /// <summary>Final preview TAA tuning preset: 0 = less jitter, 1 = stable, 2 = edge AA, 3 = sharp, 4 = no projection jitter.</summary>
    public int PreviewTaaMode { get; init; }

    /// <summary>Multiplier for the TAA history blend from the selected preset.</summary>
    public float PreviewTaaTemporalScale { get; init; } = 1f;

    /// <summary>Multiplier for the projection jitter amplitude from the selected preset.</summary>
    public float PreviewTaaJitterScale { get; init; } = 1f;

    /// <summary>Multiplier for the current-frame source filter near TAA edges.</summary>
    public float PreviewTaaSourceFilterScale { get; init; } = 1f;

    /// <summary>Multiplier for the edge blur/coverage blend from the selected preset.</summary>
    public float PreviewTaaEdgeBlendScale { get; init; } = 1f;

    /// <summary>Multiplier for the final FXAA-lite pass strength.</summary>
    public float PreviewTaaFxaaStrengthScale { get; init; } = 1f;

    /// <summary>Multiplier for how strongly luma edges participate in the final FXAA-lite mask.</summary>
    public float PreviewTaaFxaaLumaEdgeScale { get; init; } = 1f;

    /// <summary>Luma contrast threshold for the final FXAA-lite pass.</summary>
    public float PreviewTaaFxaaLumaThreshold { get; init; } = 0.018f;

    /// <summary>Debug: bypass geometry/depth masks and apply FXAA-lite to all luma edges.</summary>
    public bool PreviewTaaForceFxaa { get; init; }

    /// <summary>Debug overlay: sun projection frustum lines in the preview viewport.</summary>
    public bool ShowSunProjectionDebug { get; init; }

    /// <summary>When true, scene passes write linear scene-referred color and present encodes to scRGB.</summary>
    public bool HdrPresentActive { get; init; }

    /// <summary>Paper-white reference luminance (nits) for scRGB present scale.</summary>
    public float HdrPaperWhiteNits { get; init; } = 200f;

    /// <summary>Display peak luminance (nits) for soft knee; 0 = unknown.</summary>
    public float HdrPeakNits { get; init; }
}
