# CQ4 — Sparse voxel/SDF volumetric cloud backend

**Status:** Complete — CQ4.0–CQ4.8 accepted
**Roadmap:** [Volumetric cloud quality roadmap](volumetric-cloud-quality-roadmap.md)  
**Depends on:** [CQ1 precision and reconstruction](volumetric-cloud-cq1-precision-reconstruction.md), [CQ2 density textures](volumetric-cloud-cq2-density-textures.md), [CQ3 lighting cache](volumetric-cloud-cq3-lighting-cache.md)  
**Fallback:** Accepted CQ3.9 procedural flat-layer renderer

## Goal

Add a desktop Cinematic cloud-density backend that supports stable flight through genuinely three-dimensional cloud formations. CQ4 uses a logical sparse-brick volume with conservative distance data, deterministic cloud-envelope templates, bounded residency updates, and three camera-centered clipmaps. It reuses CQ1 reconstruction, CQ2 boundary detail, and CQ3 lighting rather than creating a parallel compositor.

Success means:

- nearby clouds retain structure when the camera enters, exits, flies above, or looks horizontally through them;
- large-scale cloud forms do not reveal one repeated shell texture;
- empty space is skipped efficiently through conservative brick/page and distance data;
- clipmap recentering and LOD transitions do not pop, swim, or invalidate terrain depth ordering;
- memory and per-frame generation work remain hard bounded;
- missing capabilities, residency, or GPU failures fall back to the CQ3 shell without disabling clouds.

## Non-goals

- Do not require OpenGL sparse-texture extensions. CQ4 implements logical sparsity over ordinary GL 4.3 textures and buffers.
- Do not run a full Navier-Stokes weather simulation at runtime.
- Do not stream internet weather or external cloud assets.
- Do not replace CQ2 weather control, CQ2 fine detail, CQ3 lighting, or CQ1 reconstruction.
- Do not enable the sparse backend for Low, Medium, or High during CQ4.
- Do not remove the procedural flat-layer backend after CQ4 acceptance.
- Do not add precipitation particles, lightning, aerodynamic forces, or local point-light injection.

## Baseline

The accepted CQ3.9 backend evaluates flat world-altitude cloud density procedurally for every view and cloud-light query. It preserves continuous height traversal, far-distance fade, and opaque-scene depth behavior and remains the required fallback, but it has no persistent three-dimensional occupancy, no bounded residency model, and no conservative empty-space representation for long camera-inside fly-throughs. CQ4 begins only after that procedural-layer path, CQ1 reconstruction, CQ2 assets, and CQ3 lighting have passed their exit gates.

## Capability fallback and backend selection

Introduce an internal cloud-density backend selection:

- `ProceduralLayer`: accepted CQ3.9 flat world-altitude density path.
- `SparseVoxel`: CQ4 brick/page/SDF density path.

`SparseVoxel` is eligible only when:

- quality is Cinematic;
- the context is desktop GL;
- compute shaders, image load/store, and shader storage buffers are available;
- sparse shaders and every required resource initialize successfully;
- no session-level sparse runtime fault is active.

Expose `CanUseSparseCloudVolumes` from capability policy. Backend selection is automatic inside Cinematic; it is reported in diagnostics but does not add a separate end-user toggle. A debug-only force-shell option is allowed for parity testing.

Unsupported Cinematic uses the CQ3 Cinematic shell profile, including CQ1 two-thirds reconstruction and CQ3 lighting where supported.

## Architecture and data flow

Weather channels select and deform a deterministic envelope template for each requested world brick. A bounded compute queue writes density/distance bricks and the build page tables, then publishes a fenced active-table generation. View rays and CQ3 cloud-light generation query the same page-table DDA and conservative-distance sampler, fall through to coarser or shell density when data is absent, and add CQ2 procedural boundary detail only near occupied regions. The view result continues through CQ1 metadata, temporal reconstruction, edge repair, and composition.

## Formats and dimensions: logical clipmaps

Use three camera-centered, world-axis-aligned logical clipmaps. Origins snap to whole logical bricks.

