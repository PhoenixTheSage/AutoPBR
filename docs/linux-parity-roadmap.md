# Linux parity roadmap

Win64 WGL / D3D11 / DXGI HDR / ONNX-GPU paths stay behind `OperatingSystem.IsWindows()` gates. Linux work must not change those paths.

## Locked defaults

| Topic | Policy |
|-------|--------|
| HDR | Windows-only (`PreviewHdrDxgiSwapchain` / DXGI scRGB) |
| ML | linux-x64 ONNX Runtime **CPU** in baseline; CUDA EP later |
| Preview (Phase 1) | Avalonia `OpenGlControlBase` always available; never blank when OpenGL 4 is on |
| Preview (Phase 2) | Linux EGL desktop-GL sidecar + async PBO present (no DX interop) |
| Window chrome | Custom undecorated chrome on Windows; system decorations on Linux |

## Phases

0. Guardrails — visibility helpers, configurator tests, this doc  
1. Working baseline — bootstrap, chrome, fonts, Minecraft roots, ORT CPU, xvfb smoke  
2. EGL desktop OpenGL 4.x sidecar  
3. Hardening — path-key tests, `linux-x64` publish notes, optional CUDA  

## Win64 safety

- Register `Win32PlatformOptions` only on Windows.
- Native WGL host only when `RequestedDesktopGl4 && IsWindows()`.
- Do not rewrite WGL owner thread or NV_DX interop for shared code unless a tiny present helper is required.

## Publish (linux-x64)

```bash
dotnet publish src/AutoPBR.App/AutoPBR.App.csproj -c Release -r linux-x64 --self-contained false
```

Runtime needs a display (X11/Wayland), `libEGL` / `libGL`, and fonts. Satellite resources land under `lang/<culture>/` via the existing non-Windows `mv` publish target.

CLI:

```bash
dotnet publish src/AutoPBR.Cli/AutoPBR.Cli.csproj -c Release -r linux-x64 --self-contained false
```

## Linux CUDA EP (Phase 3 follow-on)

Not enabled in the baseline. When adding:

1. Document redistributable `linux-x64` CUDA/cuDNN `.so` layout parallel to [`src/AutoPBR.Core/Data/native/README.md`](../src/AutoPBR.Core/Data/native/README.md) under e.g. `runtimes/linux-x64/native`.
2. Gate load with `OperatingSystem.IsLinux()` + `NativeLibrary` probe; never change `runtimes/win-x64/native` search order.
3. Keep CPU `Microsoft.ML.OnnxRuntime` RID assets as the default fallback.

## Cloud-agent checklist

- [ ] App launches; native WM title bar on Linux
- [ ] 3D preview draws with OpenGL 4 toggle on or off (EGL sidecar when Mesa/driver allows; else Avalonia GL)
- [ ] HUD/debug monospace uses bundled Cascadia Mono
- [ ] ML CPU (SpecLab / DeepBump / MiniLM) loads
- [ ] Minecraft assets resolve from `~/.minecraft` when present
- [ ] Soft GPU (llvmpipe) demotes features via capability gates (expected)
