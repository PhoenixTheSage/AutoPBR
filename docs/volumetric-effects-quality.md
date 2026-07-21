# Volumetric effects quality (Genesis preview)

Notes on god rays, atmospheric sky LUT, and paired effects for believable outdoor lighting.

## Sun alignment (fixed)

Sun disc bloom, god-ray march target, and the sun billboard share **`PreviewSunScreenProjection`** (world position at `eye + (-lightDir) * 85`, projected with the live `view * proj`).

The sky pass now:

- Builds the camera **before** drawing the atmosphere.
- Samples the sky LUT by **per-pixel view ray** (not raw screen UV).
- Places additive bloom with an **aspect-correct disc** at `uSunUv`.

## Roadmap (follow-ups)

Each phase is a self-contained slice. Complete P0 before P1; later phases can overlap once dependencies are met.

---

### P0 — Alignment & stability

**Goal:** Sun bloom, god rays, and billboard stay locked; regressions are caught in CI.

| # | Task | Owner files | Done |
|---|------|-------------|------|
| 0.1 | Shared sun screen projection (billboard / sky / god rays) | `PreviewSunScreenProjection.cs` | [x] |
| 0.2 | Sky LUT sampled by per-pixel view ray | `atmo_sky.frag`, `PassScene.cs` | [x] |
| 0.3 | Aspect-correct sun disc & god-ray cone | `atmo_sky.frag`, `genesis_godrays.frag` | [x] |
| 0.4 | **Debug overlay:** dev-only crosshair + disc at projected `uSunUv` | `OpenGlPreviewBackend.Debug.cs`, toggle in settings | [x] |
| 0.5 | **Golden projection test:** fixed eye/view/proj/yaw/pitch → expected `sunUv` ± ε | `PreviewSunScreenProjectionTests.cs` | [x] |
| 0.6 | **Regression screenshot hook** (optional): capture preview with known light pose | `PreviewVolumetricRegressionFixtures.cs`, `PreviewSunScreenProjectionRegressionTests.cs` | [x] |

**Exit criteria:** Overlay matches billboard at all aspect ratios and orbit angles; unit test green.

#### P0.6 — Regression fixtures & manual sign-off

CI golden tests live in `tests/AutoPBR.App.Tests/PreviewVolumetricRegressionFixtures.cs` and
`PreviewSunScreenProjectionRegressionTests.cs`. Each fixture fixes camera eye, light yaw/pitch,
aspect, and cone scale; tests assert sun UV, disc/cone radii, and a stable fingerprint.

**Refresh golden values** (after intentional projection changes):

```bash
dotnet test tests/AutoPBR.App.Tests/AutoPBR.App.Tests.csproj --filter PrintGoldenProjectionValues
```

Remove `Skip` on `PreviewVolumetricRegressionGoldenCaptureTests.PrintGoldenProjectionValues` first.

**Manual visual sign-off** (before release / after sky or volume shader edits):

| Check | Fixture IDs | Pass criteria |
|-------|-------------|---------------|
| Sun alignment | `default-day-16x9`, `orbit-45-16x9` | Debug overlay crosshair on sun disc; billboard overlaps |
| Aspect ratios | `aspect-21x9`, `aspect-4x3` | Sun stays in viewport; no stretched disc |
| Day/night cycle | `noon-12h`, `sunset-18h`, `midnight-0h` | Noon = blue sky; sunset = warm horizon; midnight = dark + stars |
| God-ray cone | `cone-wide` | Shafts wider than default; no full-screen white bloom |
| Grey dome | all + clouds on | No camera-attached grey hemisphere |
| Volume pairing | default + clouds + god rays | Rays attenuate behind clouds; aerial haze on distant geometry |

Pose parameters: call `PreviewVolumetricRegressionFixtures.ManualCaptureChecklist()` from a test or read fixture IDs in the test file.

---

### P1 — God-ray quality (screen-space)

**Goal:** Sharper, cheaper shafts that respect occluders; user can tune cone width.

