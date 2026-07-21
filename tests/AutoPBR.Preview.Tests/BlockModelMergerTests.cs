using AutoPBR.Preview;

using System.IO.Compression;
using System.Text;

namespace AutoPBR.Core.Tests;

public sealed class BlockModelMergerTests
{
    [Fact]
    public void Merge_uses_parent_elements_when_child_only_overrides_textures()
    {
        using var fixture = new BlockModelFixture();
        fixture.Write(
            "assets/minecraft/models/block/template_plate.json",
            """
            {
              "elements": [
                {
                  "from": [0, 0, 0],
                  "to": [16, 16, 3],
                  "faces": {
                    "north": { "texture": "#texture", "uv": [0, 0, 16, 16] }
                  }
                }
              ],
              "textures": { "texture": "minecraft:block/oak_trapdoor" }
            }
            """);
        fixture.Write(
            "assets/minecraft/models/block/oak_trapdoor.json",
            """
            {
              "parent": "minecraft:block/template_plate",
              "textures": { "texture": "minecraft:block/oak_trapdoor" }
            }
            """);

        var source = new DirectoryAssetSource(fixture.Root);
        Assert.True(MinecraftModelMerger.TryMerge(source, "assets/minecraft/models/block/oak_trapdoor.json", out var merged));
        Assert.Single(merged.Elements);
        Assert.Equal(3f, merged.Elements[0].To[2]);
    }

    [Fact]
    public void Merge_does_not_abort_when_parent_is_builtin()
    {
        using var fixture = new BlockModelFixture();
        fixture.Write(
            "assets/minecraft/models/block/custom_panel.json",
            """
            {
              "parent": "builtin/generated",
              "elements": [
                {
                  "from": [0, 0, 0],
                  "to": [16, 16, 3],
                  "faces": {
                    "north": { "texture": "#texture", "uv": [0, 0, 16, 16] }
                  }
                }
              ],
              "textures": { "texture": "minecraft:block/oak_trapdoor" }
            }
            """);

        var source = new DirectoryAssetSource(fixture.Root);
        Assert.True(MinecraftModelMerger.TryMerge(source, "assets/minecraft/models/block/custom_panel.json", out var merged));
        Assert.Single(merged.Elements);
    }

    [Fact]
    public void TryMergeMany_concatenates_elements_from_multiple_models()
    {
        using var fixture = new BlockModelFixture();
        fixture.Write(
            "assets/minecraft/models/block/part_a.json",
            """
            {
              "elements": [
                {
                  "from": [0, 0, 0],
                  "to": [8, 16, 16],
                  "faces": { "north": { "texture": "#a" } }
                }
              ],
              "textures": { "a": "minecraft:block/stone" }
            }
            """);
        fixture.Write(
            "assets/minecraft/models/block/part_b.json",
            """
            {
              "elements": [
                {
                  "from": [8, 0, 0],
                  "to": [16, 16, 16],
                  "faces": { "north": { "texture": "#b" } }
                }
              ],
              "textures": { "b": "minecraft:block/dirt" }
            }
            """);

        var source = new DirectoryAssetSource(fixture.Root);
        Assert.True(MinecraftModelMerger.TryMergeMany(
            source,
            [
                "assets/minecraft/models/block/part_a.json",
                "assets/minecraft/models/block/part_b.json",
            ],
            out var merged));
        Assert.Equal(2, merged.Elements.Count);
        Assert.True(merged.Textures.ContainsKey("a"));
        Assert.True(merged.Textures.ContainsKey("b"));
    }

