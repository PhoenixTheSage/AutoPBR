using System.Text.Json;


namespace AutoPBR.Preview.Tests;

public sealed class MinecraftPreviewVersionGateTests
{
    [Theory]
    [InlineData("Z:\\packs\\minecraft-parity\\1.21.11\\vanilla.zip", true, 1, 21, 11)]
    [InlineData("tools/minecraft-parity/26.1.2/client.jar", true, 26, 1, 2)]
    [InlineData(@"C:\Users\me\resourcepack.zip", false, 0, 0, 0)]
    public void TryDetectFromPath_resolves_version_tokens(string path, bool expectFound, int major, int minor, int build)
    {
        var found = MinecraftPreviewVersionDetection.TryDetectFromPath(path, out var v);
        Assert.Equal(expectFound, found);
        if (expectFound)
        {
            Assert.Equal(new Version(major, minor, build), v);
        }
    }

    [Fact]
    public void ResolveForPreview_uses_1_21_11_native_when_path_indicates_legacy()
    {
        using var temp = new TempNativeRoot();
        var profile = MinecraftNativeProfileResolver.ResolveForPreview(
            temp.Root,
            inputZipPath: @"D:\minecraft-parity\1.21.11\client.jar",
            extractedPackDir: null);
        Assert.NotNull(profile);
        Assert.Equal("1.21.11", profile.Name);
    }

    [Fact]
    public void ResolveForPreview_uses_modern_native_when_path_indicates_26_1_2()
    {
        using var temp = new TempNativeRoot();
        var profile = MinecraftNativeProfileResolver.ResolveForPreview(
            temp.Root,
            inputZipPath: @"D:\minecraft-parity\26.1.2\resourcepack.zip",
            extractedPackDir: null);
        Assert.NotNull(profile);
        Assert.Equal("26.1.2", profile.Name);
    }

    [Fact]
    public void ResolveForPreview_defaults_to_modern_when_version_unknown()
    {
        using var temp = new TempNativeRoot();
        var profile = MinecraftNativeProfileResolver.ResolveForPreview(
            temp.Root,
            inputZipPath: @"C:\packs\my_textures.zip",
            extractedPackDir: null);
        Assert.NotNull(profile);
        Assert.Equal("26.1.2", profile.Name);
    }

