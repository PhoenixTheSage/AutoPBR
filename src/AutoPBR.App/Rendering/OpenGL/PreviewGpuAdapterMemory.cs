using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Queries dedicated GPU memory for terrain mesh-pool budgeting.
/// Prefers DXGI adapter descriptors on Windows; falls back to
/// <c>GL_NVX_gpu_memory_info</c> when available.
/// </summary>
internal static class PreviewGpuAdapterMemory
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private const int S_OK = 0;
    private const uint MicrosoftBasicRenderVendorId = 0x1414;
    private const int EnumAdapters1VtableIndex = 12; // IDXGIFactory1
    private const int AdapterGetDesc1VtableIndex = 10; // IDXGIAdapter1
    private const int GpuMemoryInfoDedicatedVidmemNvx = 0x9047;

    private static readonly object Gate = new();
    private static bool _queried;
    private static long _cachedBytes;
    private static string _cachedSource = "none";

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint adapter, out IntPtr adapterOut);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, out DxgiAdapterDesc1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    /// <summary>
    /// Best-effort dedicated video memory in bytes. Returns false when unknown.
    /// Results are cached for the process.
    /// </summary>
    public static bool TryGetDedicatedVideoMemoryBytes(
        GL? gl,
        string? rendererHint,
        out long bytes,
        out string source)
    {
        lock (Gate)
        {
            if (_queried)
            {
                bytes = _cachedBytes;
                source = _cachedSource;
                return bytes > 0;
            }

            _queried = true;
            if (OperatingSystem.IsWindows() &&
                TryQueryDxgi(rendererHint, out bytes, out source))
            {
                _cachedBytes = bytes;
                _cachedSource = source;
                return true;
            }

            if (gl is not null && TryQueryNvx(gl, out bytes, out source))
            {
                _cachedBytes = bytes;
                _cachedSource = source;
                return true;
            }

            bytes = 0;
            source = "unavailable";
            _cachedBytes = 0;
            _cachedSource = source;
            return false;
        }
    }

    /// <summary>Test seam: reset the process cache.</summary>
    internal static void ResetCacheForTests()
    {
        lock (Gate)
        {
            _queried = false;
            _cachedBytes = 0;
            _cachedSource = "none";
        }
    }

    /// <summary>Test seam: seed the cache without DXGI/GL.</summary>
    internal static void SeedCacheForTests(long bytes, string source)
    {
        lock (Gate)
        {
            _queried = true;
            _cachedBytes = Math.Max(0, bytes);
            _cachedSource = source;
        }
    }

    private static bool TryQueryDxgi(string? rendererHint, out long bytes, out string source)
    {
        bytes = 0;
        source = "dxgi";
        var factoryIid = IID_IDXGIFactory1;
        var hr = CreateDXGIFactory1(ref factoryIid, out var factory);
        if (hr != S_OK || factory == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var enumAdapters = GetVTableFn<EnumAdapters1Delegate>(factory, EnumAdapters1VtableIndex);
            long bestBytes = 0;
            var bestDescription = "";
            var matchedHint = false;
            var hint = string.IsNullOrWhiteSpace(rendererHint) ? null : rendererHint;

            for (uint i = 0; ; i++)
            {
                hr = enumAdapters(factory, i, out var adapter);
                if (hr != S_OK || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var getDesc = GetVTableFn<GetDesc1Delegate>(adapter, AdapterGetDesc1VtableIndex);
                    hr = getDesc(adapter, out var desc);
                    if (hr != S_OK)
                    {
                        continue;
                    }

                    if (desc.VendorId == MicrosoftBasicRenderVendorId)
                    {
                        continue;
                    }

                    var dedicated = (long)desc.DedicatedVideoMemory;
                    if (dedicated <= 0)
                    {
                        continue;
                    }

                    var description = desc.Description ?? "";
                    var hintMatch = hint is not null &&
                                    description.Length > 0 &&
                                    hint.Contains(description, StringComparison.OrdinalIgnoreCase);
                    if (!hintMatch && hint is not null && description.Length > 0)
                    {
                        // Renderer strings often contain a shortened adapter name token.
                        foreach (var token in description.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (token.Length >= 4 &&
                                hint.Contains(token, StringComparison.OrdinalIgnoreCase))
                            {
                                hintMatch = true;
                                break;
                            }
                        }
                    }

                    if (hintMatch)
                    {
                        bestBytes = dedicated;
                        bestDescription = description;
                        matchedHint = true;
                        break;
                    }

                    if (!matchedHint && dedicated > bestBytes)
                    {
                        bestBytes = dedicated;
                        bestDescription = description;
                    }
                }
                finally
                {
                    ReleaseCom(adapter);
                }
            }

            if (bestBytes <= 0)
            {
                return false;
            }

            bytes = bestBytes;
            source = matchedHint
                ? $"dxgi:{Sanitize(bestDescription)}"
                : $"dxgi-largest:{Sanitize(bestDescription)}";
            return true;
        }
        finally
        {
            ReleaseCom(factory);
        }
    }

    private static bool TryQueryNvx(GL gl, out long bytes, out string source)
    {
        bytes = 0;
        source = "nvx";
        try
        {
            var extensions = gl.GetStringS(StringName.Extensions) ?? "";
            if (extensions.IndexOf("GL_NVX_gpu_memory_info", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            while (gl.GetError() != GLEnum.NoError)
            {
            }

            gl.GetInteger((GetPName)GpuMemoryInfoDedicatedVidmemNvx, out var kb);
            var err = gl.GetError();
            if (err != GLEnum.NoError || kb <= 0)
            {
                return false;
            }

            bytes = (long)kb * 1024L;
            source = "GL_NVX_gpu_memory_info";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Sanitize(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "adapter";
        }

        var sb = new StringBuilder(description.Length);
        foreach (var ch in description.Trim())
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }

        var s = sb.ToString().Trim();
        return s.Length <= 48 ? s : s[..48];
    }

    private static TDelegate GetVTableFn<TDelegate>(IntPtr comObject, int index)
        where TDelegate : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var fn = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(fn);
    }

    private static void ReleaseCom(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero)
        {
            return;
        }

        var release = GetVTableFn<ReleaseDelegate>(comObject, 2);
        release(comObject);
    }

    private static void ReleaseCom(ref IntPtr comObject)
    {
        if (comObject == IntPtr.Zero)
        {
            return;
        }

        ReleaseCom(comObject);
        comObject = IntPtr.Zero;
    }
}