| Level | Voxel size | Logical voxel coverage | World coverage | Purpose |
|------|------------|------------------------|----------------|---------|
| L0 | 2 units | 256×128×256 | 512×256×512 | Camera-inside detail and nearby silhouettes |
| L1 | 8 units | 256×128×256 | 2048×1024×2048 | Mid-distance cloud bodies |
| L2 | 32 units | 256×128×256 | 8192×4096×8192 | Far formations and weather continuity |

Each logical brick contains `8×8×8` interior voxels. Each page table is therefore `32×16×32` entries. The vertical origin follows configured cloud-layer bounds and is snapped independently from horizontal camera motion.

Use 10% of each inner level's horizontal span as its blend band into the next coarser level. Density queries blend extinction and conservative distance monotonically; they do not crossfade already integrated cloud colors.

## Physical brick pool

Store an `8³` logical interior with a one-voxel border on every face, producing a `10³` physical brick. Borders duplicate adjacent logical density/distance so trilinear filtering never samples unrelated atlas bricks.

Allocate a fixed pool of 4,096 physical bricks. Lay them out in a `16×16×16` brick atlas, yielding a `160³` `RG8` 3D texture:

- R: normalized density `[0,1]`;
- G: conservative unsigned empty-space distance in logical voxels, normalized so 255 represents at least 32 voxels.

Do not generate mipmaps for the physical atlas. CQ2 procedural detail supplies sub-voxel structure; clipmap selection supplies base-density LOD.

At base level the atlas consumes approximately 7.8 MiB. Page tables, allocator state, staging buffers, and generation queues must keep total CQ4 density-residency memory below 16 MiB. CQ1/CQ3 resources are accounted separately.

## Page-table ABI

Each clipmap owns two `R16UI` 3D page tables: active and build. Entry values are:

- `0`: unmapped/empty page;
- `1..4095`: physical brick index plus one;
- `65535`: requested but not resident.

Physical brick index 4095 is permanently reserved as a cleared fallback brick. Allocatable physical indices are therefore `0..4094`, encoded as page values `1..4095`; page value zero remains unmapped and `65535` remains requested. This convention is fixed in shared CPU/shader constants and tested at both boundaries.

The active table is immutable during a draw. Compute generation writes the build table and brick atlas, issues image/SSBO/texture barriers, then publishes the build table only after a fence confirms completion. Swap active/build handles; do not copy the entire table on the CPU.

Every page-table publication increments a density-generation ID consumed by CQ1 temporal and CQ3 lighting invalidation.

## Residency and allocation

CPU-side residency metadata tracks:

- clipmap level and logical brick coordinate;
- physical brick index;
- last requested and last visible frame;
- generation/version ID;
- state: free, requested, generating, resident, retiring;
- conservative coverage priority.

Request priority is deterministic:

1. L0 bricks intersecting the camera or view frustum;
2. other visible L0 bricks by squared camera distance;
3. visible L1, then visible L2;
4. non-visible guard-band bricks by level and distance;
5. ties by stable logical coordinate ordering.

Generate at most 96 entering bricks per frame. Do not exceed the cap during teleports or settings changes. Until residency converges:

- an unmapped L0 query tries L1;
- an unmapped L1 query tries L2;
- an unmapped L2 query evaluates the CQ3 shell density;
- a requested-but-not-resident page follows the same fallback chain.

Evict least-recently-visible bricks outside all active guard bands. Never evict a brick referenced by the active page tables. Retire it only after the next published build table drops the mapping and its fence completes.

If the allocator reaches capacity, stop issuing lower-priority requests, increment a bounded overflow diagnostic, and use coarser/shell fallback. Never wrap or overwrite a resident physical index.

## Clipmap recentering

Origins snap to `brickSize × voxelSize` in each axis. When an origin changes:

1. compute overlap between old and new logical coordinates;
2. copy/reuse mappings for still-valid world bricks in the build table;
3. request only newly exposed guard-band/visible bricks;
4. preserve the active table until the build update is complete;
5. publish atomically and increment the generation ID.

