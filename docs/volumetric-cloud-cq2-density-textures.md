# CQ2 — Volumetric cloud density textures and weather data

**Status:** Complete
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

## Implementation checkpoint

CQ2.0 completed on 2026-07-28. `PreviewCloudDensityAssetContract` is the source of truth for the v2 filenames, dimensions, RGBA channel semantics, deterministic per-channel seeds, byte counts, and mip-chain memory. The runtime now selects the three legacy blobs as one coherent profile and reports that profile in cloud diagnostics.

CQ2.1 completed on 2026-07-28. `PreviewCloudDensityAssetGenerator` now produces all three v2 payloads with fixed-point toroidal value/cellular fields. Generation writes independent texels in parallel without floating-point operations or order-dependent reductions. Shape R remains a coherent field while G/B/A add progressively finer cellular structure; detail B uses integer-transformed, curl-warped anisotropic coordinates; weather produces distinct coverage, type, density-potential, and convection fields. Tests pin every SHA-256, compare repeated parallel runs byte-for-byte, require identical texels at every opposing edge, and reject constant, narrow, duplicate, or highly correlated channels. The v2 profile remains deliberately disabled at runtime until the generated assets, shader semantics, and upload path land together; this preserves the accepted CQ1.9 image during the asset rollout.

CQ2.2 completed on 2026-07-28. The three pinned v2 blobs are bundled under `Assets/Preview` and included by the existing recursive Avalonia resource declaration. `GeneratePreviewCloudAssets` now declares the generator/tool sources as inputs and all seven legacy, STBN, and v2 blobs as one output set. A missing or stale member therefore regenerates the complete set rather than leaving mixed channel versions. Repository tests compare every bundled v2 byte against a fresh deterministic generation, verify pinned hashes and dimensions, assert the complete MSBuild output list, and confirm that the bundled loader can resolve one complete v2 profile when explicitly allowed. Production continued to pass `allowV2: false` at this checkpoint; CQ2.3–CQ2.5 subsequently landed the strict runtime selection and shader semantics required to open the desktop profile.

CQ2.3 completed on 2026-07-28. V2 selection now requires the exact descriptor byte count and pinned SHA-256 for every member before it can return one coherent profile. A missing, short, or corrupt v2 member rejects the entire set and records the per-asset reason before selecting one complete bundled v1 set. GPU replacement is transactional across shape, detail, and weather: candidate textures are allocated, uploaded, mipmapped, and GL-error checked before the prior set is released. A v2 upload failure retries bundled v1; bundled load/upload failure retries a complete generated v1 set; total texture failure leaves the shader procedural fallback available. Profile diagnostics include selection/fallback reason, dimensions, and base-level bytes. Asset-version changes participate in the CQ1 history settings key and explicitly invalidate temporal history. The production gate remained on v1 at this checkpoint and was opened on desktop by CQ2.5 after its matching channel semantics landed.

CQ2.4 completed on 2026-07-28. `PreviewCloudRayFootprint` computes vertical pixel angular size from the active camera FOV and actual trace-target height. The half/two-thirds cloud trace receives its own value, while full-resolution Cinematic edge repair receives the display-height value. View samples derive `sampleFootprint = max(marchStepLength, rayDistance × pixelAngularSize)`; light samples conservatively combine that view footprint with each sun-march interval. Shape, detail, and loop-time weather reads now use explicit `textureLod` based on their world repeat size and runtime texture dimensions. Conservative weather occupancy may read one mip coarser, Cinematic detail applies the bounded `-0.35` bias, and debug density inspection passes a zero footprint to force LOD zero. Camera-FOV changes participate in temporal-history invalidation. Source, CPU-policy, GLES-adaptation, and live desktop shader compilation tests are green. CQ2.5 subsequently mapped the independent weather/detail channels and opened desktop v2 selection.

CQ2.5 completed on 2026-07-28. The shader ABI now receives the selected density-asset version in both the primary trace and full-resolution repair passes. V1 retains its accepted density equations and explicitly neutralizes the legacy weather map's placeholder B/A bytes; GLES/ANGLE continues to select this coherent v1 profile. Validated desktop v2 profiles independently use weather R for placement/coverage, G for vertical type, B for lower-body density/extinction potential, and A for convective vertical development. V2 detail R/G form boundary billows while B supplies lower/evaporating wisps; channel weights remain bounded so detail erosion cannot hollow the body. The short sun march reuses each filtered weather sample and applies the same B-channel extinction scale, allowing denser systems to develop darker bases without changing their coverage footprint. The desktop rollout gate is now open after strict asset validation and transactional upload; any v2 load/upload failure still selects one complete v1 set and invalidates temporal history. Source-policy, GLES-adaptation, and native WGL compile/draw tests cover the version branch and both trace/repair uniform paths.

