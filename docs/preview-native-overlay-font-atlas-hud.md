# Preview native overlay: font-atlas HUD (future)

Status: **planned** — not scheduled. Use after dirty-flag uploads + ~5 Hz HUD publish are in place.

## Problem

Native WGL preview HUD text is:

1. Formatted on the GL thread (`GlGpuTimingSnapshot.FormatHudLine`)
2. Rasterized on the UI thread via Avalonia `TextBlock` / `RenderTargetBitmap` (`GlPbrPreviewControl.RenderOverlayVisualToBitmap`)
3. Uploaded as premultiplied BGRA textures and drawn as screen-space quads (`GlNativeOverlayRenderer`)

Even with instance dirty-flags and throttled publishes, expanded multi-line timing HUDs still pay Avalonia layout/render and a full `TexImage`/`TexSubImage` when digits change. GPU Overlay time stays near zero; leftover cost is CPU-side raster + upload, plus UI-thread hitch risk.

## Goal

Draw FPS / GPU / CPU / debug overlay text **on the GL thread** with a glyph atlas (ImGui-style), so:

- No Avalonia bitmap path for these overlays
- No per-update `TexSubImage` of large HUD panels (only atlas upload once / on DPI change)
- Overlay CPU stay negligible with expanded timings enabled
- HDR scRGB paper-white scaling still works (`uHdrScRgbScale`)

Non-goals for v1: interactive UI widgets, rich markdown, full Unicode CJK in the atlas (latin + digits + punctuation is enough for timing HUDs).

## Proposed architecture

```
FormatHudLine / debug strings
        │
        ▼
GlOverlayTextLayout  ──► glyph quads (x,y,u,v,rgba) in a dynamic VBO/ring
        │
        ▼
GlOverlayFontAtlas   ──► R8/RG8 texture, baked from Consolas (or embedded TTF)
        │
        ▼
GlNativeOverlayRenderer (extended) ──► existing blend + HDR scale, multi-line panels
```

Keep `PreviewNativeWglOverlayBitmap` only as a fallback for debug blobs that must stay Avalonia-rendered, or remove once all panels migrate.

### Placement

| Piece | Suggested home |
| --- | --- |
| Atlas bake | `Rendering/OpenGL/GlOverlayFontAtlas.cs` (+ optional offline bake tool under `Tools`) |
| Layout | `GlOverlayTextBatch.cs` — wrap, align right stack, margin |
| Draw | Fold into `GlNativeOverlayRenderer` or sibling `GlNativeTextOverlayRenderer` |
| Wire-up | `OpenGlPreviewBackend.NativeOverlay.cs` — pass strings (or pre-split lines) instead of bitmaps |

UI (`GlPbrPreviewControl`) would stop calling `UpdateNativeWglOverlayBitmaps` for FPS/CPU once the GL path is authoritative; keep Avalonia overlays only if SW/composited preview paths still need them.

## Design choices to decide when implementing

1. **Bake source** — system Consolas (matches current look) vs embedded TTF (reproducible CI / GLES). Prefer embedded for GLES/ANGLE.
2. **Atlas lifetime** — bake at DPI / render-scale change; cache by `(fontPx, scale)`.
3. **Color / panel chrome** — replicate semi-transparent rounded pill in shader (SDF rounded rect) vs flat tint behind glyphs only.
4. **Thread** — layout+draw entirely on GL thread from latest HUD strings (already published ~5 Hz). Drop Avalonia property push for native WGL.
5. **Fallback** — capability flag if atlas bake fails; keep bitmap path behind `UseBitmapOverlayHud`.

## Implementation sketch

1. Bake monospace atlas (ASCII 32–126 + `\n` handling in layout).
2. Add shader: textured glyphs, premultiplied alpha, optional `uHdrScRgbScale`.
3. Layout right-stack panels (FPS then CPU) and optional left debug block; match current margins.
4. Switch `DrawNativeWglOverlayIfNeeded` to string inputs; retire FPS/CPU bitmap uploads.
5. Delete or fence Avalonia raster for those panels on the native WGL host.
6. Smoke test in `PreviewLiveGlSmokeTests`; visual check SDR + HDR present paths.

## Acceptance criteria

- Expanded GPU+CPU timing HUD on, looking into heavy clouds: **CPU Overlay ≲ 0.2 ms** steady-state (no upload churn).
- No Avalonia `RenderTargetBitmap` calls per HUD refresh on native WGL.
- Text remains readable at common DPI scales (100% / 125% / 150%).
- HDR present path: overlay not crushed against bright sky (paper-white scale preserved).
- Turning FPS overlay off still skips draw work.

## Risks

- Font licensing if bundling a TTF — confirm license before shipping.
- CJK / non-ASCII debug strings need a larger atlas or atlas paging.
- Duplicating UI look (rounded dark chip) takes a bit of shader work; ship flat background first if needed.

## Related

- Dirty-flag uploads: `GlNativeOverlayRenderer.OverlayTexture` (instance identity, no per-frame hash).
- Publish throttle: `GlTimingHudPublishGate` (~5 Hz), aligned with `MainWindowViewModel` camera-pose timer.
