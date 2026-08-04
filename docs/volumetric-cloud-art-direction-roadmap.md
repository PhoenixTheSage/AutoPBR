# Volumetric cloud art-direction roadmap

**Status:** In progress — CA2.0–CA2.2 implemented; Cinematic repair overfire corrected; live population acceptance pending  
**Created:** 2026-07-30  
**Scope:** Post-CQ4 cloud character, population, lighting, and reconstruction polish.  
**Technical baseline:** [Volumetric cloud quality roadmap](volumetric-cloud-quality-roadmap.md)  
**Implementation handoff:** [Volumetric cloud implementation handoff](volumetric-cloud-implementation-handoff.md)

## Intent

The CQ1–CQ4 roadmap established the required precision, temporal reconstruction,
density assets, cloud-light cache, continuous-world traversal, and optional sparse
backend. This roadmap changes the artistic distribution of that density without
reopening those architectural contracts.

The target is:

> Solid, slowly changing humid cores surrounded by energetic, wind-sheared
> evaporation, with coherent small and large cumulus populations.

The target must not regress into isotropic grain, disconnected foam, uniformly
fuzzy silhouettes, or temporally boiling noise. Large shapes remain calm and
readable; activity increases toward optically thin cloud boundaries.

The dependency chain is strict: **CA1 → CA2 → CA3**.

## Baseline diagnosis

The accepted CQ4 image is technically stable but visually smooth for three reasons:

1. `volumetric_clouds_density_maps.glsl` collapses the v2 detail texture's billow,
   wispy, and curl channels into one predominantly isotropic erosion value.
2. CQ2 limits erosion to `0.10..0.24` to prevent an earlier disconnected-foam
   regression. That safe limit also suppresses evaporating skirts and broken side
   silhouettes.
3. CQ4's `32×24×32` sparse envelope templates intentionally use connected, smooth
   macro lobes. Fine wisps therefore belong in procedural render-time detail rather
   than the sparse template payload.

CA1 changes the first two factors. CA2 may revise macro population and template
character. CA3 preserves and lights the resulting thin structure.

## Compatibility and ownership

- Low, Medium, GLES/ANGLE, v1 density assets, and CQ3 fallback behavior retain their
  accepted density material in CA1.
- High and Cinematic with v2 assets receive the CA1 boundary material.
- Sparse and procedural cloud bodies use the same CA1 post-base density function.
  Sparse templates remain the macro envelope; CA1 detail remains procedural.
- The CQ3 cloud-light cache evaluates the same CA1 density function as the camera
  trace. Lighting and visible density must not diverge.
- CA1 does not add a new texture or runtime asset. Its second boundary sample
  replaces CQ2's same-scale rotated lookup.
- User-facing controls are deferred until the style is accepted. Initial values
  are an internal quality profile, avoiding new persistence and localization ABI.

## Phase tracker

| Phase | Deliverable | Depends on | Status | Exit summary |
|---|---|---|---|---|
| CA1 | Height-aware, wind-aligned cumulus boundary material with protected cores | CQ4 accepted | Provisional | Automation green; CA1.7 locks High+Cinematic default (no public Wispiness); final visual/performance acceptance remains open |
| CA2 | Mixed small/large cumulus population and asymmetric macro envelopes | CA1 provisional baseline | In progress | Dual-scale placement and asymmetric v2 templates implemented; pending live sparse/procedural convergence acceptance (CA2.5–CA2.6) |
| CA3 | Lighting and temporal preservation for thin cloud structure | CA2 provisional baseline | In progress | CA3.0–CA3.3 implementation landed; visual/occlusion/performance acceptance remains open |

## CA1 — Boundary material

### Goals

- Distinguish humid core, growing upper billows, evaporating sides, and lower
  trailing skirts.
- Use directional shear so wisps align with atmospheric flow instead of texture
  axes.
- Increase boundary breakup without hollowing dense interiors.
- Use the existing v2 detail ABI:
  - R: broad billow.
  - G: fine billow.
  - B: wispy/sheared erosion.
  - A: curl/domain distortion.