| # | Task | Owner files | Done |
|---|------|-------------|------|
| 1.1 | **Depth-aware radial blur** — replace 64-step march with sun-UV radial samples + depth weights | `genesis_godrays.frag` | [x] |
| 1.2 | **Half-res render** of god-ray pass | `GlColorRenderTarget`, `GodRays.cs` | [x] |
| 1.3 | **Bilateral / depth upsample** to full res | `genesis_godrays_upsample.frag` | [x] |
| 1.4 | **Occluder weighting** — attenuate where depth discontinuity or low sky luminance | god-ray frag | [x] |
| 1.5 | **Temporal reprojection** — history buffer + clamp (camera motion) | `OpenGlPreviewBackend.GodRays.cs`, history FBO | [x] |
| 1.6 | **UI:** `GodRayConeScale` (shaft width) + wire to `PreviewSunScreenProjection` | settings, VM, `MainWindow.axaml` | [x] |
| 1.7 | Tune defaults after 1.1–1.3 (strength / density / decay) | `PreviewRenderSettings` | [x] |

**Exit criteria:** Visible shafts at 0.35 strength with sun behind subject; ≤ half current GPU cost at 1080p preview.

**Depends on:** P0 complete.

---

### P2 — Atmosphere & pairing

**Goal:** Rays and sky feel integrated with clouds, distance, and shadows.

| # | Task | Owner files | Done |
|---|------|-------------|------|
| 2.1 | **Cloud density attenuates god rays** — sample cloud march or precomputed density along shaft | `godray_integration.glsl`, god-ray blur | [x] |
| 2.2 | **Height fog / aerial perspective** — distance-based scatter tint on geometry (not just IBL) | `genesis.frag` (`uAerialFogStrength`) | [x] |
| 2.3 | **Epipolar god-ray sampling** — march along epipolar lines through sun | `genesis_godrays.frag` (epipolar T) | [x] |
| 2.4 | **Shadow-map-aware shafts** — multiply ray energy by directional shadow visibility | shadow map in god-ray blur | [x] |
| 2.5 | **Sun-only bloom audit** — confirm LUT knee doesn’t fight `SunDiscStrength`; document tuning | `atmo_skyview.frag`, `atmo_sky.frag` | [x] |
| 2.6 | **Silver lining** on cloud edges toward sun (optional polish) | `volumetric_clouds.glsl` | [x] |

**Exit criteria:** Rays dim behind clouds and in shadow; distant terrain reads hazy; no full-sky white blowout at default settings.

**Depends on:** P1.1 recommended (shared sun UV + depth pipeline).

---

### P3 — Physical volumetrics

**Goal:** Single participating-medium model for fog, clouds, and light shafts.

| # | Task | Owner files | Done |
|---|------|-------------|------|
| 3.1 | **Design doc:** froxel grid vs ray-marched slab (resolution, cost, ANGLE/GLES) | `docs/volumetric-froxels.md` | [x] |
| 3.2 | **Froxel or slab volume** — inject / accumulate sun in-scatter | `OpenGlPreviewBackend.Volume.cs`, `genesis_volume_*.frag` | [x] |
| 3.3 | **Mie phase** along view toward sun (match `atmosphere.glsl` g=0.76) | `genesis_volume_integrate.frag` | [x] |
| 3.4 | **Unify clouds + rays** into one medium (or shared density texture) | `volumetric_medium.glsl`, unified post composite | [x] |
| 3.5 | **Noise + temporal accumulation** on volume (dither + TAA-style clamp) | froxel jitter + god-ray history upsample | [x] |
| 3.6 | **Remove legacy screen-space god rays** once parity reached | volume-only path; `genesis_godrays.frag` deleted | [x] |

**Exit criteria:** One toggle drives coherent outdoor volumetrics; shafts stable under orbit; acceptable frame time on integrated GPU.

**Depends on:** P2.1, P2.2 strongly recommended.

---

### Suggested execution order

