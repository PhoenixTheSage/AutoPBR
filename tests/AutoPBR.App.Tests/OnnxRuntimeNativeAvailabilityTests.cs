using Microsoft.ML.OnnxRuntime;

namespace AutoPBR.App.Tests;

public sealed class OnnxRuntimeNativeAvailabilityTests
{
    [Fact]
    public void CpuSession_CanConstructAgainstBundledDeepBumpWhenNativesPresent()
    {
        var model = Path.Combine(
            AppContext.BaseDirectory,
            "Data",
            "ONNX-AI",
            "DeepBump",
            "deepbump256.onnx");
        if (!File.Exists(model))
        {
            // Bundled model optional in some CI layouts.
            return;
        }

        using var options = new SessionOptions();
        using var session = new InferenceSession(model, options);
        Assert.NotNull(session);
    }
}