- Keep trace, full-resolution repair, sparse density, and cloud-light cache density
  materially identical.

### Non-goals

- No new sparse template version.
- No satellite-cell weather population.
- No new public slider.
- No temporal or lighting retune unless needed to correct a CA1 regression.
- No changes to cirrus; its fibrous model remains independently controlled.

### Current implemented profile

The first runtime captures showed that calibration 1 affected the edge but read as
fine stipple around an unchanged smooth envelope. Its six-to-seven-world-unit
cross-flow period was too small at normal viewing distance, while the maximum dry
erosion was strong enough to form granular holes. Calibration 1 is rejected.

Calibration 2 uses:

- A normalized atmospheric flow basis with a deterministic fallback direction.
- Height shear of `(h - 0.42) × detailScale × 0.52`.
- Cross-flow curl distortion of `detailScale × 0.12`.
- A directional boundary scale of `0.82 × detailScale` on High and
  `0.68 × detailScale` on Cinematic.
- Coordinate frequency ratios of `0.18 : 0.72 : 0.70` for along-flow, vertical,
  and cross-flow axes. This makes structures longer along the wind.
- Explicit footprint LOD based on the shortest anisotropic repeat, with `+0.20`
  stabilization bias to prevent distant stipple.
- A widened CA1 material band using base density `0.12..0.70`.
- Existing primary B plus directional secondary B for side/lower wisps.
- Existing and contrast-shaped secondary R/G for upper billows.
- Core erosion reduced from `0.10` to `0.055`.
- Maximum dry lower-edge erosion of `0.28` on High and `0.34` on Cinematic,
  modulated by weather density potential.
- Maximum upper-edge erosion of `0.25` on High and `0.30` on Cinematic,
  modulated by convection.

These values remain a visual calibration, not an accepted art lock.

### Milestones

- [x] CA1.0: Record the accepted CQ4 visual diagnosis and compatibility contract.
- [x] CA1.1: Implement directional, height-aware v2 boundary material in the shared
  density function.
- [x] CA1.2: Route the same material through camera trace, Cinematic edge repair,
  sparse sampling, and CQ3 light-cache generation; add source-contract tests and
  first-use diagnostics.
- [x] CA1.3: Compile generic, High-specialized, repair, fragment-cache, and
  compute-cache shaders on live desktop GL.
- [ ] CA1.4: Capture fixed-camera before/after images for small cumulus, large
  cumulus, broken overcast, level-view horizon, camera-inside-cloud, and dense
  sun-occluding cloud.
  - Partial 2026-07-30 runtime evidence rejected calibration 1: it showed edge
    stipple, smooth slab silhouettes, and rounded isolated bodies. Calibration 2
    is awaiting the same views.
- [ ] CA1.5: Run moving wind and altitude sweeps with temporal enabled and disabled.
- [ ] CA1.6: Tune the internal profile and accept GPU cost and visual guardrails.
- [x] CA1.7: Lock CA1 as the High and Cinematic default. Do not expose a public
  `Wispiness` control unless CA1.4–CA1.6 acceptance later proves user-selectable
  character is required; Low/Medium/GLES retain the non-directional material.

### Failure handling

- Missing v2 detail assets retain the existing no-detail behavior.
- V1 and GLES follow their established material without the directional lookup.
- Shader compilation failure follows existing cloud-tier fallback and diagnostics.
- If CA1 causes foam islands, first lower lower-edge strength or increase core
  protection; do not blur the final reconstruction.
- If CA1 creates temporal boiling, first reduce cross-flow frequency or constrain
  low-alpha history in CA3; do not restore global isotropic smoothing.
- If light-cache silhouettes disagree with the trace, treat it as a blocking
  density-identity defect.

### Test and acceptance matrix