Large camera teleports clear build mappings and repopulate under the same 96-brick cap. Continue rendering from old/coarser/shell data until new pages become resident. Crossfade shell fallback to resident sparse density over eight accepted CQ1 temporal frames; reject rather than blend when world-space density disagreement exceeds the normal temporal threshold.

## Deterministic cloud-envelope library

Bundle twelve v1 envelope templates generated by the preview cloud asset tool:

| Family | Variants | Intended form |
|--------|----------|---------------|
| Cumulus humilis | 3 | Shallow detached clouds with flat bases |
| Cumulus mediocris | 3 | Moderate vertical domes and multiple lobes |
| Cumulus congestus | 3 | Tall narrow towers with dense coherent bodies |
| Stratus | 3 | Broad shallow sheets with soft broken boundaries |

Each template stores a deterministic low-resolution envelope and conservative distance field in a versioned binary asset. The generator uses seeded buoyant growth, cellular merging, erosion, and vertical profile shaping; it does not require runtime fluid simulation or external DCC software.

The v1 template ABI uses a common `32×24×32` normalized volume stored as linear `RG8`. R is envelope density. G is an unsigned conservative Chebyshev empty-space distance in template voxels, with occupied voxels encoded as zero and empty values clamped to 31. The twelve blobs total 589,824 bytes. Filenames include family, variant, dimensions, format, and version; every seed and SHA-256 is pinned by the shared generator contract.

Template requirements:

- common normalized coordinate/altitude convention;
- flat condensation base for cumulus families;
- no disconnected one-voxel islands;
- conservative distance never skips a nonzero envelope voxel;
- periodicity is not required inside a template because world placement/orientation varies;
- exact dimensions, byte counts, seeds, and SHA-256 are pinned by tests.

Weather map channels drive placement:

- R/coverage decides whether a weather cell emits a formation;
- G/type selects/blends the cloud family;
- B/precipitation increases envelope density and congestus probability;
- A/convection selects vertical scale, drift, and variant.

World-cell hashing selects template variant, rotation, mirroring, and scale deterministically. Adjacent cells blend overlapping envelope density using a smooth maximum or bounded union, never additive opacity beyond the documented density clamp.

CQ2 shape/detail textures remain responsible for sub-envelope billows and boundary erosion at ray-sample time. Do not bake high-frequency CQ2 detail into bricks.

## Brick generation

Compute generation evaluates each requested physical brick:

1. identify overlapping deterministic weather cells and templates;
2. evaluate/deform their low-frequency envelopes at the brick's world voxels;
3. apply shared condensation altitude and large-scale weather density;
4. write R8 base density including the one-voxel border;
5. compute conservative empty-space distance into G8;
6. validate occupied-voxel conservativeness before publishing residency.

Distance generation may use a bounded local jump-flood/chamfer approximation, but the stored result must be biased downward so it never overestimates safe travel to density. Clamp the maximum encoded distance to 32 logical voxels.

Generation uses fixed workgroup dimensions and bounds-checks both brick index and atlas coordinates. A failed workgroup/dispatch cannot publish the page as resident.

Wind advection is applied during world/template evaluation. Page residency uses weather coverage dilated by the maximum supported wind displacement over the cache lifetime so ordinary wind does not require rebuilding every brick each frame. Slow formation evolution advances a discrete weather generation and follows the bounded regeneration path.

## Sparse ray traversal

### Page traversal

Intersect the view ray with the selected clipmap bounds, then traverse logical bricks using 3D DDA. For each logical brick:

- empty/unmapped: advance to the next brick boundary or query the coarser cascade when required;
- requested: query coarser/shell fallback;
- resident: transform to physical atlas coordinates and enter SDF-guided sampling.

### Conservative-distance stepping

Within a resident brick:

- sample conservative distance from G;
- advance by `max(0.5 voxel, distance × 0.8 voxel)` while density is below the occupied threshold;
- once distance is at or below one voxel, switch to fine density steps;
- evaluate CQ2 shape/detail only near occupied boundaries or inside nonzero base density;
- never step beyond the current logical brick without returning to DDA traversal.

The `0.8` safety factor plus downward-biased distance prevents skipping density under interpolation. A debug mode counts page steps, distance steps, fine steps, fallback queries, and safety violations.

