## AutoPBR (C#)

**C# Generator inspired by [LaChips/PBRify](https://github.com/LaChips/PBRify)** – generates a PBR-ready version of a Minecraft resource pack by creating:

- **Specular maps** (`*_s.png`) from diffuse colors using a color→specular lookup table (`textures_data.json`)
- **Normal maps** (`*_n.png`) from the diffuse texture via a Sobel-based normal map generator


LaChips PBRify used PySimpleGUI (now paid for some uses). This project is a **.NET 8 / C# port** with:

- A **cross‑platform UI** built on **Avalonia** (`AutoPBR.App`)
- A **CLI tool** (`AutoPBR.Cli`)
- A shared **core library** (`AutoPBR.Core`) containing the conversion engine

---

### Requirements

- .NET SDK **8.0+**

---

### Projects

- `src/AutoPBR.Core` – core conversion logic
- `src/AutoPBR.Cli` – command‑line front‑end
- `src/AutoPBR.App` – Avalonia desktop app

---

### CLI usage

Run:

```bash
dotnet run --project src/AutoPBR.Cli -- \
  "path/to/input_pack.zip" \
  "path/to/output_pack.zip" \
  --fast \
  --normal 1.5 \
  --height 0.12 \
  --ignore-plants
```

**Options:**

- `--fast` – use a fast RGB distance for specular matching (like original "fast specular")
- `--normal <1..3>` – normal intensity (default `1`)
- `--height <0.01..0.5>` – height intensity exponent (default `0.12`)
- `--ignore-plants` – skips plant textures (matches the original plant ignore list)

`textures_data.json` (color→specular mapping from the original project) is embedded as content and copied to `Data/textures_data.json` next to the executables.

The main window lets you:

- **Pick input pack (.zip)** and **output folder**
- Adjust **normal** and **height** strengths
- Toggle **fast specular** and **ignore plants**
- **Load textures** to:
  - list all discovered block/item textures
  - mark specific ones to **exclude** from processing
- See a **live progress bar** and **log output**
- **Cancel** a running conversion

The output `.zip` is named after the input pack, with `_PBR_fast` or `_PBR_slow` appended.

---

### Implementation notes

- Image processing uses **SixLabors.ImageSharp**.
- Color space and ΔE2000 distance use **Colourful**.
- The initial logic closely mirrors the original Python:
  - file discovery rules (folders, exclusions)
  - specular lookup behavior
  - normal and height generation formulas