| Case | Required observation |
|---|---|
| Small humilis | Coherent body, uneven scalloped crown, limited dry fringe |
| Large mediocris/congestus | Stable dense core, billowy upper growth, directional sides |
| Broken overcast | Negative space without repeated foam islands |
| Dense overcast | No pinholes through full optical depth; Sun remains occluded |
| Level horizon | No cutoff, depth fighting, or boundary material discontinuity |
| Camera below/inside/above | Continuous density with no altitude-triggered style switch |
| Wind animation | Wisps advect coherently without axis locking or orientation pops |
| Sparse convergence | Procedural fallback and resident sparse envelopes share character |
| Temporal off/on | No isolated flicker spike, ghost fringe, or erased thin boundary |
| Terrain/subjects | Existing scene-depth ordering remains intact |

Performance gate: High cloud trace p50 must remain at or below `1.10×` the accepted
CQ4 High fixture. Cinematic may use the finer boundary period but must not add a
third detail lookup or increase march steps. Cache-generation timing is recorded
separately because CA1 changes its sampled density without changing dimensions or
cadence.

CA1 exits when the live shader matrix is green, all visual cases are accepted, the
performance gate passes, and the chosen default/exposure policy is recorded.

## CA2 — Mixed cumulus population

### Goals

- Produce intentionally mixed small and large cumulus rather than scaling one
  continuous mass.
- Add a broad weather system mask plus a finer satellite-cell selector.
- Gate small cells near parent-system boundaries or overlapping moisture so they
  read as meteorological growth rather than detached procedural noise.
- Introduce asymmetric macro silhouettes: leaning towers, uneven skirts, broad
  notches, and varied lobe placement.

### Architecture

- Preserve the v2 weather-map channel ABI. Derive population selectors from
  decorrelated, explicitly filtered world addresses unless visual evidence requires
  a versioned weather asset.
- Broad coverage establishes the parent system.
- Cloud type and convection select humilis, mediocris, congestus, or stratus
  families.
- A higher-frequency moisture selector creates attached satellite cells.
- If sparse templates require revision, add a v2 template filename/ABI. Never
  reinterpret bundled v1 bytes.
- Sparse page tables, brick layout, distance semantics, and residency budgets remain
  unchanged.

### CA2.0 population identity

- Parent-cell span is `max(1.10 × volumeSize, 160)` world units. For the current
  `175` feature/volume scale this is `192.5`.
- Satellite-cell span is `0.38 × parent span`, or `73.15` at the current scale.
- Both populations use stable signed world-cell hashes. Centers receive up to
  `±0.18` cell jitter per horizontal axis.
- Parent horizontal scale is randomized in `0.72..1.16`; satellite scale is
  `0.58..0.92`. Parent vertical scale is `0.72..1.08`; satellite vertical scale is
  `0.42..0.72`.
- Sparse envelopes rotate independently through `0..2π`. Parent aspect varies
  `0.76..1.32`, satellite aspect varies `0.68..1.24`, and a deterministic
  height-squared lean displaces upper growth by `0.045..0.145` parent-cell units
  or `0.035..0.085` satellite-cell units.
- Parent admission rises from `0.24` in dry regions toward `0.82`, with a restrained
  stratus fill contribution and a hard `0.90` cap.
- Satellite admission is biased toward moist cumulus and convection, capped at
  `0.78`. Sparse satellites select humilis/mediocris by default; strongly moist,
  convective cells may promote to congestus but never stratus.
- Satellites are multiplied by broad parent support before a probabilistic soft
  union. They cannot become a free-standing global foam population.
- The procedural shell uses a four-corner dual-scale value-field approximation to
  keep ray-march cost bounded. Sparse brick generation evaluates neighboring
  jittered envelopes. Both share spans, salts, weather probabilities, and the
  soft-union rule; exact sparse/procedural visual convergence remains a CA2.5 gate.
- Bundled `32×24×32 RG8` v1 envelope bytes, hashes, and the CQ4 atlas/page-table/
  brick/residency ABI are unchanged. CA2.3 confirmed placement/runtime
  deformation alone cannot provide the required asymmetry, so CA2.4 added a
  parallel `cq4-envelope-v2` template set (same dims, twelve assets, new seeds,
  pinned hashes) that bakes offset mass, an angular skirt notch, height-lean lobe
  centers, and an asymmetric cumulus base ellipse directly into the density. The
  loader prefers a complete, valid v2 set and falls back transactionally to v1 on
  any missing/corrupt asset; versions are never mixed.