CQ2.6 completed on 2026-07-28. V2 weather now uses a world-anchored primary period four times the legacy v1 period plus a fixed toroidal `26.6°` scaled-rotation secondary address at `1/√5` effective period. Its integer matrix preserves exact wrapping while changing frequency and direction. Primary convection selects a bounded secondary blend from `0.08` to `0.22`; both fields derive from the same advected primary coordinate, so they move with one wind velocity rather than sliding. CPU wind wrapping and temporal wind-delta unwrapping now use the `16×` v2 primary period, which remains an integer multiple of v1 weather/detail periods. V1 returns before secondary addressing and retains its prior sampling semantics. V2 detail keeps one lookup on Low/Medium. High/Cinematic perform a second explicitly filtered lookup only when the base-density edge weight is nonzero, rotating advected XZ, adding a fixed offset and a bounded A-channel curl distortion, then blending at `0.35/0.50`. Dense interiors and coherent v1 never execute it. The same quality argument is used by primary tracing, density debug inspection, and Cinematic repair. As an adjacent visual correction, the full-resolution post-temporal upsample now converts reconstructed opacity into stronger direct-beam extinction only inside the projected visible Sun disc: thin cloud softens the core, opacity `>= 0.60` seals it, off-disc opacity and cloud radiance remain unchanged, and debug/disabled/nighttime Sun states bypass the adjustment. A live floating-point GPU readback verifies `0.60 → >=0.995` alpha on-disc and unchanged `0.60` alpha off-disc.

CQ2.7 completed on 2026-07-28. The persisted debug ABI retains Off/Weather Coverage/Final Density at values `0/1/2` and appends individual Weather G/B/A, Shape RGBA, Detail RGBA, selected shape/detail LOD, base density, and asset-profile views through value `16`. Raw channel slices force LOD zero; the LOD inspector uses the production trace-step, pixel-angular-size and Cinematic detail-bias policy and encodes normalized shape LOD in red and detail LOD in green. The asset-profile view distinguishes v2 bundled, intentional v1 compatibility, v1 fallback, runtime-generated v1 and procedural shader fallback; selecting it also logs the exact loader/upload reason. Every debug view disables temporal reconstruction, Cinematic edge repair, final cloud presentation encoding and direct-disc adjustment while preserving scene-depth, planet and horizon clipping. The deterministic tool now shares a tested atomic writer whose failed commit leaves the prior valid asset untouched. Automated coverage includes enum/settings compatibility, all shader inspectors, fallback diagnostics, partial-upload cleanup, explicit-LOD contracts, GLES source adaptation, complete repeat-filtered 3D/2D mip-chain state, native desktop shader compilation, and the existing CQ1 depth/HDR/disc readbacks.

CQ2.8 completed on 2026-07-28. The opt-in `PreviewCloudCq2AcceptanceTests` harness captures thirteen debug-off `1920×1080` fixtures after 32 discarded warm-up frames and retains 240 pass-scoped GPU-query samples per fixture. The matrix covers below/inside/above cumulus, upper billows and lower wisps, long-horizon tiling, a three-pose camera translation, High/Cinematic cirrus, and fair, broken, congested and overcast weather. Each PNG is paired with camera/material metadata, SHA-256, luminance range, timing mode and adjacent-translation deltas in JSON/CSV artifacts. The final evidence used desktop GL `4.6.0 NVIDIA 610.74` on an RTX 2080 Ti and confirmed the validated `cq2-v2/v2-bundled/cq2-density-v2` profile with debug inspection disabled. The gated High dense-overcast window preserves asynchronous CQ1-comparable query scheduling and measured `0.552/0.565 ms` trace p50/p95: `0.729×` the accepted CQ1 High trace median and below the `0.908 ms` (`1.20×`) gate. Its cloud-total p50/p95 was `1.583/6.630 ms`. Non-gated visual fixtures serialize query retirement so terrain-heavy poses cannot starve the five-slot profiler; their timings are labeled and reported, but are not compared to CQ1. A final visual-gap correction adds one explicitly filtered, Cinematic-v2-only cirrus B/A lookup that subtly warps and feathers the procedural field; High and all v1/GLES paths return before that lookup.

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

Pinned CQ2.1 generation hashes:

| Asset | SHA-256 |
|-------|---------|
| Shape | `13966e74ccf9b03bcac896ab0f1869eb0cca3c01813ecfd83566e0571531f906` |
| Detail | `71782f1b10c30b38c1fa7c80da18c01fc73ba12153b1063a494dd9304c786083` |
| Weather | `c58a1549ed26a8da72c519e430b20cc5166b9d0680642cc62ea112ad4583556c` |

