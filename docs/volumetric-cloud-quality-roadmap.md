# Volumetric cloud quality roadmap

**Status:** Complete — CQ1–CQ4 accepted
**Created:** 2026-07-20  
**Scope:** Genesis 3D preview volumetric cloud body, reconstruction, density assets, lighting, and the optional desktop sparse-volume backend.
**Implementation handoff:** [Volumetric cloud implementation handoff](volumetric-cloud-implementation-handoff.md)
**Post-CQ4 art direction:** [Volumetric cloud art-direction roadmap](volumetric-cloud-art-direction-roadmap.md)

## Purpose

This roadmap moves the Genesis preview clouds from a compact procedural layer toward a production-style cloud renderer while preserving the Phase 6 correctness work. The work is intentionally split into four sequential contracts:

1. [CQ1 — Precision and reconstruction](volumetric-cloud-cq1-precision-reconstruction.md)
2. [CQ2 — Density textures and weather data](volumetric-cloud-cq2-density-textures.md)
3. [CQ3 — Cloud-light froxel cache](volumetric-cloud-cq3-lighting-cache.md)
4. [CQ3.8 — Continuous-altitude shell stabilization](volumetric-cloud-cq3-lighting-cache.md#cq38-continuous-altitude-stabilization)
5. [CQ3.9 — Flat continuous-world layers](volumetric-cloud-cq3-lighting-cache.md#cq39-flat-continuous-world-layers)
6. [CQ4 — Sparse voxel/SDF backend](volumetric-cloud-cq4-sparse-voxel-sdf.md)

The dependency chain is strict: **CQ1 → CQ2 → CQ3 → CQ3.8 → CQ3.9 → CQ4**. A later phase may be prototyped in isolation, but it must not become the production path until the preceding phase meets its exit criteria.

## Pipeline ownership

Cloud-body rendering and camera fog use different volume representations:

- `genesis_clouds.frag` ray-marches flat, world-altitude cumulus and cirrus layers into a cloud-specific offscreen target.
- The cloud temporal and upsample passes reconstruct that target and composite it against opaque scene depth.
- The existing camera-aligned froxel volume injects and integrates fog and god rays. It consumes resolved cloud transmittance/depth, but it does not represent the detailed cloud body.
- CQ3 introduces a second froxel concept: a **light-aligned cloud-light cache**. It stores cloud lighting information and must not be conflated with the existing camera fog/god-ray froxels.
- CQ4 optionally replaces procedural-layer density queries with sparse cloud density queries. It continues to use CQ1 reconstruction and CQ3 lighting.

Increasing the existing fog-froxel resolution is therefore not a substitute for this roadmap.

## Compatibility policy

- GLES/ANGLE remains a functional compatibility path using the procedural flat-layer renderer, packed RGBA8 metadata, and the current short cloud light march.
- Desktop GL 3.3 may use CQ1 floating-point targets and CQ3 fragment-slice cache generation when required formats are framebuffer-renderable.
- Desktop GL 4.3+ may use compute/image-store cache generation and the CQ4 sparse backend when capability checks pass.
- Add a persisted `Cinematic = 3` volumetric quality value. Existing Low/Medium/High values `0..2` retain their meanings and deserialize unchanged.
- Cinematic on unsupported hardware falls back to the best CQ3 procedural-layer configuration. It must never disable clouds merely because CQ4 is unavailable.
- Every capability-selected path reports its selected cloud backend, render formats, trace scale, lighting-cache mode, and fallback reason through existing preview diagnostics.

## Prerequisites

- [x] Restore a green solution/test build. Verified 2026-07-25 with a successful app solution build and all 475 app tests passing.
- [x] Capture an initial fixed-scene screenshot and GPU timer sample before CQ1 changes existing render formats. The accepted 2026-07-25 user capture is recorded in the implementation handoff.
- [x] Expand the acceptance matrix with Low/Medium/High captures, GL vendor/renderer/context, sun pose, and a controlled warm-up/sample window before CQ1.9 phase acceptance. Completed 2026-07-28 with twelve 1080p cases, 32 warm-up frames, and 240 GPU samples per case on desktop GL 4.6.
- [x] Preserve the Phase 6 depth contracts with continuous camera-altitude traversal, far-distance atmospheric fade, opaque scene ordering, terrain occlusion, and no cloud rendering over nearby subjects. CQ3.9 removes the obsolete preview-planet curvature for the continuous world; CQ3.8's endpoint and live High/Cinematic boundary behavior remains the regression baseline.
- [x] Keep the live hidden-WGL cloud compile/depth-ordering smoke test green before each phase begins. Verified through the CQ1.8 regression correction on 2026-07-27 for packed/direct metadata, linear HDR presentation, R8 STBN upload, the RG16F temporal-moment path, odd-viewport Cinematic/High target resizing, full-resolution FP16/direct-metadata edge-repair compilation/allocation/draw/readback, and opaque scene-depth rejection in the repair pass.

## Phase tracker

| Phase | Deliverable | Depends on | Compatibility fallback | Status | Exit summary |
|------|-------------|------------|------------------------|--------|--------------|
| CQ1 | Linear HDR targets, precise metadata, STBN temporal reconstruction, Cinematic trace/edge repair | Baseline and green build | Current RGBA8 shell target/history | Complete | Accepted 2026-07-28 with stable HDR reconstruction, depth ordering, deterministic STBN, and 1080p timing evidence |
| CQ2 | Versioned shape/detail/weather assets and explicit ray-footprint LOD | CQ1 | Existing v1 128³/32³/256² assets | Complete | Accepted 2026-07-28 with deterministic v2 assets, coherent fallback, explicit LOD, weather/material shaping, fixed-scene visual evidence, and a passing High trace-performance gate |
| CQ3 | Snapped light-aligned cloud-light cascades, long-range shadowing, cloud AO and ground contribution | CQ2 | Current per-sample short light march | Complete | Accepted 2026-07-29 with compute and GL 3.3 generation, transactional terrain/fog transmittance, bounded scheduling, live fallback coverage, 13-case visual evidence, and a passing High lighting gate |
| CQ3.8 | Numerically stable shell traversal and continuous cumulus/cirrus altitude boundaries | CQ3 | Accepted CQ3.7 shell renderer | Complete | Accepted 2026-07-29 with stable shell/density math, live High/Cinematic boundary sweeps, temporal-on/off coverage, preserved terrain/depth behavior, and a passing frozen High trace gate |
| CQ3.9 | Flat world-altitude slabs for an unbounded continuous world | CQ3.8 | CQ3.8 curved shell (historical rollback only) | Complete | Accepted 2026-07-30 with flat XZ-invariant layers, continuous interval-driven marching, 16 boundary sweeps, preserved depth ordering, a 13-case full-HD matrix, and a passing `0.931×` High lighting gate |
| CQ4 | Desktop sparse brick/SDF density backend and deterministic cloud envelope library | CQ3.9 | CQ3 flat procedural-layer renderer | Complete | Accepted 2026-07-30 with fenced eviction/overflow recovery, sparse debug views, bounded counters, SparseBrickGen timings, fault injection, and CQ4.8 fly-through/memory/fallback gates |

## Roadmap milestones

### CQ1 — Precision and reconstruction

- [x] CQ1.0: Capture and record the initial fixed-camera visual/timing baseline.
- [x] CQ1.1: Add persisted/localized `Cinematic = 3`, profile selection, 48-step cloud tracing, and diagnostics.
- [x] CQ1.2: Generalize cloud temporal targets to capability-selected attachment formats.
- [x] CQ1.3: Add direct-distance shader ABI and retain packed compatibility metadata.
- [x] CQ1.4: Keep trace/history radiance linear through full-resolution reconstruction.
- [x] CQ1.5: Add deterministic spatiotemporal blue-noise sampling and march jitter.
- [x] CQ1.6: Add temporal moments, variance clipping, and confidence.
- [x] CQ1.7: Add two-thirds Cinematic trace sizing and invalidation.
- [x] CQ1.8: Add bounded full-resolution edge repair. The 2026-07-28 production check confirmed stable terrain with DDA enabled. The final terrain-pool correction keeps transactional failure handling and the 768 MiB ceiling, grows vertex/index stores independently, and reaches the complete 2,401-chunk 16-ring target at 685 MiB reserved instead of deferring the outer ring.
- [x] CQ1.9: Complete desktop, GLES-source, live-GL, temporal, depth, HDR, artifact, and performance acceptance. The 2026-07-28 user-provided Cinematic capture is the accepted visual screenshot. The repeatable desktop matrix captured twelve 1920×1080 cases with 32 warm-up frames and 240 GPU samples each on an RTX 2080 Ti; High dense-overcast measured `1.453/2.146 ms` cloud p50/p95 and Cinematic measured `5.040/5.612 ms`. App tests pass `528/528`, and the live packed/floating-point shader/MRT smoke passes. Phase 6 did not preserve a controlled 240-frame High median, so the historical `1.15×` ratio is not reconstructible; this accepted High result is the comparison baseline for CQ2.

### CQ2 — Density textures and weather data

- [x] CQ2.0: Freeze and document the v2 asset channel ABI. Completed 2026-07-28 in shared generator/runtime code with fixed filenames, dimensions, four-channel meanings, per-channel seeds, strict byte validation, exact base/mip memory totals, coherent v1 selection, and profile diagnostics. Its initial rollout gate was opened for desktop by detailed CQ2.5 after generation and shader consumption were ready together.
- [x] CQ2.1: Generate deterministic shape-128, detail-64, and weather-1024 assets. Completed 2026-07-28 with fixed-point toroidal value/cellular generation, exact periodic edges, anisotropic curl-warped wispy detail, four independent weather fields, pinned SHA-256 values, repeat-run equality, channel distribution/correlation coverage, atomic validated tool output, complete-set MSBuild outputs, and bundled payload verification. The detailed specification's CQ2.2 build-output milestone is complete; runtime v2 selection remains disabled pending the roadmap's CQ2.2–CQ2.4 integration work.
- [x] CQ2.2: Add strict versioned loading with v1 fallback and diagnostics. Completed 2026-07-28 with exact-length and pinned-SHA validation, all-or-nothing v2/v1 selection, transactional three-texture upload and cleanup, bundled/generated/procedural fallback stages, profile/dimension/byte diagnostics, and asset-version temporal-history invalidation. Detailed CQ2.4 and CQ2.5 subsequently completed its shader LOD/channel prerequisites and opened desktop v2 selection.
- [x] CQ2.3: Add explicit shape/detail mip selection from ray footprint. Completed 2026-07-28 with an active-FOV/trace-height CPU contract; separate trace and full-resolution repair pixel angles; step/distance-derived view footprints; interval-aware light footprints; explicit shape, detail, and loop-time weather `textureLod`; conservative occupancy bias; Cinematic `-0.35` detail bias; LOD-zero debug inspection; and FOV history invalidation.
- [x] CQ2.4: Add High/Cinematic rotated boundary detail and weather-channel shaping. Completed 2026-07-28 across detailed CQ2.5–CQ2.6: desktop v2 independently consumes all weather/material channels, uses a four-times-longer primary weather period with a bounded rotated secondary address, and evaluates the second detail lookup only on High/Cinematic boundaries. V1/GLES remains single-address/single-detail. Full-resolution post-temporal direct-disc extinction also allows dense reconstructed cloud to fully occlude the Sun without altering off-disc cloud opacity.
- [x] CQ2.5: Complete seam, hash, distribution, upload, visual, and performance coverage. Detailed CQ2.7 completed deterministic seam/hash/distribution, atomic failure, transactional cleanup, explicit-LOD, GLES, live mip/upload, shader and debug-view automation. Detailed CQ2.8 then captured thirteen 1080p debug-off cases with 3,120 retained GPU samples on 2026-07-28. The asynchronous CQ1-comparable High dense-overcast trace measured `0.552/0.565 ms` p50/p95, or `0.729×` the accepted CQ1 High median and below the `1.20×` gate. The matrix covers height transitions, material structure, long-horizon tiling, camera translation, cirrus and four weather classes; Cinematic v2 also gains a bounded explicit-LOD cirrus B/A feathering warp without changing High or compatibility paths.

### CQ3 — Cloud-light froxel cache

- [x] CQ3.0: Add cloud-light cache profiles, coordinates, and stable snapped origins. Completed 2026-07-29 with the accepted High/Cinematic `RG16F` dimensions, update cadences, local-tap policy, hysteretic sun-to-world basis, texel/slice-snapped transforms, desktop compute/fragment capability preference, explicit GLES short-march fallback, and diagnostics that keep the unallocated CQ3.0 runtime on the existing short march.
- [x] CQ3.1: Add fragment-slice generation and lookup on desktop GL 3.3. Completed 2026-07-29 with transactional High/Cinematic arrays, ping-pong prefix accumulation, conservative curved-shell/detail-padded bounds, shared overlap/lookup math, fixed-density half-float readback, and a production-backend reference generation that does not yet change cloud pixels.
- [x] CQ3.2: Add compute/image-store generation on capable desktop GL. Completed 2026-07-29 with a bounded 24-slice column prefix, shared fragment/compute density integration, `RG16F` layered image writes, image-access/texture-fetch barriers, GL 4.3 capability enforcement, session-scoped demotion to fragment slices, complete fixed-density near/far parity readback, and a production DDA/terrain transition check.
- [x] CQ3.3: Replace High/Cinematic cloud light marches with cache sampling. Completed 2026-07-29 with committed-transform binding, explicit logical-depth interpolation, continuous near/far overlap, independently valid cascades, and short-march fallback for Low/Medium/GLES, invalid generation, and samples beyond far coverage. The live lookup covers center, overlap, far-only, outside-far, missing-near, and missing-far cases; the production DDA/terrain transition remains green.
- [x] CQ3.4: Add two-octave scattering controls, final sky visibility/ground contribution, and Cinematic local cone taps. Completed 2026-07-29 with the documented `0.50/0.50/0.55` and `0.25/0.25/0.30` octave controls, post-sum energy clamp, two-probe local hemispherical cache visibility, material-derived linear ground bounce limited to lower cumulus, cached cirrus lighting, and exactly two Cinematic boundary samples ending at `0.88` near-cache texel. High performs no local taps.
- [x] CQ3.5: Feed cloud ground transmittance to terrain, fog, and god-ray consumers. Completed 2026-07-29 with a transactional `R16F` far-footprint publication, Beer-Lambert conversion, High far-native and Cinematic near/far-overlap profiles, direct-only terrain and camera-froxel consumers, a two-texel full-sun footprint feather, and full-sun fallback for missing, stale, out-of-range, or non-finite samples. Ambient/IBL, froxel density/occupancy, and view-ray cloud depth remain unchanged. Fixed-density live readback, all `588/588` app tests, and the production DDA/terrain lifecycle pass.
- [x] CQ3.6: Add cache scheduling, invalidation, wind reprojection, scrolling, diagnostics, and GPU timings. Completed 2026-07-29 with independent High `2/4` and Cinematic `1/4` near/far cadence, a four-frame reuse ceiling, immediate material/camera/sun/basis invalidation, cascade-local generation/fallback, wind-reprojected cloud and ground consumers, snapped scroll-overlap planning, generation-age diagnostics, and separate near/far GPU timing scopes. Due cascades retain the specification's valid full-regeneration first implementation; CQ3.7 captured timings subsequently confirmed that physical overlap-copy scrolling is not required for the final performance gate.
- [x] CQ3.7: Complete lighting, shadow, fallback, visual, and performance coverage. Completed 2026-07-29 with a 13-case `1920×1080` matrix and 3,120 retained GPU-query samples. The exact-HG High specialization measured `0.671/0.705 ms` trace p50/p95, with zero scheduled cache-generation cost in the frozen fixture; its `1.215×` CQ2 ratio passes the `1.25×` gate. Moving Cinematic evidence recorded 180 near and 60 far refresh samples, matching the `1/4` cadence. Compute→fragment→short-march failure demotion, real GL 3.3 fragment generation, live shader compilation, and the long-lived DDA/resident-terrain pixel harness all pass. Exact-static generation reuse makes physical overlap-copy scrolling unnecessary for CQ3 acceptance; due moving cascades retain bounded full regeneration.
- [x] CQ3.8a: Replace discrete region telemetry and cancellation-prone shell math with continuous signed-altitude diagnostics, rationalized altitude evaluation, stable quadratic roots, zero-density support guards, ray-distance-anchored primary samples, and path-length-integrated cirrus opacity. Implemented 2026-07-29; focused CPU/source tests and explicitly enabled hidden-WGL generic/High/repair shader compilation pass.
- [x] CQ3.8b: Capture animated High/Cinematic sweeps across cumulus base/top and cirrus base/top with temporal enabled and disabled. Completed 2026-07-29: the hidden-WGL matrix passes all 16 boundary/quality/temporal combinations after render-readiness and history settling, with no isolated frame-delta spike or cloud runtime fault. The frozen 1080p High dense-overcast case retained 240 GPU samples and measured `0.617/0.718 ms` trace p50/p95, below both CQ3.7's `0.671 ms` p50 and the `0.690 ms` gate. Exact intersection math remains in place; repeated density altitude uses a stable third-order expansion, and High/Cinematic use two/four profiled cirrus taps respectively.
- [x] CQ3.9a: Replace spherical intersections, radial density altitude, cache curvature padding, planet occlusion, reconstruction planet masks, and camera-altitude opacity multipliers with a shared flat `worldY - groundY` contract. Implemented 2026-07-30 across trace, repair, density, CQ3 cache generation, CPU reference math, and upsample. User runtime evidence exposed and removed the first implementation's zero-opacity bands at all four physical cloud boundaries.
- [x] CQ3.9b: Accept live High/Cinematic altitude sweeps with temporal enabled/disabled, verify long-distance fade and scene-depth ordering, and rerun the frozen High performance gate. Completed 2026-07-30: the production sweep confirmed removal of zero-opacity bands; the corrected hidden-WGL harness retains valid uniform clear-sky frames and passes all 16 quality/temporal/boundary combinations; density-only metadata and the interval-driven level-view marcher preserve depth ordering; the 13-case `1920×1080` matrix retained 3,120 GPU samples; and frozen High dense-overcast measured `0.514/0.547 ms` trace p50/p95, `0.514 ms` amortized lighting, and `0.931×` CQ2 against the `1.25×` gate.

### CQ4 — Sparse voxel/SDF backend

- [x] CQ4.0: Add sparse-cloud capability/backend selection without changing shell behavior. Completed 2026-07-30: capable Cinematic contexts request sparse density but remain explicitly on the CQ3.9 procedural layer until later milestones publish a complete resource set; unsupported, forced, and faulted paths report a bounded fallback reason.
- [x] CQ4.1: Add the deterministic template ABI, generator, twelve bundled cumulus/stratus envelope assets, strict loader, and tests. Completed 2026-07-30 with a shared `32×24×32 RG8` layout, pinned seeds/hashes, connected envelopes, flat cumulus bases, conservative distance fields, and 589,824 bytes of all-or-nothing bundled data.
- [x] CQ4.2: Add the physical brick atlas, double-buffered page tables, allocator, residency records, and memory accounting. Completed 2026-07-30 with a fixed `160³ RG8` atlas, six `32×16×32 R16UI` tables, 4,095 allocatable bricks plus one cleared fallback slot, active-reference-safe retirement, transactional allocation/rollback, and 9,407,452 bytes of total CQ4 density-residency state under the 16 MiB ceiling. Sampling remains disabled until later publication/generation milestones.
- [x] CQ4.3: Add snapped clipmap origins, request prioritization, bounded updates, and table publication. Completed 2026-07-30 with independently snapped L0/L1/L2 origins, stable camera/frustum/level/distance ordering, a hard 96-page frame cap, overlap reuse, teleport retirement, complete sentinel/mapping staging, non-blocking fence polling, atomic active/build handle swaps, and publication generations. The expanded worst-case CPU control reservation keeps total CQ4 density-residency accounting at 12,684,220 bytes under the 16 MiB ceiling.
- [x] CQ4.4: Add compute brick generation, border filling, conservative distance, barriers, and fences. Completed 2026-07-30 with a bounded 96-record std430 queue, one in-flight batch with controller backpressure, deterministic weather-selected CQ4.1 envelope evaluation, bit-identical one-voxel borders, `RG8` atlas image writes, image/texture/SSBO barriers, per-workgroup completion markers, and non-blocking generation fences. CQ4.5 subsequently upgraded the original zero/one G seed to the exact local Chebyshev field used by traversal. Only fully completed batches replace requested page sentinels with physical mappings. The persistent status SSBO raises exact CQ4 accounting by 384 bytes to 12,684,604 bytes, still under 16 MiB. Sampling remains disabled.
- [x] CQ4.5: Add page-table DDA, conservative-distance skipping, CQ2 detail, and cascade/shell blending. Completed 2026-07-30 with shared active-table GLSL lookup, unmapped/requested coarse fallback, exact within-brick Chebyshev G generation capped at 32 voxels, brick-clipped `0.8×` distance stepping, fine CQ2 evaluation at occupied boundaries, finest-resident selection, 10% L0/L1/L2 and L2/shell transitions, traversal counters, a matching CPU oracle, and fixed-ray hidden-WGL SSBO readback. Runtime binds the published atlas/tables/origins but keeps `uHasSparseCloudTraversal=0` until CQ4.6 connects sparse generation identity to CQ1/CQ3.
- [x] CQ4.6: Feed sparse density and generation identity into CQ3 lighting and CQ1 reconstruction. Completed 2026-07-30 with a deterministic atlas/table/plan/origin sampling identity, fail-closed publication gating, procedural fallback while an active plan is incomplete, and transactional activation only after both CQ3 near/far caches commit the same identity. The active identity participates in CQ1 history rejection; the view trace, Cinematic local cone taps, full-resolution edge repair, CQ3 optical-depth/sky-visibility generation, and derived ground transmittance all consume the same sparse base density. Recenter, publication, generation, backend, and fault discontinuities demote before any mismatched sparse pixel can render. Nine focused identity/activation tests, all 685 app tests, and all three explicitly enabled cloud live-GL tests pass.
- [x] CQ4.7: Add overflow/fault recovery, debug views, counters, and GPU timings. Completed 2026-07-30 with fenced orphan recycle for bricks retired while generating, active-reference-safe retirement after publication drops mappings, bounded overflow that never wraps physical indices, nine sparse debug views (clipmap/page/atlas/density/distance/steps/fallback/template/blend), CPU residency/identity counters, SparseBrickGen pass timing, and injectable dispatch/barrier/fence/status/publication/context-loss demotion.
- [x] CQ4.8: Complete fly-through, residency, visual, memory, fallback, and performance acceptance. Completed 2026-07-30 with always-on CPU memory/overflow/teleport gates under the 16 MiB ceiling, focused CQ4.7 recovery and debug-view coverage, and an opt-in hidden-WGL fly-through harness (`AUTOPBR_RUN_CQ4_ACCEPTANCE=1`) that exercises residency diagnostics, context-loss shell recovery, and High procedural fallback.

## Quality policy

| Preset | Cloud trace | View steps | Reconstruction | Density assets | Lighting | Density backend |
|--------|-------------|------------|----------------|----------------|----------|-----------------|
| Low | 1/2 resolution | 16 | Compatibility history/no moments | v1 allowed | Current short march | Procedural flat layer |
| Medium | 1/2 resolution | 24 | FP16 where supported; temporal history | v2 on desktop | Current short march | Procedural flat layer |
| High | 1/2 resolution | 32 | FP16 + STBN + moments | v2 + rotated boundary detail | CQ3 High cache | Procedural flat layer |
| Cinematic | 2/3 resolution | 48 | FP16 + moments + edge repair | v2 maximum quality | CQ3 Cinematic cache | CQ4 sparse when supported; otherwise procedural flat layer |

Debug march-step override remains authoritative for view-step count. It does not alter target format, trace scale, cache profile, or backend selection.

## Cross-phase invariants

- Opaque scene depth remains authoritative at trace, repair, and full-resolution reconstruction.
- Camera altitude may change the ray/slab interval and sampled density, but must never directly multiply cloud opacity or radiance.
- Camera altitude or `tEnter == 0` must not select a different march integrator; sampling policy is derived continuously from the visible interval length.
- Cloud radiance is premultiplied by cloud opacity at every intermediate stage.
- Temporal history is rejected on invalid metadata, camera cuts, layer changes, wind mismatch, viewport/format changes, backend changes, or material cloud-setting changes.
- Resource creation failure is recoverable. Fall back one feature level, invalidate dependent histories, log once, and keep rendering.
- All world-space origins used by caches or clipmaps are grid-snapped. Camera motion must not make cloud density or lighting swim.
- Low/Medium/High retain their existing stored numeric values. Cinematic is additive.
- Runtime asset generation is a development fallback, not the normal startup path.
- No phase removes the GLSL source path or the GLES/ANGLE shader adaptation contract.

## Performance and evidence policy

- Use existing pass-scoped GPU timers after at least 32 warm-up frames; report median and 95th percentile over at least 240 frames.
- Record 1080p timings for ground, inside-layer, above-layer, grazing-horizon, broken-cumulus, dense-overcast, and cirrus-heavy fixtures.
- Each phase compares against the immediately preceding accepted phase on the same GPU and settings.
- A phase may not hide cost by lowering another preset or silently disabling temporal, lighting, scene-depth clipping, or detailed density.
- Store manual screenshots and timing logs under the existing artifact workflow; generated evidence is not a source-controlled golden unless a test explicitly owns it.

## Acceptance matrix

Every phase must exercise:

- desktop GL 4.6 capability path;
- desktop GL 3.3 fallback where applicable;
- GLES/ANGLE source adaptation and compatibility policy;
- camera below, inside, and above both cloud layers;
- terrain and subject depth intersections;
- horizon and far-distance views;
- wind animation, frozen wind, temporal disabled, and camera cuts;
- Low, Medium, High, and Cinematic selection/fallback;
- allocation, shader compile, and GPU runtime failure recovery.

## References

- Fabian Bauer, *Creating the Atmospheric World of Red Dead Redemption 2: A Complete and Integrated Solution*, SIGGRAPH 2019: <https://advances.realtimerendering.com/s2019/index.htm>
- Andrew Schneider, *Nubis: Authoring Real-Time Volumetric Cloudscapes with the Decima Engine*: <https://advances.realtimerendering.com/s2017/Nubis%20-%20Authoring%20Realtime%20Volumetric%20Cloudscapes%20with%20the%20Decima%20Engine%20-%20Final%20.pdf>
- Guerrilla Games, *Nubis³*: <https://www.guerrilla-games.com/read/nubis-cubed>
- Epic Games, *Volumetric Cloud Component*: <https://dev.epicgames.com/documentation/unreal-engine/volumetric-cloud-component-in-unreal-engine>
- NVIDIA, *Rendering in Real Time with Spatiotemporal Blue Noise Textures*: <https://developer.nvidia.com/blog/rendering-in-real-time-with-spatiotemporal-blue-noise-textures-part-1/>