- The no-weather-texture failure path returns a conservative coherent vertical
  layer. It does not run stochastic admission, so a missing asset cannot erase the
  entire cloud body.

### Milestones

- [x] CA2.0: Freeze fixed-camera population references and quantitative scale ratios.
- [x] CA2.1: Prototype dual-frequency procedural weather population.
- [x] CA2.2: Add attached satellite-cell gating and distribution tests.
- [x] CA2.3: Decide whether sparse template v2 is necessary. **Yes** — live
  Cinematic sparse captures after the calibration-2 rotation/aspect/lean/apron
  deformation still read as smooth/blob v1 envelopes; only baked macro asymmetry
  in the template density resolves it.
- [x] CA2.4: Generated twelve deterministic asymmetric v2 envelopes with pinned
  hashes, transactional loader fallback, and connectivity checks. See summary
  below; v1 bytes/hashes and the CQ4 atlas/page-table/brick/residency ABI are
  unchanged.
- [ ] CA2.5: Validate sparse/procedural convergence, fly-through continuity, seams,
  and memory bounds.
  - Partial 2026-08-01: cold Cinematic no longer activates CQ4.6 at the first
    `resident-96` entering batch. Activation requires ≥480 published residents at
    ≥90% requested coverage with no in-flight generation, and CQ3 light-cache
    generation stays on the High-equivalent procedural density until that gate
    opens. This removes the first-compile cubish patchwork; full sparse↔procedural
    population convergence remains open.
  - Partial 2026-08-01: fast-motion hitch amortisation — camera-centered pending
    rebuild, teleport/origin entering caps (12/24), soft-hold of the active sparse
    identity across same-origin residency growth, and single-cascade Cinematic
    large-camera light-cache invalidation.
- [ ] CA2.6: Capture population visual matrix and accept performance.

Failure handling retains the CA1 renderer and v1 sparse templates. A partial or
invalid v2 asset set must fail transactionally and select the prior coherent set.

Performance gate: High density-stage p50 must remain at or below `1.15×` accepted
CA1. Sparse memory must remain within CQ4's accepted bound unless a separately
documented budget change is approved.

CA2 exits when small/large populations remain coherent across translation and wind,
sparse convergence is visually continuous, deterministic assets pass, and no
satellite-cell foam regression is present.

## CA3 — Lighting and reconstruction preservation

### Goals

- Preserve CA1/CA2 low-alpha wisps through temporal reconstruction and edge repair.
- Improve separation between bright thin edges, shaded humid cores, and cloud bases.
- Keep full-density Sun occlusion, terrain shadows, fog, and god rays physically
  consistent with the changed cloud density.

### Architecture

- Add alpha-reactive history confidence only where representative distance and
  opacity identify changing thin cloud boundaries.
- Preserve a minimum stable traced feature width; represent finer material through
  filtered opacity rather than unstable binary density.
- Tune variance clipping and repair classification locally. Do not globally sharpen
  cloud color or reduce temporal accumulation.
- Tune cloud-light cache controls after density acceptance:
  - restrained ambient/sky visibility inside thick cores;
  - localized silver lining on optically thin boundaries;
  - darker bases through cached optical depth, not an image-space gradient;
  - unchanged direct-disc extinction and ground-transmittance contracts.

### Milestones

- [x] CA3.0: Capture CA2 temporal rejection and thin-feature loss diagnostics.
  First-use cloud profile reports
  `thinFeaturePreservation=ca3.1-low-alpha(...);ca3.2-repair(...);ca3.3-shading(...)`.
  Diagnosed loss modes: (1) low-alpha wisps over-retained by temporal history →
  ghost/boil; (2) CA1/CA2 material opacity range over-firing CQ1.8 repair →
  noisy retrace; (3) flat ambient/powder whitening thin edges into cartoon cores.
