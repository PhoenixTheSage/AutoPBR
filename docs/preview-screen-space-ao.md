# Preview screen-space AO (SSAO + GTAO)

Genesis applies selectable screen-space ambient occlusion to **lit opaque scene color** after the forward pass and before volumetric clouds / god rays / TAA.

## Pipeline

1. Scene capture writes color, TAA signal, **view-space normals** (Color2, alpha = geometry mask), and depth.
2. Half-res (or 2/3 for Cinematic) **SSAO** or **GTAO** samples depth + normals.
3. Separable **bilateral** blur (depth-aware).
4. Optional **temporal** reuse when the quality profile requests it and preview TAA is active.
5. **Composite** multiplies lit color by `mix(1, ao, strength)` and presents (HDR/SDR encode). Sky / cleared pixels (normal.a = 0) stay unoccluded.

POM contact AO (`EnableParallaxAo`) still runs inside `genesis.frag` and stacks with screen-space AO.

## Settings

| Setting | Role |
|---------|------|
| `EnableScreenSpaceAo` | Master toggle |
| `PreviewAoMode` | Auto / SSAO / GTAO |
| `AoStrength` | Multiply weight (default 0.85) |
| `AoRadius` | View-space sample radius |
| `AoPower` | Visibility power curve |
| `AoDebugView` | 0 off, 1 raw AO, 2 strength-applied |

**Auto** technique: Low/Medium → SSAO; High/Cinematic → GTAO. Profiles also set resolution scale, sample/slice counts, blur passes, and temporal.

## Capability gate

Requires `MaxColorAttachments >= 3` and `MaxDrawBuffers >= 3` (`PreviewGlCapabilities.CanUseScreenSpaceAo`). Scene capture falls back to color+TAA-only if a three-attachment FBO is incomplete; AO then stays inactive.

## Shaders

| File | Role |
|------|------|
| `common/screen_space_ao_common.glsl` | Depth/normal helpers, noise, multi-bounce |
| `genesis_ssao.frag` | Hemisphere SSAO |
| `genesis_gtao.frag` | Multi-slice horizon GTAO |
| `genesis_ao_bilateral.frag` | Separable bilateral |
| `genesis_ao_temporal.frag` | History reprojection |
| `genesis_ao_composite.frag` | Present × AO |

## Out of scope

Ambient-only split lighting, bent normals / SSGI, AO on clouds or god rays.