### Cascade selection

- Prefer the finest cascade containing the point and a resident page.
- Blend base density through the configured 10% band.
- If fine residency is missing, use coarse density without a blend delay.
- At the L2 boundary, blend to CQ3 shell density over the outer 10% so far weather remains continuous.
- Representative cloud distance comes from the first accepted fine/coarse density hit and continues through the CQ1 metadata contract.

## Integration with CQ1 and CQ3

CQ4 replaces only density/conservative-density queries. The flat altitude slab and finite far-distance fade remain broad traversal bounds, and opaque scene depth still clips the view ray.

- CQ1 owns trace resolution, STBN, temporal metadata, edge repair, and final reconstruction.
- CQ2 owns boundary detail and weather channel semantics.
- CQ3 builds its light cache from the selected density backend. A page-table generation change invalidates/rebuilds affected cache regions.
- Fog/god-ray integration continues to consume resolved cloud transmittance/distance rather than sampling sparse density independently.

Backend changes invalidate CQ1 history and CQ3 caches. Sparse fallback occurring for individual unmapped pages does not change the global backend ID; metadata confidence accounts for fallback/residency generation so newly resident data is not blended against incompatible shell history.

## Failure handling and recovery

- Capability absence selects shell before allocating sparse resources.
- Asset/template failure disables sparse initialization and retains shell.
- Atlas/page-table allocation failure releases partial resources and retains shell.
- Shader compile, dispatch, barrier, fence, or GL error marks sparse runtime faulted for the session, invalidates sparse/CQ3/CQ1 state, and restores the CQ3 shell path.
- Individual brick generation failure leaves its page requested/unmapped and uses coarser/shell fallback; repeated failures are rate-limited in diagnostics.
- Allocator overflow is recoverable and never disables the backend.
- Context recreation clears all sparse handles and residency, then reevaluates capability selection.

Emergency diagnostics include camera pose, clipmap origins, backend/generation IDs, active/build table handles, resident/requested/generating counts, free brick count, overflow, last dispatch/barrier/fence state, and fallback reason.

## Diagnostics and debug views

Provide debug modes for:

- clipmap level coloration;
- resident/requested/unmapped pages;
- physical brick index/atlas utilization;
- base density before CQ2 detail;
- conservative distance;
- DDA/distance/fine step counts;
- shell/coarse fallback contribution;
- template family/variant;
- cascade and shell blend weights.

Log bounded per-frame counters and pass-scoped GPU timings without synchronous readback during normal rendering. Existing async diagnostic infrastructure may periodically read counters.

## Implementation milestones

