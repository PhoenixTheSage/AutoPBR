using Avalonia.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Desktop OpenGL sidecar used for OpenGL 4.x preview (WGL on Windows, EGL on Linux).</summary>
internal interface IPreviewDesktopGlSidecar : IDisposable
{
    GlInterface GlInterface { get; }

    GL Gl { get; }

    string VersionString { get; }

    bool CanAttemptDxInterop { get; }

    bool DxInteropOptInEnabled { get; }

    string LastInteropFailureSummary { get; }

    bool UsesAsyncPboReadback { get; }

    bool IsOwnerThreadLikelyWedged { get; }

    void EnableDxInteropHangDiagnostics(Action<string> log, Action? requestPresentFrame);

    bool TryRenderViaDxInterop(
        PreviewOpenGlCompositionBridge compositionBridge,
        GlInterface presentationGl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        Action<int> renderCore);

    void ScheduleAsyncPboFrame(
        int width,
        int height,
        Action<int> renderCore,
        bool forceSyncPresent,
        Action requestPresentFrame);

    bool TryCopyLatestColorToEsFbo(
        GlInterface esGlInterface,
        int destFbo,
        int width,
        int height);

    void Invoke(Action work);

    IDisposable BindOnOwnerThread();

    bool UsesDxInteropPresentation { get; }
}
