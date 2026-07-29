# CQ3 — Light-aligned volumetric cloud lighting cache

**Status:** In progress
**Roadmap:** [Volumetric cloud quality roadmap](volumetric-cloud-quality-roadmap.md)  
**Depends on:** [CQ1 precision and reconstruction](volumetric-cloud-cq1-precision-reconstruction.md), [CQ2 density textures](volumetric-cloud-cq2-density-textures.md)  
**Required by:** [CQ4 sparse volume](volumetric-cloud-cq4-sparse-voxel-sdf.md)

## Goal

Replace the short, repeated cloud self-shadow march with a stable light-aligned cache that represents long-range sun optical depth and sky visibility. CQ3 should produce deep but luminous cloud interiors, darker bases, coherent silver lining, terrain cloud shadows, and consistent cloud/fog/sky lighting without making every view sample launch a long secondary ray.

Success means:

- large cloud masses cast coherent shadows through their full depth;
- shadows and ambient occlusion do not swim as the camera moves;
- cloud shadows appear on terrain and influence fog/god rays;
- sunrise, sunset, noon, below-layer, inside-layer, and above-layer views share one lighting state;
- High and Cinematic gain quality while GLES/ANGLE retains the current short light march;
- cache generation cost is predictable and visible in GPU timings.

## Terminology

The **cloud-light froxel cache** introduced here is light aligned and stores cloud-lighting data. It is not the existing camera-aligned fog/god-ray froxel volume.

| Volume | Coordinate frame | Stores | Primary consumer |
|--------|------------------|--------|------------------|
| Camera fog froxels | Camera frustum | Fog density, in-scatter, occupancy | Fog/god-ray integration |
| Cloud-light froxels | Directional-light basis | Cumulative sun optical depth and sky visibility | Detailed cloud shading, terrain shadowing, fog/god rays |

The two may exchange cloud transmittance, but they must have separate resources, histories, invalidation keys, profiles, and diagnostics.

## Non-goals

- Do not move the detailed cloud body into the camera fog-froxel grid.
- Do not replace the CQ1 cloud trace/temporal/upsample path.
- Do not replace CQ2 density assets or their explicit LOD contract.
- Do not add sparse density residency; that is CQ4.
- Do not implement local point-light illumination, lightning, precipitation shafts, or colored emissive clouds.
- Do not require compute shaders for functional desktop rendering.

## Baseline

The current cloud shader estimates direct-light optical depth with two to four density samples over a short fixed range plus one coarse far sample. It evaluates that march repeatedly from occupied view samples. This is inexpensive and responsive, but it cannot represent a distant dense tower shadowing a nearby sample, and its limited rays produce shallow/local lighting structure.

The current multi-scatter approximation brightens dense clouds analytically, and the sky-view LUT supplies ambient color. Terrain does not yet consume a stable long-range cloud shadow field.

## Architecture and data flow

CQ2 density is injected into near and far light-aligned cascades to accumulate sun optical depth and sky visibility. Cloud view tracing samples those cascades instead of repeating a long light march; terrain reuses their ground transmittance, while the existing camera fog/god-ray froxels consume the published sun transmittance without becoming cloud-density storage. CQ1 then reconstructs and composites the shaded cloud result.

## Formats and dimensions

Use two light-aligned cascades centered around the camera's ground projection and covering the full configured cumulus/cirrus altitude range.

Each cache texel stores `RG16F`:

- R: cumulative sun optical depth from the sun-facing cache boundary to the froxel center;
- G: hemispherical sky visibility approximation in `[0,1]`.

Use a 2D texture array per cascade. XY is the plane perpendicular to the sun direction; array layers advance along the sun direction. Linear sampling is enabled in XY and between logical slices through explicit two-slice interpolation. Clamp all axes at cache boundaries.

### Profiles

| Preset | Near cascade | Far cascade | Near world span | Far world span | Schedule |
|--------|--------------|-------------|-----------------|----------------|----------|
| Low | Disabled | Disabled | — | — | Current short march |
| Medium | Disabled | Disabled | — | — | Current short march |
| High | 192×192×16 | 128×128×12 | 640 units | 2,560 units | Near every 2 frames; far every 4 frames |
| Cinematic | 256×256×24 | 192×192×16 | 640 units | 2,560 units | Near every frame; far every 4 frames |

