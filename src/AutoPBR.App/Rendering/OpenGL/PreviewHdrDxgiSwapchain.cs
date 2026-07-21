using System.Runtime.InteropServices;

using Avalonia.OpenGL;
using Avalonia.Threading;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// DXGI scRGB present for native WGL HDR.
/// GL renders into a stable shared D3D11 texture (NV_DX interop registered once);
/// each Present copies that texture into a flip backbuffer owned by a <em>child</em> HWND
/// so the WGL preview HWND is never bound to DXGI (SDR SwapBuffers stays safe).
/// </summary>
internal sealed class PreviewHdrDxgiSwapchain : IDisposable
{
    private static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDXGISwapChain3 = new("94d99bdb-f1f8-4ab0-b742-7e6eabb0e555");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private const int S_OK = 0;
    private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    private const int DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x00000020;
    private const int DXGI_SCALING_STRETCH = 0;
    private const int DXGI_SWAP_EFFECT_FLIP_DISCARD = 4;
    private const int DXGI_ALPHA_MODE_IGNORE = 3;
    private const int DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 = 16;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_FEATURE_LEVEL_11_0 = 0xb000;
    private const uint D3D11_BIND_RENDER_TARGET = 0x20;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_RESOURCE_MISC_SHARED = 0x2;
    private const int D3D11_USAGE_DEFAULT = 0;

    // ID3D11Device::CreateTexture2D
    private const int CreateTexture2DVtableIndex = 5;
    // ID3D11DeviceContext::CopyResource / ClearState / Flush
    private const int CopyResourceVtableIndex = 47;
    private const int ClearStateVtableIndex = 110;
    private const int FlushVtableIndex = 111;

    private const string PresentClassName = "AutoPBR.HdrDxgiPresentHost";
    private const int WsChild = 0x4000_0000;
    private const int WsVisible = 0x1000_0000;
    private const int WsClipSiblings = 0x0400_0000;
    private const int WsExTransparent = 0x0000_0020;
    private const int WsExNoActivate = 0x0800_0000;
    private const uint WmEraseBkgnd = 0x0014;
    private const IntPtr HwndTop = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoCopyBits = 0x0100;

