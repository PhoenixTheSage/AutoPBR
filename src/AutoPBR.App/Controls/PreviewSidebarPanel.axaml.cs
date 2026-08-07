using System.Diagnostics;

using AutoPBR.App.Services;
using AutoPBR.App.ViewModels;
using AutoPBR.App.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AutoPBR.App.Controls;

public partial class PreviewSidebarPanel : UserControl
{
    private UvDebugWindow? _uvDebugWindow;
    private EntityPreviewDebugWindow? _entityPreviewDebugWindow;
    private Preview3DCameraHelpWindow? _preview3DCameraHelpWindow;

    public PreviewSidebarPanel()
    {
        InitializeComponent();
    }

    internal void WireViewModel(MainWindowViewModel vm)
    {
        if (GlPbrPreview is { } glPreview)
        {
            vm.RegisterGlPreview(glPreview);
        }

        if (LogScrollViewer is { } scroll)
        {
            const int logScrollThrottleMs = 200;
            var lastLogScrollUtc = DateTime.MinValue;
            vm.LogLines.CollectionChanged += (_, _) =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastLogScrollUtc).TotalMilliseconds >= logScrollThrottleMs)
                {
                    lastLogScrollUtc = now;
                    scroll.ScrollToEnd();
                }
            };
        }

        vm.ShowSemanticDebugDialog = ShowSemanticDebugWindow;
    }

    private void ShowSemanticDebugWindow(string text)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new Window
        {
            Title = Lang.Resources.SemanticDebugWindowTitle,
            Width = 620,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new SelectableTextBlock
                {
                    Text = text,
                    FontFamily = new Avalonia.Media.FontFamily(AutoPBR.App.Rendering.OpenGL.PreviewMonoFont.FontFamilyCss),
                    FontSize = 12,
                    Margin = new Avalonia.Thickness(12),
                    TextWrapping = Avalonia.Media.TextWrapping.NoWrap
                }
            }
        };

        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogService.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LogService.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the log folder should never crash the app.
        }
    }

    private void OpenUvDebugWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (_uvDebugWindow is { IsVisible: true })
        {
            _uvDebugWindow.Activate();
            return;
        }

        var window = new UvDebugWindow
        {
            DataContext = new UvDebugWindowViewModel(vm)
        };
        window.Closed += (_, _) => _uvDebugWindow = null;
        _uvDebugWindow = window;
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private void OpenEntityPreviewDebugWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (_entityPreviewDebugWindow is { IsVisible: true })
        {
            _entityPreviewDebugWindow.Activate();
            return;
        }

        var window = new EntityPreviewDebugWindow
        {
            DataContext = new EntityPreviewDebugWindowViewModel(vm)
        };
        window.Closed += (_, _) => _entityPreviewDebugWindow = null;
        _entityPreviewDebugWindow = window;
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private void OpenPreview3DCameraHelpWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (_preview3DCameraHelpWindow is { IsVisible: true })
        {
            _preview3DCameraHelpWindow.Activate();
            return;
        }

        var window = new Preview3DCameraHelpWindow
        {
            DataContext = new Preview3DCameraHelpWindowViewModel(vm)
        };
        window.Closed += (_, _) => _preview3DCameraHelpWindow = null;
        _preview3DCameraHelpWindow = window;
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}