World span is measured across the light-aligned XY plane. Z spans the complete cloud altitude interval plus one conservative CQ2 detail period at both ends so filtering does not clip the layer boundary.

The near/far overlap occupies the outer 20% of the near span. Sample both caches there and smoothstep blend. Outside near coverage, use far. Outside far coverage, fall back to the existing short light march with coarse explicit density LOD.

## Coordinate system and stability

Build an orthonormal light basis:

- forward points from the sun toward the world;
- choose a stable reference axis that switches only when the sun direction is nearly parallel;
- right/up span the cache plane.

Transform the camera ground projection into the light basis and snap each cascade origin independently to one cache texel in light-plane world units. Snap the light-depth origin to one logical slice.

The basis may not abruptly flip as the sun crosses the reference-axis threshold. Select the alternate reference with hysteresis and invalidate both cascades when the basis changes.

Camera motion inside one snapped texel does not move the cache. When an origin advances by whole texels, scroll reusable data where valid and regenerate newly exposed regions. A full regeneration remains valid for the first implementation; scroll reuse is required before final performance acceptance.

## Cache generation

### Density source

CQ3 calls the accepted CQ2 conservative/full density API with a footprint derived from each cloud-light froxel's world dimensions. It never uses the camera pixel angular footprint.

For each light column:

1. initialize cumulative optical depth at the sun-facing boundary;
2. advance through logical depth slices in sun-to-world order;
3. sample conservative density first;
4. evaluate full density only in potentially occupied froxels;
5. integrate optical depth with Beer-Lambert-compatible units;
6. estimate sky visibility from local density and a small fixed cone of neighboring/coarser density samples;
7. store cumulative optical depth and sky visibility at every slice.

Cloud density and extinction units must match the view integrator. The cache stores optical depth, not already exponentiated transmittance, so filtering and multiscatter calculations remain stable. Convert with `exp(-opticalDepth)` at consumption.

### Compute path

Desktop GL 4.3+ with compute and image load/store generates one workgroup per XY tile. Threads cooperate along depth so cumulative optical depth is ordered. Issue the required image/texture memory barrier before the cloud view pass samples the cache.

Use bounded loops compiled with the maximum Cinematic slice count. Profile slice count remains a uniform. Shader compilation, image binding, dispatch, or barrier failure disables compute generation for the session and retries the fragment path.

### Fragment-slice path

Desktop GL 3.3 renders cache layers to framebuffer-attached array slices. It processes slices in sun-to-world order and samples the previous slice for cumulative optical depth. The first slice starts at zero optical depth.

The fragment path must produce values matching compute within half-float tolerance on fixed scenes. GLES/ANGLE does not allocate this cache during CQ3 and continues to use the current short light march.

## Update and invalidation policy

### Scheduled updates

- Cinematic near: every frame.
- High near: frames where `cloudFrameIndex mod 2 == 0`.
- High/Cinematic far: frames where `cloudFrameIndex mod 4 == 0`.
- If near and far are both due, update near first and defer far by one frame if the previous cache GPU time exceeded its phase budget.

Between scheduled updates, reproject cache coordinates using wind advection. Do not reuse a cache beyond four frames.

### Immediate rebuild triggers

Rebuild both cascades and invalidate cache history after:

- cloud density, coverage, layer height/thickness, volume scale, cirrus strength, or density-asset profile changes;
- CQ4 density backend/generation changes once CQ4 exists;
- camera movement exceeding half the near cascade span;
- a sun-direction change greater than `0.5°` in one frame;
- a light-basis reference-axis change;
- quality, cache dimensions, viewport context, or render-format changes;
- freeze-wind toggles or debug overrides affecting density/lighting.

Sun intensity/color changes invalidate lighting use immediately but do not require density integration to rerun because the cache stores optical depth/visibility rather than radiance.

## Cloud shading

At each occupied view-march sample:

1. select/blend the near/far cache by world position;
2. read cumulative sun optical depth and sky visibility;
3. compute direct sun transmittance from optical depth;
4. apply the existing dual-lobe phase function;
5. evaluate two multiple-scattering approximation octaves;
6. add sky-LUT ambient scaled by cached sky visibility;
7. add a restrained ground contribution at lower cloud altitudes.

### Multiple scattering

Use two octaves after the single-scatter term:

| Octave | Extinction scale | Phase eccentricity scale | Energy scale |
|--------|------------------|--------------------------|--------------|
| 1 | 0.5 | 0.5 | 0.55 |
| 2 | 0.25 | 0.25 | 0.30 |

Cached sky visibility attenuates higher-order energy so dense interiors remain structured. Clamp total scattered energy before exposure, not each octave independently.

### Local Cinematic cone taps

Cinematic evaluates two short density samples toward the sun around the current view sample. Their maximum range is one near-cache XY texel and they use CQ2 explicit LOD. They refine local silver lining and boundary contrast; they do not replace the cached long-range optical depth.

High uses the cache without local cone taps. Low/Medium keep the current light march until a later measured policy change.

### Ground contribution

Sample a low-frequency ground color derived from the preview ground/terrain material and multiply it by:

- upward-facing hemisphere weight;
- cached sky visibility;
- lower-altitude profile that fades to zero by the upper third of cumulus;
- a fixed restrained energy scale documented in the shader.

Ground contribution must not brighten cirrus like low cumulus and must not bypass cloud opacity.

## Terrain cloud shadows

Derive ground transmittance by sampling the cache at the ground-facing end of the light column. Publish it as a stabilized 2D shadow texture in ground/world coordinates.

- High uses the far cache footprint at its native XY resolution.
- Cinematic combines near and far with the same overlap blend as cloud shading.
- Terrain and the simple ground mesh multiply direct sun lighting by cloud transmittance; ambient/IBL remains unaffected.
- Scene subjects may consume the same texture only after terrain parity is proven. Entity shadowing is not an initial CQ3 acceptance requirement.
- Missing/out-of-range shadow samples return full sunlight.

The texture is also available to fog/god-ray injection so direct in-scatter is attenuated by the same sun optical depth. Existing per-view cloud transmittance remains responsible for view-ray ordering.

## Interfaces and resource ownership

Plan these internal concepts:

- `CloudLightingCacheProfile`: dimensions, spans, schedules, local cone-tap count.
- `GlCloudLightFroxelCache`: near/far textures, framebuffer/image bindings, snapped transforms, generation counters.
- `CloudLightingCacheState`: validity, origins, light basis, wind offset, last update frames, failure/fallback state.
- `CanUseComputeCloudLightingCache`: desktop GL 4.3+ with compute + image load/store.
- shared shader include for world-to-cache coordinates and near/far blending.

Cache resources are owned and destroyed with volumetric cloud resources, not camera fog-froxel resources. Context loss, quality changes, and cloud runtime failure release or invalidate both cascades safely.

## Capability fallback

Fallback order:

1. compute cloud-light cache;
2. fragment-slice cloud-light cache;
3. existing short cloud light march.

GLES/ANGLE selects the short march directly. Desktop GL 3.3 uses fragment-slice generation; GL 4.3+ may select compute/image-store after capability and shader validation. A failed higher mode steps down for the session without changing the selected user quality.

## Failure handling and diagnostics

A single cache update failure invalidates the affected cascade before it can be sampled. Near failure may use valid far data in range; far failure uses the short march outside near coverage. Persistent errors disable that generation mode for the session and log once.

Diagnostics report:

- generation path and cache formats;
- cascade dimensions, spans, snapped origins, and generation IDs;
- update cadence and last update age;
- near/far/short-march sample mode;
- per-cascade GPU generation time;
- fallback reason and cache overflow/out-of-range counts;
- terrain and fog consumers enabled/disabled.

## Implementation milestones

