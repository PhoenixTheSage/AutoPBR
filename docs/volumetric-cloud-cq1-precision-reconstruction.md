# CQ1 — Volumetric cloud precision and reconstruction

**Status:** Proposed  
**Roadmap:** [Volumetric cloud quality roadmap](volumetric-cloud-quality-roadmap.md)  
**Depends on:** Green build and recorded Phase 6 baseline  
**Required by:** [CQ2 density](volumetric-cloud-cq2-density-textures.md), [CQ3 lighting](volumetric-cloud-cq3-lighting-cache.md), [CQ4 sparse volume](volumetric-cloud-cq4-sparse-voxel-sdf.md)

## Goal

Make cloud sampling precision and reconstruction worthy of higher-quality density and lighting. CQ1 removes early 8-bit/sRGB quantization, improves temporal convergence, adds an opt-in Cinematic trace tier, and repairs sub-pixel cloud boundaries without weakening scene-depth ordering.

Success means:

- gradients and dim cloud interiors do not band or pulse after temporal accumulation;
- cloud color is integrated and filtered in linear HDR space;
- High converges more cleanly without increasing its trace resolution;
- Cinematic resolves noticeably finer silhouettes and internal structure;
- camera motion, wind, layer transitions, horizon clipping, terrain, and subjects remain stable;
- GLES/ANGLE keeps the established RGBA8 shell behavior.

## Non-goals

- Do not change the procedural density model or bundled cloud noise assets; that is CQ2.
- Do not add cloud shadow volumes, ground cloud shadows, or new multiple-scattering models; that is CQ3.
- Do not add sparse voxels, cloud templates, or a new density backend; that is CQ4.
- Do not merge detailed cloud density into the existing fog/god-ray froxel grid.
- Do not expose independent UI controls for trace scale, temporal moments, edge repair, or render formats. They belong to quality profiles and debug diagnostics.

## Baseline

The current detailed-cloud path:

1. traces every preset at half viewport resolution;
2. writes premultiplied, already exposed/soft-kneed/sRGB cloud color plus opacity to an RGBA8 target;
3. writes representative distance and cloud kind to a second RGBA8 target, with distance packed into two channels;
4. reprojects history by representative distance and layer-specific wind;
5. clips history against a current 3×3 YCoCg neighborhood;
6. performs a four-tap, scene-depth-aware full-resolution upsample;
7. composites the resolved cloud result and publishes opacity/distance to fog and god-ray integration.

This is a strong correctness baseline, but 8-bit nonlinear color is not an appropriate working representation for repeated temporal filtering. Half-resolution reconstruction also has no path to recover thin boundaries that never receive a valid source sample.

## Formats and dimensions: quality profiles

Add `Cinematic = 3` to the existing volumetric quality scale. Stored values `0`, `1`, and `2` remain Low, Medium, and High.

| Preset | Trace scale | Default view steps | Working color | Metadata | Temporal | Edge repair |
|--------|-------------|--------------------|---------------|----------|----------|-------------|
| Low | 0.5 | 16 | Compatibility RGBA8 | Packed RGBA8 | Existing compatibility behavior | Off |
| Medium | 0.5 | 24 | RGBA16F on supported desktop; RGBA8 fallback | Direct metadata on FP path; packed fallback | Reprojection + neighborhood clipping | Off |
| High | 0.5 | 32 | RGBA16F | Direct metadata | STBN + moments + neighborhood clipping | Off |
| Cinematic | 2/3 | 48 | RGBA16F | Direct metadata | STBN + moments + neighborhood clipping | On |

The user march-step override replaces only `Default view steps`, clamped to the existing safety maximum. It does not change trace scale, target formats, temporal mode, or edge-repair policy.

## Render-format contract

Introduce an internal cloud render-format profile selected once during cloud resource initialization and re-evaluated after context recreation.

### Floating-point desktop profile

