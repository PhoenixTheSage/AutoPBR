# Volumetric cloud quality roadmap

**Status:** Proposed  
**Created:** 2026-07-20  
**Scope:** Genesis 3D preview volumetric cloud body, reconstruction, density assets, lighting, and the optional desktop sparse-volume backend.

## Purpose

This roadmap moves the Genesis preview clouds from a compact procedural shell toward a production-style cloud renderer while preserving the Phase 6 correctness work. The work is intentionally split into four sequential contracts:

1. [CQ1 — Precision and reconstruction](volumetric-cloud-cq1-precision-reconstruction.md)
2. [CQ2 — Density textures and weather data](volumetric-cloud-cq2-density-textures.md)
3. [CQ3 — Cloud-light froxel cache](volumetric-cloud-cq3-lighting-cache.md)
4. [CQ4 — Sparse voxel/SDF backend](volumetric-cloud-cq4-sparse-voxel-sdf.md)

The dependency chain is strict: **CQ1 → CQ2 → CQ3 → CQ4**. A later phase may be prototyped in isolation, but it must not become the production path until the preceding phase meets its exit criteria.

## Pipeline ownership

Cloud-body rendering and camera fog use different volume representations:

- `genesis_clouds.frag` ray-marches the cumulus and cirrus cloud shells into a cloud-specific offscreen target.
- The cloud temporal and upsample passes reconstruct that target and composite it against opaque scene depth.
- The existing camera-aligned froxel volume injects and integrates fog and god rays. It consumes resolved cloud transmittance/depth, but it does not represent the detailed cloud body.
- CQ3 introduces a second froxel concept: a **light-aligned cloud-light cache**. It stores cloud lighting information and must not be conflated with the existing camera fog/god-ray froxels.
- CQ4 optionally replaces shell density queries with sparse cloud density queries. It continues to use CQ1 reconstruction and CQ3 lighting.

Increasing the existing fog-froxel resolution is therefore not a substitute for this roadmap.

## Compatibility policy

- GLES/ANGLE remains a functional compatibility path using the shell renderer, packed RGBA8 metadata, and the current short cloud light march.
- Desktop GL 3.3 may use CQ1 floating-point targets and CQ3 fragment-slice cache generation when required formats are framebuffer-renderable.
- Desktop GL 4.3+ may use compute/image-store cache generation and the CQ4 sparse backend when capability checks pass.
- Add a persisted `Cinematic = 3` volumetric quality value. Existing Low/Medium/High values `0..2` retain their meanings and deserialize unchanged.
- Cinematic on unsupported hardware falls back to the best CQ3 shell configuration. It must never disable clouds merely because CQ4 is unavailable.
- Every capability-selected path reports its selected cloud backend, render formats, trace scale, lighting-cache mode, and fallback reason through existing preview diagnostics.

## Prerequisites

- [ ] Restore a green solution/test build. The current unrelated terrain `Span<Candidate>`/`IReadOnlyList<Candidate>` compile error must be resolved outside this roadmap.
- [ ] Capture fixed-scene Low/Medium/High screenshots and GPU timer samples before CQ1 changes output.
- [ ] Record the GL vendor, renderer, context version, viewport, cloud settings, camera pose, sun pose, and warm-up frame count with every baseline.
- [ ] Preserve the Phase 6 contracts: safe below/inside/above height transitions, subtle 72,000-unit curvature, atmospheric horizon feather, opaque scene depth ordering, terrain occlusion, and no cloud rendering over nearby subjects.
- [ ] Keep the live hidden-WGL cloud compile/depth-ordering smoke test green before each phase begins.

## Phase tracker

| Phase | Deliverable | Depends on | Compatibility fallback | Status | Exit summary |
|------|-------------|------------|------------------------|--------|--------------|
| CQ1 | Linear HDR targets, precise metadata, STBN temporal reconstruction, Cinematic trace/edge repair | Baseline and green build | Current RGBA8 shell target/history | Proposed | Stable HDR reconstruction without depth regressions or new temporal trails |
| CQ2 | Versioned shape/detail/weather assets and explicit ray-footprint LOD | CQ1 | Existing v1 128³/32³/256² assets | Proposed | Finer, non-repeating structure with bounded density-stage cost |
| CQ3 | Snapped light-aligned cloud-light cascades, long-range shadowing, cloud AO and ground contribution | CQ2 | Current per-sample short light march | Proposed | Coherent deep self-shadowing and terrain shadows without swimming |
| CQ4 | Desktop sparse brick/SDF density backend and deterministic cloud envelope library | CQ3 | CQ3 shell renderer | Proposed | Stable fly-through density with bounded residency, memory, and traversal cost |