- [x] CQ3.0: Add cache profiles, quality mapping, light-space coordinate convention, stable snapped transforms, capability/resource plan, and diagnostics. Completed 2026-07-29 without allocating cache textures or changing cloud pixels: Low/Medium/GLES remain on the short march; High/Cinematic publish the accepted `RG16F` near/far dimensions and compute-versus-fragment preference while honestly reporting `resources=not-allocated-cq3.0`.
- [x] CQ3.1: Add the owned `RG16F` array resources, overlap selection, fragment-slice generation, and fixed-scene reference output on desktop GL 3.3. Completed 2026-07-29 with transactional near/far allocation, ping-pong prefix surfaces, CQ2 density injection, conservative curved-shell depth bounds, explicit two-slice lookup interpolation, and monotonic half-float live-GL readback. CQ3.2 subsequently promoted the internal scratch to `RG32F` for cross-generator parity. Production lighting remains on the short march.
- [x] CQ3.2: Implement compute generation, barriers, and compute/fragment parity tests. Completed 2026-07-29 with direct layered `RG16F` image writes, a compile-time 24-slice bound, shared fragment/compute integration, image-access plus texture-fetch barriers, session-scoped compute fault demotion, exhaustive fixed-density near/far parity readback, and the production DDA/terrain transition remaining green.
- [x] CQ3.3: Replace High/Cinematic long light marches with cache sampling. Completed 2026-07-29 with generated-cascade binding, committed light transforms, explicit depth interpolation, near/far overlap, independently valid cascade selection, and short-march fallback outside valid coverage. Low/Medium/GLES remain unchanged.
- [x] CQ3.4: Add two-octave scattering, sky visibility, ground contribution, and Cinematic cone taps. Completed 2026-07-29 with explicit internal controls, a local two-probe cache-G visibility model, restrained lower-cumulus material bounce, cached cirrus/cumulus lighting, and two explicit-LOD Cinematic boundary samples bounded to one near-cache texel.
- [x] CQ3.5: Add terrain shadow publication and fog/god-ray cache consumption. Completed 2026-07-29 with a transactional `R16F` ground field, direct-only terrain and camera-froxel attenuation, finite-value containment, fixed-density live readback, and a green production DDA/terrain lifecycle.
- [x] CQ3.6: Add update scheduling, wind reprojection, scrolling, invalidation, and fallback. Completed 2026-07-29 with the accepted Cinematic `1/4` and High `2/4` near/far cadence, four-frame maximum reuse, deterministic immediate invalidation, independently transactional cascade updates, wind-reprojected cloud/ground consumers, snapped scroll-overlap planning, per-cascade ages/failure diagnostics, and separate near/far GPU timers. Due cascades currently use the specification's valid full-regeneration first implementation; physical overlap copies remain a measured CQ3.7 performance-acceptance item.
- [ ] CQ3.7: Complete live-GL, visual, stability, and GPU performance acceptance.

### CQ3.0 implementation record — 2026-07-29

- `PreviewCloudLightingCacheProfiles` is the single quality mapping. High uses near `192×192×16` and far `128×128×12`; Cinematic uses near `256×256×24` and far `192×192×16`. Both retain spans `640/2,560`, far cadence four, and overlap `0.20`; High near cadence is two, Cinematic near cadence is one, and only Cinematic requests two local cone taps.
- `PreviewCloudLightBasisBuilder` defines forward as sun-to-world and constructs the light-plane right/up axes. World-up/world-right reference selection uses `0.94/0.88` hysteresis and sign alignment to the prior basis so a threshold crossing cannot create a 180-degree flip.
- `PreviewCloudLightCascadeTransform` freezes light-plane center and light-depth-min snapping, world/unit-cache round trips, containment, texel size, and slice size. It accepts the final depth interval explicitly; CQ3.1 derives that interval from the complete cloud altitude bounds and CQ2 detail padding.
- Desktop GL 3.3 advertises the fragment-slice path. Desktop compute plus image load/store advertises compute generation. GLES/ANGLE advertises neither and selects the short march.
- Runtime diagnostics distinguish the preferred future generator from the active runtime. CQ3.0 always reports `activeRuntime=short-march` and `resources=not-allocated-cq3.0`; this prevents the profile contract from being mistaken for a generated cache.
- The cache plan explicitly reports `cameraFogFroxels=separate`. No camera fog/god-ray resource, CQ2 density placement, CQ1 reconstruction, shell lighting, or cloud pixel output changed in this milestone.