- [x] CA3.1: Add reactive low-alpha history weighting with deterministic tests.
  Thinness `smoothstep(0.28, 0.55)`, disagreement `0.06..0.28`, mix toward
  weight `0.58` while moving (agreeing soft edges keep more history); idle
  views raise the floor to `0.86` via eased `uTemporalStability` so soft edges
  can denoise without flashing borders on pan. Dense cores retain the prior
  weight path.
- [x] CA3.2: Retune full-resolution edge-repair classification for thin wisps.
  Raised the alpha threshold from `0.08` to `0.24` and gated alpha-only repair
  behind a silhouette tap (`alphaMin ≤ 0.18`) or a strong opacity jump (`> 0.36`).
  Idle/high-confidence views freeze the post-temporal 8-step STBN
  retrace (`idleFreeze>=0.85` via eased `uRepairStability`, with latch
  hysteresis and a `0.20..0.85` retrace ramp) so Cinematic soft edges keep
  CA3.1 history without idle shimmer or motion border flashes.
- [x] CA3.3: Calibrate cloud-light contrast, silver lining, sky AO, and ground bounce.
  Defaults: cached-sky floor `0.14` (was `0.18`), ground bounce `0.13` (was `0.11`),
  local-cone optical-depth scale `0.38` (was `0.45`), powder `mix(0.70, 0.88)`
  (was `0.72..0.90`), higher-order sky-visibility mixes `0.22/0.10` (was `0.28/0.14`).
- [ ] CA3.4: Validate Sun occlusion, terrain shadows, fog/god-ray coupling, and all
  cache fallbacks.
- [ ] CA3.5: Run static/moving visual and performance acceptance.
- [ ] CA3.6: Expose one public style control only if the accepted references require
  user-selectable character.

Failure handling retains accepted CA2 history and lighting settings independently.
Invalid cache generations continue through the accepted short-march fallback.

Performance gate: High total cloud p50 must remain at or below `1.10×` accepted CA2.
Cinematic edge repair must remain bounded to classified pixels and no new
full-resolution volume pass may be introduced.

CA3 exits when wisps survive motion without boiling or ghosting, dense cores retain
stable optical depth, lighting reads at both small and large scales, and the full
CQ4 fallback/depth/altitude matrix remains green.

## Handoff checklist

Before another model continues:

1. Read this document and the current checkpoint in
   `volumetric-cloud-implementation-handoff.md`.
2. Inspect `git status --short`; the CQ4/AO working tree may contain intentionally
   uncommitted changes and must not be discarded.
3. Run `PreviewCloudDensityProfileTests`, shader source/adaptation tests, and the
   enabled hidden-WGL live cloud smoke before visual tuning.
4. Do not change sparse template bytes during CA1.
5. Record every tuned constant here, including rejected values and why they failed.
6. Update milestone checkboxes only after automation or user-provided live evidence.
7. Treat terrain depth, level-view traversal, altitude continuity, DDA, and direct
   Sun occlusion as non-negotiable regressions.

## Current implementation touchpoints

- `src/AutoPBR.App/Rendering/Shaders/common/volumetric_clouds_density_maps.glsl`
- `src/AutoPBR.App/Rendering/Shaders/genesis_clouds.frag`
- `src/AutoPBR.App/Rendering/Shaders/genesis_clouds_repair.frag`
- `src/AutoPBR.App/Rendering/Shaders/common/cloud_light_cache_generation.glsl`
- `src/AutoPBR.App/Rendering/OpenGL/OpenGlPreviewBackend.VolumetricClouds.cs`
- `tests/AutoPBR.App.Tests/PreviewCloudDensityProfileTests.cs`

## Validation log

### 2026-07-30 — CA1.0 through CA1.3

- Release source-contract/adaptation suite: `83/83` passed.
- Enabled hidden-WGL flat-cloud shader matrix: `1/1` passed. It compiles generic
  trace, High-specialized trace, full-resolution repair, fragment-slice cloud-light
  generation, compute cloud-light generation, packed/direct metadata variants, and
  sparse traversal variants.
