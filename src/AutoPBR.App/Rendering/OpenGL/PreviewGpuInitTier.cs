namespace AutoPBR.App.Rendering.OpenGL;

[Flags]
internal enum PreviewGpuInitTier
{
    None = 0,
    Core = 1 << 0,
    GodRays = 1 << 1,
    Clouds = 1 << 2,
    PreviewTaa = 1 << 3,
    ScreenSpaceAo = 1 << 4,
}

internal static class PreviewGpuInitTierExtensions
{
    public static bool HasAll(this PreviewGpuInitTier state, PreviewGpuInitTier required) =>
        (state & required) == required;
}
