# Volumetric cloud implementation handoff

**Status:** Active  
**Last updated:** 2026-07-30
**Branch/base:** `main` at `434a3627` before the CQ3.8 working-tree changes
**Roadmap:** [Volumetric cloud quality roadmap](volumetric-cloud-quality-roadmap.md)  
**Active specification:** [CQ4 sparse voxel/SDF backend](volumetric-cloud-cq4-sparse-voxel-sdf.md)

## Current checkpoint

CQ1 and CQ2 are complete and accepted as of 2026-07-28. CQ2.0 froze the v2 density-asset ABI, CQ2.1 implemented deterministic generators, detailed CQ2.2 bundled the pinned payloads, CQ2.3 added strict transactional profile selection, CQ2.4 added explicit ray-footprint LOD, CQ2.5 implemented versioned weather/material shaping, CQ2.6 added expanded weather addressing plus edge-only rotated detail, CQ2.7 completed debug inspection and automated asset/shader coverage, and CQ2.8 completed fixed-scene visual and GPU performance acceptance. Validated desktop contexts select v2; GLES/ANGLE and any v2 load/upload failure retain one coherent v1 profile. CQ3.0 through CQ3.9 are complete and accepted as of 2026-07-30: the light-cache ABI/generation/consumption path includes controlled multiple scattering, local hemispherical sky visibility, restrained terrain-material ground bounce, cached cirrus/cumulus lighting, Cinematic two-tap boundary refinement, transactional terrain/fog transmittance, bounded scheduling, exact-static reuse, flat continuous-world layers, and camera-region-independent level-view marching. Live GL 3.3, compute/fragment/short-march failure paths, DDA/resident terrain, opaque depth ordering, and all 16 High/Cinematic temporal boundary combinations remain green. Low/Medium/GLES and invalid or out-of-range cloud-body cache samples retain the accepted short march; ground-transmittance consumers use full sunlight when their publication is unavailable or invalid.

Commit `434a3627` contains the accepted CQ1–CQ3.7 implementation. The current working tree contains the accepted CQ3.8 continuous-altitude stabilization and CQ3.9 flat continuous-world conversion. CQ4.0 capability/backend selection is the next roadmap task; the CQ3.9 procedural flat-layer path is its mandatory High and unsupported-Cinematic fallback.

## Completed

### P5.4 DDA initialization correction — 2026-07-29

- The post-CQ2 failure reproduced in the isolated hidden-WGL P2.3 acceleration smoke as a timeout waiting for `GlTerrainOccluderAtlas`.
- CPU baking and the RG32F upload were completing. The validity predicate nevertheless required `_filledVersion >= 0`, while `_filledVersion` comes from `HashCode.Combine` and may legally be negative. A different per-process hash seed therefore made an otherwise valid uploaded atlas rebake forever, explaining why the problem appeared after unrelated rebuilds.
- Atlas residency now has an explicit `_hasResidentData` flag; signed hash values remain valid identity values and no longer double as sentinels.
- The worker remains single-flight until it publishes or reports a caught failure. The prior elapsed-time-only retry could launch duplicate full-atlas generations under startup load and has been removed.
- Bake generation IDs prevent stale publication, the worker uses a dedicated long-running task, and CPU exceptions plus RG32F allocation/upload errors are retained and emitted once from the GL thread.
- Focused residency tests pass, and the isolated hidden-WGL P2.3 smoke now initializes the atlas and passes its DDA compaction assertion.

### CQ3.0 — cloud-light cache ABI

- Added the accepted `RG16F` High and Cinematic near/far profiles, spans, update cadences, `0.20` overlap, and Cinematic two-tap local-light policy. Low and Medium remain disabled for the cache.
- Added a sun-to-world orthonormal light basis with reference-axis hysteresis and prior-basis sign stabilization.
- Added independently texel-snapped light-plane centers, slice-snapped depth minima, world/cache round trips, and bounds tests.
- Capability selection prefers compute plus image load/store, falls back to desktop GL 3.3 fragment slices, and selects the existing short march on GLES/ANGLE or disabled presets.
- First-use diagnostics report the profile, format, dimensions, cadence, preferred generator, active runtime, allocation state, and separation from camera fog froxels.
- CQ3.0 allocates no cache texture and changes no cloud shading. Its active runtime is intentionally reported as `short-march`; CQ3.1 is the first resource/generation milestone.

### CQ3.1 — fragment reference cache

- `GlCloudLightFroxelCache` owns transactional High/Cinematic near/far `RG16F` texture arrays and two prefix scratch textures per cascade. CQ3.2 upgraded the internal prefix scratch to `RG32F` so both generators preserve a full-precision recurrence and round only when publishing each `RG16F` layer. Allocation, clear, per-layer framebuffer completeness, generation, and teardown are independent of camera fog/god-ray froxels.
- `GlCloudLightFragmentSliceGenerator` processes slices in sun-to-world order and alternates scratch textures, avoiding framebuffer feedback. It injects accepted CQ2 density, retains thin cirrus through slice-overlap integration, and stores cumulative optical depth plus bounded reference sky visibility.
- Conservative depth bounds include the full configured cumulus/cirrus envelope, one CQ2 detail period at both ends, projected light-space footprint, and a snapping guard. CQ3.9 removed the former footprint-dependent curvature drop.
- CPU and GLSL near/far selection use the outer 20% of the near cascade; GLSL lookup explicitly interpolates adjacent array layers.
- Hidden-WGL fixed-density readback verifies analytic cumulative optical depth, monotonic half-float slices, bounded visibility, and the interpolated center lookup. The production `862×683` harness reports the reference ready while terrain and P5.4 DDA remain intact.
- Cache allocation/generation failure is optional and diagnostic. Low/Medium/GLES allocate nothing, and every CQ3.1 path still reports `activeRuntime=short-march`.

### CQ3.2 — compute/image-store generation

- Added a desktop GL 4.3 compute generator that writes the existing near/far `RG16F` arrays directly. No second texture layout, coordinate ABI, density units, or lookup representation was introduced.
- Each `4×4×24` workgroup covers sixteen light-plane columns. Its Z lanes cooperatively perform an ordered, compile-time-bounded inclusive prefix through at most 24 logical slices and store cumulative sun optical depth plus bounded sky visibility.
- Fragment and compute generation share `common/cloud_light_cache_generation.glsl`, including the CQ2 conservative/full density path, cirrus overlap, extinction coefficients, deterministic fixture, and visibility recurrence.
- Fragment ping-pong prefix scratch uses `RG32F`; the published cache stays `RG16F`. This removes repeated half-float drift and makes the storage boundary identical to compute image publication.
- Image-store dispatches publish through image-access and texture-fetch barriers. Image binding, dispatch, barrier, compilation, or validation failure disables compute for the backend session and retries fragment slices; if fragment generation is also unavailable, the accepted short march remains active.
- Capability selection now explicitly requires desktop GL 4.3 or newer in addition to compute and image load/store. GL 3.3–4.2 stays on fragment generation and GLES/ANGLE allocates no cache.
- Hidden-WGL readback compares every fixed-density near/far texel and layer between compute and fragment output. The `862×683` production harness selects compute generation while keeping DDA, complete terrain residency, and `activeRuntime=short-march` intact.

### CQ3.3 — production cache consumption

