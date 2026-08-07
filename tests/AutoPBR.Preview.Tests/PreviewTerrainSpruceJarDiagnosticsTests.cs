
namespace AutoPBR.Preview.Tests;

public sealed class PreviewTerrainSpruceJarDiagnosticsTests
{
    private static string? FindMinecraftJar()
    {
        var jar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft",
            "versions",
            "26.2",
            "26.2.jar");
        return File.Exists(jar) ? jar : null;
    }

    [Fact]
    public async Task Jar_spruce_kit_templates_use_distinct_slots_and_spruce_provenance()
    {
        var jar = FindMinecraftJar();
        if (jar is null)
        {
            // Optional local diagnostic against a real Minecraft 26.2 install (not present on CI).
            return;
        }

        var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
        Assert.True(File.Exists(dataPath), $"missing textures_data.json at {dataPath}");

        var options = new AutoPBROptions
        {
            SpecularData = SpecularData.LoadFromFile(dataPath),
            FastSpecular = true,
            FoliageMode = "No Height",
        };

        var kit = await PreviewTerrainVegetationKitResolver.TryResolveAsync(
            jar,
            preferScannedPack: true,
            minecraftAssetsDirectory: null,
            options);

        Assert.True(kit.HasAny, "expected vegetation from 26.2.jar");
        Assert.True(kit.TryGet(PreviewTerrainTreeSpecies.Spruce, out var spruce));
        Assert.True(kit.ModelTemplates.TryGet(PreviewTerrainTreeSpecies.Spruce, out var templates));

        Assert.NotNull(templates.LogOrCactus);
        Assert.NotNull(templates.Leaves);
        Assert.Contains("spruce_log", templates.LogOrCactus.ProvenanceDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spruce_leaves", templates.Leaves.ProvenanceDetail, StringComparison.OrdinalIgnoreCase);

        Assert.True(
            templates.LogOrCactus.VerticesBySlot.ContainsKey(spruce.LogSlot),
            $"log template missing LogSlot {spruce.LogSlot}; keys=[{string.Join(",", templates.LogOrCactus.VerticesBySlot.Keys)}]");
        Assert.True(
            templates.Leaves.VerticesBySlot.ContainsKey(spruce.LeavesOrTopSlot),
            $"leaves template missing LeavesSlot {spruce.LeavesOrTopSlot}; keys=[{string.Join(",", templates.Leaves.VerticesBySlot.Keys)}]");

        // Log should not dump into leaf slot; leaves should not dump into log slot.
        Assert.False(templates.LogOrCactus.VerticesBySlot.ContainsKey(spruce.LeavesOrTopSlot));
        Assert.False(templates.Leaves.VerticesBySlot.ContainsKey(spruce.LogSlot));

        if (spruce.LogTopSlot is not null)
        {
            Assert.True(
                templates.LogOrCactus.VerticesBySlot.ContainsKey(spruce.LogTopSlot.Value),
                $"expected log_top faces in slot {spruce.LogTopSlot.Value}");
        }

        // Stamp into buckets and ensure only spruce slots receive geometry.
        var buckets = new List<float>[kit.TotalSlotCount];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        PreviewTerrainBlockModelTemplates.Stamp(templates.LogOrCactus, 0, 1, 0, 0f, buckets);
        PreviewTerrainBlockModelTemplates.Stamp(templates.Leaves, 0, 4, 0, 0f, buckets);

        Assert.True(buckets[spruce.LogSlot].Count > 0);
        Assert.True(buckets[spruce.LeavesOrTopSlot].Count > 0);
        if (spruce.LogTopSlot is not null)
        {
            Assert.True(buckets[spruce.LogTopSlot.Value].Count > 0);
        }

        // Each stamped face must keep four distinct corners (reverse-winding bug made i3==i1).
        foreach (var (_, verts) in templates.LogOrCactus.VerticesBySlot)
        {
            Assert.True(verts.Length % 48 == 0, "expected 4 verts × 12 floats per face");
            for (var face = 0; face < verts.Length; face += 48)
            {
                var corners = new HashSet<(int, int, int)>();
                for (var v = 0; v < 4; v++)
                {
                    var o = face + v * 12;
                    corners.Add((
                        (int)MathF.Round(verts[o] * 1000f),
                        (int)MathF.Round(verts[o + 1] * 1000f),
                        (int)MathF.Round(verts[o + 2] * 1000f)));
                    var u = verts[o + 6];
                    var vv = verts[o + 7];
                    Assert.InRange(u, -0.05f, 1.05f);
                    Assert.InRange(vv, -0.05f, 1.05f);
                }

                Assert.Equal(4, corners.Count);
            }
        }

        // Oak slots must stay empty when only spruce was stamped.
        Assert.True(kit.TryGet(PreviewTerrainTreeSpecies.Oak, out var oak));
        Assert.Empty(buckets[oak.LogSlot]);
        Assert.Empty(buckets[oak.LeavesOrTopSlot]);
    }
}
