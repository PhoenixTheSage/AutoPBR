using System.Collections.ObjectModel;
using System.IO.Compression;
using Avalonia.Media;
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
    private readonly UserSettings _settings;
    private bool _loadingSettings;
    private DateTime _lastLogWriteUtc = DateTime.MinValue;
    private const int LogWriteIntervalMs = 100;
    private DateTime _conversionStartUtc;
    private ConversionStage? _currentStage;
    private DateTime _stageStartUtc;

    [ObservableProperty] private string? packPath;
    [ObservableProperty] private string? outputDirectory;

    [ObservableProperty] private double normalIntensity = AutoPbrDefaults.DefaultNormalIntensity;
    [ObservableProperty] private double heightIntensity = AutoPbrDefaults.DefaultHeightIntensity;
    [ObservableProperty] private bool fastSpecular;
    [ObservableProperty] private string foliageMode = "Ignore All";
    [ObservableProperty] private bool experimentalExtractor;
    [ObservableProperty] private double smoothnessScale = AutoPbrDefaults.DefaultSmoothnessScale;
    [ObservableProperty] private double metallicBoost = AutoPbrDefaults.DefaultMetallicBoost;
    [ObservableProperty] private double porosityBias = AutoPbrDefaults.DefaultPorosityBias;
    [ObservableProperty] private int maxThreads; // 0 = auto
    [ObservableProperty] private int maxThreadsMax = Math.Max(1, Environment.ProcessorCount);
    [ObservableProperty] private string? tempDirectory;
    [ObservableProperty] private bool processBlocks = true;
    [ObservableProperty] private bool processItems = true;
    [ObservableProperty] private bool processArmor = true;
    [ObservableProperty] private bool processParticles = true;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isConverting;
    [ObservableProperty] private string statusText = "Select a resource pack (.zip or .jar) and an output folder.";

    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMax = 1;

    [ObservableProperty] private string? outputZipPath;

    [ObservableProperty] private string textureFilter = "";

    [ObservableProperty] private string colorScheme = "Dark";
    [ObservableProperty] private IBrush windowBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush cardBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush cardBorderBrush = Brushes.Gray;
    [ObservableProperty] private IBrush accentBrush = Brushes.DeepSkyBlue;
    [ObservableProperty] private IBrush foregroundBrush = Brushes.White;

    public ObservableCollection<TextureKeyItem> AllTextureKeys { get; } = new();
    public ObservableCollection<TextureKeyItem> FilteredTextureKeys { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    public MainWindowViewModel()
    {
        _settings = UserSettings.Load();
        _loadingSettings = true;

        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.OutputDirectory))
                OutputDirectory = _settings.OutputDirectory;

            NormalIntensity = _settings.NormalIntensity;
            HeightIntensity = _settings.HeightIntensity;
            FastSpecular = _settings.FastSpecular;
            FoliageMode = string.IsNullOrWhiteSpace(_settings.FoliageMode) ? "Ignore All" : _settings.FoliageMode;
            ExperimentalExtractor = _settings.ExperimentalExtractor;
            SmoothnessScale = _settings.SmoothnessScale;
            MetallicBoost = _settings.MetallicBoost;
            PorosityBias = _settings.PorosityBias;
            MaxThreads = _settings.MaxThreads;
            TempDirectory = _settings.TempDirectory;
            ColorScheme = string.IsNullOrWhiteSpace(_settings.ColorScheme) ? "Dark" : _settings.ColorScheme;
            ProcessBlocks = _settings.ProcessBlocks;
            ProcessItems = _settings.ProcessItems;
            ProcessArmor = _settings.ProcessArmor;
            ProcessParticles = _settings.ProcessParticles;
            ApplyColorScheme();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

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
        SaveSettings();
    }

    partial void OnFastSpecularChanged(bool value)
    {
        RecomputeOutputZipPath();
        ConvertCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnNormalIntensityChanged(double value) => SaveSettings();
    partial void OnHeightIntensityChanged(double value) => SaveSettings();
    partial void OnExperimentalExtractorChanged(bool value) => SaveSettings();
    partial void OnSmoothnessScaleChanged(double value) => SaveSettings();
    partial void OnMetallicBoostChanged(double value) => SaveSettings();
    partial void OnPorosityBiasChanged(double value) => SaveSettings();
    partial void OnMaxThreadsChanged(int value) => SaveSettings();
    partial void OnTempDirectoryChanged(string? value) => SaveSettings();
    partial void OnTextureFilterChanged(string value) => ApplyTextureFilter();
    partial void OnColorSchemeChanged(string value)
    {
        ApplyColorScheme();
        SaveSettings();
    }
    partial void OnProcessBlocksChanged(bool value) => SaveSettings();
    partial void OnProcessItemsChanged(bool value) => SaveSettings();
    partial void OnProcessArmorChanged(bool value) => SaveSettings();
    partial void OnProcessParticlesChanged(bool value) => SaveSettings();

    private void RecomputeOutputZipPath()
    {
        if (string.IsNullOrWhiteSpace(PackPath) || string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputZipPath = null;
            return;
        }

        var ext = Path.GetExtension(PackPath);
        var baseName = Path.GetFileNameWithoutExtension(PackPath);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "pack";

        // Always output .zip (separate PBR layer). JAR in → ZIP out; ZIP in → ZIP with _PBR suffix.
        if (ext.Equals(".jar", StringComparison.OrdinalIgnoreCase))
            OutputZipPath = Path.Combine(OutputDirectory, baseName + ".zip");
        else
            OutputZipPath = Path.Combine(OutputDirectory, $"{baseName}_PBR.zip");
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

    private void SaveSettings()
    {
        if (_loadingSettings)
            return;

        _settings.OutputDirectory = OutputDirectory;
        _settings.NormalIntensity = NormalIntensity;
        _settings.HeightIntensity = HeightIntensity;
        _settings.FastSpecular = FastSpecular;
        _settings.FoliageMode = FoliageMode;
        _settings.ExperimentalExtractor = ExperimentalExtractor;
        _settings.SmoothnessScale = SmoothnessScale;
        _settings.MetallicBoost = MetallicBoost;
        _settings.PorosityBias = PorosityBias;
        _settings.MaxThreads = MaxThreads;
        _settings.TempDirectory = TempDirectory;
        _settings.ColorScheme = ColorScheme;
        _settings.ProcessBlocks = ProcessBlocks;
        _settings.ProcessItems = ProcessItems;
        _settings.ProcessArmor = ProcessArmor;
        _settings.ProcessParticles = ProcessParticles;
        _settings.Save();
    }

    private void ApplyColorScheme()
    {
        // Simple built-in schemes; all non-neutral themes are dark variants for readability.
        switch (ColorScheme)
        {
            case "Dark":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x18));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2A));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // green
                ForegroundBrush = Brushes.White;
                break;

            case "Blue":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x0B, 0x1B, 0x30));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x13, 0x27, 0x43));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x5B, 0x8C));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)); // blue
                ForegroundBrush = Brushes.White;
                break;

            case "Green":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x0D, 0x1F, 0x16));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x14, 0x30, 0x22));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)); // green
                ForegroundBrush = Brushes.White;
                break;

            case "Purple":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x22, 0x18, 0x3A));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x2E, 0x1F, 0x4D));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x95, 0x7D, 0xD1));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0x86, 0xFC));
                ForegroundBrush = Brushes.White;
                break;

            case "Amber":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x26, 0x15, 0x06));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x3A, 0x23, 0x0B));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00));
                ForegroundBrush = Brushes.White;
                break;

            case "Teal":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x24, 0x27));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x37, 0x3B));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0xAB, 0xA8));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x96, 0x88));
                ForegroundBrush = Brushes.White;
                break;

            case "Rose":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x2B, 0x0B, 0x18));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x3B, 0x12, 0x22));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x82));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x63));
                ForegroundBrush = Brushes.White;
                break;

            case "Mono":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                ForegroundBrush = Brushes.White;
                break;

            case "Ocean":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x05, 0x21, 0x2F));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x0A, 0x33, 0x45));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0xA6, 0xD4));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x02, 0x88, 0xD1));
                ForegroundBrush = Brushes.White;
                break;

            case "Sunset":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x29, 0x19, 0x14));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x3C, 0x22, 0x1B));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x57, 0x22));
                ForegroundBrush = Brushes.White;
                break;

            default:
                // Fallback to Dark if something unexpected is stored.
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x18));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2A));
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                ForegroundBrush = Brushes.White;
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadTextures))]
    public async Task LoadTexturesAsync()
    {
        if (!IsPackPath(PackPath) || !File.Exists(PackPath))
            return;

        IsBusy = true;
        StatusText = "Scanning textures in pack...";
        LogLines.Add($"Scanning: {PackPath}");

        var tempRoot = Path.Combine(Path.GetTempPath(), "AutoPBR", "scan", Guid.NewGuid().ToString("N"));
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
                var scanOpts = new AutoPbrOptions
                {
                    ProcessBlocks = ProcessBlocks,
                    ProcessItems = ProcessItems,
                    ProcessArmor = ProcessArmor,
                    ProcessParticles = ProcessParticles
                };
                var scan = ResourcePackConverter.ScanTextures(extracted, scanOpts);
                return scan.Select(t => t.RelativeKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var keepIgnored = AllTextureKeys.Where(x => x.IsIgnored).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                AllTextureKeys.Clear();
                foreach (var k in keys)
                    AllTextureKeys.Add(new TextureKeyItem(k, keepIgnored.Contains(k)));

                if (FoliageMode == "Ignore All")
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

    private static bool IsPackPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

    private bool CanLoadTextures() => !IsConverting && !IsBusy && IsPackPath(PackPath) && File.Exists(PackPath);

    partial void OnFoliageModeChanged(string value)
    {
        if (value == "Ignore All")
            MarkPlantsIgnored();
        SaveSettings();
    }

    private void MarkPlantsIgnored()
    {
        var plants = new HashSet<string>(AutoPbrDefaults.PlantTextureKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var item in AllTextureKeys)
        {
            if (plants.Contains(item.Key))
                item.IsIgnored = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    public async Task ConvertAsync()
    {
        if (!IsPackPath(PackPath) || !File.Exists(PackPath))
            return;
        if (string.IsNullOrWhiteSpace(OutputZipPath))
            return;

        IsConverting = true;
        IsBusy = true;
        ProgressValue = 0;
        ProgressMax = 1;
        _lastLogWriteUtc = DateTime.MinValue;
        _conversionStartUtc = DateTime.UtcNow;
        _currentStage = null;
        CancelCommand.NotifyCanExecuteChanged();

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
            if (FoliageMode == "Ignore All")
            {
                foreach (var p in AutoPbrDefaults.PlantTextureKeys)
                    ignore.Add(p);
            }

            var options = new AutoPbrOptions
            {
                NormalIntensity = (float)NormalIntensity,
                HeightIntensity = (float)HeightIntensity,
                FastSpecular = FastSpecular,
                ExperimentalExtractor = ExperimentalExtractor,
                SmoothnessScale = (float)SmoothnessScale,
                MetallicBoost = (float)MetallicBoost,
                PorosityBias = (int)Math.Round(PorosityBias),
                MaxThreads = MaxThreads,
                TempDirectory = string.IsNullOrWhiteSpace(TempDirectory) ? null : TempDirectory,
                ProcessBlocks = ProcessBlocks,
                ProcessItems = ProcessItems,
                ProcessArmor = ProcessArmor,
                ProcessParticles = ProcessParticles,
                IgnoreTextureKeys = ignore,
                FoliageMode = FoliageMode,
                SpecularData = _specularData
            };

            var converter = new ResourcePackConverter();
            var prog = new Progress<ConversionProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var now = DateTime.UtcNow;
                    if (_currentStage.HasValue && _currentStage.Value != p.Stage)
                    {
                        var elapsed = (now - _stageStartUtc).TotalSeconds;
                        var stageName = StageDisplayName(_currentStage.Value);
                        LogLines.Add($"{stageName} completed in {elapsed:F1} s");
                    }
                    if (p.Stage != ConversionStage.Done)
                    {
                        _currentStage = p.Stage;
                        _stageStartUtc = now;
                    }
                    else
                    {
                        var totalSec = (now - _conversionStartUtc).TotalSeconds;
                        LogLines.Add($"Total time: {totalSec:F1} s");
                    }

                    ProgressMax = Math.Max(1, p.Total);
                    ProgressValue = p.Completed;
                    if (p.Stage == ConversionStage.Extracting && p.Completed == 0 && p.Total > 0)
                        LogLines.Add("Extracting...");
                    if (p.Stage == ConversionStage.Packing && p.Completed == 0 && p.Total > 0)
                        LogLines.Add("Packing...");
                    if (!string.IsNullOrEmpty(p.CurrentTextureName))
                    {
                        if ((now - _lastLogWriteUtc).TotalMilliseconds >= LogWriteIntervalMs)
                        {
                            _lastLogWriteUtc = now;
                            var stageLabel = p.Stage switch
                            {
                                ConversionStage.GeneratingSpecular => "Specular",
                                ConversionStage.GeneratingNormals => "Normals",
                                _ => p.Stage.ToString()
                            };
                            LogLines.Add($"{stageLabel}: {p.CurrentTextureName}");
                        }
                    }
                    StatusText = p.Stage switch
                    {
                        ConversionStage.Extracting => p.Total > 0 ? $"Extracting pack... ({p.Completed}/{p.Total})" : "Extracting pack...",
                        ConversionStage.ScanningTextures => "Scanning textures...",
                        ConversionStage.GeneratingSpecular => $"Specular: {p.CurrentTextureName}",
                        ConversionStage.GeneratingNormals => $"Normals: {p.CurrentTextureName}",
                        ConversionStage.Packing => p.Total > 0 ? $"Packing output zip... ({p.Completed}/{p.Total})" : "Packing output zip...",
                        ConversionStage.Done => "Done.",
                        _ => p.Stage.ToString()
                    };
                });
            });

            LogLines.Add($"Converting → {OutputZipPath}");
            await Task.Run(async () =>
                await converter.ConvertAsync(PackPath!, OutputZipPath!, options, prog, _cts.Token).ConfigureAwait(false));
            // Resume on UI thread so we don't get "Call from invalid thread" when updating bindings.
            LogLines.Add("Done.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            LogLines.Add("Cancelled.");
            var totalSec = (DateTime.UtcNow - _conversionStartUtc).TotalSeconds;
            LogLines.Add($"Total time: {totalSec:F1} s");
        }
        catch (Exception ex)
        {
            StatusText = "Conversion failed.";
            LogLines.Add(ex.ToString());
            var totalSec = (DateTime.UtcNow - _conversionStartUtc).TotalSeconds;
            LogLines.Add($"Total time: {totalSec:F1} s");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsConverting = false;
            IsBusy = false;
            ConvertCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            LoadTexturesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConvert() =>
        !IsConverting &&
        !IsBusy &&
        IsPackPath(PackPath) &&
        File.Exists(PackPath) &&
        !string.IsNullOrWhiteSpace(OutputZipPath);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        _cts?.Cancel();
    }

    private bool CanCancel() => IsConverting;

    private static string StageDisplayName(ConversionStage stage) => stage switch
    {
        ConversionStage.Extracting => "Extracting",
        ConversionStage.ScanningTextures => "Scanning",
        ConversionStage.GeneratingSpecular => "Specular",
        ConversionStage.GeneratingNormals => "Normals",
        ConversionStage.Packing => "Packing",
        _ => stage.ToString()
    };
}
