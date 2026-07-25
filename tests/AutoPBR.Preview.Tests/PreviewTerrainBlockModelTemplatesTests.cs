using System.IO.Compression;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.Preview.Tests;

public sealed class PreviewTerrainBlockModelTemplatesTests
{
    [Fact]
    public async Task TryBuild_cactus_from_pack_json_insets_sides()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "autopbr_cactus_json_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await CreateCactusPackAsync(zipPath);

            var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
            var options = new AutoPBROptions
            {
                SpecularData = SpecularData.LoadFromFile(dataPath),
                FastSpecular = true,
                FoliageMode = "No Height",
            };

            var kit = await PreviewTerrainVegetationKitResolver.TryResolveAsync(
                zipPath,
                preferScannedPack: true,
                minecraftAssetsDirectory: null,
                options);

            Assert.True(kit.HasAny);
            Assert.True(kit.ModelTemplates.HasAny);
            Assert.True(kit.ModelTemplates.TryGet(PreviewTerrainTreeSpecies.Cactus, out var cactus));
            Assert.NotNull(cactus.LogOrCactus);
            Assert.Contains("cactus", cactus.LogOrCactus.ProvenanceDetail, StringComparison.OrdinalIgnoreCase);

            // Origin-centered: side faces should sit near ±7/16, not ±0.5.
            var sawInset = false;
            foreach (var (_, verts) in cactus.LogOrCactus.VerticesBySlot)
            {
                for (var i = 0; i < verts.Length; i += 12)
                {
                    var x = Math.Abs(verts[i]);
                    var z = Math.Abs(verts[i + 2]);
                    if (Math.Abs(x - 0.4375f) < 1e-3f || Math.Abs(z - 0.4375f) < 1e-3f)
                    {
                        sawInset = true;
                    }
                }
            }

            Assert.True(sawInset, "expected cactus.json inset side faces at ±7/16");
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    [Fact]
    public void Stamp_translates_origin_centered_template_to_block()
    {
        var template = new PreviewTerrainBlockModelTemplate
        {
            SourceArchivePath = "assets/minecraft/textures/block/oak_log.png",
            ProvenanceDetail = "test",
            VerticesBySlot = new Dictionary<int, float[]>
            {
                [7] =
                [
                    // one vertex at origin-centered corner
                    -0.5f, -0.5f, -0.5f, 0, 1, 0, 0, 0, 1, 0, 0, 1,
                    0.5f, -0.5f, -0.5f, 0, 1, 0, 0, 0, 1, 0, 0, 1,
                    0.5f, -0.5f, 0.5f, 0, 1, 0, 0, 0, 1, 0, 0, 1,
                    -0.5f, -0.5f, 0.5f, 0, 1, 0, 0, 0, 1, 0, 0, 1,
                ],
            },
        };

        var buckets = new List<float>[9];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        PreviewTerrainBlockModelTemplates.Stamp(template, bx: 3, by: 2, bz: 5, surfaceWorldY: 0f, buckets);
        Assert.Equal(4 * 12, buckets[7].Count);
        // Center = (3.5, 2-0.5, 5.5) = (3.5, 1.5, 5.5); corner (-0.5,-0.5,-0.5) → (3,1,5)
        Assert.Equal(3f, buckets[7][0], 3);
        Assert.Equal(1f, buckets[7][1], 3);
        Assert.Equal(5f, buckets[7][2], 3);
    }

    [Fact]
    public void TryResolveFaceCorners_maps_reverse_winding_to_forward_quad()
    {
        // Baker ReverseFaceWinding: 0,2,1, 0,3,2
        Assert.True(
            PreviewTerrainBlockModelTemplates.TryResolveFaceCorners(
                10, 12, 11, 10, 13, 12,
                out var c0,
                out var c1,
                out var c2,
                out var c3));
        Assert.Equal(10u, c0);
        Assert.Equal(13u, c1);
        Assert.Equal(12u, c2);
        Assert.Equal(11u, c3);
    }

    [Fact]
    public void TryResolveFaceCorners_keeps_forward_winding()
    {
        Assert.True(
            PreviewTerrainBlockModelTemplates.TryResolveFaceCorners(
                0, 1, 2, 0, 2, 3,
                out var c0,
                out var c1,
                out var c2,
                out var c3));
        Assert.Equal(0u, c0);
        Assert.Equal(1u, c1);
        Assert.Equal(2u, c2);
        Assert.Equal(3u, c3);
    }

    private static async Task CreateCactusPackAsync(string zipPath)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        await using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        await AddPng(zip, "assets/minecraft/textures/block/cactus_side.png", 40, 160, 40, 200);
        await AddPng(zip, "assets/minecraft/textures/block/cactus_top.png", 30, 140, 30);
        await AddPng(zip, "assets/minecraft/textures/block/cactus_bottom.png", 20, 100, 20);

        var model = """
            {
              "parent": "block/block",
              "textures": {
                "particle": "block/cactus_side",
                "bottom": "block/cactus_bottom",
                "top": "block/cactus_top",
                "side": "block/cactus_side"
              },
              "elements": [
                {
                  "from": [0, 0, 0],
                  "to": [16, 16, 16],
                  "faces": {
                    "down": { "uv": [0, 0, 16, 16], "texture": "#bottom" },
                    "up": { "uv": [0, 0, 16, 16], "texture": "#top" }
                  }
                },
                {
                  "from": [0, 0, 1],
                  "to": [16, 16, 15],
                  "faces": {
                    "north": { "uv": [0, 0, 16, 16], "texture": "#side" },
                    "south": { "uv": [0, 0, 16, 16], "texture": "#side" }
                  }
                },
                {
                  "from": [1, 0, 0],
                  "to": [15, 16, 16],
                  "faces": {
                    "west": { "uv": [0, 0, 16, 16], "texture": "#side" },
                    "east": { "uv": [0, 0, 16, 16], "texture": "#side" }
                  }
                }
              ]
            }
            """;
        var entry = zip.CreateEntry("assets/minecraft/models/block/cactus.json");
        await using (var s = entry.Open())
        await using (var w = new StreamWriter(s))
        {
            await w.WriteAsync(model);
        }

        var blockstate = """
            {
              "variants": {
                "": { "model": "block/cactus" }
              }
            }
            """;
        var bs = zip.CreateEntry("assets/minecraft/blockstates/cactus.json");
        await using var bsStream = bs.Open();
        await using var bsWriter = new StreamWriter(bsStream);
        await bsWriter.WriteAsync(blockstate);
    }

    private static async Task AddPng(ZipArchive zip, string path, byte r, byte g, byte b, byte a = 255)
    {
        var entry = zip.CreateEntry(path);
        await using var s = entry.Open();
        using var image = new Image<Rgba32>(16, 16);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(r, g, b, a);
                }
            }
        });
        await image.SaveAsPngAsync(s);
    }
}
