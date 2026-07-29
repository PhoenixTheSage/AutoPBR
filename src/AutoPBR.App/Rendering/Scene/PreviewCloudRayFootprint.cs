namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ2.4 screen- and march-space footprint policy shared by CPU validation and
/// the cloud shader uniform contract.
/// </summary>
internal static class PreviewCloudRayFootprint
{
    public static float ComputePixelAngularSize(
        float verticalFieldOfViewRadians,
        int traceTargetHeight)
    {
        var height = Math.Max(traceTargetHeight, 1);
        var fov = float.IsFinite(verticalFieldOfViewRadians)
            ? Math.Clamp(verticalFieldOfViewRadians, 1e-4f, MathF.PI - 1e-4f)
            : 42f * (MathF.PI / 180f);
        return 2f * MathF.Tan(fov * 0.5f) / height;
    }

    public static float ComputeLod(
        float rayDistance,
        float marchStepLength,
        float pixelAngularSize,
        float worldRepeatSize,
        int textureDimension,
        float lodBias = 0f)
    {
        var dimension = Math.Max(textureDimension, 1);
        var repeat = Math.Max(worldRepeatSize, 1e-4f);
        var pixelFootprint =
            Math.Max(rayDistance, 0f) * Math.Max(pixelAngularSize, 0f);
        var sampleFootprint = Math.Max(Math.Max(marchStepLength, 0f), pixelFootprint);
        var worldTexelSize = repeat / dimension;
        var lod = MathF.Log2(Math.Max(sampleFootprint / worldTexelSize, 1f)) + lodBias;
        var maxMip = MathF.Floor(MathF.Log2(dimension));
        return Math.Clamp(lod, 0f, maxMip);
    }
}
