using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    internal void SetNativeWglOverlay(
        PreviewNativeWglOverlayBitmap? debug,
        PreviewNativeWglOverlayBitmap? fps,
        int marginPixels)
    {
        lock (_sync)
        {
            _nativeOverlayDebug = debug;
            _nativeOverlayFps = fps;
            _nativeOverlayMarginPixels = Math.Max(0, marginPixels);
        }
    }

    private void DrawNativeWglOverlayIfNeeded(GL gl, int viewportWidth, int viewportHeight)
    {
        PreviewNativeWglOverlayBitmap? debug;
        PreviewNativeWglOverlayBitmap? fps;
        int marginPixels;
        lock (_sync)
        {
            if (!_nativeWglPresenterActive)
            {
                return;
            }

            debug = _nativeOverlayDebug;
            fps = _nativeOverlayFps;
            marginPixels = _nativeOverlayMarginPixels;
        }

        if (debug is null && fps is null)
        {
            return;
        }

        if (_nativeOverlayRenderer is null)
        {
            _nativeOverlayRenderer = new GlNativeOverlayRenderer(
                gl,
                _useOpenGlEs,
                _glCapabilities?.CanUsePersistentUploadRing == true,
                out var err);
            if (!_nativeOverlayRenderer.IsValid)
            {
                _nativeOverlayRenderer.Dispose();
                _nativeOverlayRenderer = null;
                if (!_nativeOverlayShaderErrorLogged && !string.IsNullOrWhiteSpace(err))
                {
                    _nativeOverlayShaderErrorLogged = true;
                    EmitDiagnostic("[3D preview] Native WGL overlay shader: " + err);
                }

                return;
            }
        }

        _nativeOverlayRenderer.Draw(viewportWidth, viewportHeight, marginPixels, debug, fps);
    }

    private void DestroyNativeWglOverlay()
    {
        _nativeOverlayRenderer?.Dispose();
        _nativeOverlayRenderer = null;
        _nativeOverlayShaderErrorLogged = false;
    }
}
