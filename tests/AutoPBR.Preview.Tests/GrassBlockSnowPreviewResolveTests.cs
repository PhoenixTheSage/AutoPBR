using AutoPBR.Core.Models;
using AutoPBR.Preview;

namespace AutoPBR.Preview.Tests;

public sealed class GrassBlockSnowPreviewResolveTests
{
    private static readonly string ClientJar =
        @"C:\Users\John_Phoenix\AppData\Roaming\PrismLauncher\libraries\com\mojang\minecraft\26.1.2\minecraft-26.1.2-client.jar";

    [Fact]
    public void Snow_cap_builder_matches_vanilla_snow_height2_thickness()
    {
        var merged = new MergedJavaBlockModel
        {
            Elements =
            [
                new ModelElement
                {
                    From = [0f, 0f, 0f],
                    To = [16f, 16f, 16f],
                    Faces = new Dictionary<string, ModelFace>(StringComparer.OrdinalIgnoreCase),
                },
            ],
            Textures = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["up"] = "minecraft:block/grass_block_top",
                ["side"] = "minecraft:block/grass_block_snow",
            },
        };

        Assert.True(BlockGrassSnowPreviewPairing.TryAppendSnowCapForGrassBlockSnow(
            "assets/minecraft/textures/block/grass_block_snow.png",
            "minecraft",
            ref merged));
        Assert.Equal(2, merged.Elements.Count);
        Assert.Equal(16f, merged.Elements[1].From[1]);
        Assert.Equal(18f, merged.Elements[1].To[1]);
    }

    [Fact]
    public void Runtime_resolve_uses_pack_json_with_snow_cap_when_jar_present()
    {
        if (!File.Exists(ClientJar))
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "AutoPBR_Test", Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(tempRoot, "pack_unzipped");
        Directory.CreateDirectory(extracted);
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(ClientJar);
            var zipSource = new ZipAssetSource(zip);
            const string texturePath = "assets/minecraft/textures/block/grass_block_snow.png";
            using var assetSources = PreviewAssetSourceFactory.Create(zipSource, null, null);
            var resolved = RuntimeBlockPreviewModelResolver.Resolve(
                zipSource,
                assetSources,
                texturePath,
                extracted,
                previewNativeProfile: null,
                options: new AutoPBROptions());

            Assert.NotNull(resolved.MergedModel);
            Assert.Equal(PreviewMeshDriverKind.PackModelJson, resolved.MeshProvenance.Kind);
            Assert.Contains("snow-cap", resolved.MeshProvenance.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("grass_block_top", resolved.MergedModel!.Textures["top"], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, resolved.MergedModel.Elements.Count);
            Assert.Contains(resolved.OrderedModelTextures!, p => p.Contains("snow.png", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(resolved.OrderedModelTextures!, p => p.Contains("grass_block_snow", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
