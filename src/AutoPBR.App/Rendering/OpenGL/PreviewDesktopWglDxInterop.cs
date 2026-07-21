using System.Runtime.InteropServices;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>WGL_NV_DX_interop bindings for rendering into shared D3D11 textures.</summary>
internal static class PreviewDesktopWglDxInterop
{
    private const uint GlTexture2D = 0x0DE1;
    private const uint WglAccessWriteDiscardNv = 0x0000_0002;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WglDxOpenDeviceNvDelegate(IntPtr dxDevice);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool WglDxCloseDeviceNvDelegate(IntPtr hDevice);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WglDxRegisterObjectNvDelegate(
        IntPtr hDevice,
        IntPtr dxObject,
        uint name,
        uint type,
        uint access);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool WglDxUnregisterObjectNvDelegate(IntPtr hDevice, IntPtr hObject);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool WglDxLockObjectsNvDelegate(IntPtr hDevice, int count, IntPtr objects);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool WglDxUnlockObjectsNvDelegate(IntPtr hDevice, int count, IntPtr objects);

    private static WglDxOpenDeviceNvDelegate? _openDevice;
    private static WglDxCloseDeviceNvDelegate? _closeDevice;
    private static WglDxRegisterObjectNvDelegate? _registerObject;
    private static WglDxUnregisterObjectNvDelegate? _unregisterObject;
    private static WglDxLockObjectsNvDelegate? _lockObjects;
    private static WglDxUnlockObjectsNvDelegate? _unlockObjects;
    private static bool _procsLoaded;
    private static bool _procsAvailable;

    public static void ResetProcCache()
    {
        _procsLoaded = false;
        _procsAvailable = false;
        _openDevice = null;
        _closeDevice = null;
        _registerObject = null;
        _unregisterObject = null;
        _lockObjects = null;
        _unlockObjects = null;
    }

    public static bool EnsureProcs(GlInterface wglGl)
    {
        if (_procsLoaded)
        {
            return _procsAvailable;
        }

        _procsLoaded = true;
        try
        {
            var proc = wglGl.GetProcAddress("wglDXOpenDeviceNV");
            if (proc == IntPtr.Zero)
            {
                return false;
            }

            _openDevice = Marshal.GetDelegateForFunctionPointer<WglDxOpenDeviceNvDelegate>(proc);
            _closeDevice = Load<WglDxCloseDeviceNvDelegate>(wglGl, "wglDXCloseDeviceNV");
            _registerObject = Load<WglDxRegisterObjectNvDelegate>(wglGl, "wglDXRegisterObjectNV");
            _unregisterObject = Load<WglDxUnregisterObjectNvDelegate>(wglGl, "wglDXUnregisterObjectNV");
            _lockObjects = Load<WglDxLockObjectsNvDelegate>(wglGl, "wglDXLockObjectsNV");
            _unlockObjects = Load<WglDxUnlockObjectsNvDelegate>(wglGl, "wglDXUnlockObjectsNV");
            _procsAvailable = _closeDevice is not null &&
                              _registerObject is not null &&
                              _unregisterObject is not null &&
                              _lockObjects is not null &&
                              _unlockObjects is not null;
            return _procsAvailable;
        }
        catch
        {
            _procsAvailable = false;
            return false;
        }
    }

    public static bool TryOpenDevice(GlInterface wglGl, IntPtr d3d11Device, out IntPtr dxInteropDevice)
    {
        dxInteropDevice = IntPtr.Zero;
        if (!EnsureProcs(wglGl) || d3d11Device == IntPtr.Zero)
        {
            return false;
        }

        dxInteropDevice = _openDevice!(d3d11Device);
        return dxInteropDevice != IntPtr.Zero;
    }

    public static void CloseDevice(IntPtr dxInteropDevice)
    {
        if (dxInteropDevice == IntPtr.Zero || _openDevice is null)
        {
            return;
        }

        _closeDevice!(dxInteropDevice);
    }

    public static bool TryRegisterTexture2D(
        IntPtr dxInteropDevice,
        IntPtr d3d11Texture2D,
        uint glTextureName,
        out IntPtr registeredObject)
    {
        registeredObject = IntPtr.Zero;
        if (_registerObject is null || dxInteropDevice == IntPtr.Zero || d3d11Texture2D == IntPtr.Zero || glTextureName == 0)
        {
            return false;
        }

        registeredObject = _registerObject!(
            dxInteropDevice,
            d3d11Texture2D,
            glTextureName,
            GlTexture2D,
            WglAccessWriteDiscardNv);
        return registeredObject != IntPtr.Zero;
    }

    public static void UnregisterObject(IntPtr dxInteropDevice, IntPtr registeredObject)
    {
        if (dxInteropDevice == IntPtr.Zero || registeredObject == IntPtr.Zero || _unregisterObject is null)
        {
            return;
        }

        _unregisterObject!(dxInteropDevice, registeredObject);
    }

    public static bool TryLockObject(IntPtr dxInteropDevice, IntPtr registeredObject)
    {
        if (_lockObjects is null || dxInteropDevice == IntPtr.Zero || registeredObject == IntPtr.Zero)
        {
            return false;
        }

        unsafe
        {
            return _lockObjects(dxInteropDevice, 1, (IntPtr)(&registeredObject));
        }
    }

    public static bool TryUnlockObject(IntPtr dxInteropDevice, IntPtr registeredObject)
    {
        if (_unlockObjects is null || dxInteropDevice == IntPtr.Zero || registeredObject == IntPtr.Zero)
        {
            return false;
        }

        unsafe
        {
            return _unlockObjects(dxInteropDevice, 1, (IntPtr)(&registeredObject));
        }
    }

    private static T? Load<T>(GlInterface wglGl, string name) where T : Delegate
    {
        var proc = wglGl.GetProcAddress(name);
        return proc == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(proc);
    }
}
