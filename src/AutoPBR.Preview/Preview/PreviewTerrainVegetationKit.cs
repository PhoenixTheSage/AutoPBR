namespace AutoPBR.Preview;

/// <summary>Resolved LabPBR maps for one wood species (matching log + leaves) or cactus.</summary>
public sealed class PreviewTerrainVegetationSpeciesKit
{
    public required PreviewTerrainTreeSpecies Species { get; init; }

    public required string TextureStem { get; init; }

    public required int LogSlot { get; init; }

    public required int LeavesOrTopSlot { get; init; }

    public required PreviewTextureMaps LogMaps { get; init; }

    public required PreviewTextureMaps LeavesOrTopMaps { get; init; }

    public required string LogArchivePath { get; init; }

    public required string LeavesOrTopArchivePath { get; init; }

    public PreviewTextureMaps? LogTopMaps { get; init; }

    public string? LogTopArchivePath { get; init; }

    public int? LogTopSlot { get; init; }

    public bool IsCactus => Species == PreviewTerrainTreeSpecies.Cactus;
}

/// <summary>
/// Stage-terrain vegetation materials discovered from Pack → Minecraft install.
/// Empty kit means decorations must not be generated.
/// </summary>
public sealed class PreviewTerrainVegetationKit
{
    public required string Identity { get; init; }

    public required IReadOnlyList<PreviewTerrainVegetationSpeciesKit> Species { get; init; }

    /// <summary>Total ground material slots including terrain base + vegetation.</summary>
    public required int TotalSlotCount { get; init; }

    /// <summary>Per-slot cutout flags aligned with <see cref="TotalSlotCount"/>.</summary>
    public required bool[] CutoutBySlot { get; init; }

    /// <summary>Block-model JSON templates for stamping decorations (may be empty if bake failed).</summary>
    public PreviewTerrainVegetationModelTemplates ModelTemplates { get; init; } =
        PreviewTerrainVegetationModelTemplates.Empty;

    public bool HasAny => Species.Count > 0;

    public bool TryGet(PreviewTerrainTreeSpecies species, out PreviewTerrainVegetationSpeciesKit kit)
    {
        foreach (var candidate in Species)
        {
            if (candidate.Species == species)
            {
                kit = candidate;
                return true;
            }
        }

        kit = null!;
        return false;
    }

    public static PreviewTerrainVegetationKit Empty { get; } = new()
    {
        Identity = "veg-empty",
        Species = [],
        TotalSlotCount = PreviewTerrainGrassSlots.MaxCount,
        CutoutBySlot = CreateTerrainOnlyCutoutMask(),
        ModelTemplates = PreviewTerrainVegetationModelTemplates.Empty,
    };

    public PreviewTerrainVegetationBakePlan ToBakePlan()
    {
        if (!HasAny)
        {
            return PreviewTerrainVegetationBakePlan.Empty;
        }

        var entries = new PreviewTerrainVegetationBakeEntry[Species.Count];
        for (var i = 0; i < Species.Count; i++)
        {
            var s = Species[i];
            entries[i] = new PreviewTerrainVegetationBakeEntry(
                s.Species,
                s.LogSlot,
                s.LeavesOrTopSlot,
                s.LogTopSlot);
        }

        return new PreviewTerrainVegetationBakePlan(
            Identity,
            entries,
            TotalSlotCount,
            ModelTemplates.HasAny ? ModelTemplates : PreviewTerrainVegetationModelTemplates.Empty);
    }

    private static bool[] CreateTerrainOnlyCutoutMask()
    {
        var mask = new bool[PreviewTerrainGrassSlots.MaxCount];
        mask[PreviewTerrainGrassSlots.Overlay] = true;
        return mask;
    }
}

/// <summary>CPU bake slot indices for vegetation decorations (no texture payloads).</summary>
public readonly record struct PreviewTerrainVegetationBakeEntry(
    PreviewTerrainTreeSpecies Species,
    int LogSlot,
    int LeavesOrTopSlot,
    int? LogTopSlot);

/// <summary>Lightweight plan passed into Full chunk bakes.</summary>
public sealed class PreviewTerrainVegetationBakePlan(
    string identity,
    PreviewTerrainVegetationBakeEntry[] entries,
    int totalSlotCount,
    PreviewTerrainVegetationModelTemplates? modelTemplates = null)
{
    public string Identity { get; } = identity;

    public PreviewTerrainVegetationBakeEntry[] Entries { get; } = entries;

    public int TotalSlotCount { get; } = Math.Max(PreviewTerrainGrassSlots.MaxCount, totalSlotCount);

    public PreviewTerrainVegetationModelTemplates ModelTemplates { get; } = modelTemplates is { HasAny: true }
        ? modelTemplates
        : PreviewTerrainVegetationModelTemplates.Empty;

    public bool HasAny => Entries.Length > 0;

    public static PreviewTerrainVegetationBakePlan Empty { get; } =
        new("veg-empty", [], PreviewTerrainGrassSlots.MaxCount);

    public bool TryGet(PreviewTerrainTreeSpecies species, out PreviewTerrainVegetationBakeEntry entry)
    {
        foreach (var candidate in Entries)
        {
            if (candidate.Species == species)
            {
                entry = candidate;
                return true;
            }
        }

        entry = default;
        return false;
    }

    public bool TryResolveWood(
        PreviewTerrainTreeSpecies preferred,
        out PreviewTerrainVegetationBakeEntry entry)
    {
        foreach (var candidate in PreviewTerrainTreeSpeciesRules.FallbackChain(preferred))
        {
            if (TryGet(candidate, out entry))
            {
                return true;
            }
        }

        // Last resort: any non-cactus wood present.
        foreach (var candidate in Entries)
        {
            if (candidate.Species != PreviewTerrainTreeSpecies.Cactus)
            {
                entry = candidate;
                return true;
            }
        }

        entry = default;
        return false;
    }
}
