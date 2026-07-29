using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CPU-only preparation of bundled preview textures. GL bootstrap polls these
/// tasks and performs only the context-affine upload once bytes are ready.
/// </summary>
internal static class PreviewBundledGpuAssetPrewarm
{
    private static readonly object Gate = new();
    private static Task<byte[]>? _moonTask;
    private static Task<PreviewMaterial?>? _groundTask;

    public static bool IsMoonReady =>
        Volatile.Read(ref _moonTask)?.IsCompleted == true;

    public static bool IsGroundReady =>
        Volatile.Read(ref _groundTask)?.IsCompleted == true;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            _moonTask ??= Task.Run(
                PreviewMoonDiscTextureGenerator.GenerateRgba8);
            _groundTask ??= Task.Run(() =>
                PreviewBundledGroundMapsLoader.TryLoad(out var material)
                    ? material
                    : null);
        }
    }

    public static bool TryGetMoon(out byte[] rgba)
    {
        var task = Volatile.Read(ref _moonTask);
        if (task is { IsCompletedSuccessfully: true })
        {
            rgba = task.Result;
            return rgba.Length ==
                PreviewMoonDiscTextureGenerator.Size *
                PreviewMoonDiscTextureGenerator.Size * 4;
        }

        rgba = [];
        return false;
    }

    public static bool TryGetGround(out PreviewMaterial material)
    {
        var task = Volatile.Read(ref _groundTask);
        if (task is { IsCompletedSuccessfully: true } &&
            task.Result is { } ready)
        {
            material = ready;
            return true;
        }

        material = null!;
        return false;
    }
}
