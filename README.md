## AutoPBR (C#)

**C# PBR Generator inspired by [LaChips/PBRify](https://github.com/LaChips/PBRify)** – generates a PBR-ready layer for a Minecraft resource pack by creating:

- **Specular maps** (`*_s.png`) in **LabPBR format**  
  - R = perceptual smoothness  
  - G = F0 / metalness (dielectrics capped, metals boosted)  
  - B = porosity / subsurface  
  - A = emissive (255 = off)
- **Normal maps** (`*_n.png`) from the diffuse texture using an enhanced Sobel + VC-filter gradient, with **height baked into the alpha channel** for POM.

The tool takes an existing pack (`.zip` or `.jar`) and produces a **separate PBR `.zip` layer** containing only the generated normals/speculars plus `pack.mcmeta`/`pack.png` (when present).

LaChips PBRify used PySimpleGUI (now paid for some uses). This project is a **.NET 8 / C# project** with:

- A **cross‑platform UI** built on **Avalonia** (`AutoPBR.App`)
- A **CLI tool** (`AutoPBR.Cli`)
- A shared **core library** (`AutoPBR.Core`) containing the conversion engine

---

### Requirements

- .NET SDK **8.0+**

---

### CLI usage

Run:

```bash
dotnet run --project src/AutoPBR.Cli -- \
  "path/to/input_pack.zip" \
  "path/to/output_pack_PBR.zip" \
  --fast \
  --normal 1.5 \
  --height 0.12 \
  --ignore-plants
```

**Options:**

- `--fast` – use a fast RGB distance for specular matching
- `--normal <1..3>` – normal intensity (default `1`)
- `--height <0.01..0.5>` – height intensity exponent (default `0.12`)
- `--ignore-plants` – skips plant textures (uses a vanilla-style plant ignore list)

**Input:** `.zip` or `.jar` (JAR is treated as a zip).  
**Output:** always a **`.zip` PBR layer**; drop it above the base pack in the game’s resource pack stack.

`textures_data.json` (color→specular mapping) is embedded as content and copied to `Data/textures_data.json` next to the executables.  
It includes both **per-texture rules** and a rich `"*"` **fallback rule set** that covers common materials (grass, dirt, wood, stone, sand, wool, ores, etc.) so that almost all textures get a non-zero specular response.

---

### Desktop app (Avalonia)

The main window lets you:

- **Pick input pack (.zip or .jar)** and **output folder**
- Adjust generation strengths:
  - **Normal strength**
  - **Height strength**
  - **Smoothness scale** (dielectrics, LabPBR R)
  - **Metallic boost** (metals, LabPBR R)
  - **Porosity bias** (LabPBR B)
- Toggle:
  - **Fast specular** (approximate color distance)
  - **Ignore plants**
  - **Experimental parallel extraction** (custom parallel zip reader)
- Choose which texture groups to process:
  - **Blocks**, **Items**, **Armor (entity)**, **Particles (specular only)**
- **Load textures** to:
  - list all discovered texture keys (including mod namespaces)
  - mark specific ones to **exclude** from processing
- See a **live progress bar**, **status text**, and **log output**
- **Cancel** a running conversion

The UI names the output `.zip` after the input pack, with a `_PBR` suffix (e.g. `MyPack.zip` → `MyPack_PBR.zip`, `MyPack.jar` → `MyPack.zip`).

---

### Implementation notes

- Image processing uses **SixLabors.ImageSharp**.
- Color space and ΔE2000 distance use **Colourful**.
- Conversion is heavily **parallelized**:
  - Zip extract/pack and texture conversion use **max(1, CPU−2)** worker threads.
  - Specular, normal, and height generation run in parallel across textures.
- **Specular generation**:
  - Starts from a color→specular rule set (`textures_data.json`) with both per-texture entries and a `"*"` fallback cluster.
  - Uses a **metal vs dielectric** split (name-based metal detection for common mod metals, LabPBR metal presets, dielectric F0 cap).
  - Applies **luminance and edge-based heuristics** (VC-filter-style multi-orientation edge detector) to adjust smoothness and porosity.
- **Normal and height generation**:
  - Greyscale pre-processing uses a light unsharp mask to better match perceived “form”.
  - Edges are detected with a **VC-filter-inspired multi-orientation gradient** to reduce Sobel blind spots while preserving normal direction.
  - Height is generated from diffuse brightness and written into the **alpha channel of the normal map**.
- **File discovery**:
  - Scans all asset namespaces under `assets/<namespace>/textures` (vanilla, OptiFine, mod IDs, etc.).
  - Also processes **OptiFine-style CTM** textures under `assets/<namespace>/optifine/ctm/**`.
  - Supports blocks, items, armor/entity textures, and particle textures (specular-only).

