# Volumetric froxels / unified medium (P3 design)

Design notes for replacing screen-space god rays with a single participating-medium model shared by clouds, fog, and light shafts.

## Goals

- Coherent extinction between detailed clouds, height fog, and light shafts
- Sun in-scatter uses the same **Mie phase** as `atmosphere.glsl` (g ≈ 0.76)
- God rays emerge from medium integration, not a separate radial blur
- Stable under orbit (temporal accumulation on the volume, not just 2D history)

## Options

| Approach | Pros | Cons |
|----------|------|------|
| **Screen-space radial blur** (current P1–P2) | Simple, fast, works on ANGLE/GLES | Approximate occlusion; no true 3D medium |
| **Ray-marched slab** (clouds today) | Already in `volumetric_clouds.glsl` | Per-pixel cost; no froxel reuse |
| **Froxel grid (128³)** | Amortize march; inject sun once per froxel | Memory; sync with moving camera |
| **Half-res 3D texture (32×18×32)** | Lower memory than froxels | Coarse near camera |

**Recommendation for Genesis preview:** start with **camera-aligned froxel grid** at half resolution (e.g. 96×54×64 world units), one inject + one integrate pass, then composite over sky.

## Pipeline sketch

```
1. Inject density   — vmMediumDensity(worldPos) from volumetric_medium.glsl
2. Inject sun       — along -lightDir, multiply by shadow map visibility
3. Integrate view   — front-to-back in-scatter + transmittance per froxel
4. Composite        — trilinear sample froxel buffer along view ray
5. Temporal         — reproject froxel accumulator (3D or 2D slice history)
```

## Shared medium API (implemented)

`common/volumetric_medium.glsl`:

- `vmMediumDensity` — height fog plus a coarse analytic cloud fallback used only when the detailed cloud signal is unavailable
- `vmMediumTransmittance` — Beer–Lambert extinction
- `cloud_shared_transmittance.glsl` — resolved detailed-cloud opacity and representative view distance consumed by both full and lite froxel integration

God rays (P2.1) already call this during screen-space march as a stepping-stone.

## Migration path

1. **P3 foundation** — `volumetric_medium.glsl` + god-ray cloud/shadow gates (done)
2. **Froxel inject pass** — write density + sun energy into 3D RT
3. **Replace genesis_godrays.frag** — sample froxel integrate instead of radial blur — [x] deleted; upsample/composite only
4. **Share cloud transmittance** — detailed cloud pass publishes opacity/depth for shafts; high-frequency clouds are no longer duplicated in the coarse froxel grid — [x]
5. **Remove legacy** — delete screen-space god-ray passes when parity reached

## ANGLE / GLES constraints

See **[gles-angle-shader-guide.md](gles-angle-shader-guide.md)** for fragment-shader pitfalls (no early `return`, single `FragColor` write, sampler precisions, include splits, FBO feedback).

- Prefer **RGBA16F** froxel slices; fall back to RGBA8 if unavailable
- Limit froxel Z to 32–48 steps on ES
- Use **fixed loop bounds** in all march/inject shaders
- Shadow compare samplers already ES-compatible

## Performance budget (1080p preview target)

| Pass | Target |
|------|--------|
| Froxel inject (half res) | &lt; 1.5 ms |
| Froxel integrate | &lt; 1.0 ms |
| Composite + temporal | &lt; 0.5 ms |

Current half-res radial blur + upsample ≈ 0.8–1.2 ms; froxels should match before cutover.

## Open items

- [x] Froxel grid placement — depth anchored to world cloud slab (`ResolveVolumeHalfExtent`)
- [x] Shadow-map sampling in inject pass (`grShadowGate` in `genesis_volume_inject.frag`)
- [x] Height fog slab in `vmMediumDensity` (world-anchored ground mist)
- [x] Temporal accumulation on froxel integrate (half-res history + reprojection in `genesis_volume_integrate.frag`)
- [x] Detailed cloud opacity/depth integration with cloud-first, foreground-safe shaft composition
- [x] Phase 6 cloud/scene occlusion contract: planet horizon clipping, conservative half-resolution depth footprints, and per-tap full-resolution scene/cloud distance rejection
- [x] Phase 6 morphology pass: subtle 72k-radius far-horizon curvature, altitude-local cumulus domes/flat bases, and detached branching cirrus fibers
- [ ] Cascaded shadow sampling in inject pass
- [ ] Cutover flag: `EnableFroxelGodRays` vs screen-space fallback