    [Fact]
    public void Blockstate_defaults_pick_door_lower_and_upper_variants()
    {
        const string json = """
            {
              "variants": {
                "facing=north,half=upper": { "model": "minecraft:block/oak_door_top" },
                "facing=north,half=lower": { "model": "minecraft:block/oak_door_bottom" },
                "facing=south,half=lower": { "model": "minecraft:block/oak_door_bottom" }
              }
            }
            """;

        Assert.True(JavaModelPathResolver.TryPickModelPathsFromBlockstate(json, "oak_door", null, out var paths));
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, p => p.Contains("oak_door_bottom", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("oak_door_top", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Door_pairing_appends_sibling_half_model_paths()
    {
        using var fixture = new BlockModelFixture();
        fixture.Write(
            "assets/minecraft/models/block/oak_door_bottom.json",
            """{ "elements": [{ "from": [0,0,0], "to": [16,16,16], "faces": { "north": { "texture": "#b" } } }], "textures": { "b": "minecraft:block/oak_door_bottom" } }""");
        fixture.Write(
            "assets/minecraft/models/block/oak_door_top.json",
            """{ "elements": [{ "from": [0,0,0], "to": [16,16,16], "faces": { "north": { "texture": "#t" } } }], "textures": { "t": "minecraft:block/oak_door_top" } }""");

        var source = new DirectoryAssetSource(fixture.Root);
        var paths = new List<string>();
        BlockDoorPreviewPairing.TryAppendDoorPairModelPaths(
            source,
            "oak_door_bottom",
            "minecraft",
            paths);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void PreviewAssetSourceFactory_prefers_pack_over_install()
    {
        using var install = new BlockModelFixture();
        install.Write(
            "assets/minecraft/models/block/stone.json",
            """{ "elements": [{ "from": [0,0,0], "to": [16,16,16], "faces": { "north": { "texture": "#t" } } }], "textures": { "t": "minecraft:block/stone" } }""");

        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("assets/minecraft/models/block/stone.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("""{ "parent": "minecraft:block/missing_parent" }""");
        }

        zipStream.Position = 0;
        using var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        using var sources = PreviewAssetSourceFactory.Create(
            new ZipAssetSource(zipArchive),
            install.Root,
            null);
        Assert.Equal(PreviewModelJsonOrigin.Pack, sources.ResolveModelJsonOrigin("assets/minecraft/models/block/stone.json"));
    }

    [Fact]
    public void MinecraftInstallAssetPaths_accepts_version_folder_with_assets()
    {
        using var fixture = new BlockModelFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "assets", "minecraft", "models", "block"));
        var versionRoot = Path.Combine(fixture.Root, "versions", "26.1.2");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(Path.Combine(versionRoot, "assets", "minecraft", "models", "block"));

        Assert.True(MinecraftInstallAssetPaths.TryResolveAssetsRoot(versionRoot, out var assetsRoot));
        Assert.EndsWith("assets", assetsRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MinecraftInstallAssetPaths_versions_folder_prefers_latest_non_snapshot_jar()
    {
        using var fixture = new BlockModelFixture();
        var versionsDir = Path.Combine(fixture.Root, "versions");
        WriteVersionJar(versionsDir, "26.1.2", type: "release", assetMarker: "old");
        WriteVersionJar(versionsDir, "26.3-snapshot-4", type: "snapshot", assetMarker: "snap");
        WriteVersionJar(versionsDir, "26.2", type: "release", assetMarker: "latest");

        Assert.True(MinecraftInstallAssetPaths.TryResolve(versionsDir, out var location));
        Assert.Equal(MinecraftInstallAssetsKind.ClientJar, location.Kind);
        Assert.Contains($"{Path.DirectorySeparatorChar}26.2{Path.DirectorySeparatorChar}", location.Path, StringComparison.OrdinalIgnoreCase);

        Assert.True(MinecraftInstallAssetPaths.TryResolvePreferredVersionFolder(fixture.Root, out var versionFolder));
        Assert.Equal("26.2", Path.GetFileName(versionFolder), ignoreCase: true);

        Assert.True(MinecraftInstallAssetSource.TryOpen(versionsDir, out var source, out var lifetime));
        using (lifetime)
        {
            Assert.True(source!.TryReadText(
                "assets/minecraft/models/block/stone.json",
                out var text));
            Assert.Contains("latest", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MinecraftInstallAssetPaths_accepts_explicit_snapshot_version_folder()
    {
        using var fixture = new BlockModelFixture();
        var versionsDir = Path.Combine(fixture.Root, "versions");
        WriteVersionJar(versionsDir, "26.2", type: "release", assetMarker: "release");
        WriteVersionJar(versionsDir, "26.3-snapshot-4", type: "snapshot", assetMarker: "snap");
        var snapshotFolder = Path.Combine(versionsDir, "26.3-snapshot-4");

        Assert.True(MinecraftInstallAssetPaths.TryResolve(snapshotFolder, out var location));
        Assert.Equal(MinecraftInstallAssetsKind.ClientJar, location.Kind);
        Assert.Contains("26.3-snapshot-4", location.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MinecraftInstallAssetPaths_resolves_local_dot_minecraft_versions_when_present()
    {
        var versionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft",
            "versions");
        if (!Directory.Exists(versionsDir))
        {
            return;
        }

        Assert.True(MinecraftInstallAssetPaths.TryResolve(versionsDir, out var location));
        Assert.Equal(MinecraftInstallAssetsKind.ClientJar, location.Kind);
        Assert.True(File.Exists(location.Path));
        Assert.DoesNotContain("snapshot", location.Path, StringComparison.OrdinalIgnoreCase);

        Assert.True(MinecraftInstallAssetSource.TryOpen(versionsDir, out var source, out var lifetime));
        using (lifetime)
        {
            Assert.True(source!.Exists("assets/minecraft/textures/block/grass_block_top.png"));
            Assert.True(source.Exists("assets/minecraft/models/block/stone.json"));
            Assert.True(source.Exists("assets/minecraft/textures/colormap/grass.png"));
        }
    }

    private static void WriteVersionJar(string versionsDir, string id, string type, string assetMarker)
    {
        var versionDir = Path.Combine(versionsDir, id);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(
            Path.Combine(versionDir, id + ".json"),
            $$"""{"id":"{{id}}","type":"{{type}}"}""");

        var jarPath = Path.Combine(versionDir, id + ".jar");
        using var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("assets/minecraft/models/block/stone.json");
        using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
        {
            writer.Write($$"""{"marker":"{{assetMarker}}"}""");
        }

        var grass = zip.CreateEntry("assets/minecraft/textures/block/grass_block_top.png");
        using var grassStream = grass.Open();
        grassStream.WriteByte(0);
    }

    internal sealed class BlockModelFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AutoPBR_BlockModelTests", Guid.NewGuid().ToString("N"));

        public void Write(string relativePath, string json)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