```
P0.4 → P0.5 → P1.1 → P1.6 → P1.2 → P1.3 → P1.4 → P2.1 → P2.2 → P2.4 → P1.5 → P2.3 → P3.*
```

Quick wins first: debug overlay (P0.4), projection test (P0.5), radial blur (P1.1), cone scale UI (P1.6).

---

### P0 — Alignment & stability (summary table)

| Item | Status | Notes |
|------|--------|-------|
| Shared sun screen projection | Done | `PreviewSunScreenProjection.cs` |
| Sky LUT by view direction | Done | `atmo_sky.frag` |
| Aspect-correct disc & cone | Done | sky + god rays |
| Debug overlay | Done | P0.4 — `ShowSunProjectionDebug` |
| Projection unit test | Done | P0.5 — `PreviewSunScreenProjectionTests.cs` |

## Volumetric cloud quality roadmap

The proposed desktop cloud-quality program is tracked in the
[Volumetric Cloud Quality Roadmap](volumetric-cloud-quality-roadmap.md). It sequences precision and
reconstruction, density textures, a dedicated cloud-light cache, and the optional Cinematic sparse
backend as CQ1 → CQ2 → CQ3 → CQ4 while preserving the current GLES/ANGLE shell renderer.

## Current pipeline

1. **Sky-view LUT** (`atmo_skyview.frag`) — precomputed in-scatter from sun direction, turbidity, and exposure.
2. **Sky composite** (`atmo_sky.frag`) — full-screen LUT sample + optional sun-disc bloom.
3. **Detailed clouds** — curved cumulus shell plus a separate wind-sheared cirrus ice layer. Cloud rays are clipped by opaque scene depth and the solid preview planet; the half-resolution upsample repeats the packed cloud-distance test at full resolution.
4. **God rays** — froxel fog inject + Mie integrate consuming resolved detailed-cloud opacity/depth → half-res history → bilateral/temporal upsample. Detailed clouds composite first, then cloud-aware shaft radiance is added so foreground shafts remain visible while samples behind clouds are attenuated.

### Phase 6 visual-correctness contract

- Cumulus follows the WMO morphology target: detached dense masses, a shared nearly horizontal condensation base, and vertically organized domes/towers. Weather type now controls horizontal footprint, vertical lobe frequency, and upper-level drift; detail erosion remains concentrated at the silhouette so the interior does not break into foam islands.
- Cirrus follows the WMO morphology target: detached delicate patches with a fibrous or silky character. A broad warped moisture field gates two differently oriented filament families, producing forks, hooks, and feathered edges instead of uniform parallel streaks. High quality samples twice through the thin ice shell; Beer-Lambert opacity remains intentionally lower than cumulus.
- The artistic planet radius is 72,000 world units. Surface drop is about 1.74 units at 500 units and 6.94 units at 1,000 units, while the default cloud base still rolls over at a roughly 1,610-unit geometric horizon. Curvature is therefore a far-distance cue rather than a visible near-scene arc.
- The planet sphere is an occluder. A very narrow angular visibility feather lets clouds recede a few pixels behind the geometric horizon before disappearing, leaving the sky pass's below-horizon atmospheric fog visible without a hard cutout.
- Opaque scene depth remains authoritative during both tracing and full-resolution reconstruction. The half-resolution trace conservatively keeps the farthest sample in each reconstruction footprint, while the full-resolution pass compares every cloud tap against the destination pixel's reconstructed scene distance. This prevents terrain from erasing adjacent sky clouds without allowing cloud bleed over the ground mesh or nearby subjects.

## Sky LUT bloom (tuning)

White skies usually come from the product of:

- Large scatter coefficients × linear sun intensity × transmittance near the sun.
- Additive sun disc on the composite pass.
- God rays sampling the same bright pixels (now avoided via procedural sun emitter).

**User controls (Render tab → Atmosphere LUT):**

| Control | Role |
|--------|------|
| Sky exposure | Master LUT brightness; lower to kill bloom |
| Sun intensity | Scattering energy (sqrt-scaled in shader) |
| Sun disc bloom | Additive glare around the disc only |
| Turbidity / horizon falloff | Haze and horizon rolloff |

