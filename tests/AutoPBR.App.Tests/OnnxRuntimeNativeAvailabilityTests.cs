using System.Text;

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
        if (!File.Exists(model) || IsGitLfsPointer(model))
        {
            // Bundled model optional / may still be an LFS pointer in CI without git-lfs pull.
            return;
        }

        using var options = new SessionOptions();
        using var session = new InferenceSession(model, options);
        Assert.NotNull(session);
    }

    private static bool IsGitLfsPointer(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 1024)
            {
                return false;
            }

            var header = File.ReadAllText(path, Encoding.ASCII);
            return header.StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
