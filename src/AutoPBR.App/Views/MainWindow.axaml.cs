using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AutoPBR.App.ViewModels;

namespace AutoPBR.App.Views;

public partial class MainWindow : Window
{
    private const int LogScrollThrottleMs = 200;
    private DateTime _lastLogScrollUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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