### CQ3.1 implementation record — 2026-07-29

- `GlCloudLightFroxelCache` transactionally allocates the documented High or Cinematic near/far `RG16F` 2D arrays. Every layer is framebuffer-validated and initialized to optical depth zero/sky visibility one before publication. Any near or far allocation failure destroys the entire candidate and leaves the short march active.
- Each cascade owns two 2D prefix surfaces. `GlCloudLightFragmentSliceGenerator` alternates them while rendering layers in sun-to-world order, so a slice samples the completed previous prefix without sampling any texture subresource currently attached for drawing. CQ3.2 upgraded this internal scratch from `RG16F` to `RG32F` so fragment and compute retain the same full-precision recurrence and round only when publishing a logical layer to the `RG16F` cache.
- The fragment generator uses the CQ2 conservative weather test before full shape/detail density. Cumulus extinction matches the view-march units; thin cirrus uses slice/altitude overlap so a sub-slice sheet is not skipped. The current sky-visibility prefix is a deterministic bounded reference approximation; CQ3.4 owns the final cone/AO model.
- `PreviewCloudLightAltitudeBounds` includes cumulus, enabled cirrus, and one complete CQ2 detail period at both ends. `PreviewCloudLightDepthInterval` projects a conservative cascade AABB into the light axis and includes curved-surface drop plus a slice guard before the existing snapped transform is constructed.
- `PreviewCloudLightCascadeBlend` and `common/cloud_light_cache.glsl` implement the same outer-20% near/far weights. The shader include performs explicit interpolation between adjacent logical array layers; it never relies on texture-array layer filtering.
- High/Cinematic desktop contexts allocate and generate one CQ2-backed reference after cloud startup. Low, Medium, GLES/ANGLE, shader failure, incomplete framebuffer, draw failure, and GL allocation/upload failure retain the short march with a diagnostic. Quality changes rebuild transactionally.
- The fixed-density hidden-WGL fixture compiles both generator and lookup shaders, checks cumulative optical depth against the analytic value at every slice, verifies nondecreasing half-float output and bounded sky visibility, and validates the interpolated center lookup. The full `862×683` production backend reports `fragmentReference=ready` while DDA terrain remains visible.
- CQ3.1 deliberately does not bind the cache to production cloud lighting and continues to report `activeRuntime=short-march`. Compute generation begins in CQ3.2; view-shader consumption begins in CQ3.3.

### CQ3.2 implementation record — 2026-07-29

- `GlCloudLightComputeGenerator` binds the existing near/far array as layered `rg16f` image unit zero. A `4×4×24` workgroup covers one light-plane tile; the Z lanes cooperatively perform an inclusive ordered prefix for each of the sixteen XY columns.
- The loop is statically bounded by the accepted Cinematic maximum of 24 slices. High and far cascades terminate at their uniform depth, and profiles exceeding the bound fail generation before dispatch rather than silently truncating data.
- `common/cloud_light_cache_generation.glsl` is now the single density, cirrus-overlap, extinction, and sky-visibility integration implementation for compute and fragment generation. The storage mechanism is the only intended difference between the paths.
- The fragment prefix scratch is `RG32F`, while the published cache remains the specified `RG16F`. This prevents repeated half-float accumulation drift and lets compute and fragment round at the same publication boundary.
- Each cascade dispatch is followed by `GL_SHADER_IMAGE_ACCESS_BARRIER_BIT | GL_TEXTURE_FETCH_BARRIER_BIT` before the texture can be read or the next cascade is generated. Image bind, dispatch, barrier, or GL error invalidates the affected generation.
- Compute selection requires desktop GL 4.3 plus compute shaders, image load/store, texture arrays, and the existing desktop cache contract. Compile/link or runtime failure disables compute for the backend session and immediately retries the already validated fragment generator. Loss of both generators leaves the production short march.
- The hidden-WGL fixture generates separate fragment and compute caches, reads every texel in every near/far layer, and compares optical depth and visibility within the documented half-float tolerance. The same test retains analytic monotonicity and lookup assertions.
- The full `862×683` backend harness reports `generatedBy=compute-image-store`, `referenceReady=ready`, and `activeRuntime=short-march` while P5.4 DDA and resident terrain remain valid. CQ3.2 still does not bind the cache to cloud shading; that transition belongs to CQ3.3.