- High and Cinematic cloud tracing bind generated near/far cache arrays plus their committed light basis, snapped plane centers, world spans, light-depth intervals, logical depths, and overlap. Low, Medium, and GLES/ANGLE never enable those samplers.
- The shared GLSL resolver uses explicit adjacent-slice interpolation and the accepted outer-20% near/far blend. A valid far cascade can cover a missing or out-of-range near cascade, while near data remains usable if far generation fails.
- Missing generation and points beyond far coverage return an explicit no-cache result and execute the existing short light march. Generator failure therefore remains local and cannot erase or falsely illuminate cloud samples.
- Cached cumulative optical depth now drives direct sun scattering, and cached sky visibility drives the existing bounded ambient term. CQ3.4 owns final scattering controls, ground contribution, and the two Cinematic cone taps.
- Diagnostics keep generator selection separate from runtime consumption: initialization reports `activeRuntime=short-march`; successful publication changes it to `cache-sampling`.
- CQ3.6 replaced CQ3.3's correctness-first regeneration bridge with the accepted independent cadence, invalidation, wind-reprojection, and bounded-reuse lifecycle.
- Live lookup readback covers center, overlap, far-only, outside-far, missing-near, and missing-far behavior. The production DDA/terrain harness remains green with cache sampling active.

### CQ3.4 — controlled cloud lighting

- Added an internal shading profile with the accepted two higher-order scattering tuples: `(0.50, 0.50, 0.55)` and `(0.25, 0.25, 0.30)`. Cached sky visibility attenuates each higher order, and total scattered energy is clamped once after summation.
- Cache G now represents local hemispherical sky visibility from two coarse upward CQ2 density probes, local density, and cirrus overlap. It is layer-local rather than a second cumulative optical-depth curve; cache R remains cumulative.
- Both cumulus and cirrus use cached long-range optical depth and sky visibility. Cirrus deliberately excludes ground bounce and local boundary taps.
- Cinematic performs exactly two CQ2 explicit-LOD boundary density samples at `0.42` and `0.88` near-cache texel. Their optical depth refines only direct sun response. High uses the cache with zero local taps.
- Ground bounce derives a low-frequency linear color from at most 4,096 active top-material albedo texels. Fixed `0.11` energy is multiplied by upward-hemisphere weight, raw cached sky visibility, and a lower-cumulus profile fading to zero at `h=0.67`.
- The live GL suite preserves compute/fragment agreement with the new channel semantics, while the production backend reports the CQ3.4 lighting model and retains DDA/terrain correctness.

### CQ3.5 — published ground transmittance

- Added quality-owned `R16F` ground-transmittance profiles over the far cache footprint. High publishes the native `128×128` far cascade; Cinematic publishes `192×192` with near/far overlap. Unsupported presets allocate nothing and preserve full sunlight.
- Added a ping-pong publication target. The inactive texture receives a complete draw before its committed transform, source generation IDs, and texture handle become consumer-visible. Consumers reject stale cache generations.
- The publisher reconstructs each ground-facing sun ray from the snapped far light-plane transform, resolves cumulative cache optical depth, and emits Beer-Lambert transmittance. Missing, degenerate, out-of-range, stale, and non-finite inputs return full sunlight.
- Terrain applies the field only to ground-pass direct sun. Camera-froxel fragment and compute injection apply it only to direct in-scatter, which feeds volumetric fog and froxel god rays. Terrain ambient/IBL, non-ground subjects, view-ray cloud depth/transmittance, froxel density, and occupancy are unchanged.
- The common lookup feathers the outer two footprint texels to full sunlight and guards UV, sampled values, and the final fog direct-light gate against NaN/Infinity.
- Allocation, publication, and diagnostic readback preserve the caller's framebuffer state. Publication also restores viewport, shader program, VAO, active texture, touched array bindings, write masks, and raster state.
- The sky-only CQ3.5 regression was a sampler ABI collision, not invalid cache data: the ground `sampler2D` initially used unit 8, which was already the albedo `sampler2DArray` unit. OpenGL rejected the streamed-terrain draws because active samplers of different types aliased one image unit. The ground field now uses dedicated unit 12; material arrays remain on 8–11.
- Fixed-density hidden-WGL publication readback matches analytic Beer-Lambert transmittance, remains finite, and verifies GL-state restoration. The explicitly enabled production `862×683` DDA lifecycle passes with both consumers enabled, including full residency, post-residency recentering, and visible terrain.

### CQ3.6 — scheduled cache lifecycle

- Added a pure lifecycle scheduler for the accepted cadence: Cinematic near every frame, High near every second frame, and both profiles' far cascade every fourth frame. Missing or four-frame-old data is forced due.
- Initial generation, cloud material/profile changes, movement beyond half the near span, a greater-than-`0.5°` one-frame sun change, and light reference-axis changes invalidate and request both cascades immediately.
- Near/far generation now runs as separate transactions. A failed compute update demotes the session to fragment slices; a failed cascade is invalidated without discarding a valid peer, and the existing short march covers missing or out-of-range samples.
- Each cascade commits its own last-generation frame, wind offset, snapped transform, and generation ID. Cloud lookup, ground publication, terrain, and camera-froxel consumers all sample through the same wrapped wind-reprojected transform.
- The two samplers continue to share one light basis. Single-cascade refreshes retain the committed paired basis; a new basis becomes visible only during a paired refresh.
- Snapped scroll plans report whole-texel XY movement, slice movement, overlap fraction, and reuse eligibility. Between refreshes the prior cache is reused and reprojected. Due cascades use full regeneration, which the specification permits as the first implementation. CQ3.7 measurement accepted this path without physical overlap copies because exact-static reuse and bounded moving refreshes pass the High performance gate.
- Diagnostics report requested cascades, invalidation reason, cadence, age, selected generator, compute demotion/fallback, generation IDs, scroll delta/reusable fraction, and ground publication state. Separate `Cloud Light Near` and `Cloud Light Far` GPU timing scopes feed the existing 240-frame cloud timing window.
- Scheduler/reprojection/scroll/timing tests and all `596/596` app tests pass. The explicitly enabled hidden-WGL shader smoke and production `862×683` DDA/terrain lifecycle both remain green.

### CQ3.7 — final lighting/cache acceptance

- Added a 13-case `1920×1080` acceptance harness with 32 warm-ups and 240 retained samples per fixture. It writes PNG captures plus JSON/CSV metadata and timings for dense, deep, broken, sun-transition, height-transition, overlap, cirrus, and moving-shadow scenarios.
- Added exact-static transactional cascade reuse. Frozen fixtures advance generation state without rewriting unchanged textures, while changing transforms/materials retain bounded scheduled regeneration and all existing invalidation rules.
- Added a desktop High trace specialization (`GENESIS_CLOUD_QUALITY=2`). Compile-time folding removes inactive Cinematic/compatibility branches that reduced High shader occupancy. Exact first-order HG, CQ2 density, 32 steps, half-resolution tracing, exact cache depth interpolation, and reconstruction remain unchanged; generic compilation is the safe fallback.
- The final High dense-overcast result is `0.671/0.705 ms` trace p50/p95, `0.889/2.001 ms` cloud-total p50/p95, and `1.215×` the accepted CQ2 baseline against the `1.25×` gate. Three independent confirmation runs remain between `0.671` and `0.674 ms`.
- The moving Cinematic fixture records 180 near and 60 far refresh samples, proving the required `1/4` cadence. Since frozen High has zero generation cost and passes, physical overlap copies are deferred; due moving cascades keep permitted full regeneration.
- Live tests cover compute→fragment→short-march demotion, stale ground-publication rejection, real GL 3.3 fragment generation, generic and specialized shader compilation, acceleration-lane pixel parity, and the long-lived resident-terrain/DDA lifecycle.

### CQ3.8 — continuous-altitude stabilization

