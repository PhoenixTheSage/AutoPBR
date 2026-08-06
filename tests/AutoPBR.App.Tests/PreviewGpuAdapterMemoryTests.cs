using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewGpuAdapterMemoryTests
{
    public PreviewGpuAdapterMemoryTests()
    {
        PreviewGpuAdapterMemory.ResetCacheForTests();
    }

    [Fact]
    public void SeedCache_IsReturnedByTryGet()
    {
        PreviewGpuAdapterMemory.SeedCacheForTests(4L * 1024 * 1024 * 1024, "test");
        Assert.True(PreviewGpuAdapterMemory.TryGetDedicatedVideoMemoryBytes(
            gl: null,
            rendererHint: null,
            out var bytes,
            out var source));
        Assert.Equal(4L * 1024 * 1024 * 1024, bytes);
        Assert.Equal("test", source);
    }

    [Fact]
    public void CapabilitiesDiagnostic_IncludesDedicatedVramWhenPresent()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.6.0 NVIDIA",
            "NVIDIA",
            "RTX",
            "GL_ARB_buffer_storage",
            forceOpenGlEs: false) with
        {
            DedicatedVideoMemoryBytes = 8L * 1024 * 1024 * 1024,
            VideoMemorySource = "dxgi:RTX",
        };

        Assert.Contains("dedicatedVram=8192MiB@dxgi:RTX", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("8192MiB VRAM", caps.FormatContextSuffix(), StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilitiesFromStrings_DefaultsVramUnknown()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.6.0",
            "Vendor",
            "Renderer",
            string.Empty,
            forceOpenGlEs: false);
        Assert.Equal(0, caps.DedicatedVideoMemoryBytes);
        Assert.Contains("dedicatedVram=unknown", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("VRAM unknown", caps.FormatContextSuffix(), StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_DxgiQuery_ReportsPositiveDedicatedMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PreviewGpuAdapterMemory.ResetCacheForTests();
        var ok = PreviewGpuAdapterMemory.TryGetDedicatedVideoMemoryBytes(
            gl: null,
            rendererHint: null,
            out var bytes,
            out var source);
        Assert.True(ok, "DXGI adapter enumeration should find a hardware GPU on Windows CI/dev machines.");
        Assert.True(bytes >= 512L * 1024 * 1024, $"expected ≥512MiB dedicated, got {bytes} ({source})");
        Assert.StartsWith("dxgi", source, StringComparison.OrdinalIgnoreCase);
    }
}