### CQ3.3 implementation record — 2026-07-29

- `genesis_clouds.frag` now binds the near and far `RG16F` arrays on independent sampler units and receives the committed basis, plane centers, world spans, light-depth intervals, logical depths, and overlap fraction. The cache path is enabled only for generated High/Cinematic cascades.
- `common/cloud_light_cache.glsl` owns production selection. It explicitly interpolates logical array layers, blends both cascades across the accepted outer 20% of the near footprint, lets a valid far cascade cover a missing or out-of-range near cascade, and lets a valid near cascade survive far generation failure.
- The resolver returns a cache-use flag rather than manufacturing neutral lighting outside coverage. Low/Medium/GLES, unavailable resources, invalid cascades, and points outside the far cascade execute the existing per-sample light march.
- Cached cumulative optical depth replaces the view shader's long light march. Cached sky visibility feeds the existing bounded ambient-visibility term. CQ3.4 still owns final two-octave controls, ground contribution, and the two Cinematic local cone samples.
- The runtime plan continues to distinguish the generator from the consumer: `preferredGenerator` remains `compute-image-store` or `fragment-slices`, while `activeRuntime` changes to `cache-sampling` only after at least one generated cascade is bound.
- CQ3.6 replaced the correctness-first per-draw regeneration bridge with the accepted independent cascade cadence, invalidation, and wind-reprojection policy.
- The live hidden-WGL lookup now validates center, overlap, far-only, outside-far, missing-near, and missing-far selection. The complete cloud shader compiles with the shared resolver, and the `862×683` production harness preserves terrain residency and P5.4 DDA while reporting `activeRuntime=cache-sampling`.

### CQ3.4 implementation record — 2026-07-29

- `PreviewCloudLightingShadingProfiles` freezes the two higher-order controls as `(extinction, phase-eccentricity, energy)`: octave one is `(0.50, 0.50, 0.55)` and octave two is `(0.25, 0.25, 0.30)`. The profile also owns the `2.25` post-sum energy clamp, `0.18` cached-sky floor, `0.11` ground-bounce energy, and `0.45` local-cone optical-depth scale.
- `vcSunScatterCq34` evaluates one direct term plus exactly those two higher orders. Cached sky visibility suppresses the higher orders independently, local cone optical depth affects only the direct boundary term, and total energy is clamped once after the complete sum. Low/Medium, repair, and outside-cache fallback retain the compatibility wrapper.
- Cache G is no longer cumulative optical depth with a different coefficient. Each light froxel evaluates two coarse upward-cone CQ2 density probes plus local density/cirrus overlap and publishes a layer-local hemispherical sky-visibility estimate. Fragment and compute still share the exact implementation; their R prefix remains cumulative while G publishes the current local visibility.
- Cumulus and cirrus both resolve the near/far cache. Cirrus receives cached sun optical depth and sky visibility but never receives ground bounce or local cone taps.
- Cinematic evaluates exactly two explicit-LOD density samples on occupied cumulus boundaries. Their distances are `0.42` and `0.88` of one near-cache XY texel; High advertises zero taps and performs no local density refinement.
- `PreviewCloudGroundBounceEstimator` samples at most 4,096 texels from the active terrain-top albedo, alpha-weights the result, converts sRGB to linear, and clamps malformed extremes. The shader multiplies this color by the fixed energy, upward-hemisphere weight, raw cached sky visibility, and a lower-altitude profile that reaches zero by `h=0.67`. It remains inside density integration and cannot bypass cloud opacity.
- Diagnostics report `lighting=cq3.4-two-octave+sky-visibility+ground-bounce` and the active local tap count. The app suite passes `584/584`; the live shader/cache test and `862×683` DDA/terrain production harness both remain green.

### CQ3.5 implementation record — 2026-07-29

