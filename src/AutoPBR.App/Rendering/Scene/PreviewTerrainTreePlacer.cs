
namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Deterministic biome-aware tree / cactus placement for Full and distant LOD terrain chunks.
/// Skips the subject flat pad and requires a vegetation bake plan with textures.
/// </summary>
public static class PreviewTerrainTreePlacer
{
    public readonly record struct Placement(
        int RootX,
        int RootZ,
        int SurfaceHeight,
        PreviewTerrainTreeSpecies Species,
        PreviewTerrainVegetationBakeEntry Materials,
        int TrunkHeight,
        int VariantSalt);

    /// <param name="placementStep">
    /// Candidate grid step in meters. Full chunks use 1; combined LOD sections pass their
    /// sample step so coarse rings stay tractable while still carrying vegetation.
    /// </param>
    public static List<Placement> CollectForChunk(
        int cx0,
        int cz0,
        int cx1,
        int cz1,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        in PreviewTerrainWorldGenSettings worldGen,
        PreviewTerrainVegetationBakePlan plan,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int placementStep = 1)
    {
        var result = new List<Placement>(8);
        if (!plan.HasAny)
        {
            return result;
        }

        placementStep = Math.Max(1, placementStep);
        var gen = PreviewTerrainWorldGenSettings.Resolve(worldGen);
        var occupied = new HashSet<long>();
        flatPadHalfExtent = Math.Max(0, flatPadHalfExtent);

        for (var z = cz0; z < cz1; z += placementStep)
        {
            for (var x = cx0; x < cx1; x += placementStep)
            {
                var chebyshev = Math.Max(Math.Abs(x), Math.Abs(z));
                if (chebyshev <= flatPadHalfExtent)
                {
                    continue;
                }

                var column = columnAt(x, z);
                if (!TrySelectSpecies(column, x, z, gen, plan, out var species, out var materials))
                {
                    continue;
                }

                var chance = PreviewTerrainTreeSpeciesRules.SpawnChance((byte)column.Biome, species);
                var roll = Hash01(x, z, gen.Seed ^ PreviewStageConstants.TerrainVegetationSeedSalt);
                if (roll > chance)
                {
                    continue;
                }

                // Coarse candidate grids keep world spacing at least one cell apart.
                var spacing = PreviewTerrainTreeSpeciesRules.MinSpacing(species);
                if (placementStep > 1)
                {
                    spacing = Math.Max(spacing, placementStep);
                }

                if (IsTooClose(occupied, x, z, spacing))
                {
                    continue;
                }

                if (!IsSupportedSurface(column, species))
                {
                    continue;
                }

                var shape = PreviewTerrainTreeSpeciesRules.GetShape(species);
                var trunkSpan = Math.Max(0, shape.MaxTrunkHeight - shape.MinTrunkHeight);
                var trunkHeight = shape.MinTrunkHeight +
                    (trunkSpan == 0
                        ? 0
                        : HashInt(x, z, gen.Seed ^ 0x51AA51AA) % (trunkSpan + 1));
                var variant = HashInt(x, z, gen.Seed ^ 0x7E3E7E3E);

                occupied.Add(PackKey(x, z));
                result.Add(new Placement(
                    x,
                    z,
                    column.Height,
                    species,
                    materials,
                    trunkHeight,
                    variant));
            }
        }

        return result;
    }