Shader-side: soft knee compression before sRGB encode on LUT build and sky draw.

## God-ray improvements (roadmap)

### Near term (screen-space)

- **Depth-aware radial blur** — single pass from sun UV with depth weights (faster than 64-step march).
- **Bilateral / depth upsample** — render rays at half-res, upsample with depth edge preservation.
- **Occluder mask** — use scene luminance + depth discontinuities to weight shafts (trees, buildings).
- **Temporal reprojection** — stabilize flicker when the camera moves (jittered march + history clamp).

### Medium term (quality)

- **Epipolar sampling** — march along epipolar lines through the sun for fewer samples.
- **Phase function** — Henyey–Greenstein along view toward sun (match atmosphere Mie).
- **Noise + TAA** — dither march steps; accumulate over frames.

### Long term (physical)

- **Froxel or ray-marched volumetrics** — single medium for fog + god rays + clouds.
- **Shadow map sampling** — light shafts only where cascades are lit.
- **Cloud-aware shafts** — attenuate march inside cloud density (pairs with volumetric clouds).

## Effects that pair well

| Effect | Why it helps |
|--------|----------------|
| **Atmospheric sky LUT** | Consistent sun color and horizon; god rays should not re-sample blown sky |
| **Aerial perspective / height fog** | Darkens distant geometry so shafts read in depth |
| **Volumetric clouds** | Occludes and tints rays; silver lining at cloud edges |
| **Controlled sun disc bloom** | Separate from scatter intensity; avoids washing the whole sky |
| **Exposure / tone mapping** | Global headroom before additive passes |
| **Contact shadows + cascades** | Grounding; shafts need dark occluders to be visible |
| **IBL from same LUT** | Materials and background share the same sun |

## Quality presets (P4 — performance & cleanup)

| Preset | Froxel divisor | Slices | Cloud quality | Pass temporal (base) | Preview TAA |
|--------|----------------|--------|---------------|----------------------|-------------|
| Low | 8 (min 24 px) | 12 | 0 | off | off |
| Medium (default) | 4 (min 32 px) | 20 | 1 | volume 0.35 / upsample 0.45 / cloud 0.42 | 0.55 |
| High | 3 (min 48 px) | 24 | 2 | volume 0.42 / upsample 0.55 / cloud 0.55 | 0.72 |

When **Preview TAA** is enabled, per-pass temporal weights are multiplied by **0.5** so froxel/cloud/god-ray histories do not double-smear noise the final TAA pass already stabilizes. With god rays on, TAA uses scene depth for motion rejection (screen velocity from reprojection).

**UI:** Render tab → Volumetric effects — god rays toggle, clouds toggle, quality combo, strength slider.

**Profiling:** `LogVolumetricTiming` logs inject/integrate ms when debug mode is on (exceeds documented budget).

Legacy screen-space radial blur (`genesis_godrays.frag`) removed; volume path is the only god-ray implementation.

## Screen-space volumetric clouds (preview)

Froxels own fog and god rays only; clouds are ray-marched through a curved spherical shell in `genesis_clouds.frag` and clipped to reconstructed opaque-scene depth.

| Layer | Model |
|-------|--------|
| Cumulus shell | Weather-map coverage → altitude-local, weather-type-shaped Perlin-Worley lobes → coherent vapor body → edge-focused detail erosion |
| Cirrus | Thin curved shell above cumulus; warped detached moisture patches plus branching primary/secondary ice filaments; two shell samples on High |
| Lighting | Multi-scatter sun + sky-LUT ambient; exposure matched to `atmo_sky.frag` |
| Integration | Always half-res march → cloud-specific temporal resolve → depth-aware upsample; scene-depth-clipped shell segment; optional final preview TAA |
| Compositing | Detailed clouds publish opacity/depth to froxel integration; clouds composite first, followed by cloud-aware additive shafts |

