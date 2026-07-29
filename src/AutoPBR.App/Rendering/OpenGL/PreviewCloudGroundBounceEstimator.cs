using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Produces a restrained low-frequency linear ground color for CQ3.4 cloud bounce. The estimate
/// is refreshed only when the ground material changes and samples at most 4,096 source texels.
/// </summary>
internal static class PreviewCloudGroundBounceEstimator
{
    public static readonly Vector3 DefaultLinear = new(0.08f, 0.10f, 0.06f);

    public static Vector3 Estimate(PreviewMaterial? material)
    {
        if (material is null)
        {
            return DefaultLinear;
        }

        var rgba = material.AlbedoRgba.Span;
        var declaredPixels =
            (long)Math.Max(0, material.Width) *
            Math.Max(0, material.Height);
        var availablePixels = (int)Math.Min(
            declaredPixels,
            rgba.Length / 4L);
        if (availablePixels <= 0)
        {
            return DefaultLinear;
        }

        var stride = Math.Max(
            1,
            (int)((availablePixels + 4095L) / 4096L));
        var sum = Vector3.Zero;
        var weight = 0f;
        for (var pixel = 0; pixel < availablePixels; pixel += stride)
        {
            var offset = pixel * 4;
            var alpha = rgba[offset + 3] / 255f;
            if (alpha <= 1f / 255f)
            {
                continue;
            }

            sum += new Vector3(
                SrgbByteToLinear(rgba[offset]),
                SrgbByteToLinear(rgba[offset + 1]),
                SrgbByteToLinear(rgba[offset + 2])) * alpha;
            weight += alpha;
        }

        if (weight <= 1e-5f)
        {
            return DefaultLinear;
        }

        // The bounce strength is applied separately in the shader. This clamp only prevents
        // malformed emissive-looking albedo payloads from dominating the atmospheric result.
        return Vector3.Clamp(
            sum / weight,
            new Vector3(0.01f),
            new Vector3(0.65f));
    }

    private static float SrgbByteToLinear(byte value)
    {
        var srgb = value / 255f;
        return srgb <= 0.04045f
            ? srgb / 12.92f
            : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
    }
}