- Removes the diagnostic-only Below/Inside/Above camera state; no renderer path depended on it.
- Replaces cancellation-prone radius subtraction and quadratic roots with stable signed-altitude shell math shared by CPU references, view trace, repair, and CQ2/CQ3 density queries.
- Adds zero-density cumulus/cirrus support guards and ray-distance-anchored primary sampling so a shell entry crossing cannot relocate the complete sample pattern at visible density.
- Replaces the cirrus minimum-one slant clamp with smooth vertical profiling and actual path-length optical depth in trace and repair.
- Passes a live render-readiness-aware boundary matrix covering High/Cinematic, temporal enabled/disabled, and cumulus/cirrus base/top crossings. The frozen 1080p High dense-overcast rerun retained 240 samples at `0.617/0.718 ms` trace p50/p95, below the `0.690 ms` gate.
- Keeps exact rationalized altitude for intersections and diagnostics. The repeated density hot path uses a stable third-order radius expansion, while High retains two profiled cirrus taps and Cinematic uses four.
- Keeps failure telemetry through continuous radial altitude and signed cumulus/cirrus boundary distances.
- CPU/source tests and explicit hidden-WGL shader compilation pass. Do not start CQ4.0 until animated High/Cinematic sweeps accept all four boundaries with temporal on/off.

### CQ3.9 — flat continuous-world layers

- Replaces the preview planet's spherical cloud intersections with horizontal world-altitude slabs. Cumulus and cirrus now remain at exactly the same `worldY - groundY` altitude across arbitrarily large XZ positions.
- Uses a 4,096-unit bounded trace interval and fades only slab entries in its final 20 percent. This keeps far-distance cost finite without a spherical horizon, visible deck curvature, or a hard terminal cutoff.
- The first flat-layer build applied a symmetric camera-boundary opacity crossfade in trace, repair, and final reconstruction. User screenshots on 2026-07-30 showed three repeatable sky-color extinction bands while ascending: the factor reached zero at cumulus base/top and cirrus base/top, recreating the visual signature of the removed camera-region transitions. The helper, trace/repair multipliers, reconstruction multiplier, and reconstruction layer-height uniforms are now removed. Camera altitude may alter slab intersections and sampled density but cannot directly attenuate cloud opacity or radiance.
- Mid-layer level views then showed a horizontal cut-through of the nearest bank and a later range cliff when no near cloud was present: coverage/steps inherited the floor/ceiling exit, soft near weather exhausted the density budget inside the near span, and clear slabs published that exit as nearest depth. The handoff correction correctly made metadata density-only, counted only real density samples, and added near plus full-interval coverage probes; retain all three. Its `tEnter <= 0.001` inside-slab branch, however, switched to a different step lattice at the boundary and made mid-layer views snap away from the otherwise seamless altitude sweep. The current reconciliation removes that classification: short intervals use their actual length, every long/grazing interval uses the same bounded near span, and step growth is based on local distance through the interval. Equal intervals therefore render with the same policy below, inside, or above the layer while the depth-fighting fix remains intact.
- Keeps a restrained daytime diffuse-sky floor for dense camera-inside paths so removing the curvature does not turn long horizontal optical paths into unlit black.
- Removes radial density altitude from CQ2 shape/detail evaluation, short light marches, and CQ3 light-cache generation. The legacy center/radius uniforms remain temporarily for compatibility ABI only; their sum reconstructs the flat ground datum.
- Removes cache curvature padding, solid-planet cloud rejection, and the full-resolution upsample planet mask. Opaque scene depth remains authoritative in trace, repair, and per-tap reconstruction.
- Replaces the CPU curved-shell reference with `PreviewCloudLayerGeometry`, covering large-XZ altitude invariance, centimeter-scale crossings, horizontal inside/outside rays, distance fade, and scene occlusion.
- `AUTOPBR_RUN_CQ39_ACCEPTANCE=1` runs the High/Cinematic, temporal-on/off boundary matrix. CQ4.0 remains gated on that live matrix plus the frozen High performance rerun.

### CQ2.0 — density asset ABI

- Added `PreviewCloudDensityAssetContract` as the shared generator/loader/test authority for asset version `2` and generation ABI `cq2-density-v2`.
- Frozen assets are `cloud_noise_shape_128_v2.bin` (`128³ RGBA8`), `cloud_noise_detail_64_v2.bin` (`64³ RGBA8`), and `cloud_weather_1024_v2.bin` (`1024² RGBA8`).
- Shape channels are coherent body, broad billow, medium breakup, and fine erosion. Detail channels are broad billow, fine billow, wispy erosion, and curl distortion. Weather channels are coverage, cloud type, density potential, and convection.
- Each channel has a committed integer seed. Descriptor construction enforces one contract for each RGBA channel, and payload validation rejects any byte-count mismatch before upload.
- Exact storage is `13,631,488` base-level bytes and `16,377,756` bytes with complete mip chains.
- The baked loader validates a complete v2 set. CQ2.5 now provides its matching shader channel semantics and permits production selection on desktop; GLES/ANGLE remains v1.
- The current three v1 blobs are now selected atomically. If any packaged v1 blob is absent or invalid, the renderer generates a complete v1 set rather than mixing packaged and generated channel profiles.
- First-use diagnostics include the selected density profile and all three dimensions. Focused CQ2/CQ1 asset tests pass `10/10` in Release.

### CQ2.1 — deterministic v2 generation

- Added `PreviewCloudDensityAssetGenerator` in the shared GPU-assets project. It uses only fixed-point integer field evaluation, with no machine-dependent random source, floating-point noise, locale behavior, scheduling reductions, or shared writes.
- Shape R combines a coherent five-octave body with a broad cellular envelope. G/B/A use decorrelated broad, medium, and fine cellular lobes without independently thresholding the body.
- Detail R/G provide decorrelated broad/fine billow fields. B uses periodic integer coordinate transforms plus curl-derived domain warp to form anisotropic wispy erosion. A stores the signed curl/distortion scalar around neutral midpoint.
- Weather uses periodic domain warp and distinct coverage, type, precipitation/density-potential, and convection fields. Integer coordinate transforms preserve exact toroidal seams while decorrelating weather systems.
- Opposing X/Y/Z volume faces and X/Y weather edges are byte-identical. Every channel spans at least 96 byte values and a 96-value range, and sampled pairwise absolute correlation remains below `0.97`.
- Pinned hashes are shape `13966e74ccf9b03bcac896ab0f1869eb0cca3c01813ecfd83566e0571531f906`, detail `71782f1b10c30b38c1fa7c80da18c01fc73ba12153b1063a494dd9304c786083`, and weather `c58a1549ed26a8da72c519e430b20cc5166b9d0680642cc62ea112ad4583556c`.
- The asset tool supports `--v2-only`, validates exact descriptor byte counts, logs dimensions/bytes/SHA-256, writes temporary sibling files, verifies their lengths, and atomically replaces final outputs.
- CQ2.1 initially left generated blobs unbundled and disabled to avoid a partial rollout. CQ2.2 subsequently bundled them, CQ2.3 made selection transactional, and CQ2.5 enabled the completed desktop profile.

### CQ2.2 — complete build outputs and bundled assets

- Added the pinned `cloud_noise_shape_128_v2.bin`, `cloud_noise_detail_64_v2.bin`, and `cloud_weather_1024_v2.bin` payloads under `src/AutoPBR.App/Assets/Preview`. Their exact sizes are 8,388,608, 1,048,576, and 4,194,304 bytes.
- `AutoPBR.App.csproj` now tracks shared generator sources, tool sources, and both project files as inputs to `GeneratePreviewCloudAssets`.
- The target declares all seven cloud blobs as outputs: the three legacy density assets, STBN, and all three CQ2 v2 assets. Standard MSBuild incremental execution skips a current complete set and regenerates every output if any member is missing or older than a generator input.
- The generator still writes each file through a validated temporary sibling and atomic replacement. A target run cannot expose a partially written final blob.
- Automated source coverage asserts the complete output list and rejects restoration of the old two-file existence condition.
- Automated repository coverage reads every bundled v2 file, compares it byte-for-byte with fresh generation, validates dimensions and pinned SHA-256, and verifies that the assembly-bound asset loader resolves a coherent v2 set when `allowV2: true`.
- Runtime initialization remained `allowV2: false` at this checkpoint. CQ2.3 subsequently enforced strict hashes, cleanup, coherent fallback, and history diagnostics; CQ2.5 then opened desktop v2 selection.