**Temporal ownership (independent histories)**

| System | When active | Reprojection anchor | Disabled when |
|--------|-------------|---------------------|---------------|
| Cloud temporal resolve | Clouds on, Medium/High | Packed representative cloud distance + camera motion + cumulus/cirrus wind advection | Low quality, cloud debug view, or Cloud debug “Disable temporal” |
| God-ray upsample | God rays on, stabilize off | Scene geometry depth | God-ray stabilize debug |
| Preview TAA | Preview TAA on, Medium+ | Scene geometry depth only (sky passthrough) | Low quality, toggle off |

Cloud history stays independent when **god rays + clouds** are both on. The resolve rejects history by representative-distance disagreement, cloud-layer identity, motion, viewport borders, luminance/coverage changes, and a current-frame 3×3 YCoCg neighborhood clamp. Large camera cuts and material cloud-setting changes invalidate the history on the CPU. Final preview TAA still reduces the cloud history weight by half to avoid stacked persistence.

The trace target publishes premultiplied radiance/opacity plus a second portable RGBA8 attachment containing packed cloud distance and cumulus/cirrus identity. Wind-aware reprojection follows both layers rather than pinning history to the screen. An eight-frame low-discrepancy jitter sequence replaces the unstructured phase increment.

**Render tab → Cloud layer:** density, coverage, layer height/thickness, feature scale, wind, cirrus strength. **Cloud debug** expander: coverage/density overlays, temporal off, march step override, freeze wind.

**Tuning tips**

- **Coverage ~0.5–0.8** for broken cumulus; **density ~0.25–0.4** at noon.
- **Cirrus 0–0.5** — upper wisps; set 0 to hide the high layer.
- Restart the preview (or reload GPU resources) after shader/noise-bake changes so procedural textures regenerate.

**Horizon / gaps**

- Analytic inner/outer sphere intersections give every ray a finite cloud interval below, inside, and above the layer; no camera-elevation discard or horizon-lifetime fade is used.
- Opaque scene depth clips the interval rather than discarding all non-sky pixels, so geometry is correctly obscured while the camera is inside cloud.
- Coarse weather mips provide a conservative empty-space test before detailed 3D density and sun-light evaluation.
- Skylight fill along clear ray segments keeps gaps between puffs bright in daylight instead of reading as night void.
- Stars fade with `dayAmt` in `atmo_sky.frag` so clear sky gaps at dusk do not show a full star field.

## Recommended defaults for preview

- Atmospheric sky on; **sky exposure ~0.85**, **sun intensity ~10**, **sun disc ~0.35**.
- God rays on (medium quality); **strength ~0.45** with sun behind or beside the subject.
- Enable clouds for occlusion; **coverage ~0.7**, **density ~0.3**, **cirrus ~0.35** as a starting point; lower density if rays compete with cloud brightness.

## Polish (P5)

| # | Task | Status |
|---|------|--------|
| 5.1 | Auto time-of-day animation (`AnimateTimeOfDay`, `TimeOfDaySpeed`) | [x] |
| 5.2 | Explicit moon UV (`PreviewSunScreenProjection.ComputeMoon`, debug overlay) | [x] |
| 5.3 | i18n for atmosphere / volumetric / time-of-day strings (9 locales) | [x] |
| 5.4 | GPU framebuffer fingerprint hook (`CapturePreviewFingerprint`, debug log) | [x] |
| 5.5 | Cascaded shadow sampling in volume inject (near + far maps) | [x] |

**GPU regression:** enable debug mode, open 3D preview — diagnostics log `Frame fingerprint` every ~2 s. Golden projection + moon UV tests remain the CI gate; remove `Skip` on `PrintGoldenProjectionValues` to refresh CPU goldens.

## Anti-patterns

- Sampling scene color at the sun UV for ray energy when the sky LUT is overexposed.
- Full-sky radial blur without a sun cone (reads as global bloom).
- High sun intensity + high god-ray strength + high sun disc (triple blowout).
