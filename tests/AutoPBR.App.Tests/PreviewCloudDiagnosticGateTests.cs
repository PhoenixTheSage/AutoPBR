using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudDiagnosticGateTests
{
    [Fact]
    public void TryReport_ReportsEachOscillatingStateOnlyOnce()
    {
        var gate = new PreviewCloudDiagnosticGate(capacity: 4);

        Assert.True(gate.TryReport("procedural"));
        Assert.True(gate.TryReport("sparse"));
        Assert.False(gate.TryReport("procedural"));
        Assert.False(gate.TryReport("sparse"));
        Assert.Equal(2, gate.ReportedCount);
    }

    [Fact]
    public void TryReport_StopsNewStatesAtHardCapacity()
    {
        var gate = new PreviewCloudDiagnosticGate(capacity: 2);

        Assert.True(gate.TryReport("first"));
        Assert.True(gate.TryReport("second"));
        Assert.False(gate.TryReport("third"));
        Assert.Equal(2, gate.ReportedCount);
    }
}