    [Fact]
    public void ResolveForPreview_uses_legacy_when_pack_format_maps_to_1_21_11()
    {
        using var temp = new TempNativeRoot();
        var packDir = Path.Combine(Path.GetTempPath(), "AutoPBR_TestPack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packDir);
        try
        {
            var mcmeta = JsonSerializer.Serialize(new
            {
                pack = new { pack_format = 75 }
            });
            File.WriteAllText(Path.Combine(packDir, "pack.mcmeta"), mcmeta);

            var profile = MinecraftNativeProfileResolver.ResolveForPreview(
                temp.Root,
                inputZipPath: null,
                extractedPackDir: packDir);
            Assert.NotNull(profile);
            Assert.Equal("1.21.11", profile.Name);
        }
        finally
        {
            Directory.Delete(packDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForPreview_uses_modern_when_pack_format_maps_to_26_1()
    {
        using var temp = new TempNativeRoot();
        var packDir = Path.Combine(Path.GetTempPath(), "AutoPBR_TestPack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packDir);
        try
        {
            var mcmeta = JsonSerializer.Serialize(new
            {
                pack = new { pack_format = 84 }
            });
            File.WriteAllText(Path.Combine(packDir, "pack.mcmeta"), mcmeta);

            var profile = MinecraftNativeProfileResolver.ResolveForPreview(
                temp.Root,
                inputZipPath: null,
                extractedPackDir: packDir);
            Assert.NotNull(profile);
            Assert.Equal("26.1.2", profile.Name);
        }
        finally
        {
            Directory.Delete(packDir, recursive: true);
        }
    }

    [Fact]
    public void NativeIrVersionLabels_falls_back_to_catalogued_modern_for_any_nonexact_label()
    {
        var legacy = new MinecraftNativeProfile("1.21.11", "unused", new Version(1, 21, 11));
        var labels = NativeIrVersionLabels.ForProfile(legacy).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal("1.21.11", labels[0]);
        Assert.Equal("26.1.2", labels[1]);

        var modern = new MinecraftNativeProfile("26.1.2", "unused", new Version(26, 1, 2));
        labels = NativeIrVersionLabels.ForProfile(modern).ToList();
        Assert.Single(labels);
        Assert.Equal("26.1.2", labels[0]);

        var game26_2 = new MinecraftNativeProfile("26.2", "unused", new Version(26, 2));
        labels = NativeIrVersionLabels.ForProfile(game26_2).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal("26.2", labels[0]);
        Assert.Equal("26.1.2", labels[1]);
        Assert.Equal(labels, NativeIrVersionLabels.ForGeometryIrShardLookup(game26_2).ToList());
    }

    [Fact]
    public void NativeIrVersionLabels_unknown_profile_falls_back_to_modern_geometry_label()
    {
        var unknown = new MinecraftNativeProfile("unknown", "unused", null);
        var labels = NativeIrVersionLabels.ForProfile(unknown).ToList();
        Assert.Single(labels);
        Assert.Equal("26.1.2", labels[0]);

        var geo = NativeIrVersionLabels.ForGeometryIrShardLookup(unknown).ToList();
        Assert.Single(geo);
        Assert.Equal("26.1.2", geo[0]);
    }

    [Fact]
    public void ResolveAutoLatestModern_ignores_geometry_animation_setup_anim_folders()
    {
        using var temp = new TempNativeRootWithIrPayloadDirs();
        var profile = MinecraftNativeProfileResolver.ResolveAutoLatestModern(temp.Root);
        Assert.NotNull(profile);
        Assert.Equal("26.1.2", profile.Name);
    }

    [Fact]
    public void ForGeometryIrShardLookup_legacy_tries_modern_after_1_21_11()
    {
        var legacy = new MinecraftNativeProfile("1.21.11", "unused", new Version(1, 21, 11));
        var labels = NativeIrVersionLabels.ForGeometryIrShardLookup(legacy).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal("1.21.11", labels[0]);
        Assert.Equal("26.1.2", labels[1]);
    }

    [Fact]
    public void ForGeometryIrShardLookup_nonexact_modern_falls_back_to_catalogued_26_1_2()
    {
        var nearby = new MinecraftNativeProfile("26.1.0", "unused", new Version(26, 1, 0));
        var labels = NativeIrVersionLabels.ForGeometryIrShardLookup(nearby).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal("26.1.0", labels[0]);
        Assert.Equal("26.1.2", labels[1]);

        var exact = new MinecraftNativeProfile("26.1.2", "unused", new Version(26, 1, 2));
        labels = NativeIrVersionLabels.ForGeometryIrShardLookup(exact).ToList();
        Assert.Single(labels);
        Assert.Equal("26.1.2", labels[0]);
    }

    [Fact]
    public void ForProfile_26_2_falls_back_to_catalogued_26_1_2()
    {
        Assert.True(MinecraftPreviewVersionDetection.TryDetectFromPath(
            @"C:\Users\me\AppData\Roaming\.minecraft\versions\26.2\26.2.jar",
            out var detected));
        Assert.Equal(new Version(26, 2, 0), detected);

        var profile = new MinecraftNativeProfile("26.2", "unused", detected);
        var labels = NativeIrVersionLabels.ForProfile(profile).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal("26.2", labels[0]);
        Assert.Equal("26.1.2", labels[1]);
    }

    private sealed class TempNativeRoot : IDisposable
    {
        public string Root { get; }

        public TempNativeRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "AutoPBR_NativeRoot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "1.21.11"));
            Directory.CreateDirectory(Path.Combine(Root, "26.1.2"));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    private sealed class TempNativeRootWithIrPayloadDirs : IDisposable
    {
        public string Root { get; }

        public TempNativeRootWithIrPayloadDirs()
        {
            Root = Path.Combine(Path.GetTempPath(), "AutoPBR_NativeRootIr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "26.1.2"));
            Directory.CreateDirectory(Path.Combine(Root, "geometry"));
            Directory.CreateDirectory(Path.Combine(Root, "animation"));
            Directory.CreateDirectory(Path.Combine(Root, "setup-anim"));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }
}