- Full Release app suite: `692/694` passed. The two failures are existing
  source-string assertions in `PreviewRenderingTests` for scene-capture/TAA logic
  changed by the parallel screen-space AO/CQ4 working tree. CA1 does not modify
  `GlSceneCaptureTarget` or those tests. Reconcile that independent pair before
  claiming a completely green repository.
- Debug output validation was blocked by the running preview/debugger holding
  `AutoPBR.Preview.dll` and its PDB. Release output was used instead; no process was
  stopped and no live session was disturbed.

### 2026-07-30 — CA1.4 partial capture and calibration 2

- User-provided below/level/above captures confirm that calibration 1 executes and
  preserves terrain, altitude continuity, and high frame rate.
- Rejected visual traits: small isotropic edge stipple, smooth continuous slab
  envelopes, and a rounded isolated cloud with detail concentrated mainly at its
  lower rim.
- Calibration 2 broadens directional structure by roughly three to four times
  cross-flow and more along-flow, uses explicit anisotropic LOD, widens the
  material band, reduces granular maximum erosion, and increases upper-billow
  shaping.
- Calibration 2 focused source/adaptation tests pass `83/83`; the enabled
  hidden-WGL generic/High/repair/fragment-cache/compute-cache matrix passes.
- This remains CA1 boundary calibration. Breaking the continuous system into a
  deliberate mix of small and large cloud populations remains CA2.

### 2026-07-30 — CA2.0 through CA2.2

- User-provided CA1 calibration-2 captures keep altitude continuity and performance
  healthy but show the remaining macro defects clearly: large smooth slabs,
  rectangular walls, oversized rounded lobes, repeated scale, and little attached
  satellite cumulus. These captures are the CA2.0 reference set.
- Added `common/cloud_population.glsl` and a matching CPU reference contract.
  Placement is deterministic in global world space, including negative cells, so
  brick borders and clipmap levels do not derive local random identities.
- The v2 procedural shell now applies a bounded-cost parent/satellite population
  mask before coverage erosion. GLES/ANGLE and v1 density semantics are unchanged.
- Sparse brick generation now evaluates a `3×3` parent neighborhood and a `3×3`
  satellite neighborhood, applies weather-family selection and per-cell horizontal
  and vertical scale, then joins candidates with a soft union. Satellites require
  parent support.
- CQ4 atlas dimensions, bordered brick layout, page tables, conservative-distance
  channel, residency cap, and all twelve v1 envelope payloads remain unchanged.
- Diagnostics report
  `cloudPopulation=ca2-dual-scale-asymmetric-v2-templates` after CA2.4.
- Focused CPU/source tests pass `92/92`. The enabled hidden-WGL generic/High cloud
  shader matrix and conservative bordered sparse-brick generation both pass
  (`2/2`).
- Full Release app coverage passes `696/698`. The same two pre-existing
  `PreviewRenderingTests` scene-capture/TAA source-string assertions documented
  under CA1 remain the only failures; CA2 does not touch their source area.
- CA2.3 was deliberately left open at this log entry. Later same-day captures
  confirmed persistent blob envelopes after calibration-2 deformation, so CA2.3
  resolved **yes** and CA2.4 landed asymmetric template v2 (see the CA2.3/CA2.4
  validation entry below).

### 2026-07-31 — level-view correction and CA2 calibration 2

- New High/Cinematic level-view captures show a vertical cloud termination and
  long horizontal sample bands. This is a march-budget regression, not the removed
  three-height state machine: the long-ray ramp ended at `interval / steps`, so its
  average step was too short and a continuously occupied ray could spend all
  density samples before reaching the far slab.
- Long rays now retain the accepted near step but solve the far endpoint as
  `2 × interval / steps - baseStep`. The arithmetic ramp therefore spans the
  complete interval in exactly the preset sample count. Sampling is bounded by
  total march cells rather than occupied-density hits, preventing CA2 coverage
  changes from moving the cutoff distance.
