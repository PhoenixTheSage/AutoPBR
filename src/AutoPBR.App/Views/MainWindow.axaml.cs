using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AutoPBR.App.ViewModels;

namespace AutoPBR.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BrowsePack_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select resource pack (.zip)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Zip") { Patterns = ["*.zip"] }
            ]
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
            return;

        if (DataContext is MainWindowViewModel vm)
            vm.PackPath = path;
    }

    private async void BrowseOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
}