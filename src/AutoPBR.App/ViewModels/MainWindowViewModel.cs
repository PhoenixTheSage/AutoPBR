using System.Collections.ObjectModel;
using System.IO.Compression;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AutoPBR.App.Models;
using AutoPBR.Core;
using AutoPBR.Core.Models;

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private CancellationTokenSource? _cts;
    private SpecularData? _specularData;

    [ObservableProperty] private string? packPath;
    [ObservableProperty] private string? outputDirectory;

    [ObservableProperty] private double normalIntensity = PbrifyDefaults.DefaultNormalIntensity;
    [ObservableProperty] private double heightIntensity = PbrifyDefaults.DefaultHeightIntensity;
    [ObservableProperty] private bool fastSpecular;
    [ObservableProperty] private bool ignorePlants;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isConverting;
    [ObservableProperty] private string statusText = "Select a resource pack (.zip) and an output folder.";

    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMax = 1;

    [ObservableProperty] private string? outputZipPath;

    [ObservableProperty] private string textureFilter = "";

    public ObservableCollection<TextureKeyItem> AllTextureKeys { get; } = new();
    public ObservableCollection<TextureKeyItem> FilteredTextureKeys { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    partial void OnPackPathChanged(string? value)
    {
        RecomputeOutputZipPath();
        LoadTexturesCommand.NotifyCanExecuteChanged();
        ConvertCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        RecomputeOutputZipPath();
        ConvertCommand.NotifyCanExecuteChanged();
    }
    partial void OnFastSpecularChanged(bool value)
    {
        RecomputeOutputZipPath();
        ConvertCommand.NotifyCanExecuteChanged();
    }
    partial void OnTextureFilterChanged(string value) => ApplyTextureFilter();

    private void RecomputeOutputZipPath()
    {
        if (string.IsNullOrWhiteSpace(PackPath) || string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputZipPath = null;
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(PackPath);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "pack";

        var suffix = FastSpecular ? "fast" : "slow";
        OutputZipPath = Path.Combine(OutputDirectory, $"{baseName}_PBR_{suffix}.zip");
    }

    private void ApplyTextureFilter()
    {
        FilteredTextureKeys.Clear();
        var f = (TextureFilter ?? "").Trim();
        foreach (var item in AllTextureKeys)
        {
            if (string.IsNullOrEmpty(f) || item.Key.Contains(f, StringComparison.OrdinalIgnoreCase))
                FilteredTextureKeys.Add(item);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadTextures))]
    public async Task LoadTexturesAsync()
    {
        if (string.IsNullOrWhiteSpace(PackPath) || !File.Exists(PackPath))
            return;

        IsBusy = true;
        StatusText = "Scanning textures in pack...";
        LogLines.Add($"Scanning: {PackPath}");

        var tempRoot = Path.Combine(Path.GetTempPath(), "PBRify", "scan", Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(tempRoot, "pack_unzipped");
        Directory.CreateDirectory(extracted);

        try
        {
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(PackPath!, extracted);
            }).ConfigureAwait(false);

            var keys = await Task.Run(() =>
            {
                var scan = ResourcePackConverter.ScanTextures(extracted, new PbrifyOptions());
                return scan.Select(t => t.RelativeKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var keepIgnored = AllTextureKeys.Where(x => x.IsIgnored).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                AllTextureKeys.Clear();
                foreach (var k in keys)
                    AllTextureKeys.Add(new TextureKeyItem(k, keepIgnored.Contains(k)));

                if (IgnorePlants)
                    MarkPlantsIgnored();

                ApplyTextureFilter();
                StatusText = $"Loaded {AllTextureKeys.Count} textures.";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = "Failed to scan textures.";
                LogLines.Add(ex.ToString());
            });
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort */ }
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                LoadTexturesCommand.NotifyCanExecuteChanged();
                ConvertCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private bool CanLoadTextures() => !IsConverting && !IsBusy && !string.IsNullOrWhiteSpace(PackPath) && File.Exists(PackPath);

    partial void OnIgnorePlantsChanged(bool value)
    {
        if (value)
            MarkPlantsIgnored();
    }

    private void MarkPlantsIgnored()
    {
        var plants = new HashSet<string>(PbrifyDefaults.PlantTextureKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var item in AllTextureKeys)
        {
            if (plants.Contains(item.Key))
                item.IsIgnored = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    public async Task ConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(PackPath) || !File.Exists(PackPath))
            return;
        if (string.IsNullOrWhiteSpace(OutputZipPath))
            return;

        IsConverting = true;
        IsBusy = true;
        ProgressValue = 0;
        ProgressMax = 1;

        _cts = new CancellationTokenSource();

        try
        {
            StatusText = "Loading specular data...";
            _specularData ??= SpecularData.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json"));

            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in AllTextureKeys)
            {
                if (item.IsIgnored)
                    ignore.Add(item.Key);
            }
            if (IgnorePlants)
            {
                foreach (var p in PbrifyDefaults.PlantTextureKeys)
                    ignore.Add(p);
            }

            var options = new PbrifyOptions
            {
                NormalIntensity = (float)NormalIntensity,
                HeightIntensity = (float)HeightIntensity,
                FastSpecular = FastSpecular,
                IgnoreTextureKeys = ignore,
                SpecularData = _specularData
            };

            var converter = new ResourcePackConverter();
            var prog = new Progress<ConversionProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressMax = Math.Max(1, p.Total);
                    ProgressValue = p.Completed;
                    StatusText = p.Stage switch
                    {
                        ConversionStage.Extracting => "Extracting pack...",
                        ConversionStage.ScanningTextures => "Scanning textures...",
                        ConversionStage.GeneratingSpecular => $"Specular: {p.CurrentTextureName}",
                        ConversionStage.GeneratingNormals => $"Normals: {p.CurrentTextureName}",
                        ConversionStage.GeneratingHeights => $"Heights: {p.CurrentTextureName}",
                        ConversionStage.Packing => "Packing output zip...",
                        ConversionStage.Done => "Done.",
                        _ => p.Stage.ToString()
                    };
                });
            });

            LogLines.Add($"Converting → {OutputZipPath}");
            await converter.ConvertAsync(PackPath, OutputZipPath, options, prog, _cts.Token);
            LogLines.Add("Done.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            LogLines.Add("Cancelled.");
        }
        catch (Exception ex)
        {
            StatusText = "Conversion failed.";
            LogLines.Add(ex.ToString());
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsConverting = false;
            IsBusy = false;
            ConvertCommand.NotifyCanExecuteChanged();
            LoadTexturesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConvert() =>
        !IsConverting &&
        !IsBusy &&
        !string.IsNullOrWhiteSpace(PackPath) &&
        File.Exists(PackPath) &&
        !string.IsNullOrWhiteSpace(OutputZipPath);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        _cts?.Cancel();
    }

    private bool CanCancel() => IsConverting;
}
