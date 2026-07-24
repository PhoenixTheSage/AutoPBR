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

namespace AutoPBR.App.ViewModels;

public partial class MainWindowViewModel
{
    public bool IsBatchScanActive => HasScannedArchive && _exploreController.Data?.IsBatch == true;
    /// <summary>Search filter for the Resource Explorer tree (Explore tab). Filters nodes by path/name.</summary>
    [ObservableProperty] private string _exploreFilter = "";

    /// <summary>When set, Explore tree shows only files that have this tag (and their ancestor folders). Empty = no tag filter.</summary>
    [ObservableProperty] private string _exploreTagFilterId = "";

    /// <summary>Explore tree column widths (shared by header and rows via binding). Drag splitters in the header to resize.</summary>
    [ObservableProperty] private GridLength _exploreTreeColumnResourceWidth = new(1, GridUnitType.Star);

    [ObservableProperty] private GridLength _exploreTreeColumnMaterialsWidth = new(140, GridUnitType.Pixel);
    [ObservableProperty] private GridLength _exploreTreeColumnFlagsWidth = new(140, GridUnitType.Pixel);
    /// <summary>Root of the scanned archive tree for the Explore tab. Null until user clicks Scan or when cleared.</summary>
    [ObservableProperty] private ArchiveNode? _scannedArchiveRoot;

    public bool HasScannedArchive => ScannedArchiveRoot != null;
    public bool ShowExploreEmptyMessage => !HasScannedArchive;

    private static readonly ObservableCollection<ArchiveNode> EmptyArchiveNodes = new();

    public ObservableCollection<ArchiveNode> ScannedArchiveTopLevel =>
        ScannedArchiveRoot?.Children ?? EmptyArchiveNodes;

    /// <summary>Folder we're currently viewing in Explore; null = root. After scan, defaults to "assets" if present.</summary>
    [ObservableProperty] private ArchiveNode? _focusedArchiveNode;

    /// <summary>Direct children of the focused folder (or archive root). Source for flattened <see cref="ExploreDisplayItems"/>.</summary>
    public ObservableCollection<ArchiveNode> ExploreViewItems => FocusedArchiveNode?.Children ?? ScannedArchiveTopLevel;

    /// <summary>Virtualized Explore list: visible nodes under the focused folder, including expanded descendants.</summary>
    public ObservableCollection<ExploreListRow> ExploreDisplayItems { get; } = new();

    private int _exploreDisplayRebuildQueued;
    private CancellationTokenSource? _exploreFilterDebounceCts;
    private const int ExploreFilterDebounceMs = 200;

    private IReadOnlyList<ExploreTagFilterOption>? _exploreTagFilterOptions;

    /// <summary>Options for "Show tag" dropdown in Explore: All plus each effective tag rule (cached so the ComboBox does not rebuild the list on every binding pass).</summary>
    public IReadOnlyList<ExploreTagFilterOption> ExploreTagFilterOptions =>
        _exploreTagFilterOptions ??= BuildExploreTagFilterOptions();

    private IReadOnlyList<ExploreTagFilterOption> BuildExploreTagFilterOptions() =>
        [new ExploreTagFilterOption { Id = "", DisplayName = LocalizedStrings.ExploreTagFilterAll }, .. GetEffectiveTagRules().Select(r => new ExploreTagFilterOption { Id = r.Id, DisplayName = r.DisplayName })];
    /// <summary>Breadcrumb path for Explore (from root to current folder); click to navigate.</summary>
    public ObservableCollection<ArchiveNode> ExploreBreadcrumb { get; } = new();

    public bool CanGoBackExplore => FocusedArchiveNode != null;

    private void RebuildExploreBreadcrumb()
    {
        ExploreBreadcrumb.Clear();
        if (FocusedArchiveNode is null)
        {
            return;
        }


        var path = new List<ArchiveNode>();
        for (var n = FocusedArchiveNode; n != null && !string.IsNullOrEmpty(n.Name); n = n.Parent)
        {
            path.Add(n);
        }


        path.Reverse();
        foreach (var node in path)
        {
            ExploreBreadcrumb.Add(node);
        }
    }