### CQ2.3 — strict selection and transactional upload

- `PreviewCloudBakedAssetLoader` validates each v2 member against both its exact descriptor byte count and pinned SHA-256. One invalid member rejects the complete v2 profile; v1 also requires all three exact-size members before selection.
- The loader reports asset-specific failure reasons and selects only coherent profiles. Tests inject a corrupt v2 detail blob and verify fallback to one complete v1 shape/detail/weather set without cross-version mixing.
- Shape, detail, and weather texture candidates are uploaded as one transaction. Each allocation/upload/mipmap stage is checked for GL errors; prior live textures are swapped out only after all three candidates succeed.
- Failed candidates are disposed safely, including during context teardown. V2 upload failure retries bundled v1; bundled failure retries a complete runtime-generated v1 set; complete texture failure retains the shader procedural fallback with one diagnostic.
- The selected profile diagnostic records name, fallback path, dimensions, and base-level bytes. Density asset version participates in the cloud-history settings key, and a committed version transition invalidates CQ1 temporal state.
- The rollout gate remained closed at this checkpoint. CQ2.5 now opens it only for desktop contexts after the main v2 shader semantics landed; GLES/ANGLE remains unchanged.

### CQ2.4 — explicit ray-footprint LOD

- Added `PreviewCloudRayFootprint` as the CPU reference for `2 × tan(verticalFov / 2) / targetHeight` and for the matching bounded mip equation. Fixed-value and monotonic-distance/step tests cover the policy.
- `GlRenderFrame` records the active vertical FOV. The primary cloud trace publishes pixel angle using the resolved half-resolution or two-thirds target; Cinematic edge repair publishes the smaller full-resolution pixel angle.
- View marching computes `max(fineStep, sampleDistance × pixelAngularSize)`. Conservative occupancy uses the corresponding coarse-step footprint. The short sun march combines the originating view footprint with each light interval, and its far tap uses an appropriately coarse footprint.
- Shape and detail volumes derive mip levels from their actual `textureSize` and world repeat period. Cinematic detail receives the specified `-0.35` bias; other presets use zero bias.
- Weather reads reached from dynamic marches also use explicit `textureLod`, avoiding an undefined derivative path left adjacent to shape/detail. Conservative occupancy may add one mip; debug inspection passes a zero footprint and remains at LOD zero.
- The full-resolution repair retrace uses the same density/light contract. Camera-FOV changes now alter the cloud-history settings key so a footprint discontinuity cannot reuse stale CQ1 history.
- Desktop live-WGL compilation and GLES source-adaptation tests pass. CQ2.5 subsequently supplied the independent B/A semantics and opened the validated desktop v2 asset gate.

### CQ2.5 — versioned weather/material semantics

- Added `uDensityAssetVersion` to the primary cloud trace and Cinematic full-resolution repair ABI, including cached uniform-location resolution, draw binding, and first-use diagnostics.
- V1 explicitly replaces its legacy weather B/A placeholders with neutral density potential and zero convection before evaluation. Its fixed shape/detail weighting and height behavior remain selected by the version branch.
- V2 independently maps weather R to coverage placement, G to type/vertical profile, B to density and light extinction potential, and A to convective lift, upper development, narrowing, and lobe weighting.
- V2 detail R/G form broad/fine billow erosion and B forms lower/evaporating wisps. Boundary erosion remains bounded and cannot replace the coherent body.
- View and short-light paths reuse one explicitly filtered weather lookup per sample. The density-potential scale is shared by view extinction and sun optical depth, so dark bases track the same material field.
- The desktop v2 gate is open only after the matching shader profile is compiled; GLES/ANGLE deliberately requests v1. Existing loader validation and transactional upload still fall back to a complete bundled or generated v1 profile.
- Focused profile/LOD/GLES source tests pass, and the hidden native-WGL shader compile plus v2 repair draw/readback test passes.

### CQ2.6 — expanded addressing, rotated boundary detail, and Sun-disc extinction

- V2 increases the primary weather world period from the legacy `4 × feature scale` to `16 × feature scale`; v1 returns before the new path and remains byte/channel compatible.
- V2 adds a toroidal integer transform equivalent to a `26.6°` rotation with `√5` frequency scaling (`1/√5` effective period), plus a fixed offset. Primary convection selects a blend of `0.08..0.22`, below the specification's `0.25` ceiling.
- Both weather addresses derive from the same advected primary coordinate, so the two systems share one world-space wind translation and cannot temporally slide. CPU wind and temporal-delta wrapping now use the `16×` primary period, an integer multiple of the v1 weather/detail periods.
- Low/Medium retain one detail lookup. V2 High/Cinematic add a second `33°`-rotated, fixed-offset detail lookup only when `edgeWeight > 0.001`, using detail A as a small curl distortion and fixed `0.35/0.50` blends.
- The second detail sample remains explicit-LOD, is not evaluated in dense interiors, and is absent from coherent v1/GLES fallback semantics. Primary and Cinematic repair pass their resolved quality into the same density function.
- Dense clouds now extinguish the projected visible Sun disc in the full-resolution upsample after temporal reconstruction. A fifth-power direct-beam response preserves a softened disc through thin cloud, while the `0.45..0.60` dense seal reaches full opacity; the surrounding aureole and all off-disc opacity remain unchanged. Sun visibility/strength gates prevent a circular opacity artifact at night or when the disc is disabled.
- Placing direct-disc extinction after cloud history prevents a moving Sun from leaving temporal alpha trails. Cloud debug views bypass it.
- Native WGL compilation and floating-point readback prove a reconstructed `0.60` cloud opacity becomes at least `0.995` over the disc core and remains `0.58..0.62` off-disc.

### CQ2.7 — debug inspection and automated coverage

- Preserved the existing persisted debug values `0/1/2` and appended fourteen inspectors: Weather G/B/A, Shape RGBA, Detail RGBA, normalized selected LOD, base density and asset profile/fallback.
- Raw texture-channel views use explicit LOD zero. The selected-LOD view reuses production march-step, pixel-angular-size, texture-dimension and Cinematic `-0.35` detail-bias policy, encoding normalized shape/detail LOD in red/green.
- Asset-profile colors distinguish validated v2, intentional v1 compatibility, v1 failure fallback, generated v1 and procedural fallback. The exact profile and loader/upload reason is emitted whenever a debug view is selected.
- Debug modes now unconditionally bypass cloud temporal history, Cinematic full-resolution edge repair, display encoding/direct-disc extinction and procedural cirrus, while the trace and upsample retain authoritative scene-depth, planet and horizon rejection.
- Moved cloud-asset atomic replacement into a shared tested writer. An injected commit failure proves the prior valid file remains byte-identical and temporary files are removed.
- Added source-policy coverage for all debug modes, settings clamps, profile codes, fallback diagnostics and partial texture cleanup. Existing deterministic hash, distribution, seam, profile-fallback and explicit-LOD tests remain green.
- Extended the hidden native-WGL smoke with complete 3D shape/detail and 2D weather mip-chain/filter/wrap validation; the desktop shader and all existing depth, HDR, moments, repair and Sun-disc readbacks pass.

### CQ2.8 — fixed-scene visual and performance acceptance

