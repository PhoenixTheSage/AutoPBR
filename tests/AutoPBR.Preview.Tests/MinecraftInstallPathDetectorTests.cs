using AutoPBR.Preview;

namespace AutoPBR.Preview.Tests;

public sealed class MinecraftInstallPathDetectorTests
{
    [Fact]
    public void LinuxRoots_PreferXdgAndHomeMinecraft_NotAppDataConfig()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var roots = MinecraftInstallPathDetector.EnumerateCandidateGameRootsForTests().ToArray();
        Assert.NotEmpty(roots);
        Assert.DoesNotContain(
            roots,
            r => r.Replace('\\', '/').Contains("/.config/.minecraft", StringComparison.Ordinal));
        Assert.Contains(
            roots,
            r => r.Replace('\\', '/').EndsWith("/.minecraft", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsRoots_StillIncludeAppDataMinecraft()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var roots = MinecraftInstallPathDetector.EnumerateCandidateGameRootsForTests().ToArray();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Contains(Path.Combine(appData, ".minecraft"), roots);
    }
}