    /// <summary>
    /// Stable subset of Full roots for distant LOD. Keep-mask 1 retains all; larger masks thin
    /// by root hash. If thinning would empty a non-empty forested set, force-keeps one root
    /// nearest the section center so horizons never go bald.
    /// </summary>
    public static List<Placement> FilterForLodKeep(
        IReadOnlyList<Placement> fullRoots,
        byte lodLevel,
        int sectionCenterX,
        int sectionCenterZ)
    {
        var mask = PreviewStageConstants.ResolveLodVegetationKeepMask(lodLevel);
        if (mask <= 1 || fullRoots.Count == 0)
        {
            return fullRoots is List<Placement> list ? list : [.. fullRoots];
        }

        var keepBits = mask - 1;
        var kept = new List<Placement>((fullRoots.Count + mask - 1) / mask);
        foreach (var p in fullRoots)
        {
            if ((StableKeepHash(p.RootX, p.RootZ) & keepBits) == 0)
            {
                kept.Add(p);
            }
        }

        if (kept.Count == 0)
        {
            // Floor: never drop every tree from a forested section.
            var best = fullRoots[0];
            var bestDist = long.MaxValue;
            foreach (var p in fullRoots)
            {
                var dx = (long)p.RootX - sectionCenterX;
                var dz = (long)p.RootZ - sectionCenterZ;
                var d = dx * dx + dz * dz;
                if (d < bestDist ||
                    (d == bestDist &&
                     (p.RootX < best.RootX || (p.RootX == best.RootX && p.RootZ < best.RootZ))))
                {
                    bestDist = d;
                    best = p;
                }
            }

            kept.Add(best);
        }

        return kept;
    }

    /// <summary>True when <paramref name="root"/> survives the LOD keep-mask (ignores floor).</summary>
    public static bool ShouldKeepRootForLod(int rootX, int rootZ, byte lodLevel)
    {
        var mask = PreviewStageConstants.ResolveLodVegetationKeepMask(lodLevel);
        if (mask <= 1)
        {
            return true;
        }

        return (StableKeepHash(rootX, rootZ) & (mask - 1)) == 0;
    }

    private static int StableKeepHash(int x, int z) =>
        HashInt(x, z, PreviewStageConstants.TerrainVegetationSeedSalt ^ unchecked((int)0x4BEEF001));

    private static bool TrySelectSpecies(
        in PreviewTerrainColumnSample column,
        int x,
        int z,
        in PreviewTerrainWorldGenSettings gen,
        PreviewTerrainVegetationBakePlan plan,
        out PreviewTerrainTreeSpecies species,
        out PreviewTerrainVegetationBakeEntry materials)
    {
        species = PreviewTerrainTreeSpecies.Oak;
        materials = default;

        if (column.Biome == PreviewTerrainBiomeId.Desert)
        {
            if (!plan.TryGet(PreviewTerrainTreeSpecies.Cactus, out materials))
            {
                return false;
            }

            species = PreviewTerrainTreeSpecies.Cactus;
            return true;
        }

        PreviewTerrainBiomeSampler.SampleClimate(x, z, gen, out var temperature, out var humidity, out _);
        if (!PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(
                (byte)column.Biome,
                temperature,
                humidity,
                out var preferred))
        {
            return false;
        }

        if (!plan.TryResolveWood(preferred, out materials))
        {
            return false;
        }

        species = materials.Species;
        return true;
    }

    private static bool IsSupportedSurface(
        in PreviewTerrainColumnSample column,
        PreviewTerrainTreeSpecies species)
    {
        if (species == PreviewTerrainTreeSpecies.Cactus)
        {
            return column.Surface == PreviewTerrainBlockKind.Sand;
        }

        return column.Surface is PreviewTerrainBlockKind.Grass or PreviewTerrainBlockKind.Dirt;
    }

    private static bool IsTooClose(HashSet<long> occupied, int x, int z, int spacing)
    {
        for (var dz = -spacing; dz <= spacing; dz++)
        {
            for (var dx = -spacing; dx <= spacing; dx++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }

                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) > spacing)
                {
                    continue;
                }

                if (occupied.Contains(PackKey(x + dx, z + dz)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static long PackKey(int x, int z) => ((long)x << 32) ^ (uint)z;

    private static float Hash01(int x, int z, int seed)
    {
        var h = HashInt(x, z, seed);
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    private static int HashInt(int x, int z, int seed)
    {
        unchecked
        {
            var h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)z * 0x85EBCA6Bu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return (int)h;
        }
    }
}