## Roadmap milestones

### CQ1 — Precision and reconstruction

- [ ] CQ1.0: Capture baseline screenshots, timing artifacts, and format diagnostics.
- [ ] CQ1.1: Add render-format capability selection and floating-point cloud targets.
- [ ] CQ1.2: Keep trace/history radiance linear through full-resolution reconstruction.
- [ ] CQ1.3: Add deterministic spatiotemporal blue-noise sampling and temporal moments.
- [ ] CQ1.4: Add Cinematic quality and two-thirds-resolution trace allocation.
- [ ] CQ1.5: Add bounded full-resolution edge repair.
- [ ] CQ1.6: Complete desktop, GLES-source, live-GL, temporal, depth, and HDR regression coverage.

### CQ2 — Density textures and weather data

- [ ] CQ2.0: Freeze and document the v2 asset channel ABI.
- [ ] CQ2.1: Generate deterministic shape-128, detail-64, and weather-1024 assets.
- [ ] CQ2.2: Add strict versioned loading with v1 fallback and diagnostics.
- [ ] CQ2.3: Add explicit shape/detail mip selection from ray footprint.
- [ ] CQ2.4: Add High/Cinematic rotated boundary detail and weather-channel shaping.
- [ ] CQ2.5: Complete seam, hash, distribution, upload, visual, and performance coverage.

### CQ3 — Cloud-light froxel cache

- [ ] CQ3.0: Add cloud-light cache profiles, coordinates, and stable snapped origins.
- [ ] CQ3.1: Add fragment-slice generation and lookup on desktop GL 3.3.
- [ ] CQ3.2: Add compute/image-store generation on capable desktop GL.
- [ ] CQ3.3: Replace long cloud light marches with cache sampling and local Cinematic cone taps.
- [ ] CQ3.4: Feed cloud ground transmittance to terrain, fog, and god-ray consumers.
- [ ] CQ3.5: Add cache scheduling, invalidation, history, diagnostics, and GPU timings.
- [ ] CQ3.6: Complete lighting, shadow, scrolling, fallback, and performance coverage.

### CQ4 — Sparse voxel/SDF backend

- [ ] CQ4.0: Add sparse-cloud capability/backend selection without changing shell behavior.
- [ ] CQ4.1: Add brick atlas, page tables, allocator, residency policy, and bounded updates.
- [ ] CQ4.2: Add three snapped logical clipmaps and double-buffered page-table publication.
- [ ] CQ4.3: Add deterministic envelope templates and weather-driven brick generation.
- [ ] CQ4.4: Add page-table DDA, conservative-distance skipping, and cascade blending.
- [ ] CQ4.5: Feed sparse density into CQ3 lighting and CQ1 reconstruction.
- [ ] CQ4.6: Complete fly-through, overflow, fault, memory, temporal, and fallback coverage.

## Quality policy

| Preset | Cloud trace | View steps | Reconstruction | Density assets | Lighting | Density backend |
|--------|-------------|------------|----------------|----------------|----------|-----------------|
| Low | 1/2 resolution | 16 | Compatibility history/no moments | v1 allowed | Current short march | Shell |
| Medium | 1/2 resolution | 24 | FP16 where supported; temporal history | v2 on desktop | Current short march | Shell |
| High | 1/2 resolution | 32 | FP16 + STBN + moments | v2 + rotated boundary detail | CQ3 High cache | Shell |
| Cinematic | 2/3 resolution | 48 | FP16 + moments + edge repair | v2 maximum quality | CQ3 Cinematic cache | CQ4 sparse when supported; otherwise shell |

Debug march-step override remains authoritative for view-step count. It does not alter target format, trace scale, cache profile, or backend selection.

## Cross-phase invariants

- Scene depth and planet depth remain authoritative at trace and full-resolution reconstruction.
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

