# AGENTS.md

## Cursor Cloud specific instructions

AutoPBR is a **local, offline** .NET 8 tool that generates a LabPBR overlay pack
(`*_s.png` specular + `*_n.png` normals) from a Minecraft resource pack. There is
no backend/database/web service. The two runnable products are the **CLI**
(`src/AutoPBR.Cli`, headless) and the **Avalonia desktop App** (`src/AutoPBR.App`,
GUI). Standard build/test/run commands live in `README.md` and
`.github/workflows/dotnet.yml`; use the `.slnf` solution filters they reference
(`AutoPBR.Core.slnf` for Core+CLI+tools, `AutoPBR.App.slnf` for the App).

### Environment
- The .NET 8 SDK is installed at `~/.dotnet` and put on `PATH` via `~/.bashrc`
  (`dotnet --version` should print `8.0.100`). The update script only restores
  NuGet packages + local tools; the SDK itself persists in the VM snapshot.
- Local tool `csharpier` is used for formatting: `dotnet tool restore` then
  `dotnet csharpier check .`. It is **not** part of CI, and a clean checkout of
  `main` currently reports pre-existing formatting diffs — do not mass-reformat.

### Non-obvious gotchas
- **ONNX Runtime does not work on Linux here.** The projects reference
  `Microsoft.ML.OnnxRuntime.Managed` only; the native runtime is Windows-only and
  user-supplied (see `src/AutoPBR.Core/Data/native/README.md`). On Linux, ONNX
  session creation throws (`type initializer for 'NativeMethods'`), so **ML
  specular, DeepBump GPU normals, and MiniLM semantic tagging silently fall back /
  are disabled**. Conversions still succeed via the heuristic path — this is
  expected and matches CI. Do not treat these ONNX log lines as failures.
- **App test failures are pre-existing.** `dotnet test AutoPBR.App.Tests` has ~5
  failing source/shader string-assertion tests on a clean `main`; these also fail
  in CI (`build-app` is red, `build-core` is green). Not an environment problem.
- **Running the App headless:** it is `OutputType=WinExe` but runs fine on
  Linux/X11 via Avalonia. A display is available at `DISPLAY=:1` (1920x1200) with
  mesa software GL. Launch with `DISPLAY=:1 dotnet run --project src/AutoPBR.App`.
  It opens **maximized filling the screen**, so the bottom action bar (Cancel /
  **Convert**) sits at the extreme bottom-right corner (~y=1180 at 1920x1200).
- **App conversion flow:** the `Convert` button stays disabled until an **Output
  folder** is set in the **Settings** tab (`OutputZipPath` auto-derives to
  `<output>/<packname>_PBR.zip`). Set the input pack path in the top input box,
  set the output folder in Settings, then click Convert (it auto-scans if needed).
- The CLI needs `Data/textures_data.json` (copied to output on build); ONNX models
  under `Data/ONNX-AI/**` are bundled in-repo (Git LFS) — no downloads required.