- Added an opt-in hidden-WGL CQ2 acceptance harness using the CQ1.9 `1920×1080`, 32-warm-up, 240-sample protocol.
- Captures thirteen production/debug-off fixtures: High and Cinematic dense overcast, below/inside/above cumulus, upper-billow/lower-wisp structure, long horizon, three adjacent camera-translation poses, High/Cinematic cirrus, and sparse broken weather. Fair, broken, congested and overcast weather classes are all represented.
- Artifacts include one PNG per fixture, a JSON report with GL identity, settings, timing summaries, SHA-256/luminance statistics and adjacent-translation deltas, plus a flat CSV timing summary.
- The final RTX 2080 Ti run retained 3,120 GPU-query samples. The gated High dense-overcast case preserves asynchronous CQ1-comparable scheduling: trace p50/p95 is `0.552/0.565 ms`, `0.729×` the accepted CQ1 High trace median and safely below the `0.9084 ms` gate. High cloud-total p50/p95 is `1.583/6.630 ms`.
- Cinematic and other visual-fixture timings are reported with serialized query retirement and are not part of the High comparison. Dense-overcast cloud-total p50/p95 is `13.532/20.590 ms`; the final Cinematic cirrus comparison is `7.415/11.081 ms`.
- The acceptance work also fixed a timing-infrastructure defect: when multiple frames retired during one availability poll, `GlGpuTimerProfiler` overwrote up to four results. It now queues every completed snapshot. A live WGL regression submits three frames before polling and requires all three results.
- Visual review found the High/Cinematic cirrus fixtures shared essentially the same procedural field. Cinematic v2 now applies one explicitly filtered detail lookup: B supplies anisotropic wispy feathering and A supplies a subtle rotated curl warp. High, v1 and GLES return before this work.
- The live matrix passes with the exact validated `cq2-v2/v2-bundled/cq2-density-v2;upload-valid` diagnostic, `densitySemantics=v2`, debug view off, stable terrain/depth ordering, distinct/bounded translation captures, and no shader/session/render-state failure.

### CQ1.0 accepted reference capture

The user supplied the initial visual/timing reference on 2026-07-25. The conversation image is not a repository file, so reproduce it from this recorded state when generating controlled artifacts:

| Field | Captured value |
|-------|----------------|
| Display viewport | `862×683 px` (`575×455 @ 1.5×`) |
| Camera eye | `(8.13, 15.99, -6.86)` |
| Camera target | `(9.26, 16.27, -1.63)` |
| Volumetric preset | Cinematic |
| Density / coverage | `0.75` / `1.63` |
| Layer height / thickness | `4.8` / `60` |
| Feature scale | `178` |
| Wind speed / heading | `1.5` / `35°` |
| Cirrus strength | `0.13` |
| Displayed frame rate | `357 FPS` |
| Displayed GPU total | `1.1 ms` |
| GPU scene / cloud trace / temporal / TAA | `0.7 / 0.3 / 0.0 / 0.1 ms` |
| Displayed CPU total / scene | `2.1 / 1.8 ms` |
| CPU terrain stream / draw | `0.1 / 1.7 ms` |
| CPU cloud temporal / upsample / TAA | `0.0 / 0.0 / 0.1 ms` |

The view exercises a near-horizon forest/terrain depth intersection with a dense cumulus bank and cirrus streaks. It is accepted as the CQ1.0 initial baseline. Before CQ1.9 phase acceptance, add controlled Low/Medium/High captures with GL vendor/context, sun pose, warm-up count, and 240-frame timing windows.

### Prerequisite gate

- `dotnet build AutoPBR.App.slnf --no-restore` succeeds.
- The five pre-existing failing source-contract assertions were refreshed to match already-landed HDR/TAA/shadow implementation names and signatures. No HDR, TAA, or shadow runtime behavior was changed.
- `dotnet test tests\AutoPBR.App.Tests\AutoPBR.App.Tests.csproj --no-restore` passes all 475 tests.
- The opt-in hidden-WGL cloud shader/MRT/depth-ordering smoke passes with `AUTOPBR_RUN_LIVE_GL_SMOKE=1`.

### CQ1.1

- Added stable volumetric quality constants `Low = 0`, `Medium = 1`, `High = 2`, and `Cinematic = 3`.
- Existing persisted values `0..2` retain their meanings. Load, save, view-model-to-render settings, and out-of-range clamping now accept `0..3`.
- Cinematic deliberately reuses High fog/god-ray froxel dimensions, history weights, and preview-TAA profile. It raises only `CloudQuality` to `3`; CQ3 may add a dedicated lighting-cache profile later.
- The cloud shader uses 48 default view steps for quality `3`; the existing user override remains authoritative and clamped to the established 64-step maximum.
- Added the Cinematic UI option and resource keys in English, Arabic, German, Spanish, French, Hindi, Japanese, Portuguese, Russian, and Simplified Chinese.
- First cloud activation diagnostics now report the named volumetric preset and effective cloud quality.
- Added unit/source-contract coverage for profile values, clamping, names, persistence/UI/diagnostics wiring, and the 48-step shader branch.

### CQ1.2 — render-format profiles

- Added an internal cloud render-format profile shared by trace, temporal resolve/history, upsample, and downstream fog/god-ray consumption.
- Low and GLES/ANGLE retain `RGBA8` color plus packed `RGBA8` distance/type metadata.
- Medium, High, and Cinematic select desktop `RGBA16F` working color plus `RG32F` direct distance/type metadata when desktop GL capabilities permit it.
- `GlCloudTemporalRenderTarget` now allocates, clears, copies, filters, and validates both attachment layouts. Copies across different profiles are rejected.
- Direct metadata clears to distance `0`, kind `-1`; packed metadata clears to transparent zero. Both metadata textures use nearest sampling.
- A quality change that changes the target profile recreates the target set and invalidates cloud history.
- Every floating-point allocation is framebuffer-completeness checked. A failure logs once, permanently selects the packed target for that context session, invalidates history, and retries without disabling clouds.
- Capability and first-cloud diagnostics report preferred support, active profile, and fallback.
- Temporal moments remain intentionally unallocated until CQ1.6.

### CQ1.3 — direct metadata ABI

- Added one runtime metadata ABI switch to the trace, temporal, upsample, and fog/god-ray volume-integration shaders; shader recompilation is not required after an allocation fallback.
- The direct contract stores representative ray distance in R and cloud kind/validity in G. Negative G is invalid; `0`, `0.5`, and `1` retain clear-shell, cumulus-density, and cirrus identities.
- The packed contract remains byte-for-byte compatible: perspective-packed distance in RG, kind in B, and validity in A.
- Temporal reprojection uses ABI-aware validity, distance, and kind rejection. Scene/planet depth reconstruction and shared cloud transmittance use the same helpers.
- Hidden WGL coverage now allocates and copies both MRT profiles and proves front/behind opaque-depth ordering for both packed and direct metadata.

### CQ1.4 — linear radiance and final encoding

- The user reported the CQ1.3 build running well on 2026-07-25; this is recorded as the runtime stability gate before changing cloud color handling.
- Cloud trace now writes raw nonnegative, scene-referred, premultiplied linear radiance. It no longer applies sky exposure, soft-knee shaping, HDR selection, or sRGB conversion.
- Cloud temporal history and four-tap depth-aware reconstruction therefore filter linear radiance on both the floating-point and compatibility target profiles.
- A shared final-cloud helper applies exposure and the established `0.08` soft knee only during destination composition. SDR then receives one sRGB encode; HDR retains the established linear display-target contribution.
- Final encoding converts premultiplied radiance to straight radiance, shapes/encodes it, and re-premultiplies by reconstructed opacity. This preserves correct `ONE, ONE_MINUS_SRC_ALPHA` edge blending.
- Cloud debug views bypass final encoding and retain their diagnostic colors.
- The depth-aware upsample and the simpler god-ray-composite fallback use the same final encoding helper. The shared god-ray program explicitly disables cloud presentation when compositing shafts.
- Atmosphere sky exposure no longer invalidates cloud history because it is a final-composition control. Density/lighting settings still participate in the history key.
- First-use diagnostics now identify `linear-trace-history/final-composite-encode`.
- Source-contract tests prohibit trace/temporal presentation encoding. The live WGL test proves a `2.5` radiance value survives floating-point history copy and is shaped differently by the final HDR and SDR paths.

