# Volumetric froxels / unified medium (P3 design)

Design notes for replacing screen-space god rays with a single participating-medium model shared by clouds, fog, and light shafts.

## Goals

- Coherent extinction between detailed clouds, height fog, and light shafts
- Sun in-scatter uses the same **Mie phase** as `atmosphere.glsl` (g ≈ 0.76)
- God rays emerge from medium integration, not a separate radial blur
- Stable under orbit (temporal accumulation on the volume, not just 2D history)

## Medium contract

| Layer | Owns | Does not own |
|-------|------|--------------|
| **Froxel volume** | Dual-lobe atmospheric fill (valley mist + column haze), Mie sun shafts, ambient fill in-scatter, view transmittance for `scene * T + inscatter` | Detailed cloud body shape |
| **Detailed clouds** | Ray-marched cloud radiance/opacity, CQ3 light caches, CQ3.5 ground sun transmittance, shared view opacity/distance sheet | Coarse fog density inside froxels |
| **Mesh aerial fog** | Residual distance haze on terrain when volume fill is off or gated down | Primary valley fill when froxel god rays are active |

Horizon Fog / `AerialFogStrength` scales sky haze, residual mesh aerial fog, and froxel height-fog / ambient-fill gains together.

## Options

| Approach | Pros | Cons |
|----------|------|------|
| **Screen-space radial blur** (legacy fallback) | Simple, fast, works on ANGLE/GLES | Approximate occlusion; no true 3D medium |
| **Ray-marched slab** (clouds today) | Already in `volumetric_clouds.glsl` | Per-pixel cost; no froxel reuse |
| **Froxel grid (128³)** | Amortize march; inject sun once per froxel | Memory; sync with moving camera |
| **Half-res 3D texture (32×18×32)** | Lower memory than froxels | Coarse near camera |

**Recommendation for Genesis preview:** start with **camera-aligned froxel grid** at half resolution (e.g. 96×54×64 world units), one inject + one integrate pass, then composite over sky.

## Pipeline sketch

```
1. Inject density   — dual-lobe fill (+ analytic cloud fallback) into froxels
2. Inject sun       — shadow map × CQ3.5 ground transmittance × density
3. Integrate view   — Mie shafts + ambient fill; march sky to froxel far plane
4. Upsample         — sky-safe bilateral (no sky/geometry tap mixing)
5. Composite        — scene * T + inscatter after cloud color (Blend One, SrcAlpha)
6. Temporal         — reproject froxel / half-res history when stabilize-debug is off
```

## Shared medium API (implemented)

`common/volumetric_medium.glsl` / `volumetric_inject_density.glsl`:

- `vmHeightFogDensity` / `viHeightFogDensity` — **valley mist** (soft near-ground boost, no hard top) + **atmospheric column** (scale height ~64 so shafts still have medium at mountain/camera altitude)
- `vmMediumDensity` — fill plus a coarse analytic cloud fallback used only when the detailed cloud signal is unavailable
- `vmMediumTransmittance` — Beer–Lambert extinction
- `cloud_shared_transmittance.glsl` — resolved detailed-cloud opacity and representative view distance consumed by both full and lite froxel integration

Integrate applies **scatter gain only to Mie sun shafts**; ambient fill uses a separate low gain so looking downward from altitude does not milk into an opaque height-fog sea. Outputs **RGB in-scatter + A transmittance**. Empty buffers clear to `(0,0,0,1)`.

## Migration path

1. **P3 foundation** — `volumetric_medium.glsl` + god-ray cloud/shadow gates (done)
2. **Froxel inject pass** — write density + sun energy into 3D RT
3. **Replace genesis_godrays.frag** — sample froxel integrate instead of radial blur — [x] primary path; SS kept as fallback
4. **Share cloud transmittance** — detailed cloud pass publishes opacity/depth for shafts; high-frequency clouds are no longer duplicated in the coarse froxel grid — [x]
5. **Atmospheric fill** — dual-lobe density, sky marches, transmittance composite — [x]
6. **Remove legacy** — delete screen-space god-ray passes when parity reached

## ANGLE / GLES constraints

See **[gles-angle-shader-guide.md](gles-angle-shader-guide.md)** for fragment-shader pitfalls (no early `return`, single `FragColor` write, sampler precisions, include splits, FBO feedback).

- Prefer **RGBA16F** froxel slices; fall back to RGBA8 if unavailable
- Limit froxel Z to 32–48 steps on ES
- Use **fixed loop bounds** in all march/inject shaders
- Shadow compare samplers already ES-compatible
- Lite integrate mirrors full path (sky marches + ambient fill + transmittance alpha)

## Performance budget (1080p preview target)

| Pass | Target |
|------|--------|
| Froxel inject (half res) | &lt; 1.5 ms |
| Froxel integrate | &lt; 1.0 ms |
| Composite + temporal | &lt; 0.5 ms |

Current half-res radial blur + upsample ≈ 0.8–1.2 ms; froxels should match before cutover.

## Open items

- [x] Froxel grid placement — depth anchored to world cloud slab (`ResolveVolumeHalfExtent`); fill strength extends far plane up to 128
- [x] Shadow-map sampling in inject pass (`grShadowGate` / cascaded in `genesis_volume_inject.frag`)
- [x] Sharp volume inject shadows (1-tap compare, near-cascade preference, reduced bias)
- [x] Dual-lobe atmospheric fill in `vmMediumDensity` / `viHeightFogDensity`
- [x] Temporal accumulation on froxel integrate (half-res history + reprojection in `genesis_volume_integrate.frag`)
- [x] Detailed cloud opacity/depth integration with cloud-first, foreground-safe shaft composition
- [x] Phase 6 cloud/scene occlusion contract: historical planet-horizon clipping, conservative half-resolution depth footprints, and per-tap full-resolution scene/cloud distance rejection. CQ3.9 supersedes planet clipping with a flat finite-distance layer while preserving scene depth.
- [x] Phase 6 morphology pass: historical 72k-radius far-horizon curvature, altitude-local cumulus domes/flat bases, and detached branching cirrus fibers. CQ3.9 removes curvature for the continuous world.
- [x] Open-sky integrate + sky-safe upsample (no dark frustum from sky/geometry bilateral bleed)
- [x] Transmittance composite (`scene * T + inscatter`) for volume path
- [x] Screen-space canopy refine after upsample — replaced sun-cone depth march with cutout foliage mask in `TaaSignal.A` (`genesis_godrays_canopy_refine.frag`)
- [x] HDR-correct present encode — integrate/upsample stay scene-referred linear (desktop RGBA16F); shaft composite uses `cpEncodeShaftRadiance` (HDR: soft-knee 0.35 at 0.55× paper-white so open fill does not veil; SDR: display-referred soft-knee). Ambient fill is shadow-only; isotropic phase floor kept low (0.06).
- [x] Screen-space beam layer — depth-occlusion + cloud-opacity march; TOD strength/tint (moon steely-blue, night thins terrain shafts, raises sky wash floor); froxel inject stores scalar sun energy and integrate applies TOD `uLightColor` so day/dusk/dawn/night tint permeates terrain fog; structured wash gate.
- [ ] Cutover flag: `EnableFroxelGodRays` vs screen-space fallback
