using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Probes Windows Advanced Color / DXGI output HDR capability for a preview HWND.</summary>
internal static class PreviewHdrDisplayProbe
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
    private const int DxgiColorSpaceHdr10 = 12;

    private const int S_OK = 0;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr thisPtr, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr thisPtr, uint adapter, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumOutputsDelegate(IntPtr thisPtr, uint output, out IntPtr ppOutput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr thisPtr, out DxgiOutputDesc1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiOutputDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public RectL DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public float RedPrimary0;
        public float RedPrimary1;
        public float GreenPrimary0;
        public float GreenPrimary1;
        public float BluePrimary0;
        public float BluePrimary1;
        public float WhitePoint0;
        public float WhitePoint1;
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static PreviewHdrDisplayInfo ProbeForWindow(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
        {
            return PreviewHdrDisplayInfo.Unsupported;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return PreviewHdrDisplayInfo.Unsupported;
        }

        var iidFactory = IID_IDXGIFactory1;
        var hr = CreateDXGIFactory1(ref iidFactory, out var factory);
        if (hr != S_OK || factory == IntPtr.Zero)
        {
            return PreviewHdrDisplayInfo.Unsupported;
        }

        try
        {
            return ProbeFactory(factory, monitor);
        }
        finally
        {
            Release(factory);
        }
    }

    private static PreviewHdrDisplayInfo ProbeFactory(IntPtr factory, IntPtr monitor)
    {
        // IDXGIFactory1 vtable: IUnknown(3) + IDXGIObject(4) + EnumAdapters(1) + MakeWindowAssociation(1)
        // + GetWindowAssociation(1) + CreateSwapChain(1) + CreateSoftwareAdapter(1) + EnumAdapters1(1=11)
        var enumAdapters1 = GetVTableFn<EnumAdapters1Delegate>(factory, 12);

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            hrCheck(enumAdapters1(factory, adapterIndex, out var adapter), out var adapterHr);
            if (adapterHr == DXGI_ERROR_NOT_FOUND)
            {
                break;
            }

            if (adapterHr != S_OK || adapter == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                // IDXGIAdapter vtable: IUnknown(3) + IDXGIObject(4) + EnumOutputs(1=7)
                var enumOutputs = GetVTableFn<EnumOutputsDelegate>(adapter, 7);
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    hrCheck(enumOutputs(adapter, outputIndex, out var output), out var outputHr);
                    if (outputHr == DXGI_ERROR_NOT_FOUND)
                    {
                        break;
                    }

                    if (outputHr != S_OK || output == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if (TryReadOutputDesc(output, monitor, out var info))
                        {
                            return info;
                        }
                    }
                    finally
                    {
                        Release(output);
                    }
                }
            }
            finally
            {
                Release(adapter);
            }
        }

        return PreviewHdrDisplayInfo.Unsupported;
    }

    private static bool TryReadOutputDesc(IntPtr output, IntPtr monitor, out PreviewHdrDisplayInfo info)
    {
        info = PreviewHdrDisplayInfo.Unsupported;
        var iid = IID_IDXGIOutput6;
        var qi = GetVTableFn<QueryInterfaceDelegate>(output, 0);
        var hr = qi(output, ref iid, out var output6);
        if (hr != S_OK || output6 == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // IDXGIOutput6::GetDesc1 at vtable slot 27 (after Output5::DuplicateOutput1 @ 26).
            var getDesc1 = GetVTableFn<GetDesc1Delegate>(output6, 27);
            hr = getDesc1(output6, out var desc);
            if (hr != S_OK)
            {
                return false;
            }

            if (desc.Monitor != monitor)
            {
                return false;
            }

            var supportsHdr = desc.ColorSpace == DxgiColorSpaceHdr10
                              || (desc.BitsPerColor >= 10 && desc.MaxLuminance > 80f);
            info = new PreviewHdrDisplayInfo(
                supportsHdr,
                desc.MaxLuminance,
                desc.MaxFullFrameLuminance,
                (int)desc.BitsPerColor);
            return true;
        }
        finally
        {
            Release(output6);
        }
    }

    private static void hrCheck(int hr, out int result) => result = hr;

    private static T GetVTableFn<T>(IntPtr comObject, int index) where T : Delegate
    {
        unsafe
        {
            var vtable = *(IntPtr*)comObject;
            var fnPtr = ((IntPtr*)vtable)[index];
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }
    }

    private static void Release(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero)
        {
            return;
        }

        var release = GetVTableFn<ReleaseDelegate>(comObject, 2);
        _ = release(comObject);
    }
}
