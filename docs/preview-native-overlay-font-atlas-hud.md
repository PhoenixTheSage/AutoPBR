# Preview native overlay: font-atlas HUD (ImGui-style)

Status: **implemented** — UI publishes strings; GL Overlay owns layout + draw.

## Problem (solved)

Native WGL preview HUD text previously:

1. Formatted on the GL thread (`GlGpuTimingSnapshot.FormatHudLine`)
2. Rasterized on the UI thread via Avalonia `TextBlock` / `RenderTargetBitmap`
3. Uploaded as full-panel BGRA textures every time digits changed

That drove multi-ms **CPU Overlay** time (hash + `TexSubImage`) even though GPU Overlay stayed ~0.

## Architecture

```
UI thread                          GL Overlay pass
─────────                          ───────────────
HUD strings (~5 Hz gate)  ──►  dirty layout (strings/scale)
renderScale → BakeConsolas atlas (once) ──► atlas TexImage once
                                   dirty VBO upload
                                   draw panel + glyph quads
```

- [`GlOverlayFontAtlas`](../src/AutoPBR.App/Rendering/OpenGL/GlOverlayFontAtlas.cs) — Cascadia/Consolas ASCII bake on UI at 2× oversample in pixel-space (96 DPI); procedural atlas for tests
- [`GlOverlayTextLayout`](../src/AutoPBR.App/Rendering/OpenGL/GlOverlayTextLayout.cs) — strings → `{x,y,u,v,rgba}` quads (display-space cell metrics)
- [`GlNativeOverlayRenderer`](../src/AutoPBR.App/Rendering/OpenGL/GlNativeOverlayRenderer.cs) — atlas + mesh draw, HDR `uHdrScRgbScale`
- [`OpenGlPreviewBackend.NativeOverlay.cs`](../src/AutoPBR.App/Rendering/OpenGL/OpenGlPreviewBackend.NativeOverlay.cs) — `SetNativeWglOverlayTexts`
- Publish throttle: [`GlTimingHudPublishGate`](../src/AutoPBR.App/Rendering/OpenGL/GlTimingHudPublishGate.cs) (~5 Hz)

## Non-goals (still)

- Rounded SDF panel chrome (flat tint quads)
- CJK / full Unicode atlas
- Interactive widgets

## Acceptance

- Expanded GPU+CPU HUD: steady-state **CPU Overlay ≲ 0.2 ms**
- No Avalonia `RenderTargetBitmap` per HUD refresh on native WGL
- Layout/VBO only when strings or scale change
- Readable at common DPI scales (2× oversampled atlas, gutters, pixel-space bake); HDR paper-white preserved
- FPS overlay off ⇒ no overlay draw work
