using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ1.7 trace-target sizing. Cinematic uses an even-rounded two-thirds target while
/// compatibility through High retain the established half-resolution dimensions.
/// </summary>
internal static class PreviewCloudTraceSizing
{
    internal readonly record struct Size(int Width, int Height, float Scale);

    public static Size Resolve(int viewportWidth, int viewportHeight, int volumetricQuality)
    {
        var cinematic =
            PreviewVolumetricQuality.Clamp(volumetricQuality) == PreviewVolumetricQuality.Cinematic;
        return new Size(
            ResolveDimension(viewportWidth, cinematic),
            ResolveDimension(viewportHeight, cinematic),
            cinematic ? 2f / 3f : 0.5f);
    }

    private static int ResolveDimension(int viewportDimension, bool cinematic)
    {
        var dimension = Math.Max(1, viewportDimension);
        if (!cinematic)
        {
            return Math.Max(1, dimension / 2);
        }

        var scaled = (int)Math.Ceiling(dimension * (2.0 / 3.0));
        return Math.Max(2, (scaled + 1) & ~1);
    }
}
