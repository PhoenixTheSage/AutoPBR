using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AutoPBR.App.Views;

public partial class MainWindow : Window
{
    private void UpdateCornerRadiusFromCurrentState()
    {
        if (_rootBorder is null)
        {
            return;
        }

        // Linux uses system decorations; keep the client area square.
        if (OperatingSystem.IsLinux())
        {
            _rootBorder.CornerRadius = default;
            return;
        }

        bool useSquare = WindowState == WindowState.Maximized || IsWindowsSnapped();
        _rootBorder.CornerRadius = useSquare ? new CornerRadius(0) : new CornerRadius(RoundedCornerRadius);
    }

    /// <summary>
    /// True when the window is in a Windows snap layout (any preset: half, thirds, quarters, sixths on ultra-wide, etc.) so we use square corners.
    /// </summary>
    private bool IsWindowsSnapped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }


        if (TryGetPlatformHandle()?.Handle is not { } hwnd)
        {

            return false;
        }


        try
        {
            if (IsZoomed(hwnd) != 0)
            {
                return true;
            }


            if (!GetWindowRect(hwnd, out var r))
            {
                return false;
            }


            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (mon == IntPtr.Zero)
            {
                return false;
            }


            var mi = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(mon, ref mi))
            {

                return false;
            }


            int workW = mi.rcWork.Right - mi.rcWork.Left;
            int workH = mi.rcWork.Bottom - mi.rcWork.Top;
            int winW = r.Right - r.Left;
            int winH = r.Bottom - r.Top;
            int leftOffset = r.Left - mi.rcWork.Left;
            int topOffset = r.Top - mi.rcWork.Top;
            const int tolerance = 8;

            // Check if window matches a grid cell (2D grid: columns and rows 2,3,4,6) — covers half, thirds, quarters, sixths, and quarter snaps (top-left, bottom-left, etc.)
            for (int colDiv = 2; colDiv <= 6; colDiv++)
            {
                int colW = workW / colDiv;
                if (colW <= 0)
                {
                    continue;
                }


                for (int rowDiv = 2; rowDiv <= 6; rowDiv++)
                {
                    int rowH = workH / rowDiv;
                    if (rowH <= 0)
                    {
                        continue;
                    }


                    for (int cols = 1; cols <= colDiv; cols++)
                    {
                        int spanW = cols * colW;
                        if (Math.Abs(winW - spanW) > tolerance)
                        {
                            continue;
                        }


                        for (int rows = 1; rows <= rowDiv; rows++)
                        {
                            int spanH = rows * rowH;
                            if (Math.Abs(winH - spanH) > tolerance)
                            {
                                continue;
                            }


                            for (int startCol = 0; startCol <= colDiv - cols; startCol++)
                            {
                                if (Math.Abs(leftOffset - startCol * colW) > tolerance)
                                {
                                    continue;
                                }


                                for (int startRow = 0; startRow <= rowDiv - rows; startRow++)
                                {
                                    if (Math.Abs(topOffset - startRow * rowH) <= tolerance)
                                    {

                                        return true;
                                    }

                                }
                            }
                        }
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern int IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>
    /// On Windows, add WS_THICKFRAME and WS_MAXIMIZEBOX to the window style so the window
    /// participates in Aero Snap (drag to edge/corner to snap). Safe no-op on other platforms.
    /// </summary>
    private void TryEnableWindowsSnap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }


        if (TryGetPlatformHandle()?.Handle is not { } hwnd)
        {
            return;
        }


        try
        {
            const int gwlStyle = -16;
            const int wsThickframe = 0x00040000;
            const int wsMaximizebox = 0x00010000;
            const int wsMinimizebox = 0x00020000;
            int style = GetWindowLong(hwnd, gwlStyle);
            int newStyle = style | wsThickframe | wsMaximizebox | wsMinimizebox;
            if (newStyle != style)
            {
                NativeSetLastError(0);
                var previousStyle = SetWindowLong(hwnd, gwlStyle, newStyle);
                _ = previousStyle;
            }

        }
        catch
        {
            // Ignore if Win32 calls fail (e.g. handle invalid)
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    private static extern void NativeSetLastError(uint dwErrCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    internal void HandleTitleBarDragRegionPointerPressed(object? sender, PointerPressedEventArgs e) =>
        TitleBarDragRegion_PointerPressed(sender, e);

    internal void HandleWindowMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowMinimize_Click(sender, e);

    internal void HandleWindowMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowMaximize_Click(sender, e);

    internal void HandleWindowCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowClose_Click(sender, e);

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }

    }

    private void ResizeWest_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.West, e);
        }

    }

    private void ResizeEast_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.East, e);
        }

    }

    private void ResizeSouth_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.South, e);
        }

    }

    private void ResizeSouthWest_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthWest, e);
        }

    }

    private void ResizeSouthEast_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }

    }

    private void ResizeNorthWest_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.NorthWest, e);
        }

    }

    private void ResizeNorthEast_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.NorthEast, e);
        }

    }

    private void WindowMinimize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void WindowMaximize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void WindowClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