- `PreviewCloudGroundTransmittanceProfiles` publishes the far cascade footprint as `R16F`: High uses the native `128×128` far footprint, while Cinematic uses `192×192` and resolves near/far data through the same outer-20% overlap contract as cloud-body lighting. Low, Medium, GLES/ANGLE, and a disabled cache allocate no publication target.
- `GlCloudGroundTransmittanceTarget` owns two textures and publishes transactionally. A draw completes into the inactive texture before generation IDs, the committed far transform, and the consumer-visible handle change. A stale source generation is never sampled.
- `genesis_cloud_ground_transmittance.frag` reconstructs the ground-facing point of each light column, samples cumulative optical depth, and publishes Beer-Lambert transmittance. Missing cache coverage, a grazing/degenerate light ray, invalid cache data, and non-finite output all resolve to `1.0` full sunlight.
- `common/cloud_ground_transmittance.glsl` is the shared terrain/fog lookup. It maps receivers through the committed snapped light basis, returns full sunlight for missing, out-of-range, or non-finite samples, clamps the valid signal to `[0,1]`, and feathers the outer two texels back to full sunlight so the far footprint cannot draw a square shadow boundary.
- The ground mesh path multiplies the published field into direct sun visibility only. Ambient/IBL and all non-ground subject passes are unchanged. The camera-froxel fragment and compute injectors apply the same field to direct in-scatter only; packed density and occupancy remain byte-for-byte on their prior paths. The integrated camera-froxel result is shared by volumetric fog and froxel god rays.
- Publication failure leaves the previous transaction private and consumers fall back to full sunlight. Allocation, publication, and diagnostic readback preserve the caller's read/draw framebuffers; publication additionally restores viewport, program, VAO, active texture, touched array bindings, masks, and raster state. Producer, lookup, and final fog-gate finite guards contain malformed input without changing density.
- Production validation caught an independent sampler-layout regression: the first terrain binding placed the new `sampler2D` on unit 8, already occupied by the albedo `sampler2DArray`. Active samplers of different types may not alias one texture unit, so streamed-terrain draws failed with `GL_INVALID_OPERATION` after CQ3.5 became active. The ground field now uses dedicated unit 12, outside material units 8–11, and a source contract pins that separation.
- The hidden-WGL fixed-density fixture compiles the publisher, verifies finite `R16F` readback against analytic Beer-Lambert transmittance, and checks publication-state restoration. Source contracts cover direct-only placement, untouched compatibility shaders, footprint fallback, finite guards, and sampler-unit separation. The complete app suite passes `588/588`, and the explicitly enabled `862×683` production harness retains P5.4 DDA, full terrain residency, post-residency recentering, cache publication, and visible terrain with both consumers active.

### CQ3.6 implementation record — 2026-07-29

- `PreviewCloudLightUpdateScheduler` is the deterministic lifecycle authority. Cinematic refreshes near every frame and far every fourth; High refreshes near every second frame and far every fourth. A generated cascade is never reused beyond four cloud draws, and a missing or expired cascade is selected independently.
- Initial generation, a material/profile hash change, movement beyond half the near span, a greater-than-`0.5°` material sun-direction change in one frame, and a light-reference-axis change immediately invalidate and rebuild both cascades. Resource/profile/context changes reconstruct the owner and therefore enter the same initial-generation path.
- Near and far generation are independent transactions. A selected compute failure invalidates only that cascade, demotes compute for the session, and retries the fragment path. If one cascade still fails, the valid peer remains available and the existing per-sample short march covers missing or out-of-range samples.
- Every generated cascade commits its own frame index, wind offset, transform, and generation ID. Cloud-body sampling reprojects the committed transform by the wrapped wind delta; the `R16F` ground publication does the same while publishing and again while terrain/fog consume it. Ground publication remains generation-ID transactional when only one cascade refreshes.
- The light basis remains shared by the two cache samplers. Gradual sun motion uses the last paired basis during single-cascade updates and adopts a newly constructed basis only when both cascades refresh, preventing near/far coordinate disagreement.
- `PreviewCloudLightScrollPlan` measures exact snapped XY texel displacement, depth-slice movement, overlap fraction, and full-refresh conditions. Scheduled frames reuse the prior texture and reproject its transform; due frames currently perform the coordinate section's permitted full regeneration. Physical overlap copies are reserved for CQ3.7 only if the captured cache-generation timing requires them, and remain mandatory before final performance acceptance when that gate is exceeded.
- Diagnostics now include lifecycle frame, selected cascades, invalidation reason, near/far age and cadence, generation path/failure, snapped scroll delta/reusable fraction, committed generation frames, and four-frame reuse policy. `Cloud Light Near` and `Cloud Light Far` own separate GPU timer queries and are included in the 240-frame cloud timing window without being folded invisibly into trace time.
- Scheduler, sun threshold, wind wrapping/reprojection, scroll-overlap, and timing-accounting tests pass. The complete app suite passes `596/596`; the explicitly enabled live WGL shader smoke and `862×683` production DDA/terrain lifecycle both pass with CQ3.6 active.