### CQ1.5 — deterministic spatiotemporal blue noise

- Added the versioned `cloud_stbn_128x128x64_r8_v1.bin` asset and its deterministic offline generator. The integer high-pass/rank construction is toroidal in XY and time, produces exactly 64 occurrences of every R8 value per frame, and is fixed by SHA-256 `38af39ee46763013169a3ab5bcdb1da67acf8e1ff8166074e93fec39da5d81f3`.
- The asset-generation tool and app build target now produce the STBN volume when it is missing. Runtime generation is not used for this optional sampling asset.
- Loading rejects the wrong byte length or generation hash. A missing, unreadable, corrupt, or failed-upload asset retains the original deterministic eight-frame jitter and reports the fallback reason.
- Desktop uploads use a nearest-filtered, repeat-wrapped `128×128×64` R8 3D texture. High and Cinematic enable it; Low, Medium, and every GLES/ANGLE path retain the lightweight sequence.
- Trace pixels index XY from `floor(gl_FragCoord.xy) mod 128` and Z from the cloud sampling frame modulo 64. The rank is remapped to the center of its R8 interval before placing the first fine march step.
- History invalidation no longer rewinds the sampling frame. The explicit cloud-temporal-disable control freezes the frame for deterministic inspection; freeze-wind continues to freeze only density advection.
- The STBN generation version participates in the history settings key. First-use diagnostics report asset/fallback state and whether the current preset activates STBN.
- The cloud fragment now samples the shared sky-view LUT through a compact local sampler and imports the segment integration helpers directly. This avoids compiling unrelated sun-disc, moon, and star routines and keeps the hidden NVIDIA/WGL source compile green after the shader key changes.
- CQ1.5 adds no temporal moments, confidence, trace-scale, or edge-repair behavior.

### CQ1.6 — temporal moments, variance clipping, and confidence

- Extended the desktop cloud render-format contract with an optional third `RG16F` attachment. High and Cinematic request `RGBA16F + RG32F + RG16F`; Medium retains the two-attachment floating-point profile; Low and GLES/ANGLE retain the packed two-attachment profile.
- Capability discovery records `GL_MAX_COLOR_ATTACHMENTS` and `GL_MAX_DRAW_BUFFERS`. Moment allocation is attempted only when both limits are at least three. Framebuffer completeness remains authoritative.
- Allocation fallback is staged: an incomplete three-attachment framebuffer logs once and retries `RGBA16F + RG32F` with neighborhood-only clipping; failure of that profile retains the established packed `RGBA8` fallback. Optional moment failure does not disable detailed clouds.
- `GlCloudTemporalRenderTarget` now allocates, clears, filters, copies, and destroys the optional moment texture with the color and metadata history. Moments use linear filtering and clear to `(-1, 0)`, where negative X is the invalid sentinel. History copies reject different attachment profiles.
- The primary cloud trace explicitly draws only color and metadata even when a moment texture is attached. The temporal resolve enables all three attachments and writes moment history, avoiding undefined trace output in attachment 2.
- Valid current samples store unpremultiplied linear luminance and luminance squared. Luminance is bounded to `64` before squaring so both values remain representable in `RG16F`; opacity at or below `1e-4` remains invalid.
- Moment history uses the same representative-distance/kind reprojection and depth, motion, border, coverage, and reactive rejection as cloud color. Variance is `max(E[x²] - E[x]², 0)`.
- Reprojected unpremultiplied history luminance is clipped around the valid current 3×3 luminance mean. High uses `1.5σ` with a `0.015` minimum band; Cinematic uses `2σ` with a `0.01` minimum. YCoCg chroma remains neighborhood-bounded. Missing or invalid moments retain the prior full YCoCg min/max clip.
- Alpha neighborhood clipping now rescales history RGB with alpha so the history remains premultiplied before luminance evaluation and blending.
- Global cloud-history confidence resets with every existing cloud-history invalidation and reaches one over eight successful history copies. Valid moment pixels multiply their fully rejected history weight by this confidence. High and Cinematic maximum weights are now `0.72` and `0.84` respectively before the established final-preview-TAA reduction.
- Diagnostics report attachment/draw-buffer support, selected moment format or fallback reason, and current confidence frames. No CQ1.7 trace-size or CQ1.8 edge-repair behavior was introduced.

### CQ1.7 — Cinematic trace sizing and horizon-seam correction

- Added `PreviewCloudTraceSizing` as the single CPU sizing policy. Low, Medium, and High retain `max(1, viewport / 2)`. Cinematic computes `ceil(viewport × 2/3)` and rounds each result upward to an even dimension.
- Trace, temporal resolve, color/metadata/moment history, and the reconstruction texel footprint use the same resolved dimensions. For example, `575×455` resolves to `384×304` on Cinematic and `287×227` on High; the accepted `862×683` baseline resolves to `576×456` on Cinematic.
- Cloud history and its eight-frame confidence are invalidated when either the full viewport or resolved trace dimensions change. Quality remains in the settings history key, while render-format and backend/resource transitions retain their explicit invalidation paths.
- First-use cloud diagnostics now include the selected trace dimensions and scale, for example `trace=576x456@0.667`.
- Hidden WGL coverage resizes and copies the three-attachment moment target/history across odd-viewport Cinematic and High profiles, in addition to the existing shader, format, depth, HDR, and moment checks.
- Attempted to correct the reported hard horizontal horizon seam without adding CQ1.8 edge repair. The previous visibility fade was centered at 50% on the geometric tangent and the full-resolution reconstruction multiplied the same visibility a second time, reducing a tangent-crossing cloud to roughly quarter strength.
- The shell horizon transition is now biased behind the tangent: visibility is approximately `0.966` at the tangent, remains softly visible just behind it, and reaches zero two feather widths into the hidden side. Full-resolution reconstruction applies only a deep-occlusion guard because trace/history already contain the visible atmospheric fade.
- Opaque scene depth remains hard, and clouds fully behind the planet remain rejected. The user-provided 2026-07-26 runtime screenshot showed that this first correction did not remove the visible seam; CQ1.8 therefore includes the second correction described below.

### CQ1.8 — bounded full-resolution edge repair

