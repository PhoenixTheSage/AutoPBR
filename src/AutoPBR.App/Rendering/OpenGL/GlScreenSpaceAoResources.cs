using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Half-res AO + blur ping-pong + optional temporal history targets.</summary>
internal sealed class GlScreenSpaceAoResources(GL gl, bool useOpenGlEs) : IDisposable
{
    private readonly GlColorRenderTarget _raw = new(gl, useOpenGlEs);
    private readonly GlColorRenderTarget _blurA = new(gl, useOpenGlEs);
    private readonly GlColorRenderTarget _blurB = new(gl, useOpenGlEs);
    private readonly GlColorRenderTarget _historyA = new(gl, useOpenGlEs);
    private readonly GlColorRenderTarget _historyB = new(gl, useOpenGlEs);
    private bool _historyUseA = true;
    private bool _disposed;

    public GlColorRenderTarget Raw => _raw;
    public GlColorRenderTarget BlurA => _blurA;
    public GlColorRenderTarget BlurB => _blurB;
    public bool IsValid => _raw.IsValid && _blurA.IsValid && _blurB.IsValid;

    public bool EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return _raw.EnsureSize(width, height) &&
               _blurA.EnsureSize(width, height) &&
               _blurB.EnsureSize(width, height) &&
               _historyA.EnsureSize(width, height) &&
               _historyB.EnsureSize(width, height);
    }

    public GlColorRenderTarget CurrentHistory => _historyUseA ? _historyA : _historyB;
    public GlColorRenderTarget NextHistory => _historyUseA ? _historyB : _historyA;

    public void SwapHistory() => _historyUseA = !_historyUseA;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _raw.Dispose();
        _blurA.Dispose();
        _blurB.Dispose();
        _historyA.Dispose();
        _historyB.Dispose();
    }
}
