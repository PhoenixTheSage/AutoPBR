using System.Runtime.InteropServices;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>KHR_parallel_shader_compile when available (ANGLE on Windows often supports it).</summary>
internal sealed class GlParallelShaderCompile
{
    private const int CompletionStatusKhr = 0x91B1;

    private readonly GL _gl;
    private readonly MaxShaderCompilerThreadsKhr? _maxCompilerThreads;

    public bool IsSupported { get; }

    public GlParallelShaderCompile(GL gl)
    {
        _gl = gl;
        IsSupported =
            HasExtension(gl, "GL_KHR_parallel_shader_compile") ||
            HasExtension(gl, "GL_ARB_parallel_shader_compile");
        _maxCompilerThreads = IsSupported ? TryResolveMaxCompilerThreads(gl) : null;
    }

    /// <summary>
    /// Cap driver-side parallel shader compiler threads so WGL bootstrap does not saturate
    /// the GPU/CPU to the detriment of other apps. No-op when the entry point is missing.
    /// </summary>
    public bool LimitCompilerThreads(uint threads)
    {
        if (_maxCompilerThreads is null)
        {
            return false;
        }

        _maxCompilerThreads(threads);
        return true;
    }

    public void WaitForShader(uint shader)
    {
        if (!IsSupported)
        {
            return;
        }

        // Soft-poll instead of a tight spin — keeps the WGL owner thread from burning a core
        // while the driver compiles, and gives other processes a chance to run.
        int complete;
        do
        {
            _gl.GetShader(shader, (GLEnum)CompletionStatusKhr, out complete);
            if (complete == 0)
            {
                Thread.Sleep(1);
            }
        }
        while (complete == 0);
    }

    private static MaxShaderCompilerThreadsKhr? TryResolveMaxCompilerThreads(GL gl)
    {
        if (gl.Context.TryGetProcAddress("glMaxShaderCompilerThreadsKHR", out var proc) &&
            proc != IntPtr.Zero)
        {
            return Marshal.GetDelegateForFunctionPointer<MaxShaderCompilerThreadsKhr>(proc);
        }

        if (gl.Context.TryGetProcAddress("glMaxShaderCompilerThreadsARB", out proc) &&
            proc != IntPtr.Zero)
        {
            return Marshal.GetDelegateForFunctionPointer<MaxShaderCompilerThreadsKhr>(proc);
        }

        return null;
    }

    private static bool HasExtension(GL gl, string extension)
    {
        // OpenGL 3+ core profiles reject glGetString(GL_EXTENSIONS). Enumerate with
        // glGetStringi; otherwise the desktop WGL path silently misses KHR parallel compile
        // and lets the NVIDIA driver consume every CPU core.
        try
        {
            var count = gl.GetInteger(GetPName.NumExtensions);
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(
                        gl.GetStringS(StringName.Extensions, (uint)i),
                        extension,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            // ANGLE/legacy contexts may not expose indexed extension strings.
            var extensions = gl.GetStringS(StringName.Extensions) ?? string.Empty;
            return extensions
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(extension, StringComparer.Ordinal);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MaxShaderCompilerThreadsKhr(uint count);
}