    private static readonly object PresentClassGate = new();
    private static readonly WndProcDelegate PresentWndProc = PresentWndProcCore;
    private static bool _presentClassRegistered;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr thisPtr, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSwapChainForHwndDelegate(
        IntPtr thisPtr,
        IntPtr pDevice,
        IntPtr hWnd,
        ref DxgiSwapChainDesc1 pDesc,
        IntPtr pFullscreenDesc,
        IntPtr pRestrictToOutput,
        out IntPtr ppSwapChain);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetColorSpace1Delegate(IntPtr thisPtr, int colorSpace);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr thisPtr, uint buffer, ref Guid riid, out IntPtr ppSurface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(IntPtr thisPtr, uint syncInterval, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResizeBuffersDelegate(
        IntPtr thisPtr,
        uint bufferCount,
        uint width,
        uint height,
        int newFormat,
        uint swapChainFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(
        IntPtr thisPtr,
        ref D3D11Texture2DDesc desc,
        IntPtr initialData,
        out IntPtr ppTexture2D);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr thisPtr, IntPtr dst, IntPtr src);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearStateDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FlushDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public WndProcDelegate LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClass lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSwapChainDesc1
    {
        public uint Width;
        public uint Height;
        public int Format;
        public int Stereo;
        public DxgiSampleDesc SampleDesc;
        public int BufferUsage;
        public uint BufferCount;
        public int Scaling;
        public int SwapEffect;
        public int AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public DxgiSampleDesc SampleDesc;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _factory;
    private IntPtr _swapChain;
    private IntPtr _swapChain3;
    private IntPtr _sharedTexture;
    private IntPtr _dxInteropDevice;
    private IntPtr _registeredObject;
    private uint _glTexture;
    private uint _glFbo;
    private uint _glDepthRb;
    private uint _flipTexture;
    private uint _flipProgram;
    private uint _flipVao;
    private int _flipTexLoc = -1;
    private int _width;
    private int _height;
    private IntPtr _parentHwnd;
    private IntPtr _presentHwnd;
    private int _presentHwndWidth;
    private int _presentHwndHeight;
    private int _presentWindowCreateInFlight;
    private bool _disposed;
    private string? _lastFailure;
    private bool _frameLocked;

    private const string FlipVertexSrc =
        """
        #version 330 core
        out vec2 vUv;
        void main()
        {
            // Fullscreen triangle; flip V so GL bottom-up matches DXGI top-down.
            float x = (gl_VertexID == 2) ? 3.0 : -1.0;
            float y = (gl_VertexID == 1) ? 3.0 : -1.0;
            vUv = vec2(x, y) * 0.5 + 0.5;
            vUv.y = 1.0 - vUv.y;
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private const string FlipFragmentSrc =
        """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTex;
        out vec4 FragColor;
        void main()
        {
            FragColor = texture(uTex, vUv);
        }
        """;

    public bool IsActive =>
        _swapChain != IntPtr.Zero &&
        _sharedTexture != IntPtr.Zero &&
        _glFbo != 0 &&
        _registeredObject != IntPtr.Zero &&
        !_disposed;

    public int Framebuffer => (int)_glFbo;
    public string? LastFailure => _lastFailure;

    /// <summary>Returned when the present child HWND is still being created on the UI thread.</summary>
    public const string PresentWindowPendingMessage = "HDR present HWND is being created on the UI thread.";

    public bool TryEnsure(
        IntPtr hwnd,
        int width,
        int height,
        GlInterface wglGl,
        GL gl,
        out string? failure)
    {
        failure = null;
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero || _disposed)
        {
            failure = "HDR present unavailable on this platform.";
            _lastFailure = failure;
            return false;
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        // Present child HWND is owned by the Avalonia UI thread. Never CreateWindow/Wait from the
        // WGL owner — that deadlocks with UI→Owner.Run(Dispose).
        if (!EnsurePresentWindow(hwnd, width, height, out failure))
        {
            _lastFailure = failure;
            return false;
        }

        var needsRecreate =
            _parentHwnd != hwnd ||
            _device == IntPtr.Zero ||
            _width != width ||
            _height != height;
        if (needsRecreate)
        {
            EndFrame();
            TeardownGl(gl);
            // Keep the UI-owned present HWND; only rebuild DXGI/GL around it.
            TeardownDxgi(disposePresentWindow: false);
            if (!TryCreateDeviceAndSwapchain(width, height, out failure) ||
                !TryCreateSharedTexture(width, height, out failure) ||
                !TryRegisterGlInterop(wglGl, gl, width, height, out failure))
            {
                _lastFailure = failure;
                TeardownGl(gl);
                TeardownDxgi(disposePresentWindow: false);
                return false;
            }

            _parentHwnd = hwnd;
            _width = width;
            _height = height;
        }

        _lastFailure = null;
        return IsActive;
    }

    public bool TryBeginFrame(GL gl, out string? failure)
    {
        failure = null;
        if (!IsActive)
        {
            failure = "HDR swapchain inactive.";
            return false;
        }

        if (_frameLocked)
        {
            return true;
        }

        if (!PreviewDesktopWglDxInterop.TryLockObject(_dxInteropDevice, _registeredObject))
        {
            failure = "wglDXLockObjectsNV failed for HDR shared texture.";
            _lastFailure = failure;
            return false;
        }

        _frameLocked = true;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _glFbo);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            EndFrame();
            failure = $"HDR interop FBO incomplete ({status}).";
            _lastFailure = failure;
            return false;
        }

        return true;
    }

    public void EndFrame()
    {
        if (!_frameLocked)
        {
            return;
        }

        if (_dxInteropDevice != IntPtr.Zero && _registeredObject != IntPtr.Zero)
        {
            PreviewDesktopWglDxInterop.TryUnlockObject(_dxInteropDevice, _registeredObject);
        }

        _frameLocked = false;
    }

    /// <summary>
    /// Flips the locked HDR color buffer in Y so GL (bottom-up) matches DXGI (top-down).
    /// Must run after the full frame (including overlays) and before <see cref="EndFrame"/>.
    /// Uses CopyTexSubImage + a tiny shader (BlitFramebuffer Y-invert is unreliable on NV_DX interop).
    /// </summary>
    public bool TryFlipFramebufferY(GL gl)
    {
        if (!_frameLocked || _glFbo == 0 || _flipTexture == 0 || _flipProgram == 0 ||
            _width <= 0 || _height <= 0)
        {
            _lastFailure = "HDR Y-flip resources missing.";
            return false;
        }

        // Scene/overlay passes often leave sticky GL errors; ignore those before our work.
        DrainGlErrors(gl);

        var priorRead = gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = gl.GetInteger(GetPName.DrawFramebufferBinding);
        var priorProgram = gl.GetInteger(GetPName.CurrentProgram);
        var priorVao = gl.GetInteger(GetPName.VertexArrayBinding);
        var priorTex = gl.GetInteger(GetPName.TextureBinding2D);
        var priorViewport = new int[4];
        gl.GetInteger(GetPName.Viewport, priorViewport);
        var depthWasEnabled = gl.IsEnabled(EnableCap.DepthTest);
        var blendWasEnabled = gl.IsEnabled(EnableCap.Blend);
        var cullWasEnabled = gl.IsEnabled(EnableCap.CullFace);
        var depthMask = gl.GetInteger(GetPName.DepthWritemask) != 0;

        try
        {
            // Staging copy off the locked interop color attachment (same orientation).
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _glFbo);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            gl.BindTexture(TextureTarget.Texture2D, _flipTexture);
            gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, (uint)_width, (uint)_height);
            var copyErr = gl.GetError();
            if (copyErr != GLEnum.NoError)
            {
                _lastFailure = $"HDR Y-flip CopyTexSubImage2D failed ({copyErr}).";
                return false;
            }

            // Draw flipped staging texture back into the interop FBO.
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _glFbo);
            gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
            gl.Viewport(0, 0, (uint)_width, (uint)_height);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.CullFace);
            gl.DepthMask(false);
            gl.UseProgram(_flipProgram);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _flipTexture);
            if (_flipTexLoc >= 0)
            {
                gl.Uniform1(_flipTexLoc, 0);
            }

            gl.BindVertexArray(_flipVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            var drawErr = gl.GetError();
            if (drawErr != GLEnum.NoError)
            {
                _lastFailure = $"HDR Y-flip draw failed ({drawErr}).";
                return false;
            }

            _lastFailure = null;
            return true;
        }
        finally
        {
            gl.DepthMask(depthMask);
            if (depthWasEnabled) gl.Enable(EnableCap.DepthTest); else gl.Disable(EnableCap.DepthTest);
            if (blendWasEnabled) gl.Enable(EnableCap.Blend); else gl.Disable(EnableCap.Blend);
            if (cullWasEnabled) gl.Enable(EnableCap.CullFace); else gl.Disable(EnableCap.CullFace);
            gl.Viewport(priorViewport[0], priorViewport[1], (uint)priorViewport[2], (uint)priorViewport[3]);
            gl.BindTexture(TextureTarget.Texture2D, (uint)Math.Max(0, priorTex));
            gl.BindVertexArray((uint)Math.Max(0, priorVao));
            gl.UseProgram((uint)Math.Max(0, priorProgram));
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        }
    }

    public bool TryPresent(bool vsync, out string? failure)
    {
        failure = null;
        if (_swapChain == IntPtr.Zero || _sharedTexture == IntPtr.Zero || _context == IntPtr.Zero)
        {
            failure = "HDR swapchain missing.";
            return false;
        }

        if (_frameLocked)
        {
            EndFrame();
        }

        var iid = IID_ID3D11Texture2D;
        var getBuffer = GetVTableFn<GetBufferDelegate>(_swapChain, 9);
        var hr = getBuffer(_swapChain, 0, ref iid, out var backBuffer);
        if (hr != S_OK || backBuffer == IntPtr.Zero)
        {
            failure = $"GetBuffer failed (hr=0x{hr:X8}).";
            _lastFailure = failure;
            return false;
        }

        try
        {
            var copy = GetVTableFn<CopyResourceDelegate>(_context, CopyResourceVtableIndex);
            copy(_context, backBuffer, _sharedTexture);

            if (_swapChain3 != IntPtr.Zero)
            {
                var setCs = GetVTableFn<SetColorSpace1Delegate>(_swapChain3, 37);
                _ = setCs(_swapChain3, DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
            }

            var present = GetVTableFn<PresentDelegate>(_swapChain, 8);
            hr = present(_swapChain, vsync ? 1u : 0u, 0);
            if (hr != S_OK && hr != unchecked((int)0x087A0001))
            {
                failure = $"IDXGISwapChain::Present failed (hr=0x{hr:X8}).";
                _lastFailure = failure;
                return false;
            }

            return true;
        }
        finally
        {
            ReleaseCom(ref backBuffer);
        }
    }

    public void Dispose() => Dispose(gl: null);

    /// <param name="gl">
    /// Current WGL context API when available. Required to close NV_DX interop and delete GL objects safely.
    /// </param>
    public void Dispose(GL? gl)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndFrame();
        TeardownGl(gl);
        TeardownDxgi();
    }

    private bool EnsurePresentWindow(IntPtr parentHwnd, int width, int height, out string? failure)
    {
        failure = null;
        if (!EnsurePresentClassRegistered())
        {
            failure = "HDR present window class registration failed.";
            return false;
        }

        var present = Volatile.Read(ref _presentHwnd);
        var parentOk = _parentHwnd == parentHwnd || _parentHwnd == IntPtr.Zero;
        var sizeOk = _presentHwndWidth == width && _presentHwndHeight == height;
        if (present != IntPtr.Zero && parentOk && sizeOk)
        {
            _parentHwnd = parentHwnd;
            return true;
        }

        // Parent changed: drop the old child (UI thread) and request a new one.
        if (present != IntPtr.Zero && _parentHwnd != IntPtr.Zero && _parentHwnd != parentHwnd)
        {
            DestroyPresentWindow();
            present = IntPtr.Zero;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!TryCreateOrResizePresentWindowCore(parentHwnd, width, height, out failure))
            {
                return false;
            }

            _parentHwnd = parentHwnd;
            return true;
        }

        if (Interlocked.CompareExchange(ref _presentWindowCreateInFlight, 1, 0) == 0)
        {
            var capturedParent = parentHwnd;
            var capturedW = width;
            var capturedH = height;
            Dispatcher.UIThread.Post(
                () =>
                {
                    try
                    {
                        _ = TryCreateOrResizePresentWindowCore(capturedParent, capturedW, capturedH, out _);
                        _parentHwnd = capturedParent;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _presentWindowCreateInFlight, 0);
                    }
                },
                DispatcherPriority.Send);
        }

        failure = PresentWindowPendingMessage;
        return false;
    }

    private bool TryCreateOrResizePresentWindowCore(IntPtr parentHwnd, int width, int height, out string? failure)
    {
        failure = null;
        var existing = _presentHwnd;
        if (existing != IntPtr.Zero && _parentHwnd == parentHwnd)
        {
            _ = SetWindowPos(
                existing,
                HwndTop,
                0,
                0,
                width,
                height,
                SwpShowWindow | SwpNoActivate | SwpNoCopyBits);
            _presentHwndWidth = width;
            _presentHwndHeight = height;
            return true;
        }

        if (existing != IntPtr.Zero)
        {
            _ = DestroyWindow(existing);
            _presentHwnd = IntPtr.Zero;
        }

        var hwnd = CreateWindowExW(
            WsExTransparent | WsExNoActivate,
            PresentClassName,
            "AutoPBR HDR Present",
            WsChild | WsVisible | WsClipSiblings,
            0,
            0,
            width,
            height,
            parentHwnd,
            IntPtr.Zero,
            GetModuleHandleW(IntPtr.Zero),
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            failure = "CreateWindowEx for HDR present child failed.";
            return false;
        }

        _ = SetWindowPos(
            hwnd,
            HwndTop,
            0,
            0,
            width,
            height,
            SwpShowWindow | SwpNoActivate | SwpNoCopyBits);
        _presentHwnd = hwnd;
        _presentHwndWidth = width;
        _presentHwndHeight = height;
        return true;
    }

    private void DestroyPresentWindow()
    {
        var hwnd = Interlocked.Exchange(ref _presentHwnd, IntPtr.Zero);
        _presentHwndWidth = 0;
        _presentHwndHeight = 0;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            _ = DestroyWindow(hwnd);
            return;
        }

        // Never Wait here: UI may already be blocked inside Owner.Run(Dispose).
        Dispatcher.UIThread.Post(() => DestroyWindow(hwnd), DispatcherPriority.Send);
    }

    private static bool EnsurePresentClassRegistered()
    {
        lock (PresentClassGate)
        {
            if (_presentClassRegistered)
            {
                return true;
            }

            var wndClass = new WndClass
            {
                Style = 0,
                LpfnWndProc = PresentWndProc,
                CbClsExtra = 0,
                CbWndExtra = 0,
                HInstance = GetModuleHandleW(IntPtr.Zero),
                HIcon = IntPtr.Zero,
                HCursor = IntPtr.Zero,
                HbrBackground = IntPtr.Zero,
                LpszMenuName = null,
                LpszClassName = PresentClassName
            };

            var atom = RegisterClassW(ref wndClass);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                // Already registered in this process.
                if (err != 1410)
                {
                    return false;
                }
            }

            _presentClassRegistered = true;
            return true;
        }
    }

    private static IntPtr PresentWndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmEraseBkgnd)
        {
            return 1;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool TryCreateDeviceAndSwapchain(int width, int height, out string? failure)
    {
        failure = null;
        if (_presentHwnd == IntPtr.Zero)
        {
            failure = "HDR present HWND missing.";
            return false;
        }

        IntPtr device;
        IntPtr context;
        int featureLevel;
        int hr;
        unsafe
        {
            var level = D3D_FEATURE_LEVEL_11_0;
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                (IntPtr)(&level),
                1,
                D3D11_SDK_VERSION,
                out device,
                out featureLevel,
                out context);
        }

        if (hr != S_OK || device == IntPtr.Zero)
        {
            failure = $"D3D11CreateDevice failed (hr=0x{hr:X8}).";
            return false;
        }

        _device = device;
        _context = context;

        if (!TryGetFactory2(device, out var factory, out failure))
        {
            TeardownDxgi();
            return false;
        }

        _factory = factory;

        var desc = new DxgiSwapChainDesc1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = DXGI_FORMAT_R16G16B16A16_FLOAT,
            Stereo = 0,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = 2,
            Scaling = DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode = DXGI_ALPHA_MODE_IGNORE,
            Flags = 0
        };

        var createSc = GetVTableFn<CreateSwapChainForHwndDelegate>(factory, 15);
        hr = createSc(factory, device, _presentHwnd, ref desc, IntPtr.Zero, IntPtr.Zero, out var swapChain);
        if (hr != S_OK || swapChain == IntPtr.Zero)
        {
            failure = $"CreateSwapChainForHwnd failed (hr=0x{hr:X8}).";
            TeardownDxgi();
            return false;
        }

        _swapChain = swapChain;
        var sc3Iid = IID_IDXGISwapChain3;
        var qi = GetVTableFn<QueryInterfaceDelegate>(swapChain, 0);
        if (qi(swapChain, ref sc3Iid, out var sc3) == S_OK && sc3 != IntPtr.Zero)
        {
            _swapChain3 = sc3;
            var setCs = GetVTableFn<SetColorSpace1Delegate>(_swapChain3, 37);
            hr = setCs(_swapChain3, DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
            if (hr != S_OK)
            {
                failure = $"SetColorSpace1(scRGB) failed (hr=0x{hr:X8}).";
                TeardownDxgi();
                return false;
            }
        }

        return true;
    }

    private bool TryCreateSharedTexture(int width, int height, out string? failure)
    {
        failure = null;
        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_R16G16B16A16_FLOAT,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
            CpuAccessFlags = 0,
            MiscFlags = D3D11_RESOURCE_MISC_SHARED
        };

        var createTex = GetVTableFn<CreateTexture2DDelegate>(_device, CreateTexture2DVtableIndex);
        var hr = createTex(_device, ref desc, IntPtr.Zero, out var texture);
        if (hr != S_OK || texture == IntPtr.Zero)
        {
            failure = $"CreateTexture2D(shared HDR) failed (hr=0x{hr:X8}).";
            return false;
        }

        _sharedTexture = texture;
        return true;
    }

    private bool TryRegisterGlInterop(GlInterface wglGl, GL gl, int width, int height, out string? failure)
    {
        failure = null;
        if (!PreviewDesktopWglDxInterop.TryOpenDevice(wglGl, _device, out _dxInteropDevice))
        {
            failure = "wglDXOpenDeviceNV unavailable for HDR present.";
            return false;
        }

        _glTexture = gl.GenTexture();
        if (!PreviewDesktopWglDxInterop.TryRegisterTexture2D(
                _dxInteropDevice,
                _sharedTexture,
                _glTexture,
                out _registeredObject))
        {
            failure = "wglDXRegisterObjectNV failed for HDR shared texture.";
            return false;
        }

        _glDepthRb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _glDepthRb);
        gl.RenderbufferStorage(
            RenderbufferTarget.Renderbuffer,
            InternalFormat.DepthComponent24,
            (uint)width,
            (uint)height);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        _glFbo = gl.GenFramebuffer();
        if (!PreviewDesktopWglDxInterop.TryLockObject(_dxInteropDevice, _registeredObject))
        {
            failure = "wglDXLockObjectsNV failed during HDR FBO setup.";
            return false;
        }

        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _glFbo);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _glTexture,
                0);
            gl.FramebufferRenderbuffer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer,
                _glDepthRb);
            var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (status != GLEnum.FramebufferComplete)
            {
                failure = $"HDR interop FBO incomplete ({status}).";
                return false;
            }
        }
        finally
        {
            PreviewDesktopWglDxInterop.TryUnlockObject(_dxInteropDevice, _registeredObject);
        }

        if (!TryCreateFlipResources(gl, width, height, out failure))
        {
            return false;
        }

        return true;
    }

    private bool TryCreateFlipResources(GL gl, int width, int height, out string? failure)
    {
        failure = null;
        _flipTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _flipTexture);
        unsafe
        {
            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba16f,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Rgba,
                PixelType.HalfFloat,
                (void*)0);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        if (_flipProgram == 0)
        {
            if (!TryCreateFlipProgram(gl, out failure))
            {
                return false;
            }
        }

        if (_flipVao == 0)
        {
            _flipVao = gl.GenVertexArray();
        }

        DrainGlErrors(gl);
        return true;
    }

    private bool TryCreateFlipProgram(GL gl, out string? failure)
    {
        failure = null;
        var vs = CompileShader(gl, ShaderType.VertexShader, FlipVertexSrc, out var vsErr);
        if (vs == 0)
        {
            failure = "HDR Y-flip vertex shader: " + (vsErr ?? "compile failed");
            return false;
        }

        var fs = CompileShader(gl, ShaderType.FragmentShader, FlipFragmentSrc, out var fsErr);
        if (fs == 0)
        {
            gl.DeleteShader(vs);
            failure = "HDR Y-flip fragment shader: " + (fsErr ?? "compile failed");
            return false;
        }

        var program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        gl.GetProgram(program, GLEnum.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            failure = "HDR Y-flip program link: " + gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            return false;
        }

        _flipProgram = program;
        _flipTexLoc = gl.GetUniformLocation(program, "uTex");
        return true;
    }

    private static uint CompileShader(GL gl, ShaderType type, string source, out string? error)
    {
        error = null;
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, GLEnum.CompileStatus, out var ok);
        if (ok == 0)
        {
            error = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            return 0;
        }

        return shader;
    }

    private static void DrainGlErrors(GL gl)
    {
        for (var i = 0; i < 16; i++)
        {
            if (gl.GetError() == GLEnum.NoError)
            {
                break;
            }
        }
    }

    private static bool TryGetFactory2(IntPtr device, out IntPtr factory2, out string? failure)
    {
        factory2 = IntPtr.Zero;
        failure = null;
        var dxgiDeviceIid = IID_IDXGIDevice;
        var qi = GetVTableFn<QueryInterfaceDelegate>(device, 0);
        var hr = qi(device, ref dxgiDeviceIid, out var dxgiDevice);
        if (hr != S_OK || dxgiDevice == IntPtr.Zero)
        {
            var factory1Iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            hr = CreateDXGIFactory1(ref factory1Iid, out var factory1);
            if (hr != S_OK || factory1 == IntPtr.Zero)
            {
                failure = $"CreateDXGIFactory1 failed (hr=0x{hr:X8}).";
                return false;
            }

            try
            {
                var factory2Iid = IID_IDXGIFactory2;
                hr = GetVTableFn<QueryInterfaceDelegate>(factory1, 0)(factory1, ref factory2Iid, out factory2);
                if (hr != S_OK || factory2 == IntPtr.Zero)
                {
                    failure = $"QI IDXGIFactory2 failed (hr=0x{hr:X8}).";
                    return false;
                }

                return true;
            }
            finally
            {
                ReleaseCom(ref factory1);
            }
        }

        try
        {
            var getAdapter = GetVTableFn<GetParentOrAdapterDelegate>(dxgiDevice, 7);
            hr = getAdapter(dxgiDevice, out var adapter);
            if (hr != S_OK || adapter == IntPtr.Zero)
            {
                failure = $"IDXGIDevice::GetAdapter failed (hr=0x{hr:X8}).";
                return false;
            }

            try
            {
                var factory2Iid = IID_IDXGIFactory2;
                var getParent = GetVTableFn<GetParentDelegate>(adapter, 6);
                hr = getParent(adapter, ref factory2Iid, out factory2);
                if (hr != S_OK || factory2 == IntPtr.Zero)
                {
                    failure = $"GetParent(IDXGIFactory2) failed (hr=0x{hr:X8}).";
                    return false;
                }

                return true;
            }
            finally
            {
                ReleaseCom(ref adapter);
            }
        }
        finally
        {
            ReleaseCom(ref dxgiDevice);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetParentOrAdapterDelegate(IntPtr thisPtr, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetParentDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr ppParent);

    private void TeardownGl(GL? gl)
    {
        EndFrame();

        if (_dxInteropDevice != IntPtr.Zero && _registeredObject != IntPtr.Zero)
        {
            PreviewDesktopWglDxInterop.UnregisterObject(_dxInteropDevice, _registeredObject);
            _registeredObject = IntPtr.Zero;
        }

        if (_dxInteropDevice != IntPtr.Zero)
        {
            PreviewDesktopWglDxInterop.CloseDevice(_dxInteropDevice);
            _dxInteropDevice = IntPtr.Zero;
        }

        if (gl is not null)
        {
            if (_flipVao != 0)
            {
                gl.DeleteVertexArray(_flipVao);
                _flipVao = 0;
            }

            if (_flipProgram != 0)
            {
                gl.DeleteProgram(_flipProgram);
                _flipProgram = 0;
                _flipTexLoc = -1;
            }

            if (_flipTexture != 0)
            {
                gl.DeleteTexture(_flipTexture);
                _flipTexture = 0;
            }

            if (_glFbo != 0)
            {
                gl.DeleteFramebuffer(_glFbo);
                _glFbo = 0;
            }

            if (_glDepthRb != 0)
            {
                gl.DeleteRenderbuffer(_glDepthRb);
                _glDepthRb = 0;
            }

            if (_glTexture != 0)
            {
                gl.DeleteTexture(_glTexture);
                _glTexture = 0;
            }
        }
        else
        {
            _flipVao = 0;
            _flipProgram = 0;
            _flipTexLoc = -1;
            _flipTexture = 0;
            _glFbo = 0;
            _glDepthRb = 0;
            _glTexture = 0;
        }
    }

    private void TeardownDxgi(bool disposePresentWindow = true)
    {
        ReleaseCom(ref _sharedTexture);
        ReleaseCom(ref _swapChain3);
        // Flip-model swapchains defer destruction; ClearState+Flush forces HWND release.
        ReleaseCom(ref _swapChain);
        if (_context != IntPtr.Zero)
        {
            try
            {
                GetVTableFn<ClearStateDelegate>(_context, ClearStateVtableIndex)(_context);
                GetVTableFn<FlushDelegate>(_context, FlushVtableIndex)(_context);
            }
            catch
            {
                // Best-effort; continue releasing COM objects.
            }
        }

        ReleaseCom(ref _factory);
        ReleaseCom(ref _context);
        ReleaseCom(ref _device);
        if (disposePresentWindow)
        {
            DestroyPresentWindow();
            _parentHwnd = IntPtr.Zero;
        }

        _width = 0;
        _height = 0;
    }

    private static T GetVTableFn<T>(IntPtr comObject, int index) where T : Delegate
    {
        unsafe
        {
            var vtable = *(IntPtr*)comObject;
            return Marshal.GetDelegateForFunctionPointer<T>(((IntPtr*)vtable)[index]);
        }
    }

    private static void ReleaseCom(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return;
        }

        var release = GetVTableFn<ReleaseDelegate>(ptr, 2);
        _ = release(ptr);
        ptr = IntPtr.Zero;
    }
}
