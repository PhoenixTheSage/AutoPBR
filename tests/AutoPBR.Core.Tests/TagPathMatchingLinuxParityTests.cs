using AutoPBR.Contracts;

namespace AutoPBR.Core.Tests;

public sealed class TagPathMatchingLinuxParityTests
{
    [Theory]
    [InlineData(@"\minecraft\textures\block\stone", @"textures\block\stone")]
    [InlineData("/minecraft/textures/block/stone", @"textures\block\stone")]
    [InlineData(@"minecraft\textures\block\stone", @"textures\block\stone")]
    [InlineData("minecraft/optifine/ctm/foo", @"optifine\ctm\foo")]
    public void PathBelowNamespace_NormalizesSlashStyles(string relativeKey, string expected)
    {
        Assert.Equal(expected, TagPathMatching.PathBelowNamespace(relativeKey));
    }

    [Fact]
    public void CanonicalRelativeKey_ForcesBackslashSegments()
    {
        // Mirrors TextureScanner.Enumerate: OS GetRelativePath may use '/', then we force '\\'.
        var mixed = Path.Combine("textures", "block", "stone").Replace('\\', '/');
        var canonical = mixed.Replace('/', '\\');
        Assert.Equal(@"textures\block\stone", canonical);
        Assert.Equal(@"textures\block\stone", TagPathMatching.PathBelowNamespace(@"\minecraft\" + canonical));
    }
}
