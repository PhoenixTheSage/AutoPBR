# Volumetric cloud implementation handoff

**Status:** Active  
**Last updated:** 2026-07-26  
**Branch/base:** `main` at `1b68d734` before the working-tree changes  
**Roadmap:** [Volumetric cloud quality roadmap](volumetric-cloud-quality-roadmap.md)  
**Active specification:** [CQ1 precision and reconstruction](volumetric-cloud-cq1-precision-reconstruction.md)

## Current checkpoint

CQ1 is in progress. The prerequisite build/test gate and CQ1.0 through CQ1.8 are complete.

The working tree intentionally contains the CQ1.1–CQ1.8 implementation and prerequisite test-maintenance changes; it is not committed by this handoff.

## Completed

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

## Compatibility state

- Low, Medium, and High persisted numerical values are unchanged.
- Low uses the half-resolution packed `RGBA8` cloud target/history on every backend.
- Medium and High remain half resolution on supported desktop GL. Medium uses `RGBA16F + RG32F`; High adds `RG16F` moment history when at least three attachments/draw buffers and framebuffer completeness are available.
- Cinematic uses even-rounded two-thirds resolution and the High three-attachment format when supported.
- High/Cinematic moment allocation failure steps down to `RGBA16F + RG32F` with neighborhood clipping before considering the packed `RGBA8` fallback.
- GLES/ANGLE continues through the existing source-adapted shell renderer and packed metadata path.
- Trace, temporal history, and upsampling now operate on linear premultiplied cloud radiance. Low/GLES stores that linear signal in `RGBA8`, so values above one saturate on the compatibility path by design.
- Exposure, soft-knee shaping, and SDR display encoding occur once during final cloud composition for both the primary upsample and fallback composite.
- High and Cinematic use the bundled STBN march-placement volume on desktop; Low, Medium, GLES/ANGLE, and asset/upload failures use the existing lightweight jitter.
- Desktop Cinematic uses the bounded full-resolution repair target when its shader and FP16/direct-metadata framebuffer are available. Optional failure retains the two-thirds CQ1.7 source.
- The Phase 6 height safety and opaque-scene depth contracts are unchanged. The planet-horizon fade now operates on integrated premultiplied layer output while preserving full far-side occlusion.

## Validation record

| Check | Result |
|-------|--------|
| App solution build | Pass |
| CQ1.8 classifier/shader/shell/sizing focused tests | 73/73 pass |
| Complete app test assembly | 521/521 pass |
| Hidden-WGL shader compile, R8 STBN upload, packed/two-attachment FP/three-attachment moment profiles, odd-viewport sizing transitions, CQ1.8 full-resolution repair allocation/draw/readback, depth ABIs, HDR retention, initial moment write, accepted-history accumulation | 1/1 pass |
| `git diff --check` | Pass |
| Initial fixed-camera cloud capture | Accepted user capture |
| Initial displayed GPU timing | `1.1 ms` total; `0.3 ms` cloud trace |

Existing analyzer warnings remain in unrelated HDR, terrain, desktop-WGL, and test code; they do not fail the build or tests. The CQ1 files introduce no reported analyzer warning.

## Next implementation task

1. Capture a user runtime stability/visual check for CQ1.8. On supported desktop Cinematic, first-use diagnostics should include `cloudFormat=rgba16f-rg32f-rg16f`, `trace=<even width>x<even height>@0.667`, `moments=RG16F enabled`, `edgeRepair=active full-res <width>x<height>, 8-step`, and confidence progressing toward `8/8`.
2. Reproduce both supplied grazing-horizon cameras and confirm that the hard horizontal band is gone, the slight fade-behind remains, terrain/subjects still occlude clouds, and fully far-side clouds do not leak through the planet.
3. Begin CQ1.9 acceptance:
   - capture controlled Low/Medium/High/Cinematic fixtures after at least 32 warm-up frames;
   - record median and 95th-percentile pass timings over at least 240 frames;
   - verify ground, inside-layer, above-layer, grazing-horizon, cirrus-heavy, dense, and broken-cumulus cases;
   - exercise desktop floating-point, optional-repair fallback, and GLES/ANGLE compatibility paths.
4. If repair cost or coverage needs tuning, add a low-frequency diagnostic readback/counter for repaired-pixel fraction without making the production path synchronously read the GPU each frame.