- Cinematic sparse envelopes now rotate, stretch asymmetrically, lean with height,
  and fade through a `0.075` parent / `0.10` satellite storage-domain apron.
  Template density can no longer expose an axis-aligned vertical face at a v1
  volume boundary.
- The procedural population's residual non-parent coverage was reduced from
  `0.28` to `0.055`; populated regions rise to `1.22`. This is intended to separate
  parent systems and reveal attached satellite cumulus instead of reconnecting
  everything into one white slab.
- Focused source/CPU coverage passes `99/99`. Enabled hidden-WGL generic/High
  compilation, bordered sparse generation, and no-skip sparse traversal pass
  `3/3`.
- Full Release app coverage passes `697/699`; the only failures remain the two
  unrelated scene-capture/TAA source-string assertions already tracked in this
  handoff.
- Visual acceptance remains open. If calibration 2 still reads as smooth template
  blobs after its domain wall is removed, CA2.3 resolves **yes** and CA2.4 creates
  a transactional asymmetric template-v2 set rather than increasing detail-noise
  contrast.

### 2026-07-31 — Cinematic edge-repair overfire

- User capture with Froxel Quality = Cinematic showed blocky/noisy cloud edges
  while High and below remained usable. Overlay evidence: Cloud Repair ~2.4 ms at
  `862×683`, Cloud Trace ~2.7 ms, CQ3 near allocated at Cinematic dimensions.
- Root cause: CA1/CA2 boundary opacity variation across the 2/3-resolution repair
  footprint exceeded the CQ1.8 `0.08` alpha classifier on occupied interiors.
  The eight-step retrace replaced temporal reconstruction and made Cinematic look
  worse than High (which has no repair stage).
- Classifier/shader contract now uses alpha threshold `0.24`, requires a silhouette
  tap (`alphaMin ≤ 0.18`) or strong jump (`> 0.36`) for alpha-only repair, and
  keeps distance/kind/validity/weight structural triggers unchanged.
- Recorded as an early CA3.2 correction under the active CA2 checkpoint because
  Cinematic was a functional regression. Re-capture Cinematic vs High at the same
  camera/settings before accepting CA2 visuals.

### 2026-07-31 — Cinematic sparse cell-shade outlines

- User Cinematic capture with `active=sparse-voxel` showed cartoon ink/cell
  borders on cloud lobes. High (procedural, no local cones) did not.
- Three cooperating causes:
  1. CQ4 v1 envelopes publish a hard `smoothstep(0.12, 0.62)` isosurface.
  2. Cinematic local cone taps used a wide `0.18..0.62` boundary gate, so the
     cone stayed open across that thick shell and darkened every lobe rim.
  3. Incomplete L0 residency hard-switched to L1 at brick faces; LINEAR atlas
     sampling also needed an explicit physical-brick clamp to avoid packed-atlas
     bleed.
- Fixes: widen generation shaping to `0.06..0.80`, thin/sparse-scale the cone
  gate, fade fine contribution over ~1.25 voxels when a neighbor page is
  missing/requested, and clamp atlas UVs inside each `10³` physical brick.
- Already-resident bricks keep the old shaping until regenerated (toggle
  quality or fly far enough to retire/rebuild). Cone and face-fade apply
  immediately.

### 2026-07-31 — CA2.3/CA2.4 asymmetric sparse template v2

- CA2.3 decision: **yes**, template v2 is necessary. Live Cinematic sparse
  captures after CA2 calibration-2 (rotation/aspect/lean/apron deformation)
  still read as smooth/blob v1 envelopes; asymmetry has to be baked into the
  density itself.
- CA2.4 added `PreviewSparseCloudTemplateAssetContractV2` alongside the frozen
  `PreviewSparseCloudTemplateAssetContract` (v1). Same `32×24×32` RG8 layout, 4
  families × 3 variants (12 assets), `AssetVersion = 2`,
  `GenerationAbi = "cq4-envelope-v2"`, filenames
  `cloud_envelope_{family}_{variant}_32x24x32_rg8_v2.bin`, and new seed bands
  (humilis `51011/17/23`, mediocris `52013/19/43`, congestus `53003/19/37`,
  stratus `54017/21/27`) so v2 never reuses a v1 seed.