- Added an optional desktop Cinematic repair stage after temporal resolve and before fog/god-ray consumption or final composition. It reconstructs into a full-resolution `RGBA16F + RG32F` color/metadata target; Low/Medium/High and GLES/ANGLE retain the CQ1.7 path.
- Each destination pixel classifies its four source taps using the specified contracts: opacity range greater than `0.08`, representative-distance range greater than `max(0.75, nearest × 0.01)`, validity mismatch, cloud-kind range greater than `0.24`, or normalized valid reconstruction weight below `0.75`.
- Scene and solid-planet occlusion are resolved before retracing. A destination ray whose cloud shell is entirely behind terrain, a subject, or the fully hidden side of the planet writes empty cloud data.
- Classified pixels retrace exactly eight STBN-stratified samples over the nearest representative boundary plus/minus one primary fine step, clipped to the selected cumulus/cirrus shell and scene segment. The repair uses the production density, light optical depth, scattering, ambient, weather, wind, cirrus, and shell functions.
- Repair confidence combines discontinuity severity with valid source-tap coverage and local density-sample coverage. Repaired premultiplied radiance/opacity and representative distance/type are written together.
- The full-resolution repaired target becomes `_cloudCompositeTarget`, so fog and god-ray integration consume the same repaired opacity/distance that final composition displays. Final composition switches to a center sample for this already-full-resolution source.
- Shader compile, target allocation, or draw failure disables only edge repair for the GPU session, logs once, disposes the optional target, and continues with the CQ1.7 two-thirds trace/reconstruction. Packed or GLES/ANGLE paths never attempt repair.
- Diagnostics report `edgeRepair=active full-res <width>x<height>, 8-step` or the preset/capability/failure reason.
- GPU and CPU profiling expose the optional stage as `Cloud Repair`. The rolling cloud-composite window includes repair plus final composition rather than hiding the new cost in an untimed gap.
- The remaining horizon seam was traced to fading density before Beer–Lambert integration. Thick clouds stayed nearly opaque through most of that nominally smooth range and then collapsed close to zero. Cumulus and cirrus now integrate at their physical density and apply horizon visibility once to the resulting premultiplied layer radiance/opacity. Reconstruction uses only a binary deep-occlusion guard, preventing a second visible fade.
- Runtime visual confirmation of this second horizon correction and CQ1.8 repair remains required.

### CQ1.8 runtime regression gate — 2026-07-27

The first CQ1.8 runtime screenshot did not pass the gate: it showed an empty streamed-ground region, `179 FPS`, `1.4 ms` displayed GPU time, `4.7 ms` CPU terrain draw, and `0.7 ms` Cloud Repair. The preceding view had exceeded `400 FPS`, so CQ1.9 remains unopened. A second runtime capture after the residency-gated fallback still showed the same terrain failure at `165 FPS`. Its new diagnostic reported `residentChunks=12`, `desiredChunks=2401`, and `cameraChunkResident=True`, proving that camera-chunk residency was not a sufficient visibility signal.

Two concrete defects were corrected without reducing the intentional 16-chunk distant LOD ring:

- An entirely empty four-tap source footprint had `validCount = 0` and therefore satisfied the old low-valid-weight test. This classified clear sky and opaque terrain as repair boundaries, effectively turning the bounded eight-step edge repair into a second full-screen cloud march. CPU and shader classifiers now require at least one valid source tap, and the shader exits before shell/density work when all taps and opacity are empty.
- Bootstrap retains a small grass-textured pad at `GroundPlaneWorldY - 0.015`, but it draws only while no streamed terrain chunks are available. The unconditional version exposed the selected pack material as a large striped plane and masked the real terrain-path failure.
- The safety pad is two-sided and explicitly disables POM. This prevents historical winding or malformed/extreme height maps from removing or displacing the last-resort surface. Back-face culling and configured POM state are immediately restored for streamed chunks.
- First terrain MultiDrawIndirect diagnostics include resident-chunk count, desired-chunk count, whether the camera chunk is resident, and `safetyUnderlay=startup-only`. The residency values disproved the initial gap theory.
- A post-quarantine production capture reproduced the same delayed terrain loss while P5.4 was disabled. This disproved voxel DDA and the DDA/Hi-Z handoff as the trigger. P5.4 atlas prefetch, upload, and DDA occlusion are enabled by default again.
- The long-lived hidden-WGL production-profile regression exercises the full backend at the supplied `862x683` viewport with a real four-batch material-array block subject, the supplied resource pack/client material palette, Cinematic clouds, temporal reconstruction, scene depth, translated camera, startup-only fallback, and default DDA.
- Pool telemetry found the actual late transition: streamed terrain repeatedly doubled its shared VBO/EBO from 3 MiB through 768 MiB and then 1.5 GiB as residency approached the 2,401-chunk desired set. `GlTerrainMeshPool` allocated and copied a replacement without checking GL errors, then deleted the last healthy buffers unconditionally. A failed later allocation could therefore leave CPU-side chunks and draw timing intact while every terrain draw referenced an empty replacement—the exact production symptom.
- Terrain-pool growth is transactional across both buffers. The old VBO/EBO and VAO bindings remain live unless allocation and copy both succeed; upload failures return their suballocations; LOD replacement uploads complete before the visible allocation is freed. A 768 MiB total pool ceiling remains as the failure-safe backstop.
- The first containment build proved the diagnosis in production: terrain remained visible after the ceiling deferred additional chunks. Follow-up telemetry showed the precise allocator waste at that point: `254 MiB` of vertex high water but only `31 MiB` of index high water, while lockstep doubling had reserved `256 MiB + 512 MiB`.
- The targeted correction lets each allocation combine a free range on one side with high-water space on the other, grows VBO and EBO independently, uses 1.5x aligned growth, and uses conservative growth when approaching the shared budget. No vertex packing, terrain simplification, vegetation removal, or LOD-ring reduction was required.
- The exact-pack WGL regression reaches all `2,401/2,401` chunks with `685 MiB` reserved and `534 MiB VBO / 66 MiB EBO` high water. It then recenters the camera, renders another 100 frames while edge chunks are replaced, and captures intact terrain with DDA active and no budget rejection. The user confirmed the corresponding production build keeps terrain visible.

The expanded distant LOD checkpoint remains authoritative. The formerly intermittent hidden-WGL P2.3 timeout is now understood: a successfully uploaded atlas with a negative `HashCode.Combine` identity was rejected by the old signed sentinel. The explicit residency correction fixes that process-dependent failure without reducing the LOD ring.

### CQ1.9 accepted visual screenshot — 2026-07-28

The user-designated CQ1.9 acceptance screenshot is a conversation artifact rather than a repository file. It records the production desktop Cinematic path after the terrain-pool correction:

| Field | Accepted capture |
|-------|------------------|
| Preview / trace viewport | `862x683` / `575x455 @ 1.5x` |
| Eye / target | `(-1.68, 42.78, 9.67)` / `(3.25, 42.42, 7.60)` |
| Cloud preset | Cinematic (`cloudQuality=3`) |
| Cloud controls | Density `0.75`; coverage `1.63`; layer height `4.8`; thickness `60`; feature scale `178`; wind `1.5 @ 35°`; cirrus `0.13` |
| Displayed frame rate / GPU | `361 FPS` / `2.5 ms` |
| GPU cloud passes | Trace `0.5 ms`; temporal `0.5 ms`; repair `0.7 ms`; upsample `0.0 ms` |
| GPU scene / TAA / overlay | `0.7 / 0.1 / 0.0 ms` |
| CPU total / scene | `2.2 / 1.8 ms` |
| CPU terrain stream / draw | `0.1 / 1.7 ms` |

The screenshot visibly retains streamed terrain and distant LOD coverage while rendering the camera transition into the cloud layer. Its diagnostics show the floating-point CQ1 path (`RGBA16F` radiance/opacity, `RG32F` metadata, `RG16F` moments), STBN, temporal reconstruction, full-resolution eight-step edge repair, scene depth, and startup-only safety underlay. This is the accepted CQ1.9 visual authority; the automated matrix below supplies the complementary timing and compatibility evidence.

### CQ1.9 automated acceptance — 2026-07-28

The opt-in `PreviewCloudCq1AcceptanceTests` fixture completes the evidence that the manual screenshot could not supply:

