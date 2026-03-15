using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AutoPBR.App.Models;
using AutoPBR.App.ViewModels;

namespace AutoPBR.App.Views;

public partial class MainWindow : Window
{
    private const int LogScrollThrottleMs = 200;
    private DateTime _lastLogScrollUtc = DateTime.MinValue;

    private const double RoundedCornerRadius = 8;
    private Border? _rootBorder;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryEnableWindowsSnap();
        _rootBorder = this.FindControl<Border>("RootBorder");
        RestoreWindowLayout();
        UpdateCornerRadiusFromCurrentState();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
                UpdateCornerRadiusFromCurrentState();
        };
        Resized += (_, _) => UpdateCornerRadiusFromCurrentState();
        PositionChanged += (_, _) => UpdateCornerRadiusFromCurrentState();
    }

    private void RestoreWindowLayout()
    {
        var state = WindowLayoutState.Load();
        Position = new PixelPoint((int)state.X, (int)state.Y);
        Width = state.Width;
        Height = state.Height;
        if (state.State is >= 0 and <= 2)
            WindowState = (WindowState)state.State;
        var contentGrid = this.FindControl<Grid>("ContentGrid");
        if (contentGrid?.ColumnDefinitions.Count >= 3)
            contentGrid.ColumnDefinitions[2].Width = new GridLength(state.PreviewColumnWidth, GridUnitType.Pixel);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var contentGrid = this.FindControl<Grid>("ContentGrid");
        var state = new WindowLayoutState
        {
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
            State = (int)WindowState,
            PreviewColumnWidth = 280
        };
        if (contentGrid?.ColumnDefinitions.Count >= 3 &&
            contentGrid.ColumnDefinitions[2].Width.IsAbsolute)
            state.PreviewColumnWidth = contentGrid.ColumnDefinitions[2].Width.Value;
        state.Save();
    }

    private void UpdateCornerRadiusFromCurrentState()
    {
        if (_rootBorder is null)
            return;
        bool useSquare = WindowState == WindowState.Maximized || IsWindowsSnapped();
        _rootBorder.CornerRadius = useSquare ? new CornerRadius(0) : new CornerRadius(RoundedCornerRadius);
    }

    /// <summary>
    /// True when the window is in a Windows snap layout (any preset: half, thirds, quarters, sixths on ultra-wide, etc.) so we use square corners.
    /// </summary>
    private bool IsWindowsSnapped()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (TryGetPlatformHandle()?.Handle is not { } hwnd)
            return false;
        try
        {
            if (IsZoomed(hwnd) != 0)
                return true;
            if (!GetWindowRect(hwnd, out var r))
                return false;
            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (mon == IntPtr.Zero)
                return false;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi))
                return false;
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
                    continue;
                for (int rowDiv = 2; rowDiv <= 6; rowDiv++)
                {
                    int rowH = workH / rowDiv;
                    if (rowH <= 0)
                        continue;
                    for (int cols = 1; cols <= colDiv; cols++)
                    {
                        int spanW = cols * colW;
                        if (Math.Abs(winW - spanW) > tolerance)
                            continue;
                        for (int rows = 1; rows <= rowDiv; rows++)
                        {
                            int spanH = rows * rowH;
                            if (Math.Abs(winH - spanH) > tolerance)
                                continue;
                            for (int startCol = 0; startCol <= colDiv - cols; startCol++)
                            {
                                if (Math.Abs(leftOffset - startCol * colW) > tolerance)
                                    continue;
                                for (int startRow = 0; startRow <= rowDiv - rows; startRow++)
                                {
                                    if (Math.Abs(topOffset - startRow * rowH) <= tolerance)
                                        return true;
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
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern int IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>
    /// On Windows, add WS_THICKFRAME and WS_MAXIMIZEBOX to the window style so the window
    /// participates in Aero Snap (drag to edge/corner to snap). Safe no-op on other platforms.
    /// </summary>
    private void TryEnableWindowsSnap()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (TryGetPlatformHandle()?.Handle is not { } hwnd)
            return;
        try
        {
            const int GWL_STYLE = -16;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_MAXIMIZEBOX = 0x00010000;
            const int WS_MINIMIZEBOX = 0x00020000;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            int newStyle = style | WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX;
            if (newStyle != style)
                SetWindowLong(hwnd, GWL_STYLE, newStyle);
        }
        catch
        {
            // Ignore if Win32 calls fail (e.g. handle invalid)
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ResizeWest_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.West, e);
    }

    private void ResizeEast_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.East, e);
    }

    private void ResizeSouth_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.South, e);
    }

    private void ResizeSouthWest_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthWest, e);
    }

    private void ResizeSouthEast_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
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

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && LogScrollViewer is { } scroll)
        {
            vm.LogLines.CollectionChanged += (_, _) =>
            {
                var now = DateTime.UtcNow;
                if ((now - _lastLogScrollUtc).TotalMilliseconds >= LogScrollThrottleMs)
                {
                    _lastLogScrollUtc = now;
                    scroll.ScrollToEnd();
                }
            };
        }
    }

    private async void BrowsePack_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
                return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select resource pack (.zip or .jar)",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Zip / JAR") { Patterns = ["*.zip", "*.jar"] }
                ]
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path is null)
                return;

            if (DataContext is MainWindowViewModel vm)
                vm.PackPath = path;
        }
        catch (Exception)
        {
            // Prevent unhandled exception in async void from crashing the process
        }
    }

    private async void BrowseOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
                return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select output folder",
                AllowMultiple = false
            });

            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (path is null)
                return;

            if (DataContext is MainWindowViewModel vm)
                vm.OutputDirectory = path;
        }
        catch (Exception)
        {
            // Prevent unhandled exception in async void from crashing the process
        }
    }
}