- Generator morphology bakes visible macro asymmetry: an offset primary-mass
  center, height-dependent lean applied directly to upper lobe centers, an
  angular skirt notch removing a wedge of the outer envelope (with a protected
  core so connectivity survives), and an asymmetric base ellipse for cumulus
  families. Largest-connected-component extraction and exact Chebyshev
  distance in `G` are preserved unchanged from v1.
- `PreviewSparseCloudTemplateAssetGenerator` now dispatches on
  `descriptor.Version` for both generation and payload validation; v1 and v2
  share the byte-length/dimension checks but validate against their own
  contract's pinned hash table.
- `AutoPBR.Tools.GeneratePreviewCloudAssets` writes both v1 and v2 sets; all 12
  v2 `.bin` files were generated, hashed, and pinned into
  `PreviewSparseCloudTemplateAssetContractV2`. `AutoPBR.App.csproj` bundles all
  24 template files (12 v1 + 12 v2).
- `PreviewSparseCloudTemplateAssetLoader` is transactional: it first attempts a
  complete, hash-valid v2 set and only falls back to a complete v1 set if any
  v2 asset is missing or fails validation. Versions are never mixed within a
  loaded set. `GlSparseCloudBrickGenerator.TryCreate` accepts either an
  all-v1 or all-v2 template set (same dims/count/byte-length either way).
- New tests cover v2 contract freezing, hash pinning, byte-identical
  regeneration, connectivity, flat cumulus base, measurable center-of-mass
  offset (asymmetry), presence of a missing skirt sector (notch), transactional
  loader fallback, and template-set acceptance for both versions. All existing
  v1 tests remain green.
- No changes to v1 bytes/hashes, CQ4 atlas size, page tables, brick layout, or
  residency budgets. Runtime UV-space deformation (calibration-2) is untouched;
  v2 asymmetry is additive at the source-density level.

### 2026-07-31 — CA3.0–CA3.3 thin-feature preservation and lighting contrast

- CA3.0 captured the thin-feature loss modes that followed CA1/CA2: temporal
  over-retention of low-alpha wisps, CQ1.8 repair overfire on occupied material
  variation, and flat powder/ambient whitening. First-use diagnostics now report
  `thinFeaturePreservation=ca3.1-low-alpha(...);ca3.2-repair(...);ca3.3-shading(...)`.
- CA3.1 adds reactive low-alpha history weighting in
  `genesis_clouds_temporal.frag` with a matching CPU oracle
  (`PreviewCloudTemporalLowAlphaWeight`). Thin/changing wisps mix history toward
  `0.58` under motion (agreeing soft edges keep history); standing-still /
  high-confidence views raise the floor to `0.86` (eased `uTemporalStability`)
  so soft edges can accumulate history without pan bordering. Dense cores keep
  the prior weight path.
- CA3.2 remains the repair classifier contract: alpha threshold `0.24`,
  silhouette `≤ 0.18`, strong jump `> 0.36`, plus idle retrace freeze with a
  motion ramp (`idleFreeze>=0.85`, `retraceRamp=0.20..0.85`, hysteresis) so
  post-temporal STBN repair cannot undo soft-edge denoise or flash borders.
- CA3.3 retunes `PreviewCloudLightingShadingProfiles.Default` to sky floor
  `0.14`, ground bounce `0.13`, local-cone OD `0.38`, powder `0.70..0.88`, and
  higher-order sky-visibility mixes `0.22/0.10`.
- CA1.7 locks CA1 as the High+Cinematic default without a public `Wispiness`
  control. Population diagnostics now report
  `cloudPopulation=ca2-dual-scale-asymmetric-v2-templates`.
- CA2.5/CA2.6 and CA3.4–CA3.6 remain open pending live visual/occlusion/
  performance acceptance.