- desktop GL `4.6.0 NVIDIA 610.74`, NVIDIA GeForce RTX 2080 Ti;
- twelve `1920×1080` captures: Low/Medium/High/Cinematic dense overcast plus eight additional Cinematic camera/weather cases;
- fixed `6.64 h` sun pose and frozen wind;
- 32 discarded warm-up frames and 240 raw GPU-query samples per case;
- below/inside/above cumulus, grazing horizon, broken cumulus, dense overcast, cirrus-heavy, inside cirrus, and above-both-layers coverage;
- `stbn=asset-v1` and `stbnActive=True` required on High/Cinematic;
- packed RGBA8 Low, FP16/direct Medium, FP16/direct/moments High, and FP16/direct/moments/full-resolution repair Cinematic transitions;
- no detailed-cloud session fault, shader-link failure, or render-state recovery failure.

Dense-overcast cloud-total p50/p95 results are:

| Low | Medium | High | Cinematic |
|-----|--------|------|-----------|
| `0.447/0.739 ms` | `1.180/1.908 ms` | `1.453/2.146 ms` | `5.040/5.612 ms` |

Across the additional Cinematic fixtures, cloud p95 ranges from `2.288 ms` to `6.229 ms`, and the highest full-frame p95 is `8.409 ms`. The manual production capture remains the visual acceptance authority. Phase 6 did not retain a comparable 240-frame High window, so the historical `1.15×` ratio cannot be reconstructed; the accepted High result above is the CQ2 baseline.

The live run initially exposed an STBN asset-loader dependency on Avalonia application bootstrap. `PreviewCloudBakedAssetLoader` now owns a `StandardAssetLoader` bound directly to the app assembly, so deterministic assets load in the application, WGL test host, and other headless consumers. A strict loader test covers the expected asset version, length, and hash.

## Compatibility state

- Low, Medium, and High persisted numerical values are unchanged.
- Low uses the half-resolution packed `RGBA8` cloud target/history on every backend.
- Medium and High remain half resolution on supported desktop GL. Medium uses `RGBA16F + RG32F`; High adds `RG16F` moment history when at least three attachments/draw buffers and framebuffer completeness are available.
- Cinematic uses even-rounded two-thirds resolution and the High three-attachment format when supported.
- High/Cinematic moment allocation failure steps down to `RGBA16F + RG32F` with neighborhood clipping before considering the packed `RGBA8` fallback.
- GLES/ANGLE continues through the existing source-adapted shell renderer and packed metadata path.
- Validated desktop contexts now select the v2 shape/detail/weather profile and publish `densitySemantics=v2`; GLES/ANGLE and v2 failure paths publish and execute coherent v1 semantics.
- Trace, temporal history, and upsampling now operate on linear premultiplied cloud radiance. Low/GLES stores that linear signal in `RGBA8`, so values above one saturate on the compatibility path by design.
- Exposure, soft-knee shaping, and SDR display encoding occur once during final cloud composition for both the primary upsample and fallback composite.
- High and Cinematic use the bundled STBN march-placement volume on desktop; Low, Medium, GLES/ANGLE, and asset/upload failures use the existing lightweight jitter.
- Desktop Cinematic uses the bounded full-resolution repair target when its shader and FP16/direct-metadata framebuffer are available. Optional failure retains the two-thirds CQ1.7 source.
- The Phase 6 height safety and opaque-scene depth contracts are unchanged. CQ3.9 supersedes the historical planet-horizon fade with a bounded flat-layer distance fade only; there is no camera-altitude opacity fade.

## Validation record

| Check | Result |
|-------|--------|
| App solution build | Pass |
| CQ1.8 terrain/rendering/repair focused tests | Pass |
| Complete app test assembly | 604/604 pass in Release with the CQ3.9 interval-policy reconciliation |
| Hidden-WGL CQ3.9 shader/target/depth smoke | 19/19 pass; generic/High/temporal/repair/upsample/cache programs compile and opaque-depth ordering remains green |
| Hidden-WGL CQ3.9 altitude matrix | Pass: all 16 High/Cinematic × temporal on/off × cumulus/cirrus base/top combinations pass; valid uniform clear-sky frames remain in the delta sequence while near-black startup captures are rejected |
| Hidden-WGL CQ3.9 full-HD visual/depth matrix | Pass: 13 captures and 3,120 retained GPU samples cover below/inside/above layers, grazing distance, cirrus, terrain/depth ordering, moving shadows, and sun transitions |
| Hidden-WGL CQ3.9 frozen High performance | Pass: `0.514/0.547 ms` trace p50/p95, `0.701/1.511 ms` cloud-total p50/p95, `0.514 ms` amortized lighting, and `0.931×` CQ2 versus the `1.25×` ceiling |
| Hidden-WGL full-backend terrain capture, bundled palette | Pass at `862x683` |
| Hidden-WGL full-backend terrain capture, Minecraft 26.1.2 terrain/vegetation palette | Pass at `862x683`; DDA active; `2,401/2,401` chunks before recenter; 685 MiB pool; no rejection; visible-terrain capture after a 100-frame recenter tail |
| Hidden-WGL production-profile DDA/CQ3.7 transition | 2/2 pixel harnesses pass; P5.4 and acceleration lanes remain active, and resident terrain stays visible after late cache/DDA initialization |
| Hidden-WGL cloud shader/target/depth test, including CQ3.2 parity, CQ3.3–CQ3.4 selection/visibility, and CQ3.5 publication | 1/1 pass; cumulative R remains monotonic, layer-local G remains bounded and matches across generators, barriers complete, center/overlap/far/outside/partial selection matches, and fixed-density ground transmittance matches Beer-Lambert |
| Hidden-WGL CQ1.9 1080p preset/camera/timing matrix | 1/1 pass; 12 captures; 2,880 retained GPU samples; deterministic STBN active |
| Hidden-WGL CQ2.8 1080p density visual/performance matrix | 1/1 pass; 13 debug-off captures; 3,120 retained GPU samples; asynchronous High trace gate passes at `0.729×` CQ1 |
| Hidden-WGL P2.3 acceleration smoke after signed-version DDA correction | 1/1 pass; atlas initializes and DDA compaction assertion succeeds |
| Hidden-WGL CQ3.7 1080p lighting/cache matrix | 1/1 pass; 13 captures; 3,120 retained GPU samples; High `0.671 ms` / `1.215×` CQ2 gate result |
| Hidden-WGL CQ3.7 failure and GL 3.3 paths | 2/2 pass; compute→fragment→short-march demotion, stale-publication rejection, and real fragment generation verified |
| CQ3.0–CQ3.7 profile/coordinate/bounds/blend/capability/fallback/consumer/lifecycle focused tests | Pass |
| `git diff --check` | Pass |
| Initial fixed-camera cloud capture | Accepted user capture |
| Initial displayed GPU timing | `1.1 ms` total; `0.3 ms` cloud trace |

The broader solution run completed the app and preview suites but is not green: a separate Core test host hit a native ONNX Runtime access violation, and three existing rabbit hierarchy assertions failed in `AutoPBR.GeometryCompiler.Tests`. Neither failure touches the renderer files in this correction. The CQ2.7 Release app solution build succeeds; its non-incremental rebuild reports 37 existing analyzer warnings in unrelated HDR, terrain, desktop-WGL and test code. The prior transient Enterprise-signing denial no longer reproduces: both the full app test assembly and native WGL smoke now launch and pass.

## Next implementation task

Start CQ4.0 capability/backend selection while preserving the accepted flat CQ3.9 procedural layer as the mandatory fallback:

1. Add the planned `CanUseSparseCloudVolumes` desktop capability contract.
2. Add an internal backend-selection profile without allocating sparse resources or changing cloud pixels.
3. Select sparse volumes only for Cinematic on compute/image-store/SSBO-capable desktop GL.
4. Keep High, GLES/ANGLE, and unsupported Cinematic systems on the accepted CQ3.9 procedural flat layer with explicit diagnostics.