- [x] CQ4.0: Add backend enum/policy, `CanUseSparseCloudVolumes`, diagnostics, and force-procedural-layer debug path. Completed 2026-07-30 with distinct requested/eligible/active state, granular capability/resource/fault fallback reasons, the non-persisted `AutoPBR.Preview.ForceProceduralCloudLayer` parity switch, backend-aware CQ1 history identity, CQ3 cache invalidation on future active-backend changes, and no sparse allocation or pixel-path change.
- [x] CQ4.1: Add template asset ABI/generator, twelve deterministic assets, loader, and tests. Completed 2026-07-30 with a shared `32×24×32 RG8` v1 ABI, three pinned variants for each of four families, deterministic integer lobe growth/merging/erosion, largest-component cleanup, flat cumulus condensation bases, exact conservative Chebyshev distance generation, 589,824 bytes of bundled assets, strict all-or-nothing length/hash loading, runtime readiness diagnostics, and no GPU allocation or pixel-path change.
- [x] CQ4.2: Add physical brick atlas, page tables, allocator, residency records, and memory accounting. Completed 2026-07-30 with a transactional `160³ RG8` atlas, six cleared `32×16×32 R16UI` active/build page tables, a deterministic 4,095-entry mapped allocator plus one permanently cleared fallback brick, sequential residency records with active-reference-safe retirement, fixed page sentinels, exact 9,407,452-byte density-residency accounting, allocation rollback coverage, and deliberately deferred sparse sampling/publication.
- [x] CQ4.3: Add snapped clipmap origins, request prioritization, bounded update queue, and table publication. Completed 2026-07-30 with independent `16/64/256`-unit brick snapping for L0/L1/L2, cloud-envelope-centered vertical origins, deterministic camera/frustum/level/distance/coordinate priority, an exact 96-entering-page frame cap, overlap preservation and teleport retirement, complete requested/resident/unmapped staging tables, non-blocking fenced active/build swaps, publication generation identity, and a conservative 12,684,220-byte CQ4 memory reservation. Sampling remains disabled until CQ4.4 generates valid bordered bricks.
- [x] CQ4.4: Add compute brick generation, border filling, conservative distance, barriers, and fences. Completed 2026-07-30 with a fixed `5×5×5` workgroup covering each `10³` physical brick, a bounded 96-entry two-`ivec4` std430 request ABI, one-batch controller backpressure, deterministic world-cell/weather family and variant selection over the CQ4.1 templates, altitude/convection deformation, and identical world-coordinate evaluation for every one-voxel apron. R stores quantized base density; CQ4.5 upgrades G to an exact within-brick Chebyshev distance capped at 32 voxels. Image, texture-fetch, and SSBO barriers precede a non-blocking fence; every workgroup must publish its completion magic before allocator and page-table residency advance. Empty, occupied, border, conservative-distance, fence classification, and live RG8 readback coverage pass. The 384-byte completion-status buffer raises exact CQ4 accounting to 12,684,604 bytes under 16 MiB. Sparse sampling remains disabled.
- [x] CQ4.5: Add page DDA, SDF stepping, CQ2 detail evaluation, and cascade/shell blending. Completed 2026-07-30 with a shared GLSL traversal contract and matching CPU oracle. Active `R16UI` tables decode only published `1..4095` mappings; `0`, `65535`, out-of-footprint, and missing-fine samples immediately fall through to coarser data or the CQ3 shell. Traversal clips every skip to the current logical-brick boundary, applies the conservative `max(0.5 voxel, G×0.8)` step, switches to fine density at one voxel, and fails open to the outer marcher if its 64-iteration inner budget is exhausted. CQ2 detail evaluates only after reaching a resident occupied boundary. Finest-resident density blends over the outer 10% of each clipmap, including L2-to-shell continuity. CPU fixed-ray tests cover fallback, negative coordinates, boundary continuity, no-skip behavior, and budget exhaustion without discarding the ray tail; a hidden OpenGL 4.6 compute readback proves distance and fine steps against an occupied plane. Published resources are bound and diagnosed, but visible activation remains gated until CQ4.6.
- [x] CQ4.6: Integrate sparse density with CQ1 metadata/temporal behavior and CQ3 cache invalidation. Completed 2026-07-30 with a single immutable sampling identity over the atlas generation and resident count captured when the active page-table generation/plan/origins were staged. Requested in-flight bricks remain safe coarse/shell fallbacks; a newer fenced table never displaces the still-valid active identity until its atomic swap. A candidate first rebuilds both CQ3 light cascades from sparse density, then atomically enables view traversal; promotion preserves those matching caches while every completed identity discontinuity invalidates CQ1 history before drawing. The same density reaches cache sky probes, Cinematic local cone taps, edge repair, and ground-transmittance publication. Nine focused policy tests cover stability, pending generation/publication preservation, regressed publication rejection, no residency, recenter/generation signature changes, and two-cascade activation; the full 685-test app suite and three explicit cloud live-GL tests pass.
- [x] CQ4.7: Add overflow/fault recovery, debug views, counters, and GPU timings. Completed 2026-07-30 with fenced orphan recycle for bricks retired while generating, published-table active-reference sync, recoverable atlas overflow without wrapping indices, nine sparse `uDebugView` inspectors (17–25), bounded CPU counters for residency/identity/CQ3 stalls, `SparseBrickGen` GPU timing, and injectable dispatch/barrier/fence/status/publication/context-loss demotion to the CQ3.9 shell.
- [x] CQ4.8: Complete fly-through, residency, visual, memory, fallback, and performance acceptance. Completed 2026-07-30 with always-on CPU memory/overflow/teleport gates, focused recovery and debug-view automation, and an opt-in hidden-WGL fly-through harness (`AUTOPBR_RUN_CQ4_ACCEPTANCE=1`) covering residency diagnostics, context-loss shell recovery, and High procedural fallback.