    /// <summary>Coalesce structure/filter/focus changes into one flat-list rebuild on the UI thread.</summary>
    private void ScheduleRebuildExploreDisplayItems()
    {
        if (Interlocked.Exchange(ref _exploreDisplayRebuildQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _exploreDisplayRebuildQueued, 0);
            RebuildExploreDisplayItems();
        }, DispatcherPriority.Normal);
    }

    private void RebuildExploreDisplayItems()
    {
        var previousSelected = SelectedExploreNodes.ToList();
        var previousPrimary = SelectedExploreNode;

        _exploreSelectionSyncing = true;
        try
        {
            ExploreDisplayItems.Clear();
            if (ScannedArchiveRoot is null)
            {
                SelectedExploreRow = null;
                return;
            }

            ExploreDisplayListBuilder.BuildInto(ExploreDisplayItems, ExploreViewItems);

            var visible = previousSelected
                .Where(n => ExploreDisplayItems.Any(r => ReferenceEquals(r.Node, n)))
                .ToList();
            ReplaceExploreSelection(visible);
            SelectedExploreNode = previousPrimary is not null &&
                                  ExploreDisplayItems.Any(r => ReferenceEquals(r.Node, previousPrimary))
                ? previousPrimary
                : SelectedExploreNodes.LastOrDefault();
            SelectedExploreRow = SelectedExploreNode is null
                ? null
                : ExploreDisplayItems.FirstOrDefault(r => ReferenceEquals(r.Node, SelectedExploreNode));
        }
        finally
        {
            _exploreSelectionSyncing = false;
        }
    }

    partial void OnPackPathChanged(string? value)
    {
        _ = value;
        ClearScannedArchive();
        RecomputeOutputZipPath();
        ConvertCommand.NotifyCanExecuteChanged();
        ScanArchiveCommand.NotifyCanExecuteChanged();
        ScanCurrentInputCommand.NotifyCanExecuteChanged();
    }
    partial void OnExploreFilterChanged(string value)
    {
        _ = value;
        ScheduleDebouncedExploreFilterApply();
    }

    partial void OnExploreTagFilterIdChanged(string value)
    {
        // Tag dropdown is a discrete selection — apply immediately (still cancels any pending text debounce).
        _ = value;
        ApplyExploreFilterNow();
    }

    private void ScheduleDebouncedExploreFilterApply()
    {
        _exploreFilterDebounceCts?.Cancel();
        _exploreFilterDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _exploreFilterDebounceCts = cts;
        _ = RunDebouncedExploreFilterApplyAsync(cts);
    }

    private async Task RunDebouncedExploreFilterApplyAsync(CancellationTokenSource debounceCts)
    {
        try
        {
            await Task.Delay(ExploreFilterDebounceMs, debounceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_exploreFilterDebounceCts, debounceCts))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_exploreFilterDebounceCts, debounceCts))
            {
                return;
            }

            ApplyExploreFilterNow();
        });
    }

    private void ApplyExploreFilterNow()
    {
        _exploreFilterDebounceCts?.Cancel();
        _exploreFilterDebounceCts?.Dispose();
        _exploreFilterDebounceCts = null;
        _exploreController.ApplyExploreFilter(ExploreFilter, string.IsNullOrEmpty(ExploreTagFilterId) ? null : ExploreTagFilterId);
    }
    private void ApplyTextureTypeOverridesToExplore()
    {
        var previousFocusPath = FocusedArchiveNode?.FullPath;
        var restored = _exploreController.ApplyTextureTypeOverridesToExplore(previousFocusPath, ProcessBlocks,
            ProcessItems, ProcessEntity, ProcessParticles);
        FocusedArchiveNode = restored ?? (ScannedArchiveRoot is not null
            ? ExploreTreeController.FindChildByName(ScannedArchiveRoot, "assets")
            : null);
        PreloadExpandersForCurrentView();
        ScheduleRebuildExploreDisplayItems();
    }
    private void ClearScannedArchive()
    {
        _exploreFilterDebounceCts?.Cancel();
        _exploreFilterDebounceCts?.Dispose();
        _exploreFilterDebounceCts = null;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        FocusedArchiveNode = null;
        ScannedArchiveRoot = null;
        _exploreController.Clear();
        ExploreDisplayItems.Clear();
        OnPropertyChanged(nameof(IsBatchScanActive));
        ScanCurrentInputCommand.NotifyCanExecuteChanged();
        ConvertCommand.NotifyCanExecuteChanged();
    }

    private bool HaveScanForCurrentPack() => _exploreController.HaveScanForCurrentPack(PackPath);

    /// <summary>Ensure that all folders currently shown in the Explore view have their children loaded, so expand arrows are visible.</summary>
    private void PreloadExpandersForCurrentView()
    {
        if (ScannedArchiveRoot is null)
        {
            return;
        }


        var host = (IArchiveNodeHost)_exploreController;
        var roots = FocusedArchiveNode?.Children ?? ScannedArchiveRoot.Children;
        foreach (var node in roots)
        {
            if (node.IsFolder)
            {
                host.EnsureChildrenLoaded(node);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBackExplore))]
    private void GoBackExplore()
    {
        if (FocusedArchiveNode is null)
        {
            return;
        }


        var parent = FocusedArchiveNode.Parent;
        if (parent is null || string.IsNullOrEmpty(parent.Name))
        {
            FocusedArchiveNode = null;
        }
        else
        {
            FocusedArchiveNode = parent;
        }
    }

    [RelayCommand]
    private void GoToBreadcrumb(ArchiveNode? node)
    {
        if (node != null)
        {
            FocusedArchiveNode = node;
        }
    }

    [RelayCommand]
    private void EnterFolder(ArchiveNode? node)
    {
        if (node is { IsFolder: true })
        {
            FocusedArchiveNode = node;
        }
    }

    private static void ExpandAllInSubtree(ArchiveNode node, bool expand)
    {
        node.IsExpanded = expand;
        foreach (var c in node.Children)
        {
            ExpandAllInSubtree(c, expand);
        }
    }

    [RelayCommand]
    private void ExploreExpandAll()
    {
        if (ScannedArchiveRoot is null)
        {
            return;
        }


        var root = FocusedArchiveNode ?? ScannedArchiveRoot;
        ExpandAllInSubtree(root, true);
        ScheduleRebuildExploreDisplayItems();
    }

    [RelayCommand]
    private void ExploreCollapseAll()
    {
        if (ScannedArchiveRoot is null)
        {
            return;
        }


        var root = FocusedArchiveNode ?? ScannedArchiveRoot;
        ExpandAllInSubtree(root, false);
        ScheduleRebuildExploreDisplayItems();
    }
    partial void OnScannedArchiveRootChanged(ArchiveNode? value)
    {
        _ = value; // Partial method signature is generated; parameter not needed for this handler.
        RunOnUiThread(() =>
        {
            ClearExploreSelection();
            OnPropertyChanged(nameof(HasScannedArchive));
            OnPropertyChanged(nameof(ShowExploreEmptyMessage));
            OnPropertyChanged(nameof(ScannedArchiveTopLevel));
            OnPropertyChanged(nameof(ExploreViewItems));
            OnPropertyChanged(nameof(IsBatchScanActive));
            ScheduleRebuildExploreDisplayItems();
            ScanCurrentInputCommand.NotifyCanExecuteChanged();
            ConvertCommand.NotifyCanExecuteChanged();
            ClearTagOverridesCommand.NotifyCanExecuteChanged();
            RefreshPreviewGrassColormapState();
            SchedulePreviewGroundTextureRefresh();
        });
    }

    partial void OnFocusedArchiveNodeChanged(ArchiveNode? value)
    {
        if (value is { IsFolder: true })
        {
            ((IArchiveNodeHost)_exploreController).EnsureChildrenLoaded(value);
        }
        PreloadExpandersForCurrentView();
        OnPropertyChanged(nameof(ExploreViewItems));
        OnPropertyChanged(nameof(CanGoBackExplore));
        RebuildExploreBreadcrumb();
        GoBackExploreCommand.NotifyCanExecuteChanged();
        ScheduleRebuildExploreDisplayItems();
    }

    [ObservableProperty] private ArchiveNode? _selectedExploreNode;
    [ObservableProperty] private ExploreListRow? _selectedExploreRow;
    public ObservableCollection<ArchiveNode> SelectedExploreNodes { get; } = new();
    private ArchiveNode? _selectionAnchorExploreNode;
    private bool _exploreSelectionSyncing;

    partial void OnSelectedExploreRowChanged(ExploreListRow? value)
    {
        if (_exploreSelectionSyncing)
        {
            return;
        }

        SelectedExploreNode = value?.Node;
        if (value?.Node is { } node)
        {
            _selectionAnchorExploreNode = node;
        }
    }

    /// <summary>Sync ListBox multi-selection into <see cref="SelectedExploreNodes"/> / <see cref="SelectedExploreNode"/>.</summary>
    public void SyncExploreSelectionFromList(IReadOnlyList<ArchiveNode> selectedNodes, ArchiveNode? primary)
    {
        if (_exploreSelectionSyncing)
        {
            return;
        }

        _exploreSelectionSyncing = true;
        try
        {
            ReplaceExploreSelection(selectedNodes);
            SelectedExploreNode = primary ?? SelectedExploreNodes.LastOrDefault();
            SelectedExploreRow = SelectedExploreNode is null
                ? null
                : ExploreDisplayItems.FirstOrDefault(r => ReferenceEquals(r.Node, SelectedExploreNode));
            if (SelectedExploreNode is not null)
            {
                _selectionAnchorExploreNode = SelectedExploreNode;
            }
        }
        finally
        {
            _exploreSelectionSyncing = false;
        }
    }

    [UsedImplicitly] // Called from view pointer-selection handler.
    public void HandleExploreNodePointerSelection(ArchiveNode node, KeyModifiers modifiers)
    {
        var visible = GetVisibleExploreNodesInDisplayOrder();
        if ((modifiers & KeyModifiers.Shift) != 0 &&
            _selectionAnchorExploreNode is not null &&
            visible.Count > 0)
        {
            var a = visible.IndexOf(_selectionAnchorExploreNode);
            var b = visible.IndexOf(node);
            if (a >= 0 && b >= 0)
            {
                if (a > b)
                {
                    (a, b) = (b, a);
                }

                ReplaceExploreSelection(visible.Skip(a).Take(b - a + 1));
                SelectedExploreNode = node;
                return;
            }
        }

        if ((modifiers & KeyModifiers.Control) != 0)
        {
            if (node.IsSelected)
            {
                node.IsSelected = false;
                SelectedExploreNodes.Remove(node);
                if (ReferenceEquals(SelectedExploreNode, node))
                {
                    SelectedExploreNode = SelectedExploreNodes.LastOrDefault();
                }
            }
            else
            {
                node.IsSelected = true;
                if (!SelectedExploreNodes.Contains(node))
                {
                    SelectedExploreNodes.Add(node);
                }
                SelectedExploreNode = node;
            }

            _selectionAnchorExploreNode = node;
            return;
        }

        ReplaceExploreSelection([node]);
        SelectedExploreNode = node;
    }

    private List<ArchiveNode> GetVisibleExploreNodesInDisplayOrder()
    {
        if (ExploreDisplayItems.Count > 0)
        {
            return ExploreDisplayItems.Select(static r => r.Node).ToList();
        }

        var ordered = new List<ArchiveNode>();

        void Walk(ArchiveNode n)
        {
            if (!n.IsVisibleByFilter)
            {
                return;
            }

            ordered.Add(n);
            if (n is { IsFolder: true, IsExpanded: true })
            {
                foreach (var ch in n.Children)
                {
                    Walk(ch);
                }
            }
        }

        foreach (var n in ExploreViewItems)
        {
            Walk(n);
        }

        return ordered;
    }

    private void ReplaceExploreSelection(IEnumerable<ArchiveNode> nodes)
    {
        var desired = nodes.Distinct().ToList();
        foreach (var n in SelectedExploreNodes.ToList())
        {
            if (!desired.Contains(n))
            {
                n.IsSelected = false;
                SelectedExploreNodes.Remove(n);
            }
        }

        foreach (var n in desired)
        {
            if (!n.IsSelected)
            {
                n.IsSelected = true;
            }

            if (!SelectedExploreNodes.Contains(n))
            {
                SelectedExploreNodes.Add(n);
            }
        }

        _selectionAnchorExploreNode = desired.LastOrDefault() ?? _selectionAnchorExploreNode;
    }

    private void ClearExploreSelection()
    {
        foreach (var n in SelectedExploreNodes)
        {
            n.IsSelected = false;
        }

        SelectedExploreNodes.Clear();
        SelectedExploreNode = null;
        SelectedExploreRow = null;
        _selectionAnchorExploreNode = null;
    }

    [RelayCommand]
    private async Task SetPreviewTextureAsync(ArchiveNode? node)
    {
        if (node is null || node.IsFolder)
        {
            return;
        }

        PreviewArchivePath = node.FullPath;
        PreviewTextureName = node.FullPath;
        if (_previewRefreshDebounceCts is { } oldDebounce)
        {
            await oldDebounce.CancelAsync().ConfigureAwait(false);
            oldDebounce.Dispose();
        }
        _previewRefreshDebounceCts = null;
        await UpdatePreviewAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private Task SetPreviewFromSelectionAsync() =>
        SetPreviewTextureAsync(SelectedExploreNode);
}
