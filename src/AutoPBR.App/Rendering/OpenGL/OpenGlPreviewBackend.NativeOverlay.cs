using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    internal void SetNativeWglOverlay(
        PreviewNativeWglOverlayBitmap? debug,
        PreviewNativeWglOverlayBitmap? fps,
        PreviewNativeWglOverlayBitmap? cpu,
        int marginPixels)
    {
        lock (_sync)
        {
            _nativeOverlayDebug = debug;
            _nativeOverlayFps = fps;
            _nativeOverlayCpu = cpu;
            _nativeOverlayMarginPixels = Math.Max(0, marginPixels);
        }
    }

    private void DrawNativeWglOverlayIfNeeded(GL gl, int presentFbo, int viewportWidth, int viewportHeight)
    {
        PreviewNativeWglOverlayBitmap? debug;
        PreviewNativeWglOverlayBitmap? fps;
        PreviewNativeWglOverlayBitmap? cpu;
        int marginPixels;
        lock (_sync)
        {
            if (!_nativeWglPresenterActive)
            {
                return;
            }

            debug = _nativeOverlayDebug;
            fps = _nativeOverlayFps;
            cpu = _nativeOverlayCpu;
            marginPixels = _nativeOverlayMarginPixels;
        }

        if (debug is null && fps is null && cpu is null)
        {
            return;
        }

        if (_nativeOverlayRenderer is not null && _nativeOverlayShaderRevLoaded != NativeOverlayShaderRev)
        {
            DestroyNativeWglOverlay();
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

            _nativeOverlayShaderRevLoaded = NativeOverlayShaderRev;
        }

        // HDR scRGB: 1.0 = 80 nits. Scale SDR UI bitmaps to paper white so alpha edges are not
        // dissolved by scene values much greater than 1 (bright sky / sunlit blocks).
        var hdrScale = 0f;
        if (HdrPresentActive)
        {
            var paper = _settings.HdrPaperWhiteNits;
            if (paper < 80f)
            {
                paper = 80f;
            }

            hdrScale = paper / 80f;
        }

        // Post passes may leave another FBO bound; overlays must land on the present target.
        if (presentFbo != 0)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)presentFbo);
        }
        else
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        gl.Viewport(0, 0, (uint)Math.Max(1, viewportWidth), (uint)Math.Max(1, viewportHeight));
        _nativeOverlayRenderer.Draw(viewportWidth, viewportHeight, marginPixels, debug, fps, cpu, hdrScale);
    }

    private void DestroyNativeWglOverlay()
    {
        _nativeOverlayRenderer?.Dispose();
        _nativeOverlayRenderer = null;
        _nativeOverlayShaderErrorLogged = false;
        _nativeOverlayShaderRevLoaded = 0;
    }
}