The asset tool writes to a temporary sibling file and atomically replaces the destination after validating byte count. It must not leave a partially written asset that passes only an existence check.

## Texture upload and memory

Upload v2 assets as RGBA8 with full mip chains and repeat wrapping. Use trilinear minification and linear magnification. The first CQ2 implementation keeps portable uncompressed GL formats; BC4/BC5 is a later desktop optimization and is not an acceptance dependency.

V2 base-level storage is exactly `13,631,488` bytes (13 MiB). Complete uncompressed mip chains total `16,377,756` bytes (approximately 15.62 MiB): `9,586,980` shape, `1,198,372` detail, and `5,592,404` weather bytes. Initialization diagnostics report actual dimensions and estimated allocated bytes.

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

The selected-LOD view uses `R = normalized shape LOD`, `G = normalized detail LOD`, and `B = max(R,G)`. The asset-profile legend is:

- green: validated v2 bundled profile;
- blue: intentional v1 compatibility policy, including GLES/ANGLE;
- orange: v1 selected after a v2 load or upload failure;
- purple: runtime-generated v1;
- red: procedural shader fallback;
- gray: not initialized.

The log remains authoritative for the exact filename, validation, upload or capability reason behind the profile color.

## Failure handling

- Invalid asset byte count rejects the entire asset; never upload truncated data.
- Failure to load one v2 asset selects a coherent fallback profile rather than mixing unrelated channel versions.
- Texture allocation or mip generation failure releases partial v2 resources before trying v1.
- Runtime generator exceptions are caught and reported; the shader procedural fallback remains available.
- Shader compilation failure in the v2 variant selects v1 for the session and invalidates cloud history.
- Asset/profile transitions invalidate CQ1 color, metadata, moments, and repair history.

## Implementation milestones

- [x] CQ2.0: Freeze v2 filenames, dimensions, channel ABI, seeds, and expected memory.
- [x] CQ2.1: Implement deterministic shape, detail, and weather generation.
- [x] CQ2.2: Update the asset-generation build target and commit generated v2 assets.
- [x] CQ2.3: Add strict v2 loading, coherent v1 fallback, profile diagnostics, and cleanup.
- [x] CQ2.4: Add pixel-angular-size uniform and explicit shape/detail ray-footprint LOD.
- [x] CQ2.5: Consume type, precipitation, and convection independently in density shaping.
- [x] CQ2.6: Add edge-only rotated detail and expanded weather addressing.
- [x] CQ2.7: Add debug views and automated asset/shader tests.
- [x] CQ2.8: Complete fixed-scene visual and GPU performance acceptance.

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

### CQ2.8 acceptance evidence

The acceptance run used the CQ1.9 viewport, warm-up and sample-count protocol on the same RTX 2080 Ti: desktop GL `4.6.0 NVIDIA 610.74`, fixed `6.64 h` sun pose, frozen wind, `1920×1080`, 32 discarded frames and 240 retained samples per case. `Cloud Trace` is the conservative density-stage proxy because density evaluation occurs inside that pass and has no independent GPU scope. The gated High case stays asynchronous and CQ1-comparable. Other visual cases use a GPU completion point per measured frame after the profiler's multi-retirement queue fix; this prevents terrain-heavy frames from filling all five non-blocking slots, and the artifact records `serialized-visual-fixture` so those values cannot be mistaken for the gate.

| Fixture/preset | Trace p50/p95 | CQ1 High trace ratio | Cloud total p50/p95 | Frame total p50/p95 |
|----------------|---------------|----------------------|---------------------|---------------------|
| Dense overcast, High | `0.552/0.565 ms` | `0.729×` | `1.583/6.630 ms` | `6.939/10.818 ms` |
| Dense overcast, Cinematic | `2.748/8.768 ms` | serialized/report only | `13.532/20.590 ms` | `16.280/23.476 ms` |
| Cirrus comparison, High | `0.384/0.391 ms` | serialized/report only | `1.158/6.830 ms` | `2.625/8.762 ms` |
| Cirrus comparison, Cinematic | `2.617/8.731 ms` | serialized/report only | `7.415/11.081 ms` | `9.157/16.163 ms` |

The High gate ceiling is `0.757 × 1.20 = 0.9084 ms`; the measured `0.552 ms` median passes with approximately `39.2%` headroom. The three translation captures have distinct hashes and adjacent mean RGB deltas of `0.00158` and `0.00780`, both inside the bounded continuity check, so the acceptance test rejects a frozen output and a catastrophic mip transition. Generated PNG/JSON/CSV evidence is retained under the opt-in `.artifacts` workflow rather than committed as driver-specific goldens.

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