| Attachment | Format | Meaning | Sampling |
|------------|--------|---------|----------|
| Cloud color | `RGBA16F` | Linear premultiplied radiance in RGB; optical opacity in A | Linear |
| Cloud metadata | `RG32F` | R = representative world-ray distance; G = cloud kind/valid state | Nearest |
| Temporal moments | `RG16F` | First and second linear-luminance moments | Linear |

Metadata G values are:

- `-1.0`: invalid/no cloud sample;
- `0.0`: cumulus shell segment without a representative density hit;
- `0.5`: representative cumulus density;
- `1.0`: representative cirrus density.

Distance is zero when metadata is invalid. Do not interpolate representative distance across samples or across byte/float carries. Kind comparison uses the existing tolerance rather than exact floating-point equality.

The moment attachment is optional only when fewer than three color attachments are supported or its framebuffer combination is incomplete. Failure to allocate moments retains the floating-point color/metadata path and falls back to neighborhood-only clipping.

### Compatibility profile

Keep the current two RGBA8 attachments and perspective-like two-channel distance packing. This profile remains mandatory for GLES/ANGLE and is also the final allocation fallback on desktop.

## Capability fallback

At initialization:

1. Try floating-point color + direct metadata + moments.
2. If incomplete, try floating-point color + direct metadata without moments.
3. If incomplete, restore the existing RGBA8 color/packed-metadata target.
4. If all target allocation fails, retain the existing analytic fog/cloud fallback and report the cloud target failure.

The selected profile must be applied consistently to trace, temporal source/history, resolve, upsample, history copy, clear values, texture filtering, readback tests, and shader defines. Never mix a packed producer with a direct-distance consumer.

## Architecture and data flow

The cloud trace shader must output linear radiance without `linearToSrgb`, exposure, or soft-knee compression. Opacity remains Beer-Lambert-derived and premultiplies the linear radiance.

The temporal pass:

- reads and blends linear premultiplied radiance;
- computes luminance moments from unpremultiplied linear radiance when opacity is safely nonzero;
- keeps opacity filtering independent of radiance moments;
- clamps history in linear YCoCg or an equivalent linear decorrelated space;
- never tone maps or encodes sRGB.

The upsample and edge-repair stages also remain linear. Exposure, soft knee, and sRGB conversion happen exactly once in the final cloud-composite shader immediately before writing to the same encoded destination used by the existing sky/scene composite.

Shared cloud transmittance and representative distance continue to be consumed before color encoding, so fog/god-ray ordering does not change.

## Spatiotemporal blue noise

Bundle a deterministic `128×128×64` single-channel R8 spatiotemporal blue-noise asset. The generator and asset must be deterministic and versioned; CQ1 does not depend on network retrieval at build or runtime.

Sampling contract:

- XY indexes are `gl_FragCoord mod 128` in trace-target pixels.
- Z is the cloud frame index mod 64.
- Camera cuts and history invalidation reset accumulation confidence but do not pin Z permanently to zero.
- Freeze-wind freezes density advection, not the STBN sequence. A separate temporal-disable debug option is the mechanism for a fully static sample.
- Use the STBN scalar to jitter view-ray step placement. Derive a decorrelated second scalar by a fixed spatial/temporal permutation when a second stochastic decision is required.
- Low and the GLES compatibility path may retain the existing lightweight sequence to avoid adding a mandatory compatibility texture.

The noise texture uses nearest filtering and repeat wrapping. Missing/corrupt STBN assets fall back to the existing deterministic cloud jitter and emit one diagnostic.

## Temporal moments and confidence

For valid current samples, write:

- moment X = current unpremultiplied linear luminance;
- moment Y = luminance squared.

The temporal pass reprojects moment history using the same representative distance, cloud kind, camera matrices, and wind delta as color history. Apply the existing depth, kind, motion, border, coverage, and reactive rejection before updating moments.

Compute variance as `max(secondMoment - firstMoment², 0)`. Clamp reprojected history luminance to the current 3×3 neighborhood mean plus/minus a configurable sigma band:

- High: `1.5σ`, minimum band `0.015` linear luminance;
- Cinematic: `2.0σ`, minimum band `0.01`;
- fall back to current min/max neighborhood clipping when moments are unavailable or invalid.

History confidence starts at zero after invalidation and approaches one over eight accepted frames. Maximum effective cloud-history weights remain bounded by the existing rejection logic:

- Medium: current profile weight;
- High: `0.72` before final-preview-TAA scaling;
- Cinematic: `0.84` before final-preview-TAA scaling.

Final preview TAA continues to reduce per-pass cloud history to prevent stacked temporal persistence. Do not raise both histories to their independent maxima in the same frame.

## Cinematic trace resolution

Cinematic allocates trace, resolve, and history targets at:

```text
traceWidth  = ceil(viewportWidth  * 2 / 3)
traceHeight = ceil(viewportHeight * 2 / 3)
```

Round each dimension up to an even number to keep reconstruction footprints stable. Low/Medium/High remain `max(1, viewport / 2)`.

Any trace-size, format, viewport, quality, or backend transition invalidates history before drawing into the newly sized target.

## Full-resolution edge repair

Edge repair is Cinematic-only and runs after temporal resolve but before final full-resolution composition.

### Edge classification

At each full-resolution pixel, inspect the four source reconstruction taps. Mark the pixel for repair when at least one condition holds:

- source opacity range exceeds `0.08`;
- valid representative-distance range exceeds `max(0.75, nearestDistance × 0.01)`;
- valid/invalid cloud metadata differs across the footprint;
- cumulus/cirrus kind differs across the footprint;
- the normal four-tap reconstruction has total valid weight below `0.75` while the shell intersects the destination ray.

Scene-depth and planet visibility are evaluated before repair. Pixels hidden by terrain, subjects, or the planet are never retraced.

### Repair trace

- Retrace only the classified destination pixel.
- Use the same shell intersections, scene clipping, density implementation, wind, lighting interface, and STBN frame as the primary trace.
- Use exactly eight stratified samples centered around the representative boundary interval, not eight samples across the entire shell.
- The boundary interval is the source taps' nearest valid representative distance plus/minus one primary fine-step length, clipped to the shell and scene segment.
- Composite the repaired result over the ordinary reconstruction using repair confidence derived from valid sample coverage.
- Publish repaired opacity and representative distance to downstream cloud data so fog/god-ray consumers see the same boundary.

If edge-repair shader compilation or target allocation fails, disable repair for the session, keep the two-thirds-resolution Cinematic trace, and log once.

## History invalidation

Invalidate color, metadata, moment, and repair history together when any of these changes:

- viewport or trace dimensions;
- render-format profile;
- quality or backend;
- camera cut or large camera translation;
- cloud layer height/thickness, density, coverage, volume scale, cirrus strength, or debug mode;
- temporal-disable or freeze-wind mode;
- shape/detail/weather asset generation;
- CQ3 lighting-cache generation once that phase exists;
- CQ4 clipmap generation once that phase exists.

Changing sun direction or intensity does not need to discard density metadata, but it must reduce color confidence for at least one frame. The implementation may keep metadata history only if color/moment validity remains explicitly separated; otherwise invalidate the full cloud history for correctness.

## Failure handling and diagnostics

Diagnostics must report:

- selected color, metadata, and moment formats;
- trace resolution and scale;
- STBN availability;
- temporal moments enabled/disabled and reason;
- edge repair enabled/disabled and reason;
- fallback from floating-point to compatibility targets;
- cloud history invalidation reason in debug logging.

Framebuffer incompleteness, shader failure, invalid texture handles, or GL errors use the existing cloud circuit-breaker policy. A failure in an optional CQ1 feature steps down that feature before disabling detailed clouds.

## Implementation milestones

