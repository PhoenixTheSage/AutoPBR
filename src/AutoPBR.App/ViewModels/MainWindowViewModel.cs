using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using JetBrains.Annotations;

using AutoPBR.App.Lang;
using AutoPBR.App.Models;
using AutoPBR.App.Services;
using AutoPBR.App.ViewModels.Rulesets;
using AutoPBR.Core;
using AutoPBR.Core.Embeddings;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _previewRefreshDebounceCts;
    private readonly SettingsPersistenceCoordinator _settingsPersistence;
    private readonly ExploreTreeController _exploreController = new();
    private MaterialTagSemanticMatcher? _materialTagSemanticMatcher;
    private SpecularData? _specularData;
    private readonly UserSettings _settings;

    /// <summary>During construction, true while <see cref="UserSettingsSynchronizer.LoadInto"/> runs so handlers skip <see cref="SaveSettings"/>.</summary>
    private readonly bool _loadingSettings;
    private DateTime _lastLogWriteUtc = DateTime.MinValue;
    private const int LogWriteIntervalMs = 250;
    private const int MaxLogLines = 600;
    private static readonly TimeSpan ScanDebugUpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ConversionUiProgressUpdateInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ScanUiProgressUpdateInterval = TimeSpan.FromMilliseconds(50);
    private DateTime _lastScanDebugUpdateUtc = DateTime.MinValue;

    /// <summary>Append a line to the log (e.g. from view code-behind).</summary>
    public void AppendUserLog(string line) => AddLogLine(line);

    private void AddLogLine(string line)
    {
        void Core()
        {
            LogLines.Add(line);
            var trimmed = false;
            while (LogLines.Count > MaxLogLines)
            {
                LogLines.RemoveAt(0);
                trimmed = true;
            }

            if (trimmed)
            {
                LogText = string.Join(Environment.NewLine, LogLines);
                return;
            }

            LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}{Environment.NewLine}{line}";
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Core();
        }
        else
        {
            Dispatcher.UIThread.Post(Core);
        }
    }


    private DateTime _conversionStartUtc;
    private ConversionStage? _currentStage;
    private DateTime _stageStartUtc;
    private string? _statusKey;
    private object[]? _statusFormatArgs;

    [ObservableProperty] private string? _packPath;
    [ObservableProperty] private string? _outputDirectory;

    [ObservableProperty] private double _normalIntensity = AutoPBRDefaults.DefaultNormalIntensity;
    [ObservableProperty] private double _heightIntensity = AutoPBRDefaults.DefaultHeightIntensity;
    [ObservableProperty] private bool _brickHeightMapPostProcessEnabled = AutoPBRDefaults.DefaultBrickHeightMapPostProcessEnabled;
    [ObservableProperty] private double _brickHeightMinStructuralConfidence = AutoPBRDefaults.DefaultBrickHeightMinStructuralConfidence;
    [ObservableProperty] private double _brickHeightInvertDeltaThreshold = AutoPBRDefaults.DefaultBrickHeightInvertDeltaThreshold;
    [ObservableProperty] private double _brickLightGroutDiffuseDeltaMin = AutoPBRDefaults.DefaultBrickLightGroutDiffuseDeltaMin;
    /// <summary>When true, preview refresh captures brick probe metrics into <see cref="PreviewBrickProbeDebugText"/>.</summary>
    [ObservableProperty] private bool _previewBrickProbeDebug = AutoPBRDefaults.DefaultBrickProbePreviewDebug;
    [ObservableProperty] private string? _previewBrickProbeDebugText;
    [ObservableProperty] private bool _fastSpecular;
    [ObservableProperty] private string _foliageMode = "No Height";
    [ObservableProperty] private bool _useLegacyExtractor;
    [ObservableProperty] private double _smoothnessScale = AutoPBRDefaults.DefaultSmoothnessScale;
    [ObservableProperty] private double _metallicBoost = AutoPBRDefaults.DefaultMetallicBoost;
    [ObservableProperty] private double _porosityBias = AutoPBRDefaults.DefaultPorosityBias;

    /// <summary>Extra B-channel bias for plant-tagged textures (added to <see cref="PorosityBias"/>).</summary>
    [ObservableProperty] private double _plantMaterialPorosityExtra = AutoPBRDefaults.DefaultPlantMaterialPorosityExtra;

    /// <summary>0 = heuristic specular when ML ran; 1 = full ML contribution from the heuristic/AI blend slider.</summary>
    [ObservableProperty] private double _mlSpecularHeuristicBlend = AutoPBRDefaults.DefaultMlSpecularHeuristicBlend;

    /// <summary>Enum name (SmoothnessOnly, AiMetalAndEmissive, or Full); kept in sync with <see cref="SelectedMlSpecularBlendModeOption"/>.</summary>
    [ObservableProperty] private string _mlSpecularHeuristicBlendMode =
        nameof(AutoPBR.Contracts.Ml.MlSpecularHeuristicBlendMode.SmoothnessOnly);

    /// <summary>AI Tuning channel-mode dropdown selection.</summary>
    [ObservableProperty] private FoliageModeOption? _selectedMlSpecularBlendModeOption;

    /// <summary>Blend math enum name (Linear, Additive, Multiplicative); kept in sync with <see cref="SelectedMlSpecularBlendMathOption"/>.</summary>
    [ObservableProperty] private string _mlSpecularBlendMath =
        nameof(AutoPBR.Contracts.Ml.MlSpecularBlendMath.Linear);

    /// <summary>AI Tuning blend-math dropdown selection.</summary>
    [ObservableProperty] private FoliageModeOption? _selectedMlSpecularBlendMathOption;

    [ObservableProperty] private int _maxThreads; // 0 = auto
    [ObservableProperty] private int _maxThreadsMax = Math.Max(1, Environment.ProcessorCount);
    [ObservableProperty] private string? _tempDirectory;
    [ObservableProperty] private string? _minecraftAssetsDirectory;
    [ObservableProperty] private bool _debugMode;
    [ObservableProperty] private bool _processBlocks = true;
    [ObservableProperty] private bool _processItems = true;
    [ObservableProperty] private bool _processArmor = true;
    [ObservableProperty] private bool _processEntity = true;
    [ObservableProperty] private bool _processParticles = true;
    [ObservableProperty] private bool _useDeepBumpNormals;
    [ObservableProperty] private string _deepBumpOverlap = "Large";
    [ObservableProperty] private string _deepBumpInputMode = nameof(AutoPBR.Contracts.Ml.DeepBumpInputMode.Auto);
    [ObservableProperty] private bool _deepBumpForceBlue255;
    [ObservableProperty] private double _deepBumpNormalIntensity = AutoPBRDefaults.DefaultNormalIntensity;
    [ObservableProperty] private double _deepBumpNormalSoftClamp;
    [ObservableProperty] private bool _deepBumpEdgeGuidedEnhance;
    [ObservableProperty] private double _deepBumpEdgeGuidedStrength = 1.0;
    [ObservableProperty] private double _deepBumpEdgeGuidedGamma = 1.0;
    [ObservableProperty] private double _deepBumpEdgeGuidedDirectionMix = 0.35;
    [ObservableProperty] private int _normalHeightTransparentAlphaClampMax;
    [ObservableProperty] private string _normalOperator = nameof(AutoPBR.Core.Models.NormalOperator.SobelVc);
    [ObservableProperty] private string _normalKernelSize = "3";
    [ObservableProperty] private string _normalDerivative = nameof(AutoPBR.Core.Models.NormalDerivative.Luminance);

    [ObservableProperty] private bool _preprocessLinearize;
    [ObservableProperty] private int _preprocessDenoiseRadius;
    [ObservableProperty] private double _preprocessDenoiseBlend = 0.5;
    [ObservableProperty] private bool _preprocessFrequencySplit;
    [ObservableProperty] private int _preprocessFrequencyRadius = 2;
    [ObservableProperty] private double _preprocessFrequencyDetailStrength = 1.0;

    [ObservableProperty] private bool _specularUsePercentileRemap = true;
    [ObservableProperty] private double _specularRemapLowPercentile = 0.02;
    [ObservableProperty] private double _specularRemapHighPercentile = 0.98;
    [ObservableProperty] private bool _useMlSpecularPredictor;
    [ObservableProperty] private string? _mlSpecularModelPath;
    [ObservableProperty] private string? _mlSpecularModelPath16;
    [ObservableProperty] private string? _mlSpecularModelPath32;
    [ObservableProperty] private string? _mlSpecularModelPath64;
    [ObservableProperty] private string? _mlSpecularModelPath128;
    [ObservableProperty] private string? _mlSpecularModelPath256;
    [ObservableProperty] private bool _mlSpecularUseEdgeChannel = true;
    [ObservableProperty] private int _mlSpecularTransparentAlphaClampMax;
    [ObservableProperty] private bool _specularDebugDisableHeuristicSpecular;
    [ObservableProperty] private bool _specularDebugSkipSpecularRemap;
    [ObservableProperty] private bool _specularDebugVerboseSpecularMl;

    /// <summary>When true, ONNX Runtime uses TensorRT EP for GPU models (CUDA fallback). Default false = CUDA only.</summary>
    [ObservableProperty] private bool _preferOnnxTensorRtExecutionProvider;

    /// <summary>When true, Explore suggests extra material tags via MiniLM (requires Data/ONNX-AI/all-MiniLM-L6-v2-onnx/model.onnx).</summary>
    [ObservableProperty] private bool _useSemanticMaterialTags = true;

    [ObservableProperty] private double _materialTagMinSimilarity = 0.25;
    [ObservableProperty] private double _materialTagCertaintyThreshold = 0.35;
    [ObservableProperty] private int _materialTagMaxCount = 3;
    [ObservableProperty] private bool _dictionaryEvidenceEnabled;
    [ObservableProperty] private double _dictionaryEvidenceWeight = 0.35;
    [ObservableProperty] private double _dictionaryMinEvidenceScore = 0.18;
    [ObservableProperty] private int _dictionaryRequestTimeoutMs = 900;
    private readonly IDictionaryDefinitionProvider _dictionaryDefinitionProvider = new FreeDictionaryDefinitionProvider();

    public RulesetsViewModel Rulesets { get; }

    /// <summary>Compatibility proxy for settings sync and existing bindings.</summary>
    public ObservableCollection<CustomTagRuleEntry> CustomTagRules => Rulesets.CustomTagRules;

    public int MlSpecularTransparentAlphaClampMaxSlider
    {
        get => Math.Clamp(MlSpecularTransparentAlphaClampMax, 0, 16);
        set => MlSpecularTransparentAlphaClampMax = Math.Clamp(value, 0, 255);
    }

    public int NormalHeightTransparentAlphaClampMaxSlider
    {
        get => Math.Clamp(NormalHeightTransparentAlphaClampMax, 0, 16);
        set => NormalHeightTransparentAlphaClampMax = Math.Clamp(value, 0, 255);
    }

    [ObservableProperty] private bool _generateAo;
    [ObservableProperty] private int _aoRadius = 4;
    [ObservableProperty] private double _aoStrength = 1.0;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConverting;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private string _conversionElapsedText = "";
    [ObservableProperty] private string _conversionEtaText = "";
    [ObservableProperty] private string _conversionTotalText = "";

    [ObservableProperty] private string? _outputZipPath;

    /// <summary>Folder containing multiple .zip/.jar packs for batch scan / batch convert.</summary>
    [ObservableProperty] private string? _batchFolderPath;

    /// <summary>When true, the input bar targets a batch folder and the main Convert runs batch conversion.</summary>
    [ObservableProperty] private bool _useBatchFolderInput;

    /// <summary>True when the explorer is showing a merged batch index (folder of packs).</summary>


    [ObservableProperty] private string _colorScheme = "Dark";
    [ObservableProperty] private LanguageOption? _selectedLanguage;
    [ObservableProperty] private FoliageModeOption? _selectedFoliageMode;
    [ObservableProperty] private FoliageModeOption? _selectedDeepBumpOverlap;
    [ObservableProperty] private FoliageModeOption? _selectedDeepBumpInputMode;
    [ObservableProperty] private FoliageModeOption? _selectedNormalOperator;
    [ObservableProperty] private FoliageModeOption? _selectedNormalKernelSize;
    [ObservableProperty] private FoliageModeOption? _selectedNormalDerivative;
    [ObservableProperty] private FoliageModeOption? _selectedColorSchemeOption;

    /// <summary>Minimum UI scale (75%).</summary>
    public const double MinUiScale = 0.75;

    /// <summary>Maximum UI scale (100%).</summary>
    public const double MaxUiScale = 1.0;

    /// <summary>Dropdown entries: 75%–100% in 5% steps.</summary>
    public ObservableCollection<FoliageModeOption> UiScaleOptions { get; } = new();

    [ObservableProperty] private FoliageModeOption? _selectedUiScaleOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiScaleTransform))]
    private double _uiScale = 1.0;

  private readonly ScaleTransform _uiScaleTransform = new(1.0, 1.0);

    /// <summary>Uniform scale for the main window chrome (fonts, spacing, controls) for accessibility.</summary>
    public ScaleTransform UiScaleTransform
    {
        get
        {
            _uiScaleTransform.ScaleX = UiScale;
            _uiScaleTransform.ScaleY = UiScale;
            return _uiScaleTransform;
        }
    }

    [ObservableProperty] private IBrush _windowBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _cardBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _cardBorderBrush = Brushes.Gray;
    [ObservableProperty] private IBrush _accentBrush = Brushes.DeepSkyBlue;
    [ObservableProperty] private IBrush _foregroundBrush = Brushes.White;

    [ObservableProperty] private string? _previewArchivePath;
    [ObservableProperty] private string? _previewTextureName;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _exploreMlDebugText = "";
    [ObservableProperty] private string _conversionScanDebugText = "";

    /// <summary>Color used for preview panel top/bottom gradient fade (matches CardBackground).</summary>
    [ObservableProperty] private Color _previewFadeColor = Color.FromRgb(0x22, 0x22, 0x2A);

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Persist the current in-memory log to a file (delegates to <see cref="LogService"/>).</summary>
    private void SaveLogToFile() => LogService.SaveToFile(LogLines);

    /// <summary>Localized strings for bindings; replaced when language changes.</summary>
    public LocalizedStrings Strings { get; private set; }

    /// <summary>Foliage options for the dropdown (display name from Strings, value for settings/converter).</summary>
    public ObservableCollection<FoliageModeOption> FoliageModeOptions { get; } = new();

    /// <summary>DeepBump tile overlap options (Small, Medium, Large). Matches DeepBump --color_to_normals-overlap.</summary>
    public ObservableCollection<FoliageModeOption> DeepBumpOverlapOptions { get; } = new();

    /// <summary>DeepBump input mode options (Auto, Grayscale, RGB).</summary>
    public ObservableCollection<FoliageModeOption> DeepBumpInputModeOptions { get; } = new();

    /// <summary>Normal operator options (Sobel + VC, Scharr + VC).</summary>
    public ObservableCollection<FoliageModeOption> NormalOperatorOptions { get; } = new();

    /// <summary>Normal kernel size options (3x3, 5x5, 7x7 for Sobel; 3x3,5x5 for Scharr).</summary>
    public ObservableCollection<FoliageModeOption> NormalKernelSizeOptions { get; } = new();

    /// <summary>Derivative source options: Color, Luminance, Color+Luminance Blend, Color+Luminance Max.</summary>
    public ObservableCollection<FoliageModeOption> NormalDerivativeOptions { get; } = new();

    /// <summary>Color scheme options for the Appearance dropdown (display name from Resources, value for settings).</summary>
    public ObservableCollection<FoliageModeOption> ColorSchemeOptions { get; } = new();

    /// <summary>Specular ML channel mode options (which channels keep heuristic contribution).</summary>
    public ObservableCollection<FoliageModeOption> MlSpecularBlendModeOptions { get; } = new();
    /// <summary>Specular ML blend math options (how heuristic and ML are combined).</summary>
    public ObservableCollection<FoliageModeOption> MlSpecularBlendMathOptions { get; } = new();


    /// <summary>Built-in + custom tag rules for conversion and explore. Disabled custom rules are excluded.</summary>
    public IReadOnlyList<TagRule> GetEffectiveTagRules() => Rulesets.GetEffectiveTagRules();

    /// <summary>Call when CustomTagRules or effective rules change so legend and explore use new rules.</summary>




    public MainWindowViewModel()
    {
        _settings = UserSettings.Load();
        Rulesets = new RulesetsViewModel(NotifyTagRulesChanged);
        _settingsPersistence = new SettingsPersistenceCoordinator(
            Dispatcher.UIThread,
            TimeSpan.FromMilliseconds(250),
            () => UserSettingsSynchronizer.SaveFrom(this, _settings));
        _loadingSettings = true;

        try
        {
            RefreshUiScaleOptions();
            UserSettingsSynchronizer.LoadInto(this, _settings);
            SyncSelectedUiScaleOptionToUiScale();
            _exploreController.SetTagRulesProvider(GetEffectiveTagRules);
            _exploreController.SetMaterialTagSemanticOptionsProvider(BuildMaterialTagSemanticOptions);
            ApplyColorScheme();
            var lang = string.IsNullOrWhiteSpace(_settings.Language) ? "en" : _settings.Language;
            Strings = LocalizationService.ApplyCulture(lang);
            OnPropertyChanged(nameof(Strings));
            _exploreController.SetDebugSink(msg => Dispatcher.UIThread.Post(() => { ExploreMlDebugText = msg; }));
            _exploreController.SetExploreStructureChangedHandler(ScheduleRebuildExploreDisplayItems);
            SelectedLanguage = SupportedLanguages.FirstOrDefault(x =>
                                   string.Equals(x.CultureCode, _settings.Language,
                                       StringComparison.OrdinalIgnoreCase)) ??
                               SupportedLanguages[0];
            RefreshFoliageModeOptions();
            RefreshMlSpecularBlendModeOptions();
            RefreshMlSpecularBlendMathOptions();
            RefreshDeepBumpOverlapOptions();
            RefreshDeepBumpInputModeOptions();
            RefreshNormalOperatorOptions();
            RefreshNormalKernelSizeOptions();
            RefreshNormalDerivativeOptions();
            RefreshColorSchemeOptions();
            RefreshPreviewHdrModeOptions();
            RecomputePreviewHdrDecision();
            SetStatus("Status_SelectPack");
            InitPreviewShaderPrewarm();
        }
        finally
        {
            _loadingSettings = false;
        }

        if (IsPreview3D)
        {
            RefreshPreviewGrassColormapState();
        }
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



    private void RefreshPreviewIfActive()
    {
        if (string.IsNullOrWhiteSpace(PreviewArchivePath))
        {
            return;
        }


        _ = UpdatePreviewAsync();
    }

    /// <summary>UV debug window: coalesced preview refresh without the default debounce delay.</summary>
    internal void TriggerPreviewRefreshForDebug() => ScheduleRefreshPreviewIfActive(0);

    /// <summary>
    /// Coalesces rapid slider-driven setting changes (same idea as <see cref="ExploreTreeController.ScheduleRefreshAllDisplayTags"/>):
    /// waits <paramref name="delayMilliseconds"/> after the last call, then runs <see cref="RefreshPreviewIfActive"/>.
    /// </summary>
    private void ScheduleRefreshPreviewIfActive(int delayMilliseconds = 200)
    {
        _previewRefreshDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewRefreshDebounceCts = cts;
        _ = RunDebouncedPreviewRefreshAsync(cts, delayMilliseconds);
    }

    private async Task RunDebouncedPreviewRefreshAsync(CancellationTokenSource debounceCts, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, debounceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_previewRefreshDebounceCts, debounceCts))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_previewRefreshDebounceCts, debounceCts))
            {
                return;
            }

            RefreshPreviewIfActive();
        });
    }





    private async Task FlushPendingSettingsSaveAsync()
        => await _settingsPersistence.FlushAsync().ConfigureAwait(false);

    private void ApplyColorScheme()
    {
        var palette = AppearanceService.GetPalette(ColorScheme);
        WindowBackground = palette.WindowBackground;
        CardBackground = palette.CardBackground;
        PreviewFadeColor = palette.PreviewFadeColor;
        CardBorderBrush = palette.CardBorderBrush;
        AccentBrush = palette.AccentBrush;
        ForegroundBrush = palette.ForegroundBrush;
    }



    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }



    private async Task UpdatePreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(PreviewArchivePath))
        {
            return;
        }

        if (!_exploreController.TryGetDiskPackAndEntryPath(PreviewArchivePath, out var diskPack, out var entryPath) ||
            string.IsNullOrEmpty(diskPack) || !File.Exists(diskPack))
        {
            return;
        }

        if (_previewCts is { } oldCts)
        {
            await oldCts.CancelAsync().ConfigureAwait(false);
            oldCts.Dispose();
        }

        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        try
        {
            if (_specularData is null)
            {
                SetStatus("Status_LoadingSpecularData");
                _specularData =
                    SpecularData.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json"));
            }

            IReadOnlyDictionary<string, (IReadOnlyList<string> Added, IReadOnlyList<string> Removed)>? manualPreview = null;
            if (IsBatchScanActive && PreviewArchivePath!.IndexOf('/') is > 0 and var slash)
            {
                var root = PreviewArchivePath[..slash];
                if (_exploreController.Data?.BatchPackRootToPath?.ContainsKey(root) == true)
                {
                    manualPreview = _exploreController.GetManualTagOverridesForBatchPackRoot(root);
                }
            }

            var options = BuildConversionOptions(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                manualPreview,
                PreviewBrickProbeDebug);
            using var previewPoseScope = EntityPreviewBuildContext.UsePose(SelectedPreviewPoseId);
            using var previewSizeScope = EntityPreviewBuildContext.UseSize(SelectedPreviewSizeId);
            using var previewContextTypeScope = EntityPreviewBuildContext.UseContextType(SelectedPreviewContextTypeId);
            var previewResult = await Task.Run(
                    () => PreviewService.RenderPreviewDetailedAsync(diskPack, entryPath, options, ct),
                    ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                return;
            }


            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(previewResult.PngBytes);
                PreviewImage = new Bitmap(ms);
                PreviewBrickProbeDebugText = PreviewBrickProbeDebug ? previewResult.BrickProbeDebugText : null;
                ApplyPreviewDetailedResult(previewResult);
                PushHdr2DCompositeFromPng(previewResult.PngBytes);
            });
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("No previewable textures found after extraction.", StringComparison.Ordinal))
        {
            await Dispatcher.UIThread.InvokeAsync(() => { PreviewImage = null; });
            AddLogLine("Preview skipped: no previewable textures were found for this selection.");
        }
        catch (Exception ex)
        {
            AddLogLine(ex.ToString());
        }
    }





    public void Dispose()
    {
        _cts?.Cancel();
        _scanCts?.Cancel();
        _previewCts?.Cancel();
        _previewRefreshDebounceCts?.Cancel();
        _cts?.Dispose();
        _scanCts?.Dispose();
        _previewCts?.Dispose();
        _previewRefreshDebounceCts?.Dispose();
        DisposePreviewResources();
        _materialTagSemanticMatcher?.Dispose();
        _exploreController.Dispose();
        GC.SuppressFinalize(this);
    }
}