## Test matrix

### CPU/reference tests

- Logical/world/page/brick/atlas coordinate transforms round-trip at boundaries.
- Snapped origins and overlap reuse are deterministic.
- Page sentinel/index conventions cover zero, first, last, requested, and invalid values.
- Allocator never duplicates a physical brick and never evicts active references.
- Priority order is stable and respects the 96-brick frame cap.
- Three cascade dimensions produce the documented world coverage.
- Template selection and deformation are deterministic from weather/world coordinates.
- Generated distance never exceeds CPU reference distance to occupied density.

### Asset tests

- Twelve expected templates exist with pinned dimensions, byte counts, seeds, and hashes.
- Cumulus bases satisfy the documented flatness tolerance.
- Every occupied template component exceeds the minimum connected-volume threshold.
- Distance conservativeness holds for every voxel and border.

### Live GL tests

- Allocate the full atlas and all active/build page tables within the memory bound.
- Generate known bricks, publish a build table through barrier/fence, and sample expected density.
- Recenter by positive/negative brick offsets and prove overlapping world bricks retain data.
- Force atlas capacity and verify bounded overflow without out-of-range writes.
- Compare sparse traversal against brute-force reference density for fixed rays.
- Force shader/dispatch/fence failures and verify CQ3 shell recovery.
- Keep CQ1 direct/packed metadata, scene depth, and CQ3 lighting smoke tests green.

### Visual scenarios

- Slow and fast flight into, through, and out of each cloud family.
- Camera stationary while wind advects formations.
- Teleport across all three cascade spans.
- Horizontal view inside the layer and vertical view from above/below.
- Dense congestus, broken fair-weather cumulus, broad stratus, and mixed cells.
- Near/mid/far cascade bands and L2-to-shell boundary.
- Terrain/subject intersections and geometric horizon.
- Residency warm-up, forced low brick budget, and atlas saturation.

### Quantitative correctness

- Sparse traversal never reports a missed occupied reference sample in randomized CPU/GPU ray fixtures.
- Entering-brick generation never exceeds 96 requests in one frame.
- Atlas/page/staging density-residency memory remains below 16 MiB.
- No page-table publication occurs before its generation fence completes.
- Fixed fly-through density/opacity is continuous across brick and cascade boundaries within R8/trilinear tolerance.
- High and all unsupported Cinematic contexts remain pixel-equivalent to the accepted CQ3 shell path within existing format tolerances.

## Performance gate

- Cinematic sparse rendering remains within the roadmap interactive budget and reports view traversal, brick generation, and CQ3 lighting separately.

## Exit criteria

- Cinematic automatically uses sparse density on eligible desktop GL and CQ3 shell everywhere else.
- The brick pool, page tables, update queue, and memory are hard bounded and diagnosable.
- Three clipmaps recenter and blend without visible seams or density swimming.
- Conservative-distance traversal skips empty space without missing occupied density.
- All twelve cloud templates are deterministic and weather-driven.
- CQ1 reconstruction, CQ2 detail, CQ3 lighting, Phase 6 depth/height behavior, and shell fallback remain intact.
- Failure recovery, fixed-scene artifacts, live-GL coverage, and GPU/memory evidence are complete.

## References

- Guerrilla Games, *Nubis³ — compressed SDF acceleration, voxel up-resolution, and cloud-specific lighting*: <https://www.guerrilla-games.com/read/nubis-cubed>
- Andrew Schneider, *Nubis: Authoring Real-Time Volumetric Cloudscapes with the Decima Engine*: <https://advances.realtimerendering.com/s2017/Nubis%20-%20Authoring%20Realtime%20Volumetric%20Cloudscapes%20with%20the%20Decima%20Engine%20-%20Final%20.pdf>
- Fabian Bauer, *Creating the Atmospheric World of Red Dead Redemption 2*: <https://advances.realtimerendering.com/s2019/index.htm>
