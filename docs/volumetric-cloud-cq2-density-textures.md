# CQ2 — Volumetric cloud density textures and weather data

**Status:** Proposed  
**Roadmap:** [Volumetric cloud quality roadmap](volumetric-cloud-quality-roadmap.md)  
**Depends on:** [CQ1 precision and reconstruction](volumetric-cloud-cq1-precision-reconstruction.md)  
**Required by:** [CQ3 lighting](volumetric-cloud-cq3-lighting-cache.md), [CQ4 sparse volume](volumetric-cloud-cq4-sparse-voxel-sdf.md)

## Goal

Replace the visibly repetitive, low-resolution density-detail inputs with a deterministic, versioned cloud-density asset contract. CQ2 improves coherent cloud bodies, boundary detail, weather-scale variation, and mip stability without increasing the primary shape volume beyond 128³.

Success means:

- nearby cumulus boundaries contain fine billowy and wispy structure without breaking into foam;
- distant clouds select stable mip levels and do not shimmer under camera motion;
- weather patterns do not visibly repeat across the new far-horizon distance;
- cloud type, density, and convection are driven by separate data rather than one overloaded coverage value;
- v2 asset failure preserves a working v1 shell renderer;
- density-stage GPU cost remains bounded.

## Non-goals

- Do not change HDR reconstruction, temporal formats, or edge repair; those are owned by CQ1.
- Do not add cached sun lighting or terrain cloud shadows; those are owned by CQ3.
- Do not add sparse bricks or authored volumetric envelopes; those are owned by CQ4.
- Do not use a raw uncompressed 256³ RGBA shape texture. It would consume roughly 64 MiB before mipmaps while retaining the same basic representation.
- Do not download cloud assets at build or runtime.
- Do not add a general-purpose texture container or compression pipeline solely for CQ2.

## Baseline

The current bundled inputs are:

- `cloud_noise_shape_128.bin`: 128³ RGBA8 Perlin-Worley/Worley shape data;
- `cloud_noise_detail_32.bin`: 32³ RGBA8 Worley detail;
- `cloud_coverage_256.bin`: 256² RGBA8 periodic coverage/type data.

The base shape uses explicit LOD for some queries, but detail erosion relies on implicit texture derivatives inside a dynamic ray-march branch. The detail volume is small enough that its repeating cells and interpolation are visible near the camera. The weather field repeats too frequently relative to the new 72,000-unit shell radius and approximately 1,610-unit default cloud horizon.

## Formats and dimensions: versioned v2 asset ABI

Bundle these immutable raw assets:

| Asset | Dimensions/format | Filename | Approximate base-level size |
|------|-------------------|----------|-----------------------------|
| Shape | 128³ RGBA8 | `cloud_noise_shape_128_v2.bin` | 8 MiB |
| Detail | 64³ RGBA8 | `cloud_noise_detail_64_v2.bin` | 1 MiB |
| Weather | 1024² RGBA8 | `cloud_weather_1024_v2.bin` | 4 MiB |

Dimensions and generation version remain encoded in filenames. Loaders validate exact byte counts before upload. The generator owns deterministic seeds and channel semantics; runtime code must not infer a version from content.

Keep v1 assets during rollout. Preferred loading order is v2 bundled asset, matching deterministic v2 runtime generation for development, then v1 bundled asset. Normal packaged startup must load v2 rather than generate it.

## Channel definitions

### Shape volume

| Channel | Meaning |
|---------|---------|
| R | Coherent Perlin-Worley vapor body |
| G | Broad cellular billow envelope |
| B | Medium cellular lobe breakup |
| A | Fine shape erosion envelope |

The R channel must remain spatially coherent and dominate cloud interiors. G/B/A alter the boundary and upper lobes; they must not independently threshold the full body into disconnected cells.

### Detail volume

| Channel | Meaning |
|---------|---------|
| R | Broad billowy erosion |
| G | Fine billowy erosion |
| B | Wispy/sheared erosion |
| A | Curl/domain-distortion scalar |

The generator creates periodic seams in every axis. Broad and fine billow channels use decorrelated cellular seeds. The wispy channel uses an anisotropic, curl-warped field rather than another isotropic Worley octave. The distortion channel is zero-mean after shader remapping and is never used directly as density.

### Weather map

| Channel | Meaning | Range behavior |
|---------|---------|----------------|
| R | Coverage/humidity | Zero clears the column; one supplies a fully available vapor envelope |
| G | Cloud type | Zero favors shallow/stratiform; one favors vertically developed cumulus |
| B | Precipitation/density potential | Modulates extinction and dark-base response without changing coverage alone |
| A | Convection/updraft | Narrows/drifts towers and increases upper-lobe development |

All channels are periodic, but their periods, warps, and seeds differ. Large-scale coverage must contain features spanning hundreds of world units; detail must not dominate the weather silhouette.

## Deterministic generation

Update the preview cloud asset generator rather than introducing an external DCC dependency.

Generation requirements:

- fixed integer seeds committed with the generator;
- no time, locale, thread scheduling, or machine-dependent randomness;
- byte-identical output across supported development platforms;
- parallel generation may write disjoint voxels, but reductions and normalization use deterministic ordering;
- each output logs dimensions, byte count, and SHA-256;
- tests pin expected hashes and require an explicit version/filename change for intentional algorithm updates;
- the build target declares all v2 assets as outputs instead of checking only the legacy shape file;
- missing outputs regenerate the complete matching v2 set so channel generations cannot become mixed.

The asset tool writes to a temporary sibling file and atomically replaces the destination after validating byte count. It must not leave a partially written asset that passes only an existence check.

## Texture upload and memory

Upload v2 assets as RGBA8 with full mip chains and repeat wrapping. Use trilinear minification and linear magnification. The first CQ2 implementation keeps portable uncompressed GL formats; BC4/BC5 is a later desktop optimization and is not an acceptance dependency.

V2 base-level storage is approximately 13 MiB; mip chains raise this to roughly 17.3 MiB. Initialization diagnostics report actual dimensions and estimated allocated bytes.

## Capability fallback

On GLES/ANGLE or on allocation failure:

1. try the existing v1 assets;
2. if unavailable, use the current runtime procedural generation at v1 dimensions;
3. if texture creation fails entirely, retain the shader's procedural hash-density fallback and report degraded cloud density once.

Do not partially combine v2 channel semantics with v1 shader semantics. A single internal density-asset profile selects matching shader defines and texture bindings.

## Ray-footprint LOD

Every view/light density call receives or derives:

- current world-ray distance `t`;
- current march step length;
- trace-target pixel angular size;
- shape/detail world-space repeat periods;
- texture dimensions and maximum mip.

The CPU publishes vertical pixel angular size:

```text
pixelAngularSize = 2 * tan(verticalFov / 2) / traceTargetHeight
```

The shader estimates the world footprint:

```text
pixelFootprint = t * pixelAngularSize
sampleFootprint = max(marchStepLength, pixelFootprint)
lod = clamp(log2(sampleFootprint / worldTexelSize), 0, maxMip)
```

Use separate shape and detail world-texel sizes. Add a small negative detail LOD bias only on Cinematic (`-0.35`, clamped) and no bias on other presets.

Requirements:

- use `textureLod` for shape and detail samples inside view and light marches;
- never depend on implicit `texture` derivatives inside dynamic march control flow;
- conservative/occupancy queries may use a coarser explicit LOD than full material queries;
- debug density slices use LOD zero so asset inspection remains exact;
- light-cache generation in CQ3 uses its own voxel footprint rather than the camera pixel footprint.

## Architecture and data flow

The CPU selects one coherent density-asset profile, validates and uploads its textures, and publishes pixel angular size and texture metrics. Each shell or cache march samples weather first, builds the coherent shape field at an explicit footprint LOD, then applies boundary-weighted detail at its own explicit LOD. The resulting density feeds CQ1 view tracing and, after CQ2 acceptance, CQ3 cloud-light cache generation. CQ1 remains the only owner of temporal reconstruction and composition.

### Density material changes

### Coherent base

Keep weather coverage as an outside-in erosion threshold, but preserve a coherent R-channel contribution throughout occupied interiors. G/B/A shape data controls boundary lobes with weather type and convection. Do not multiply independent hard thresholds.

### Vertical structure

- Coverage controls where clouds can exist.
- Type controls shallow versus vertically developed profile.
- Convection controls footprint narrowing, upper drift, and upper-lobe contribution.
- Precipitation/density potential controls extinction, lower-body density, and CQ3 shadow strength.
- The condensation base remains shared and nearly horizontal except where strong convection/weather breakup explicitly perturbs it.

### Boundary detail

Low/Medium sample one detail coordinate. High/Cinematic sample a second coordinate only when the base-density edge weight is nonzero.

The second coordinate:

- rotates world XZ by a fixed non-axis-aligned matrix;
- uses a fixed decorrelated offset;
- advances with the same wind speed so the two fields do not slide through each other;
- blends at `0.35` High and `0.5` Cinematic;
- never evaluates in dense interiors.

Use billowy channels near upper cumulus boundaries and wispy channels near the lower/evaporating edge. Cirrus continues to use its procedural fiber model, but may use the wispy/detail distortion channels at Cinematic to break repeated filament patterns.

## Weather scale and addressing

The v2 weather map repeats over at least four times the v1 world period. Keep the map camera-independent and world anchored. Use wind advection as an offset before wrapping.

To suppress obvious tiling without a second weather texture:

- sample a low-frequency primary coordinate;
- sample a second rotated coordinate at a different scale;
- use A/convection as the low-frequency blend selector;
- keep the secondary contribution below `0.25` so weather systems do not become noisy at distance.

CQ4 may replace direct weather-map density placement near the camera, but it must retain this weather ABI as its large-scale driver.

## Debugging and diagnostics

Extend cloud debug views to identify:

- weather RGBA channels individually;
- shape R/G/B/A slices;
- detail R/G/B/A slices;
- selected shape/detail LOD;
- base density before erosion and final density after erosion;
- v1/v2 asset profile and fallback reason.

Debug views bypass temporal history and edge repair. They preserve scene/planet clipping so inspection cannot reintroduce the old foreground-cloud artifact.

## Failure handling

- Invalid asset byte count rejects the entire asset; never upload truncated data.
- Failure to load one v2 asset selects a coherent fallback profile rather than mixing unrelated channel versions.
- Texture allocation or mip generation failure releases partial v2 resources before trying v1.
- Runtime generator exceptions are caught and reported; the shader procedural fallback remains available.
- Shader compilation failure in the v2 variant selects v1 for the session and invalidates cloud history.
- Asset/profile transitions invalidate CQ1 color, metadata, moments, and repair history.

## Implementation milestones

- [ ] CQ2.0: Freeze v2 filenames, dimensions, channel ABI, seeds, and expected memory.
- [ ] CQ2.1: Implement deterministic shape, detail, and weather generation.
- [ ] CQ2.2: Update the asset-generation build target and commit generated v2 assets.
- [ ] CQ2.3: Add strict v2 loading, coherent v1 fallback, profile diagnostics, and cleanup.
- [ ] CQ2.4: Add pixel-angular-size uniform and explicit shape/detail ray-footprint LOD.
- [ ] CQ2.5: Consume type, precipitation, and convection independently in density shaping.
- [ ] CQ2.6: Add edge-only rotated detail and expanded weather addressing.
- [ ] CQ2.7: Add debug views and automated asset/shader tests.
- [ ] CQ2.8: Complete fixed-scene visual and GPU performance acceptance.

## Test matrix

### Asset-generation tests

- Exact dimensions and byte counts for every v2 output.
- Pinned SHA-256 for fixed generator version.
- Seam equality/continuity across all three volume axes and both weather axes.
- Channel histograms remain inside documented occupancy ranges and are not constant/correlated duplicates.
- Repeated parallel generation is byte-identical.
- Atomic output leaves prior valid data intact when generation is intentionally failed.

### Loader/upload tests

- V2 preferred when all assets are valid.
- Wrong size, missing file, and corrupt file select one coherent fallback profile.
- V1 assets remain accepted with v1 shader semantics.
- Live GL upload creates complete mip chains and expected wrapping/filtering.
- Failed v2 allocation releases textures before v1 retry.

### Shader/source tests

- No implicit `texture(detailNoise, ...)` remains in dynamic cloud/light marches.
- CPU pixel-angular-size calculation matches fixed expected FOV/height values.
- LOD increases monotonically with ray distance and march-step length.
- High/Cinematic second detail lookup is guarded by edge weight and quality.
- Weather B/A channels affect density/extinction and convection independently.

### Visual scenarios

- Close camera below, within, and above cumulus.
- Thin upper billows, dark flat base, and evaporating lower wisps.
- Long horizon view with frozen wind to expose tiling.
- Slow camera translation to expose mip shimmer.
- Cirrus-heavy view with and without Cinematic distortion detail.
- Sparse/broken, fair-weather, congested, and overcast weather fixtures.

### Quantitative correctness

- V2 seam tests have zero byte discontinuity at periodic boundaries.
- Fixed-camera wind-frozen density is deterministic across runs.
- Selected detail LOD changes without temporal oscillation for a monotonic distance sweep.
- Phase 6 scene-depth and CQ1 temporal/HDR fixtures remain green.

## Performance gate

- High density-evaluation GPU time is no more than `1.20×` the accepted CQ1 High density-stage median.
- Cinematic cost is reported independently and must not change Low/Medium/High profile settings to compensate.

## Exit criteria

- All v2 assets are deterministic, versioned, bundled, strictly validated, and diagnosable.
- Shape interiors remain coherent while boundaries gain distinct billowy and wispy scales.
- Weather coverage, type, precipitation/density, and convection have separate stable meanings.
- All dynamic cloud/light marches use explicit ray-footprint LOD.
- High/Cinematic hide obvious short-period detail repetition without evaluating the second detail sample in interiors.
- V1 and procedural compatibility fallbacks remain functional.
- Visual, asset, live-GL, performance, and Phase 6/CQ1 regression evidence is complete.

## References

- Andrew Schneider, *Nubis: Authoring Real-Time Volumetric Cloudscapes with the Decima Engine*: <https://advances.realtimerendering.com/s2017/Nubis%20-%20Authoring%20Realtime%20Volumetric%20Cloudscapes%20with%20the%20Decima%20Engine%20-%20Final%20.pdf>
- Guerrilla Games, *Nubis³*: <https://www.guerrilla-games.com/read/nubis-cubed>
- Epic Games, *Volumetric Cloud Reference — Conservative Density*: <https://dev.epicgames.com/documentation/en-us/unreal-engine/volumetric-clouds-reference?application_version=4.27>
