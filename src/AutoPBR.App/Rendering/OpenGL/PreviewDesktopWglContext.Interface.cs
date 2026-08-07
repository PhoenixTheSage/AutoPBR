using Avalonia.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed partial class PreviewDesktopWglContext
{
    GL IPreviewDesktopGlSidecar.Gl => Gl;

    string IPreviewDesktopGlSidecar.VersionString => VersionString;

    bool IPreviewDesktopGlSidecar.CanAttemptDxInterop => CanAttemptDxInterop;

    bool IPreviewDesktopGlSidecar.DxInteropOptInEnabled => DxInteropOptInEnabled;

    string IPreviewDesktopGlSidecar.LastInteropFailureSummary => LastInteropFailureSummary;

    bool IPreviewDesktopGlSidecar.UsesAsyncPboReadback => UsesAsyncPboReadback;

    bool IPreviewDesktopGlSidecar.IsOwnerThreadLikelyWedged => IsOwnerThreadLikelyWedged;

    bool IPreviewDesktopGlSidecar.UsesDxInteropPresentation => UsesDxInteropPresentation;

    void IPreviewDesktopGlSidecar.EnableDxInteropHangDiagnostics(Action<string> log, Action? requestPresentFrame) =>
        EnableDxInteropHangDiagnostics(log, requestPresentFrame);

    bool IPreviewDesktopGlSidecar.TryRenderViaDxInterop(
        PreviewOpenGlCompositionBridge compositionBridge,
        GlInterface presentationGl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        Action<int> renderCore) =>
        TryRenderViaDxInterop(compositionBridge, presentationGl, framebuffer, pixelWidth, pixelHeight, renderCore);

    void IPreviewDesktopGlSidecar.Invoke(Action work) => Invoke(work);

    IDisposable IPreviewDesktopGlSidecar.BindOnOwnerThread() => BindOnOwnerThread();
}