## Test matrix

### CPU/reference tests

- Stable light basis remains orthonormal and uses hysteresis near reference-axis transitions.
- Snapped origins change only at documented texel boundaries.
- Near/far overlap weights sum to one and are continuous.
- Update cadence and immediate invalidation triggers match the profile contract.
- World-to-cache transforms map fixed known positions to expected texels/slices.
- Quality values select the documented dimensions and schedules.

### Live GL tests

- Allocate, clear, generate, barrier, and sample both cache profiles.
- Compare compute and fragment fixed-density outputs within RG16F tolerance.
- Prove cumulative optical depth is monotonic along the light direction.
- Force compute failure and verify fragment fallback; force both and verify short-march fallback.
- Sample terrain transmittance and fog injection from the same fixed cache.
- Preserve CQ1 floating-point/compatibility cloud targets and cloud/scene depth ordering.

### Visual scenarios

- Dense cumulus tower shadowing its lower body.
- Broken cumulus with visible sunlit gaps.
- Noon, low sun, sunrise, sunset, and night transition.
- Camera below, inside, and above cloud layers.
- Slow sub-texel orbit and translation with frozen wind.
- Moving wind with scheduled cache updates.
- Terrain under a moving cloud shadow.
- Cirrus above cumulus without low-cloud ground-bounce contamination.
- Near/far cascade transition and positions outside far coverage.

### Quantitative correctness

- Compute and fragment cache optical depths differ by no more than two half-float ULPs in the fixed-density fixture.
- Optical depth never decreases along an individual light column beyond half-float tolerance.
- Frozen-wind camera motion below one snapped texel produces no cache-origin change and no measurable fixed-pixel lighting change.
- No visible line appears in the near/far overlap fixture.
- Terrain, fog, and cloud samples agree on fixed world-position transmittance within format tolerance.

## Performance gate

- High's amortized cache generation plus simplified view lighting does not exceed `1.25×` accepted CQ2 High cloud-lighting time.
- Cinematic cache timing is recorded separately; a missed schedule may defer far updates but may not silently reduce near dimensions.

## Exit criteria

- High and Cinematic use stable near/far cloud-light caches on supported desktop paths.
- Compute and fragment generation are equivalent within documented tolerance.
- Long-range self-shadowing, sky AO, controlled multiple scattering, and terrain shadows share one cache.
- Fog/god rays consume the same sun transmittance without changing view-depth ordering.
- GLES/ANGLE and all cache failures retain the current short light march.
- No cache swimming, invalid sampling, stale generation, or Phase 6/CQ1/CQ2 regression remains.
- Required visual artifacts and pass-scoped GPU timing evidence are complete.

## References

- Fabian Bauer, *Creating the Atmospheric World of Red Dead Redemption 2: A Complete and Integrated Solution*: <https://advances.realtimerendering.com/s2019/index.htm>
- Epic Games, *Volumetric Cloud Component — multiple scattering and Beer shadow maps*: <https://dev.epicgames.com/documentation/unreal-engine/volumetric-cloud-component-in-unreal-engine>
- Epic Games, *Volumetric Cloud Reference*: <https://dev.epicgames.com/documentation/en-us/unreal-engine/volumetric-clouds-reference?application_version=4.27>