- [ ] CQ1.0: Add fixed baseline fixtures and GPU timing capture.
- [ ] CQ1.1: Add `Cinematic = 3`, profile selection, persistence, localization, and diagnostics.
- [ ] CQ1.2: Generalize the cloud temporal target to capability-selected attachment formats.
- [ ] CQ1.3: Add direct-distance shader ABI and packed compatibility shader ABI.
- [ ] CQ1.4: Move trace, temporal, and upsample color to linear HDR; encode only during final composition.
- [ ] CQ1.5: Add deterministic STBN generation/loading and march jitter.
- [ ] CQ1.6: Add moment allocation, reprojection, variance clipping, and confidence.
- [ ] CQ1.7: Add two-thirds Cinematic sizing and history invalidation.
- [ ] CQ1.8: Add classification and bounded full-resolution edge repair.
- [ ] CQ1.9: Complete automated, live-GL, artifact, and performance acceptance.

## Test matrix

### CPU and source-contract tests

- Quality values `0..3` map to the documented profiles and old persisted settings retain their meanings.
- Trace-size calculation produces even two-thirds dimensions for Cinematic and half dimensions otherwise.
- Format selection follows the required fallback order.
- Shader variants agree on packed versus direct metadata semantics.
- The floating-point shader path contains no early exposure, soft knee, or sRGB conversion.
- STBN generation is deterministic by hash and has the required dimensions.
- Every documented history-invalidating setting contributes to the history key or explicit invalidation path.

### Live GL tests

- Create, clear, draw, copy, and sample every supported attachment profile on hidden WGL.
- Prove that direct distance preserves front/behind opaque-scene ordering at near and horizon distances.
- Render a linear HDR value above one through trace/history and verify it is not clamped before final composition.
- Exercise moments enabled, moments-disabled fallback, and RGBA8 compatibility.
- Compile and draw the edge-repair path with no GL errors.
- Retain the existing cloud/scene depth and cloud-shared-froxel smoke tests.

### Visual scenarios

- Thin bright cloud edge against blue zenith.
- Dim cloud interior at noon and twilight.
- Cirrus over broken cumulus.
- Camera orbit with moving and frozen wind.
- Below/inside/above layer transitions.
- Terrain and entity silhouettes, including a near-ground camera.
- Grazing horizon with the Phase 6 fade-behind behavior.
- Resize and Low/Medium/High/Cinematic transitions.

### Quantitative correctness

- Static High temporal luminance standard deviation after warm-up is lower than the Phase 6 baseline without increasing visible ghost length.
- Cinematic resolves strictly more valid boundary pixels than High in the fixed thin-cloud fixture.
- Floating-point history retains monotonic HDR radiance through repeated copy/resolve.
- Scene-depth ordering differs by zero pixels from the accepted Phase 6 occlusion fixture.

## Performance gate

- High GPU cloud time does not exceed `1.15×` the Phase 6 High median on the same reference capture.
- Cinematic cloud time is recorded separately and must remain within the roadmap's interactive preview budget; it is not allowed to weaken High to compensate.

## Exit criteria

- Floating-point desktop targets and packed GLES/ANGLE targets both work through trace, temporal, upsample, and final composition.
- Linear HDR survives until the single final encode.
- High uses stable STBN/moment reconstruction; Cinematic uses two-thirds trace plus bounded edge repair.
- All Phase 6 height, horizon, planet, terrain, and subject ordering fixtures remain correct.
- Optional feature failures step down cleanly and are diagnosable.
- Required tests, fixed-scene artifacts, and GPU timing evidence are complete.

## References

- NVIDIA, *Rendering in Real Time with Spatiotemporal Blue Noise Textures*: <https://developer.nvidia.com/blog/rendering-in-real-time-with-spatiotemporal-blue-noise-textures-part-1/>
- Epic Games, *Volumetric Cloud Component*: <https://dev.epicgames.com/documentation/unreal-engine/volumetric-cloud-component-in-unreal-engine>
