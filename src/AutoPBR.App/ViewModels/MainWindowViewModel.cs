using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetBrains.Annotations;
using AutoPBR.App.Lang;
using AutoPBR.App.Models;
using AutoPBR.Core;
using AutoPBR.Core.Models;
using NormalOperatorEnum = AutoPBR.Core.Models.NormalOperator;
using NormalKernelSizeEnum = AutoPBR.Core.Models.NormalKernelSize;
using NormalDerivativeEnum = AutoPBR.Core.Models.NormalDerivative;

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IArchiveNodeHost
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _previewCts;
    private ScannedArchiveData? _scannedArchiveData;
    private string? _scannedArchivePath;
    private readonly ConcurrentDictionary<string, bool?> _pathOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _folderVisibilityCache = new(StringComparer.OrdinalIgnoreCase);
    private SpecularData? _specularData;
    private readonly UserSettings _settings;
    private readonly bool _loadingSettings;
    private DateTime _lastLogWriteUtc = DateTime.MinValue;
    private const int LogWriteIntervalMs = 250;
    private const int MaxLogLines = 600;

    private void AddLogLine(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }

    private DateTime _conversionStartUtc;
    private ConversionStage? _currentStage;
    private DateTime _stageStartUtc;
    private string? _statusKey;
    private object[]? _statusFormatArgs;

    [ObservableProperty] private string? _packPath;
    [ObservableProperty] private string? _outputDirectory;

    [ObservableProperty] private double _normalIntensity = AutoPbrDefaults.DefaultNormalIntensity;
    [ObservableProperty] private double _heightIntensity = AutoPbrDefaults.DefaultHeightIntensity;
    [ObservableProperty] private bool _fastSpecular;
    [ObservableProperty] private string _foliageMode = "Ignore All";
    [ObservableProperty] private bool _useLegacyExtractor;
    [ObservableProperty] private double _smoothnessScale = AutoPbrDefaults.DefaultSmoothnessScale;
    [ObservableProperty] private double _metallicBoost = AutoPbrDefaults.DefaultMetallicBoost;
    [ObservableProperty] private double _porosityBias = AutoPbrDefaults.DefaultPorosityBias;
    [ObservableProperty] private int _maxThreads; // 0 = auto
    [ObservableProperty] private int _maxThreadsMax = Math.Max(1, Environment.ProcessorCount);
    [ObservableProperty] private string? _tempDirectory;
    [ObservableProperty] private bool _processBlocks = true;
    [ObservableProperty] private bool _processItems = true;
    [ObservableProperty] private bool _processArmor = true;
    [ObservableProperty] private bool _processEntity = true;
    [ObservableProperty] private bool _processParticles = true;
    [ObservableProperty] private bool _useDeepBumpNormals;
    [ObservableProperty] private string _deepBumpOverlap = "Large";
    [ObservableProperty] private string _normalOperator = nameof(AutoPBR.Core.Models.NormalOperator.SobelVc);
    [ObservableProperty] private string _normalKernelSize = "3";
    [ObservableProperty] private string _normalDerivative = nameof(AutoPBR.Core.Models.NormalDerivative.Luminance);

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConverting;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 1;

    [ObservableProperty] private string? _outputZipPath;

    /// <summary>Search filter for the Resource Explorer tree (Explore tab). Filters nodes by path/name.</summary>
    [ObservableProperty] private string _exploreFilter = "";

    [ObservableProperty] private string _colorScheme = "Dark";
    [ObservableProperty] private LanguageOption? _selectedLanguage;
    [ObservableProperty] private FoliageModeOption? _selectedFoliageMode;
    [ObservableProperty] private FoliageModeOption? _selectedDeepBumpOverlap;
    [ObservableProperty] private FoliageModeOption? _selectedNormalOperator;
    [ObservableProperty] private FoliageModeOption? _selectedNormalKernelSize;
    [ObservableProperty] private FoliageModeOption? _selectedNormalDerivative;
    [ObservableProperty] private FoliageModeOption? _selectedColorSchemeOption;
    [ObservableProperty] private IBrush _windowBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _cardBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _cardBorderBrush = Brushes.Gray;
    [ObservableProperty] private IBrush _accentBrush = Brushes.DeepSkyBlue;
    [ObservableProperty] private IBrush _foregroundBrush = Brushes.White;

    [ObservableProperty] private string? _previewArchivePath;
    [ObservableProperty] private string? _previewTextureName;
    [ObservableProperty] private Bitmap? _previewImage;
    /// <summary>Color used for preview panel top/bottom gradient fade (matches CardBackground).</summary>
    [ObservableProperty] private Color _previewFadeColor = Color.FromRgb(0x22, 0x22, 0x2A);

    public ObservableCollection<string> LogLines { get; } = new();

    private static string LogsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPBR", "logs");

    /// <summary>Persist the current in-memory log to a file under the logs directory, keeping at most 10 files.</summary>
    private void SaveLogToFile()
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"AutoPBR_{timestamp}.log";
            var fullPath = Path.Combine(LogsDirectory, fileName);
            File.WriteAllLines(fullPath, LogLines);

            // Keep only the 10 newest log files.
            var files = Directory.GetFiles(LogsDirectory, "AutoPBR_*.log")
                .OrderBy(File.GetCreationTimeUtc)
                .ToList();
            while (files.Count > 10)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                try
                {
                    File.Delete(oldest);
                }
                catch
                {
                    /* ignore cleanup errors */
                }
            }
        }
        catch
        {
            // Logging should never crash the app; ignore IO errors.
        }
    }

    /// <summary>Localized strings for bindings; replaced when language changes.</summary>
    public LocalizedStrings Strings { get; private set; }

    /// <summary>Foliage options for the dropdown (display name from Strings, value for settings/converter).</summary>
    public ObservableCollection<FoliageModeOption> FoliageModeOptions { get; } = new();

    /// <summary>DeepBump tile overlap options (Small, Medium, Large). Matches DeepBump --color_to_normals-overlap.</summary>
    public ObservableCollection<FoliageModeOption> DeepBumpOverlapOptions { get; } = new();

    /// <summary>Normal operator options (Sobel + VC, Scharr + VC).</summary>
    public ObservableCollection<FoliageModeOption> NormalOperatorOptions { get; } = new();

    /// <summary>Normal kernel size options (3x3, 5x5, 7x7 for Sobel; 3x3,5x5 for Scharr).</summary>
    public ObservableCollection<FoliageModeOption> NormalKernelSizeOptions { get; } = new();

    /// <summary>Derivative source options: Color, Luminance, Color+Luminance Blend, Color+Luminance Max.</summary>
    public ObservableCollection<FoliageModeOption> NormalDerivativeOptions { get; } = new();

    /// <summary>Color scheme options for the Appearance dropdown (display name from Resources, value for settings).</summary>
    public ObservableCollection<FoliageModeOption> ColorSchemeOptions { get; } = new();

    /// <summary>Root of the scanned archive tree for the Explore tab. Null until user clicks Scan or when cleared.</summary>
    [ObservableProperty] private ArchiveNode? _scannedArchiveRoot;

    public bool HasScannedArchive => ScannedArchiveRoot != null;
    public bool ShowExploreEmptyMessage => !HasScannedArchive;

    private static readonly ObservableCollection<ArchiveNode> EmptyArchiveNodes = new();

    public ObservableCollection<ArchiveNode> ScannedArchiveTopLevel =>
        ScannedArchiveRoot?.Children ?? EmptyArchiveNodes;

    /// <summary>Folder we're currently viewing in Explore; null = root. After scan, defaults to "assets" if present.</summary>
    [ObservableProperty] private ArchiveNode? _focusedArchiveNode;

    /// <summary>Items to show in Explore tree: children of focused folder, or root's children when no focus.</summary>
    public ObservableCollection<ArchiveNode> ExploreViewItems => FocusedArchiveNode?.Children ?? ScannedArchiveTopLevel;

    /// <summary>Breadcrumb path for Explore (from root to current folder); click to navigate.</summary>
    public ObservableCollection<ArchiveNode> ExploreBreadcrumb { get; } = new();

    public bool CanGoBackExplore => FocusedArchiveNode != null;

    private void RebuildExploreBreadcrumb()
    {
        ExploreBreadcrumb.Clear();
        if (FocusedArchiveNode is null)
            return;
        var path = new List<ArchiveNode>();
        for (var n = FocusedArchiveNode; n != null && !string.IsNullOrEmpty(n.Name); n = n.Parent)
            path.Add(n);
        path.Reverse();
        foreach (var node in path)
            ExploreBreadcrumb.Add(node);
    }

    /// <summary>Languages shown in the Language dropdown (display name, culture code). Top 10 most spoken worldwide.</summary>
    public ObservableCollection<LanguageOption> SupportedLanguages { get; } = new(
    [
        new LanguageOption("English", "en"),
        new LanguageOption("中文 (简体)", "zh-Hans"),
        new LanguageOption("Español", "es"),
        new LanguageOption("हिन्दी", "hi"),
        new LanguageOption("Français", "fr"),
        new LanguageOption("العربية", "ar"),
        new LanguageOption("Português", "pt"),
        new LanguageOption("Русский", "ru"),
        new LanguageOption("Deutsch", "de"),
        new LanguageOption("日本語", "ja"),
    ]);

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
            UseLegacyExtractor = _settings.UseLegacyExtractor;
            SmoothnessScale = _settings.SmoothnessScale;
            MetallicBoost = _settings.MetallicBoost;
            PorosityBias = _settings.PorosityBias;
            MaxThreads = _settings.MaxThreads;
            TempDirectory = _settings.TempDirectory;
            ColorScheme = string.IsNullOrWhiteSpace(_settings.ColorScheme) ? "Dark" : _settings.ColorScheme;
            ProcessBlocks = _settings.ProcessBlocks;
            ProcessItems = _settings.ProcessItems;
            ProcessArmor = _settings.ProcessArmor;
            ProcessEntity = _settings.ProcessEntity;
            ProcessParticles = _settings.ProcessParticles;
            UseDeepBumpNormals = _settings.UseDeepBumpNormals;
            DeepBumpOverlap = string.IsNullOrWhiteSpace(_settings.DeepBumpOverlap)
                ? "Large"
                : _settings.DeepBumpOverlap;
            NormalOperator = string.IsNullOrWhiteSpace(_settings.NormalOperator)
                ? nameof(AutoPBR.Core.Models.NormalOperator.SobelVc)
                : _settings.NormalOperator;
            NormalKernelSize = string.IsNullOrWhiteSpace(_settings.NormalKernelSize) ? "3" : _settings.NormalKernelSize;
            NormalDerivative = string.IsNullOrWhiteSpace(_settings.NormalDerivative)
                ? nameof(AutoPBR.Core.Models.NormalDerivative.Luminance)
                : _settings.NormalDerivative;
            ApplyColorScheme();
            var lang = string.IsNullOrWhiteSpace(_settings.Language) ? "en" : _settings.Language;
            ApplyCulture(lang);
            Strings = new LocalizedStrings();
            SelectedLanguage = SupportedLanguages.FirstOrDefault(x =>
                                   string.Equals(x.CultureCode, _settings.Language,
                                       StringComparison.OrdinalIgnoreCase)) ??
                               SupportedLanguages[0];
            RefreshFoliageModeOptions();
            RefreshDeepBumpOverlapOptions();
            RefreshNormalOperatorOptions();
            RefreshNormalKernelSizeOptions();
            RefreshNormalDerivativeOptions();
            RefreshColorSchemeOptions();
            SetStatus("Status_SelectPack");
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void ApplyCulture(string cultureCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureCode);
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
            Resources.Culture = culture;
        }
        catch
        {
            Resources.Culture = null;
        }

        Strings = new LocalizedStrings();
        OnPropertyChanged(nameof(Strings));
        RefreshFoliageModeOptions();
        RefreshDeepBumpOverlapOptions();
        RefreshNormalOperatorOptions();
        RefreshNormalKernelSizeOptions();
        RefreshNormalDerivativeOptions();
        RefreshColorSchemeOptions();
        UpdateStatusText();
    }

    private void RefreshColorSchemeOptions()
    {
        ColorSchemeOptions.Clear();
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeDark"), "Dark"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeBlue"), "Blue"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeGreen"), "Green"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemePurple"), "Purple"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeAmber"), "Amber"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeTeal"), "Teal"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeRose"), "Rose"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeMono"), "Mono"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeOcean"), "Ocean"));
        ColorSchemeOptions.Add(new FoliageModeOption(Resources.GetString("ColorSchemeSunset"), "Sunset"));
        SelectedColorSchemeOption = ColorSchemeOptions.FirstOrDefault(x =>
                                        string.Equals(x.Value, ColorScheme, StringComparison.OrdinalIgnoreCase))
                                    ?? ColorSchemeOptions[0];
    }

    private void RefreshDeepBumpOverlapOptions()
    {
        DeepBumpOverlapOptions.Clear();
        DeepBumpOverlapOptions.Add(new FoliageModeOption(Strings.DeepBumpOverlapSmall, "Small"));
        DeepBumpOverlapOptions.Add(new FoliageModeOption(Strings.DeepBumpOverlapMedium, "Medium"));
        DeepBumpOverlapOptions.Add(new FoliageModeOption(Strings.DeepBumpOverlapLarge, "Large"));
        SelectedDeepBumpOverlap =
            DeepBumpOverlapOptions.FirstOrDefault(x =>
                string.Equals(x.Value, DeepBumpOverlap, StringComparison.OrdinalIgnoreCase)) ??
            DeepBumpOverlapOptions[2];
    }

    private void RefreshFoliageModeOptions()
    {
        FoliageModeOptions.Clear();
        FoliageModeOptions.Add(new FoliageModeOption(Strings.IgnoreAll, "Ignore All"));
        FoliageModeOptions.Add(new FoliageModeOption(Strings.NoHeight, "No Height"));
        FoliageModeOptions.Add(new FoliageModeOption(Strings.ConvertAll, "Convert All"));
        SelectedFoliageMode =
            FoliageModeOptions.FirstOrDefault(x =>
                string.Equals(x.Value, FoliageMode, StringComparison.OrdinalIgnoreCase)) ?? FoliageModeOptions[0];
    }

    private void RefreshNormalOperatorOptions()
    {
        NormalOperatorOptions.Clear();
        NormalOperatorOptions.Add(new FoliageModeOption("Sobel + VC (default)",
            nameof(AutoPBR.Core.Models.NormalOperator.SobelVc)));
        NormalOperatorOptions.Add(new FoliageModeOption("Scharr + VC (stronger edges)",
            nameof(AutoPBR.Core.Models.NormalOperator.ScharrVc)));
        SelectedNormalOperator = NormalOperatorOptions.FirstOrDefault(x =>
                                     string.Equals(x.Value, NormalOperator, StringComparison.OrdinalIgnoreCase))
                                 ?? NormalOperatorOptions[0];
    }

    private void RefreshNormalKernelSizeOptions()
    {
        NormalKernelSizeOptions.Clear();
        var isScharr = string.Equals(NormalOperator, nameof(AutoPBR.Core.Models.NormalOperator.ScharrVc),
            StringComparison.OrdinalIgnoreCase);
        NormalKernelSizeOptions.Add(new FoliageModeOption("3x3", "3"));
        NormalKernelSizeOptions.Add(new FoliageModeOption("5x5", "5"));
        if (!isScharr)
            NormalKernelSizeOptions.Add(new FoliageModeOption("7x7", "7"));
        SelectedNormalKernelSize = NormalKernelSizeOptions.FirstOrDefault(x =>
                                       string.Equals(x.Value, NormalKernelSize, StringComparison.OrdinalIgnoreCase))
                                   ?? NormalKernelSizeOptions[0];
    }

    private void RefreshNormalDerivativeOptions()
    {
        NormalDerivativeOptions.Clear();
        NormalDerivativeOptions.Add(new FoliageModeOption(Resources.GetString("NormalDerivative_Luminance"),
            nameof(AutoPBR.Core.Models.NormalDerivative.Luminance)));
        NormalDerivativeOptions.Add(new FoliageModeOption(Resources.GetString("NormalDerivative_Color"),
            nameof(AutoPBR.Core.Models.NormalDerivative.Color)));
        NormalDerivativeOptions.Add(new FoliageModeOption(Resources.GetString("NormalDerivative_ColorLuminanceBlend"),
            nameof(AutoPBR.Core.Models.NormalDerivative.ColorLuminanceBlend)));
        NormalDerivativeOptions.Add(new FoliageModeOption(Resources.GetString("NormalDerivative_ColorLuminanceMax"),
            nameof(AutoPBR.Core.Models.NormalDerivative.ColorLuminanceMax)));
        SelectedNormalDerivative = NormalDerivativeOptions.FirstOrDefault(x =>
                                       string.Equals(x.Value, NormalDerivative, StringComparison.OrdinalIgnoreCase))
                                   ?? NormalDerivativeOptions[0];
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusFormatArgs = args.Length > 0 ? args : null;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_statusKey is null)
        {
            StatusText = Resources.GetString("Status_SelectPack");
            return;
        }

        StatusText = _statusFormatArgs is null || _statusFormatArgs.Length == 0
            ? Resources.GetString(_statusKey)
            : Resources.GetStatusString(_statusKey, _statusFormatArgs);
    }

    partial void OnPackPathChanged(string? value)
    {
        _ = value;
        ClearScannedArchive();
        RecomputeOutputZipPath();
        ConvertCommand.NotifyCanExecuteChanged();
        ScanArchiveCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        _ = value;
        RecomputeOutputZipPath();
        ConvertCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    private void RefreshPreviewIfActive()
    {
        if (string.IsNullOrWhiteSpace(PreviewArchivePath))
            return;

        _ = UpdatePreviewAsync();
    }

    partial void OnFastSpecularChanged(bool value)
    {
        _ = value;
        RecomputeOutputZipPath();
        ConvertCommand.NotifyCanExecuteChanged();
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnNormalIntensityChanged(double value)
    {
        _ = value;
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnHeightIntensityChanged(double value)
    {
        _ = value;
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnUseLegacyExtractorChanged(bool value)
    {
        _ = value;
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnSmoothnessScaleChanged(double value)
    {
        _ = value;
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnMetallicBoostChanged(double value)
    {
        _ = value;
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnPorosityBiasChanged(double value)
    {
        _ = value;
        SaveSettings();
        RefreshPreviewIfActive();
    }
    partial void OnMaxThreadsChanged(int value) { _ = value; SaveSettings(); }
    partial void OnTempDirectoryChanged(string? value) { _ = value; SaveSettings(); }
    partial void OnExploreFilterChanged(string value) { _ = value; ApplyExploreFilter(); }

    partial void OnColorSchemeChanged(string value)
    {
        _ = value;
        ApplyColorScheme();
        SaveSettings();
    }

    partial void OnSelectedColorSchemeOptionChanged(FoliageModeOption? value)
    {
        if (value != null)
            ColorScheme = value.Value;
    }

    [UsedImplicitly] // Invoked by CommunityToolkit.Mvvm source generator when SelectedLanguage changes
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_loadingSettings)
            return;
        var code = value?.CultureCode ?? "en";
        ApplyCulture(code);
        _settings.Language = code;
        _settings.Save();
    }

    partial void OnProcessBlocksChanged(bool value)
    {
        _ = value;
        SaveSettings();
        ApplyTextureTypeOverridesToExplore();
    }

    partial void OnProcessItemsChanged(bool value)
    {
        _ = value;
        SaveSettings();
        ApplyTextureTypeOverridesToExplore();
    }

    partial void OnProcessArmorChanged(bool value)
    {
        _ = value;
        SaveSettings();
        ApplyTextureTypeOverridesToExplore();
    }

    partial void OnProcessEntityChanged(bool value)
    {
        _ = value;
        SaveSettings();
        ApplyTextureTypeOverridesToExplore();
    }

    partial void OnProcessParticlesChanged(bool value)
    {
        _ = value;
        SaveSettings();
        ApplyTextureTypeOverridesToExplore();
    }

    partial void OnUseDeepBumpNormalsChanged(bool value)
    {
        _ = value;
        SaveSettings();
        RefreshPreviewIfActive();
    }

    [UsedImplicitly] // Invoked by CommunityToolkit.Mvvm source generator when SelectedDeepBumpOverlap changes
    partial void OnSelectedDeepBumpOverlapChanged(FoliageModeOption? value)
    {
        if (_loadingSettings)
            return;
        DeepBumpOverlap = value?.Value ?? "Large";
        SaveSettings();
        RefreshPreviewIfActive();
    }

    [UsedImplicitly] // Invoked by CommunityToolkit.Mvvm source generator when SelectedNormalOperator changes
    partial void OnSelectedNormalOperatorChanged(FoliageModeOption? value)
    {
        if (_loadingSettings)
            return;
        NormalOperator = value?.Value ?? nameof(AutoPBR.Core.Models.NormalOperator.SobelVc);
        RefreshNormalKernelSizeOptions();
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnSelectedNormalKernelSizeChanged(FoliageModeOption? value)
    {
        if (_loadingSettings)
            return;
        NormalKernelSize = value?.Value ?? "3";
        SaveSettings();
        RefreshPreviewIfActive();
    }

    partial void OnSelectedNormalDerivativeChanged(FoliageModeOption? value)
    {
        if (_loadingSettings)
            return;
        NormalDerivative = value?.Value ?? nameof(AutoPBR.Core.Models.NormalDerivative.Luminance);
        SaveSettings();
        RefreshPreviewIfActive();
    }

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
        OutputZipPath = ext.Equals(".jar", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(OutputDirectory, baseName + ".zip")
            : Path.Combine(OutputDirectory, $"{baseName}_PBR.zip");
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
        _settings.UseLegacyExtractor = UseLegacyExtractor;
        _settings.SmoothnessScale = SmoothnessScale;
        _settings.MetallicBoost = MetallicBoost;
        _settings.PorosityBias = PorosityBias;
        _settings.MaxThreads = MaxThreads;
        _settings.TempDirectory = TempDirectory;
        _settings.ColorScheme = ColorScheme;
        _settings.Language = SelectedLanguage?.CultureCode ?? "en";
        _settings.ProcessBlocks = ProcessBlocks;
        _settings.ProcessItems = ProcessItems;
        _settings.ProcessArmor = ProcessArmor;
        _settings.ProcessEntity = ProcessEntity;
        _settings.ProcessParticles = ProcessParticles;
        _settings.UseDeepBumpNormals = UseDeepBumpNormals;
        _settings.DeepBumpOverlap = DeepBumpOverlap;
        _settings.NormalOperator = NormalOperator;
        _settings.NormalKernelSize = NormalKernelSize;
        _settings.NormalDerivative = NormalDerivative;
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
                PreviewFadeColor = Color.FromRgb(0x22, 0x22, 0x2A);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // green
                ForegroundBrush = Brushes.White;
                break;

            case "Blue":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x0B, 0x1B, 0x30));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x13, 0x27, 0x43));
                PreviewFadeColor = Color.FromRgb(0x13, 0x27, 0x43);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x5B, 0x8C));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)); // blue
                ForegroundBrush = Brushes.White;
                break;

            case "Green":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x0D, 0x1F, 0x16));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x14, 0x30, 0x22));
                PreviewFadeColor = Color.FromRgb(0x14, 0x30, 0x22);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)); // green
                ForegroundBrush = Brushes.White;
                break;

            case "Purple":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x22, 0x18, 0x3A));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x2E, 0x1F, 0x4D));
                PreviewFadeColor = Color.FromRgb(0x2E, 0x1F, 0x4D);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x95, 0x7D, 0xD1));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0x86, 0xFC));
                ForegroundBrush = Brushes.White;
                break;

            case "Amber":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x26, 0x15, 0x06));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x3A, 0x23, 0x0B));
                PreviewFadeColor = Color.FromRgb(0x3A, 0x23, 0x0B);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00));
                ForegroundBrush = Brushes.White;
                break;

            case "Teal":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x24, 0x27));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x37, 0x3B));
                PreviewFadeColor = Color.FromRgb(0x00, 0x37, 0x3B);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0xAB, 0xA8));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x96, 0x88));
                ForegroundBrush = Brushes.White;
                break;

            case "Rose":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x2B, 0x0B, 0x18));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x3B, 0x12, 0x22));
                PreviewFadeColor = Color.FromRgb(0x3B, 0x12, 0x22);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x81, 0x82));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x63));
                ForegroundBrush = Brushes.White;
                break;

            case "Mono":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                PreviewFadeColor = Color.FromRgb(0x2A, 0x2A, 0x2A);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                ForegroundBrush = Brushes.White;
                break;

            case "Ocean":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x05, 0x21, 0x2F));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x0A, 0x33, 0x45));
                PreviewFadeColor = Color.FromRgb(0x0A, 0x33, 0x45);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0xA6, 0xD4));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x02, 0x88, 0xD1));
                ForegroundBrush = Brushes.White;
                break;

            case "Sunset":
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x29, 0x19, 0x14));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x3C, 0x22, 0x1B));
                PreviewFadeColor = Color.FromRgb(0x3C, 0x22, 0x1B);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x57, 0x22));
                ForegroundBrush = Brushes.White;
                break;

            default:
                // Fallback to Dark if something unexpected is stored.
                WindowBackground = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x18));
                CardBackground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2A));
                PreviewFadeColor = Color.FromRgb(0x22, 0x22, 0x2A);
                CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66));
                AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                ForegroundBrush = Brushes.White;
                break;
        }
    }

    private static readonly HashSet<string> TextureTypeFolderNames = new(StringComparer.OrdinalIgnoreCase)
        { "block", "blocks", "item", "items", "entity", "particle" };

    private static readonly HashSet<string> IgnoredOptifineFolders = new(StringComparer.OrdinalIgnoreCase)
        { "anim", "colormap", "sky" };

    /// <summary>Enumerate folder paths under .../textures/&lt;type&gt; for type in block, blocks, item, items, entity, particle.</summary>
    private HashSet<string> GetTextureTypeFolderPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_scannedArchiveData is null)
            return seen;
        foreach (var fullPath in _scannedArchiveData.EnumerateAllFilePaths())
        {
            var segments = fullPath.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!segments[i].Equals("textures", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (i + 1 >= segments.Length)
                    continue;
                var typeName = segments[i + 1];
                if (!TextureTypeFolderNames.Contains(typeName))
                    continue;
                var folderPath = string.Join("/", segments.Take(i + 2));
                seen.Add(folderPath);
            }
        }

        return seen;
    }

    private bool GetProcessValueForTextureFolder(string folderPath)
    {
        var seg = folderPath.Split('/');
        var last = seg.Length > 0 ? seg[^1] : "";
        if (last.Equals("block", StringComparison.OrdinalIgnoreCase) ||
            last.Equals("blocks", StringComparison.OrdinalIgnoreCase))
            return ProcessBlocks;
        if (last.Equals("item", StringComparison.OrdinalIgnoreCase) ||
            last.Equals("items", StringComparison.OrdinalIgnoreCase))
            return ProcessItems;
        if (last.Equals("entity", StringComparison.OrdinalIgnoreCase))
            return ProcessEntity;
        if (last.Equals("particle", StringComparison.OrdinalIgnoreCase))
            return ProcessParticles;
        return true;
    }

    /// <summary>Warm up visibility info for the first few levels of folders so tree expansion is smoother. Runs on a background thread; set thread name for debugger. Respects cancellation (e.g. when user clears archive or starts a new scan).</summary>
    private void PrewarmFolderVisibilityCache(CancellationToken cancellationToken)
    {
        try
        {
            Thread.CurrentThread.Name ??= "AutoPBR.Prewarm";
        }
        catch (InvalidOperationException)
        {
            /* already set */
        }

        var data = _scannedArchiveData;
        if (data is null)
            return;

        const int maxDepth = 3;
        var queue = new Queue<(string path, int depth)>();
        queue.Enqueue(("", 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (parent, depth) = queue.Dequeue();
            var children = data.GetChildren(parent);
            if (children is null)
                continue;

            foreach (var c in children)
            {
                if (!c.IsFolder)
                    continue;

                if (depth < maxDepth)
                    queue.Enqueue((c.FullPath, depth + 1));

                if (!_folderVisibilityCache.ContainsKey(c.FullPath))
                {
                    var visible = ComputeFolderVisible(data, c.FullPath);
                    _folderVisibilityCache[c.FullPath] = visible;
                }
            }
        }
    }

    private void ApplyTextureTypeOverridesToExplore()
    {
        // Remember where the user is in the tree so we can try to restore it after refresh.
        var previousFocusPath = FocusedArchiveNode?.FullPath;

        var paths = GetTextureTypeFolderPaths();
        if (paths.Count == 0)
            return;
        _folderVisibilityCache.Clear();
        foreach (var path in paths)
        {
            var include = GetProcessValueForTextureFolder(path);
            _pathOverrides[path] = include;
        }

        NotifyOverrideChangedForPaths(paths);
        RefreshExploreTreeFilter();

        // Try to restore the previous focused folder if it still exists after filtering.
        if (!string.IsNullOrEmpty(previousFocusPath))
        {
            var restored = FindNodeByFullPath(previousFocusPath);
            if (restored is not null)
                FocusedArchiveNode = restored;
        }

        // Ensure expand/collapse arrows are visible in the current view.
        PreloadExpandersForCurrentView();
    }

    /// <summary>Clear all loaded tree children and repopulate from root so visibility filter (no-PNG / full filter) is re-applied.</summary>
    private void RefreshExploreTreeFilter()
    {
        if (ScannedArchiveRoot is null)
            return;
        ClearChildrenRecursive(ScannedArchiveRoot);
        (this as IArchiveNodeHost).EnsureChildrenLoaded(ScannedArchiveRoot);
        ApplyExploreFilter();
    }

    /// <summary>Apply Resource Explorer search filter: show nodes whose path/name matches, or that contain a matching descendant.</summary>
    private void ApplyExploreFilter()
    {
        if (ScannedArchiveRoot is null)
            return;
        var f = ExploreFilter.Trim();
        ApplyExploreFilterRecursive(ScannedArchiveRoot, f);
    }

    private static bool ApplyExploreFilterRecursive(ArchiveNode node, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            node.IsVisibleByFilter = true;
            foreach (var child in node.Children)
                ApplyExploreFilterRecursive(child, filter);
            return true;
        }

        bool selfMatch = node.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool anyChildVisible = false;
        foreach (var child in node.Children)
        {
            if (ApplyExploreFilterRecursive(child, filter))
                anyChildVisible = true;
        }

        node.IsVisibleByFilter = selfMatch || anyChildVisible;
        return node.IsVisibleByFilter;
    }

    private static void ClearChildrenRecursive(ArchiveNode node)
    {
        foreach (var child in node.Children)
            ClearChildrenRecursive(child);
        node.Children.Clear();
    }

    private void NotifyOverrideChangedForPaths(HashSet<string> paths)
    {
        if (paths.Count == 0 || ScannedArchiveRoot is null)
            return;
        NotifyOverrideChangedRecursive(ScannedArchiveRoot, paths);
    }

    private static void NotifyOverrideChangedRecursive(ArchiveNode node, HashSet<string> paths)
    {
        if (paths.Contains(node.FullPath))
            node.NotifyOverrideChanged();
        foreach (var child in node.Children)
            NotifyOverrideChangedRecursive(child, paths);
    }

    private static bool IsPackPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

    private bool CanScanArchive() => !IsConverting && !IsBusy && IsPackPath(PackPath) && File.Exists(PackPath);

    private void ClearScannedArchive()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        FocusedArchiveNode = null;
        ScannedArchiveRoot = null;
        _scannedArchiveData = null;
        _scannedArchivePath = null;
        _pathOverrides.Clear();
        _folderVisibilityCache.Clear();
    }

    private bool HaveScanForCurrentPack() =>
        _scannedArchiveData is not null && _scannedArchivePath is not null && PackPath is not null &&
        string.Equals(Path.GetFullPath(_scannedArchivePath), Path.GetFullPath(PackPath),
            StringComparison.OrdinalIgnoreCase);

    bool? IArchiveNodeHost.GetOverride(string fullPath) =>
        _pathOverrides.GetValueOrDefault(fullPath);

    void IArchiveNodeHost.SetOverride(string fullPath, bool? value)
    {
        if (value.HasValue)
            _pathOverrides[fullPath] = value;
        else
            _pathOverrides.TryRemove(fullPath, out _);
    }

    /// <summary>True if the folder has at least one PNG file in its subtree that is not excluded by the current overrides.</summary>
    private bool HasVisiblePngUnder(string folderPath)
    {
        if (_folderVisibilityCache.TryGetValue(folderPath, out var cached))
            return cached;

        if (_scannedArchiveData is null)
            return false;

        // Walk only this folder's subtree via ChildIndex instead of scanning the entire archive.
        bool visible = ComputeFolderVisible(_scannedArchiveData, folderPath);
        _folderVisibilityCache[folderPath] = visible;
        return visible;
    }

    /// <summary>Internal helper: does folderPath have any PNG in its subtree that is not excluded by current overrides?</summary>
    private bool ComputeFolderVisible(ScannedArchiveData data, string folderPath)
    {
        var queue = new Queue<string>();
        queue.Enqueue(folderPath);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var children = data.GetChildren(parent);
            if (children is null)
                continue;
            foreach (var c in children)
            {
                if (c.IsFolder)
                {
                    queue.Enqueue(c.FullPath);
                }
                else
                {
                    if (GetEffectiveOverrideForPath(c.FullPath) != false)
                        return true;
                }
            }
        }

        return false;
    }

    private static bool IsIgnoredOptifineFolder(string fullPath)
    {
        // Ignore assets/<namespace>/optifine/{anim,colormap,sky} and everything under them.
        var segments = fullPath.Split('/');
        if (segments.Length < 4)
            return false;
        if (!segments[0].Equals("assets", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!segments[2].Equals("optifine", StringComparison.OrdinalIgnoreCase))
            return false;
        var typeSegment = segments[3];
        return IgnoredOptifineFolders.Contains(typeSegment);
    }

    void IArchiveNodeHost.EnsureChildrenLoaded(ArchiveNode node)
    {
        if (_scannedArchiveData is null || node.Children.Count > 0)
            return;
        var children = _scannedArchiveData.GetChildren(node.FullPath);
        if (children is null)
            return;
        foreach (var entry in children)
        {
            if (entry.IsFolder)
            {
                if (IsIgnoredOptifineFolder(entry.FullPath))
                    continue;
                if (!HasVisiblePngUnder(entry.FullPath))
                    continue;
            }
            else
            {
                if (GetEffectiveOverrideForPath(entry.FullPath) == false)
                    continue;
            }

            var child = new ArchiveNode(entry.Name, entry.FullPath, entry.IsFolder, node, this);
            node.Children.Add(child);
        }

        ApplyExploreFilter();
    }

    /// <summary>Ensure that all folders currently shown in the Explore view have their children loaded, so expand arrows are visible.</summary>
    private void PreloadExpandersForCurrentView()
    {
        if (ScannedArchiveRoot is null)
            return;
        var host = (IArchiveNodeHost)this;
        var roots = FocusedArchiveNode?.Children ?? ScannedArchiveRoot.Children;
        foreach (var node in roots)
        {
            if (node.IsFolder)
                host.EnsureChildrenLoaded(node);
        }
    }

    private static ArchiveNode? FindChildByName(ArchiveNode parent, string name)
    {
        foreach (var c in parent.Children)
        {
            if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return c;
        }

        return null;
    }

    /// <summary>Find a node by its archive FullPath, starting from the root, loading intermediate children as needed.</summary>
    private ArchiveNode? FindNodeByFullPath(string fullPath)
    {
        if (ScannedArchiveRoot is null)
            return null;
        if (string.IsNullOrEmpty(fullPath))
            return ScannedArchiveRoot;

        var host = (IArchiveNodeHost)this;
        var current = ScannedArchiveRoot;
        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            host.EnsureChildrenLoaded(current);
            ArchiveNode? next = null;
            foreach (var child in current.Children)
            {
                if (child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
                return null;
            current = next;
        }

        return current;
    }

    [RelayCommand(CanExecute = nameof(CanGoBackExplore))]
    private void GoBackExplore()
    {
        if (FocusedArchiveNode is null)
            return;
        var parent = FocusedArchiveNode.Parent;
        if (parent is null || string.IsNullOrEmpty(parent.Name))
            FocusedArchiveNode = null;
        else
            FocusedArchiveNode = parent;
    }

    [RelayCommand]
    private void GoToBreadcrumb(ArchiveNode? node)
    {
        if (node != null)
            FocusedArchiveNode = node;
    }

    [RelayCommand]
    private void EnterFolder(ArchiveNode? node)
    {
        if (node is { IsFolder: true })
            FocusedArchiveNode = node;
    }

    private void ExpandAllInSubtree(ArchiveNode node, bool expand)
    {
        node.IsExpanded = expand;
        foreach (var c in node.Children)
            ExpandAllInSubtree(c, expand);
    }

    [RelayCommand]
    private void ExploreExpandAll()
    {
        if (ScannedArchiveRoot is null)
            return;
        var root = FocusedArchiveNode ?? ScannedArchiveRoot;
        ExpandAllInSubtree(root, true);
    }

    [RelayCommand]
    private void ExploreCollapseAll()
    {
        if (ScannedArchiveRoot is null)
            return;
        var root = FocusedArchiveNode ?? ScannedArchiveRoot;
        ExpandAllInSubtree(root, false);
    }

    private async Task UpdatePreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(PreviewArchivePath) || !IsPackPath(PackPath) || !File.Exists(PackPath))
            return;

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        try
        {
            if (_specularData is null)
            {
                SetStatus("Status_LoadingSpecularData");
                _specularData = SpecularData.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json"));
            }

            var options = BuildConversionOptions(new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
            var converter = new ResourcePackConverter();
            var pngBytes = await converter.RenderPreviewAsync(PackPath!, PreviewArchivePath!, options, ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(pngBytes);
                PreviewImage = new Bitmap(ms);
            });
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            AddLogLine(ex.ToString());
        }
    }

    [RelayCommand(CanExecute = nameof(CanScanArchive))]
    public async Task ScanArchiveAsync()
    {
        if (!IsPackPath(PackPath) || !File.Exists(PackPath))
            return;
        IsBusy = true;
        ScanArchiveCommand.NotifyCanExecuteChanged();
        ProgressValue = 0;
        ProgressMax = 1;
        SetStatus("Status_ScanningTexturesInPack");
        AddLogLine(Resources.GetStatusString("Log_ScanningArchive", PackPath ?? ""));

        _scanCts?.Cancel();
        // ReSharper disable once MethodHasAsyncOverload -- CancellationTokenSource has no DisposeAsync in this target
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var scanToken = _scanCts.Token;

        var scanProgress = new Progress<(int completed, int total)>(p =>
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProgressMax = Math.Max(1, p.total);
                ProgressValue = p.completed;
                SetStatus("Status_ScanningPackProgress", p.completed, p.total);
            });
        });
        try
        {
            var data = await Task.Run(() => BuildArchiveIndex(PackPath!, scanProgress), scanToken).ConfigureAwait(false);
            scanToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _scannedArchiveData = data;
                _scannedArchivePath = PackPath;
                var root = new ArchiveNode("", "", true, null, this);
                (this as IArchiveNodeHost).EnsureChildrenLoaded(root);
                ScannedArchiveRoot = root;
                ApplyTextureTypeOverridesToExplore();
                FocusedArchiveNode = FindChildByName(root, "assets");
                PreloadExpandersForCurrentView();
                SetStatus("Status_LoadedTextures", data.FileCount);
                AddLogLine(Resources.GetStatusString("Log_ArchiveContentsLoaded", data.FileCount));
            });
            await Task.Run(() => PrewarmFolderVisibilityCache(scanToken), scanToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // User cleared archive or started new scan; no error message
        }
        catch (Exception ex)
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetStatus("Status_FailedToScan");
                AddLogLine(ex.ToString());
            });
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload
            _scanCts?.Dispose();
            _scanCts = null;
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsBusy = false;
                ScanArchiveCommand.NotifyCanExecuteChanged();
                ConvertCommand.NotifyCanExecuteChanged();
            });
        }
    }

    /// <summary>Build a lightweight index (path -> immediate children) and file count. Only .png files are indexed; only entry names are read.</summary>
    private static ScannedArchiveData BuildArchiveIndex(string zipPath,
        IProgress<(int completed, int total)>? progress = null)
    {
        var childLists = new Dictionary<string, List<ArchiveChildEntry>>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.ToList();
        var total = entries.Count;
        var completed = 0;
        foreach (var entry in entries)
        {
            var full = entry.FullName.TrimEnd('/');
            if (string.IsNullOrEmpty(full))
            {
                progress?.Report((completed, total));
                completed++;
                continue;
            }

            var isEntryFolder = entry.FullName.EndsWith('/');
            if (!isEntryFolder && !full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report((completed, total));
                completed++;
                continue;
            }

            var segments = full.Split('/');
            var current = "";
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var isLast = i == segments.Length - 1;
                var isFile = isLast && !isEntryFolder;
                var path = current.Length == 0 ? segment : current + "/" + segment;
                var parentPath = current;
                if (!childLists.TryGetValue(parentPath, out var siblingList))
                {
                    siblingList = new List<ArchiveChildEntry>();
                    childLists[parentPath] = siblingList;
                }

                if (siblingList.Exists(c => c.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    current = path;
                    continue;
                }

                siblingList.Add(new ArchiveChildEntry(segment, path, !isFile));
                if (isFile)
                    fileCount++;
                current = path;
            }

            progress?.Report((completed, total));
            completed++;
        }

        var index = childLists.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ArchiveChildEntry>)kv.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);
        return new ScannedArchiveData(index, fileCount);
    }

    /// <summary>Convert archive path to converter texture key (e.g. assets/minecraft/textures/block/stone.png -> \minecraft\block\stone).</summary>
    private static string? ArchivePathToTextureKey(string fullPath)
    {
        var parts = fullPath.Replace('\\', '/').Split('/');
        if (parts.Length < 4 || !parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase))
            return null;
        var ns = parts[1];
        if (parts[2].Equals("textures", StringComparison.OrdinalIgnoreCase))
        {
            var after = string.Join("\\", parts.Skip(3));
            var noExt = Path.ChangeExtension(after, null);
            return "\\" + ns + "\\" + noExt;
        }

        if (parts[2].Equals("optifine", StringComparison.OrdinalIgnoreCase))
        {
            var after = string.Join("\\", parts.Skip(3));
            var noExt = Path.ChangeExtension(after, null);
            return "\\" + ns + "\\optifine\\" + noExt;
        }

        return null;
    }

    /// <summary>Collect force-include and force-exclude from path overrides and merge into the ignore set. Most specific path wins.</summary>
    private void ApplyExploreOverridesToIgnoreSet(HashSet<string> ignore)
    {
        if (_scannedArchiveData is null)
            return;
        foreach (var fullPath in _scannedArchiveData.EnumerateAllFilePaths())
        {
            var key = ArchivePathToTextureKey(fullPath);
            if (key is null)
                continue;
            var effective = GetEffectiveOverrideForPath(fullPath);
            if (!effective.HasValue)
                continue;
            if (effective.Value)
                ignore.Remove(key);
            else
                ignore.Add(key);
        }
    }

    private bool? GetEffectiveOverrideForPath(string fullPath)
    {
        var path = fullPath;
        while (!string.IsNullOrEmpty(path))
        {
            if (_pathOverrides.TryGetValue(path, out var v) && v.HasValue)
                return v;
            var slash = path.LastIndexOf('/');
            if (slash < 0)
                break;
            path = path[..slash];
        }

        return null;
    }

    partial void OnScannedArchiveRootChanged(ArchiveNode? value)
    {
        _ = value; // Partial method signature is generated; parameter not needed for this handler.
        OnPropertyChanged(nameof(HasScannedArchive));
        OnPropertyChanged(nameof(ShowExploreEmptyMessage));
        OnPropertyChanged(nameof(ScannedArchiveTopLevel));
    }

    partial void OnFocusedArchiveNodeChanged(ArchiveNode? value)
    {
        if (value is { IsFolder: true })
            (this as IArchiveNodeHost).EnsureChildrenLoaded(value);
        PreloadExpandersForCurrentView();
        OnPropertyChanged(nameof(ExploreViewItems));
        OnPropertyChanged(nameof(CanGoBackExplore));
        RebuildExploreBreadcrumb();
        GoBackExploreCommand.NotifyCanExecuteChanged();
    }

        [ObservableProperty] private ArchiveNode? _selectedExploreNode;

        [RelayCommand]
        private async Task SetPreviewTextureAsync(ArchiveNode? node)
        {
            if (node is null || node.IsFolder)
                return;

            PreviewArchivePath = node.FullPath;
            PreviewTextureName = node.FullPath;
            await UpdatePreviewAsync().ConfigureAwait(false);
        }

        [RelayCommand]
        private Task SetPreviewFromSelectionAsync() =>
            SetPreviewTextureAsync(SelectedExploreNode);

    [UsedImplicitly] // Invoked by CommunityToolkit.Mvvm source generator when SelectedFoliageMode changes
    partial void OnSelectedFoliageModeChanged(FoliageModeOption? value)
    {
        if (_loadingSettings)
            return;
        FoliageMode = value?.Value ?? "Ignore All";
        SaveSettings();
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
            SetStatus("Status_LoadingSpecularData");
            _specularData ??=
                SpecularData.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json"));

            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (FoliageMode == "Ignore All")
            {
                foreach (var p in AutoPbrDefaults.PlantTextureKeys)
                    ignore.Add(p);
            }

            ApplyExploreOverridesToIgnoreSet(ignore);

            if (!HaveScanForCurrentPack())
            {
                AddLogLine(Resources.GetString("Log_ScanningPackForExtraction"));
                var scanProg = new Progress<(int completed, int total)>(p =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        ProgressMax = Math.Max(1, p.total);
                        ProgressValue = p.completed;
                        SetStatus("Status_ScanningPackProgress", p.completed, p.total);
                    }));
                var scanData = await Task.Run(() => BuildArchiveIndex(PackPath!, scanProg));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _scannedArchiveData = scanData;
                    _scannedArchivePath = PackPath;
                });
                AddLogLine(Resources.GetStatusString("Log_IndexedPngEntries", scanData.FileCount));
            }

            IReadOnlyList<string>? entriesToExtractOnly = null;
            if (_scannedArchiveData is not null)
            {
                var list = _scannedArchiveData.EnumerateAllFilePaths().ToList();
                list.Add("pack.mcmeta");
                entriesToExtractOnly = list;
                AddLogLine(Resources.GetString("Log_ExtractingOnlyPng"));
            }

            var options = BuildConversionOptions(ignore, entriesToExtractOnly);
            var converter = new ResourcePackConverter();
            var prog = CreateConversionProgressReporter();

            AddLogLine(Resources.GetStatusString("Log_Converting", OutputZipPath ?? ""));
            await converter.ConvertAsync(PackPath!, OutputZipPath!, options, prog, _cts.Token);
            AddLogLine(Resources.GetString("Log_Done"));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Status_Cancelled");
            AddLogLine(Resources.GetString("Log_Cancelled"));
            var totalSec = (DateTime.UtcNow - _conversionStartUtc).TotalSeconds;
            AddLogLine(Resources.GetStatusString("Log_TotalTime", totalSec));
        }
        catch (Exception ex)
        {
            SetStatus("Status_ConversionFailed");
            AddLogLine(ex.ToString());
            var totalSec = (DateTime.UtcNow - _conversionStartUtc).TotalSeconds;
            AddLogLine(Resources.GetStatusString("Log_TotalTime", totalSec));
        }
        finally
        {
            // Persist the log for this conversion run.
            SaveLogToFile();

            _cts?.Dispose();
            _cts = null;
            IsConverting = false;
            IsBusy = false;
            ClearScannedArchive();
            ConvertCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            ScanArchiveCommand.NotifyCanExecuteChanged();
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

    /// <summary>Build converter options from current VM state and scan data.</summary>
    private AutoPbrOptions BuildConversionOptions(HashSet<string> ignore, IReadOnlyList<string>? entriesToExtractOnly)
    {
        var op = Enum.TryParse<NormalOperatorEnum>(NormalOperator, ignoreCase: true, out var parsedOp)
            ? parsedOp
            : NormalOperatorEnum.SobelVc;
        var ks = NormalKernelSize switch
        {
            "5" => NormalKernelSizeEnum.K5,
            "7" => NormalKernelSizeEnum.K7,
            _ => NormalKernelSizeEnum.K3
        };
        var deriv = Enum.TryParse<NormalDerivativeEnum>(NormalDerivative, ignoreCase: true, out var parsedDeriv)
            ? parsedDeriv
            : NormalDerivativeEnum.Luminance;

        return new AutoPbrOptions
        {
            NormalIntensity = (float)NormalIntensity,
            HeightIntensity = (float)HeightIntensity,
            FastSpecular = FastSpecular,
            UseLegacyExtractor = UseLegacyExtractor,
            SmoothnessScale = (float)SmoothnessScale,
            MetallicBoost = (float)MetallicBoost,
            PorosityBias = (int)Math.Round(PorosityBias),
            MaxThreads = MaxThreads,
            TempDirectory = string.IsNullOrWhiteSpace(TempDirectory) ? null : TempDirectory,
            ProcessBlocks = ProcessBlocks,
            ProcessItems = ProcessItems,
            ProcessArmor = ProcessEntity,
            ProcessParticles = ProcessParticles,
            IgnoreTextureKeys = ignore,
            FoliageMode = FoliageMode,
            UseDeepBumpNormals = UseDeepBumpNormals,
            DeepBumpModelPath = UseDeepBumpNormals
                ? Path.Combine(AppContext.BaseDirectory, "Data", "deepbump256.onnx")
                : null,
            DeepBumpOverlap = DeepBumpOverlap,
            NormalOperator = op,
            NormalKernelSize = ks,
            NormalDerivative = deriv,
            SpecularData = _specularData,
            EntriesToExtractOnly = entriesToExtractOnly
        };
    }

    /// <summary>Progress reporter that marshals conversion progress to the UI thread and updates status/log.</summary>
    private IProgress<ConversionProgress> CreateConversionProgressReporter() =>
        new Progress<ConversionProgress>(OnConversionProgress);

    private void OnConversionProgress(ConversionProgress p)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var now = DateTime.UtcNow;
            if (_currentStage.HasValue && _currentStage.Value != p.Stage)
            {
                var elapsed = (now - _stageStartUtc).TotalSeconds;
                var stageName = GetStageDisplayName(_currentStage.Value);
                AddLogLine(Resources.GetStatusString("Log_StageCompleted", stageName, elapsed));
            }

            if (p.Stage != ConversionStage.Done)
            {
                _currentStage = p.Stage;
                _stageStartUtc = now;
            }
            else
            {
                var totalSec = (now - _conversionStartUtc).TotalSeconds;
                AddLogLine(Resources.GetStatusString("Log_TotalTime", totalSec));
            }

            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Completed;
            if (!string.IsNullOrEmpty(p.InfoMessage))
                AddLogLine(p.InfoMessage);
            if (p.Stage == ConversionStage.Extracting && p is { Completed: 0, Total: > 0 })
                AddLogLine(Resources.GetString("Log_Extracting"));
            if (p.Stage == ConversionStage.Packing && p is { Completed: 0, Total: > 0 })
                AddLogLine(Resources.GetString("Log_Packing"));
            if (!string.IsNullOrEmpty(p.CurrentTextureName))
            {
                if ((now - _lastLogWriteUtc).TotalMilliseconds >= LogWriteIntervalMs)
                {
                    _lastLogWriteUtc = now;
                    var stageLabel = GetStageDisplayName(p.Stage);
                    AddLogLine(Resources.GetStatusString("Log_StageCurrent", stageLabel, p.CurrentTextureName));
                }
            }

            (_statusKey, _statusFormatArgs) = p.Stage switch
            {
                ConversionStage.Extracting => p.Total > 0
                    ? ("Status_ExtractingPackProgress", [p.Completed, p.Total])
                    : ("Status_ExtractingPack", null),
                ConversionStage.ScanningTextures => ("Status_ScanningTextures", null),
                ConversionStage.GeneratingSpecular => ("Status_SpecularCurrent",
                    [p.CurrentTextureName ?? ""]),
                ConversionStage.GeneratingNormals => ("Status_NormalsCurrent",
                    [p.CurrentTextureName ?? ""]),
                ConversionStage.Packing => p.Total > 0
                    ? ("Status_PackingOutputProgress", [p.Completed, p.Total])
                    : ("Status_PackingOutput", null),
                ConversionStage.Done => ("Status_Done", null),
                _ => (_statusKey, _statusFormatArgs)
            };
            UpdateStatusText();
        });
    }

    private static string GetStageDisplayName(ConversionStage stage)
    {
        var key = stage switch
        {
            ConversionStage.Extracting => "Log_StageExtracting",
            ConversionStage.ScanningTextures => "Log_StageScanning",
            ConversionStage.GeneratingSpecular => "Log_StageSpecular",
            ConversionStage.GeneratingNormals => "Log_StageNormals",
            ConversionStage.Packing => "Log_StagePacking",
            _ => null
        };
        return key != null ? Resources.GetString(key) : stage.ToString();
    }
}
