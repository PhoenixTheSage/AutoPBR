using System.Runtime.InteropServices;

using Avalonia.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Creates a surfaceless EGL OpenGL core context on Linux.</summary>
internal static class PreviewDesktopEglBootstrap
{
    private const int EglFalse = 0;
    private const int EglTrue = 1;
    private const int EglSuccess = 0x3000;
    private const int EglNone = 0x3038;
    private const int EglSurfaceType = 0x3033;
    private const int EglPbufferBit = 0x0001;
    private const int EglRenderableType = 0x3040;
    private const int EglOpenglBit = 0x0008;
    private const int EglOpenglApi = 0x30A2;
    private const int EglContextMajorVersion = 0x3098;
    private const int EglContextMinorVersion = 0x30FB;
    private const int EglContextOpenglProfileMask = 0x30FD;
    private const int EglContextOpenglCoreProfileBit = 0x00000001;
    private const int EglWidth = 0x3057;
    private const int EglHeight = 0x3056;

    private static IntPtr _libEgl = IntPtr.Zero;
    private static IntPtr _libGl = IntPtr.Zero;

    public static bool TryProbe() => TryCreateContext(
            [
                new GlVersion(GlProfileType.OpenGL, 4, 6),
                new GlVersion(GlProfileType.OpenGL, 4, 0),
                new GlVersion(GlProfileType.OpenGL, 3, 3),
            ],
            out var disposable,
            out _) &&
        disposable is not null &&
        DisposeQuiet(disposable);

    public static EglDesktopContext? TryCreate(
        IReadOnlyList<GlVersion> profiles,
        Action<string>? log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        if (!TryCreateContext(profiles, out var ctx, out var detail))
        {
            log?.Invoke("[3D preview] EGL desktop context failed: " + detail);
            return null;
        }

        return ctx;
    }

    private static bool DisposeQuiet(EglDesktopContext? ctx)
    {
        ctx?.Dispose();
        return true;
    }

    private static bool TryCreateContext(
        IReadOnlyList<GlVersion> profiles,
        out EglDesktopContext? context,
        out string detail)
    {
        context = null;
        detail = "not linux";
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (!EnsureLibraries(out detail))
        {
            return false;
        }

        var display = eglGetDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            detail = "eglGetDisplay failed";
            return false;
        }

        if (eglInitialize(display, out _, out _) == EglFalse)
        {
            detail = "eglInitialize failed err=0x" + eglGetError().ToString("x");
            return false;
        }

        if (eglBindAPI(EglOpenglApi) == EglFalse)
        {
            detail = "eglBindAPI(OPENGL) failed err=0x" + eglGetError().ToString("x");
            return false;
        }

        int[] cfgAttribs =
        [
            EglSurfaceType, EglPbufferBit,
            EglRenderableType, EglOpenglBit,
            EglNone,
        ];
        var configs = new IntPtr[1];
        if (eglChooseConfig(display, cfgAttribs, configs, 1, out var numConfigs) == EglFalse || numConfigs < 1)
        {
            detail = "eglChooseConfig failed err=0x" + eglGetError().ToString("x");
            return false;
        }

        var config = configs[0];
        int[] pbufferAttribs = [EglWidth, 16, EglHeight, 16, EglNone];
        var surface = eglCreatePbufferSurface(display, config, pbufferAttribs);
        if (surface == IntPtr.Zero)
        {
            detail = "eglCreatePbufferSurface failed err=0x" + eglGetError().ToString("x");
            return false;
        }

        foreach (var profile in profiles)
        {
            if (profile.Type != GlProfileType.OpenGL)
            {
                continue;
            }

            int[] ctxAttribs =
            [
                EglContextMajorVersion, profile.Major,
                EglContextMinorVersion, profile.Minor,
                EglContextOpenglProfileMask, EglContextOpenglCoreProfileBit,
                EglNone,
            ];
            var eglContext = eglCreateContext(display, config, IntPtr.Zero, ctxAttribs);
            if (eglContext == IntPtr.Zero)
            {
                continue;
            }

            if (eglMakeCurrent(display, surface, surface, eglContext) == EglFalse)
            {
                eglDestroyContext(display, eglContext);
                continue;
            }

            context = new EglDesktopContext(display, surface, eglContext, profile);
            detail = "ok";
            return true;
        }

        eglDestroySurface(display, surface);
        detail = "no matching OpenGL core context (tried 4.6/4.0/3.3)";
        return false;
    }

    private static bool EnsureLibraries(out string detail)
    {
        if (_libEgl != IntPtr.Zero && _libGl != IntPtr.Zero)
        {
            detail = "ok";
            return true;
        }

        _libEgl = dlopen("libEGL.so.1", 2 /* RTLD_NOW */);
        if (_libEgl == IntPtr.Zero)
        {
            _libEgl = dlopen("libEGL.so", 2);
        }

        _libGl = dlopen("libGL.so.1", 2);
        if (_libGl == IntPtr.Zero)
        {
            _libGl = dlopen("libGL.so", 2);
        }

        if (_libEgl == IntPtr.Zero || _libGl == IntPtr.Zero)
        {
            detail = "libEGL/libGL not found";
            return false;
        }

        detail = "ok";
        return true;
    }

    internal static IntPtr GetProcAddress(string name)
    {
        var proc = eglGetProcAddress(name);
        if (proc != IntPtr.Zero)
        {
            return proc;
        }

        return _libGl != IntPtr.Zero ? dlsym(_libGl, name) : IntPtr.Zero;
    }

    internal sealed class EglDesktopContext : IDisposable
    {
        private readonly IntPtr _display;
        private readonly IntPtr _surface;
        private readonly IntPtr _context;
        private bool _disposed;

        public EglDesktopContext(IntPtr display, IntPtr surface, IntPtr context, GlVersion version)
        {
            _display = display;
            _surface = surface;
            _context = context;
            Version = version;
            GlInterface = new GlInterface(version, GetProcAddress);
        }

        public GlVersion Version { get; }

        public GlInterface GlInterface { get; }

        public void MakeCurrent()
        {
            if (eglMakeCurrent(_display, _surface, _surface, _context) == EglFalse)
            {
                throw new InvalidOperationException("eglMakeCurrent failed err=0x" + eglGetError().ToString("x"));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            eglDestroyContext(_display, _context);
            eglDestroySurface(_display, _surface);
        }
    }

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlsym(IntPtr handle, string symbol);

    [DllImport("libEGL.so.1")]
    private static extern IntPtr eglGetDisplay(IntPtr displayId);

    [DllImport("libEGL.so.1")]
    private static extern int eglInitialize(IntPtr display, out int major, out int minor);

    [DllImport("libEGL.so.1")]
    private static extern int eglBindAPI(int api);

    [DllImport("libEGL.so.1")]
    private static extern int eglChooseConfig(
        IntPtr display,
        int[] attribList,
        [Out] IntPtr[] configs,
        int configSize,
        out int numConfig);

    [DllImport("libEGL.so.1")]
    private static extern IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attribList);

    [DllImport("libEGL.so.1")]
    private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attribList);

    [DllImport("libEGL.so.1")]
    private static extern int eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

    [DllImport("libEGL.so.1")]
    private static extern int eglDestroyContext(IntPtr display, IntPtr context);

    [DllImport("libEGL.so.1")]
    private static extern int eglDestroySurface(IntPtr display, IntPtr surface);

    [DllImport("libEGL.so.1")]
    private static extern int eglGetError();

    [DllImport("libEGL.so.1")]
    private static extern IntPtr eglGetProcAddress(string procname);
}
