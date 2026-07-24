using AutoPBR.App.Models;
using AutoPBR.Core;

using Avalonia.Threading;

namespace AutoPBR.App.Services;

internal sealed partial class ExploreTreeController
{
    public void RefreshExploreTreeFilter()
    {
        if (Root is null)
        {
            return;
        }

        InvalidateExploreTagFilterCache();
        ClearChildrenRecursive(Root);
        ((IArchiveNodeHost)this).EnsureChildrenLoaded(Root);
        ApplyExploreFilterInternal();
    }

    public void ApplyExploreFilter(string? textFilter, string? tagFilterId)
    {
        _exploreFilter = textFilter ?? "";
        _exploreTagFilterId = string.IsNullOrEmpty(tagFilterId) ? null : tagFilterId;
        ApplyExploreFilterInternal();
    }

    /// <summary>Full-tree filter pass only when a text or tag filter is active (skipped on plain navigation).</summary>
    private void ApplyExploreFilterIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_exploreFilter) && string.IsNullOrEmpty(_exploreTagFilterId))
        {
            return;
        }

        ApplyExploreFilterInternal();
    }

    private void ApplyExploreFilterInternal()
    {
        if (Root is null || _isApplyingExploreFilter)
        {
            return;
        }

        _isApplyingExploreFilter = true;
        try
        {
            var f = _exploreFilter.Trim();
            if (!string.IsNullOrEmpty(_exploreTagFilterId))
            {
                EnsureExploreTagFilterCache();
            }

            ApplyExploreFilterRecursive(Root, f);
            ((IArchiveNodeHost)this).NotifyExploreStructureChanged();
        }
        finally
        {
            _isApplyingExploreFilter = false;
        }
    }

    /// <summary>
    /// Coalesce tag-driven filter refreshes so streaming ML completions do not rebuild the Explore list on every path.
    /// Text-only filters do not need tag-driven refreshes (visibility ignores tags).
    /// </summary>
    private void ScheduleExploreTagFilterRefresh()
    {
        if (string.IsNullOrEmpty(_exploreTagFilterId))
        {
            return;
        }

        _exploreFilterRefreshDebounceCts?.Cancel();
        _exploreFilterRefreshDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _exploreFilterRefreshDebounceCts = cts;
        _ = RunDebouncedExploreTagFilterRefreshAsync(cts);
    }

    private async Task RunDebouncedExploreTagFilterRefreshAsync(CancellationTokenSource debounceCts)
    {
        try
        {
            await Task.Delay(150, debounceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_exploreFilterRefreshDebounceCts, debounceCts))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_exploreFilterRefreshDebounceCts, debounceCts))
            {
                return;
            }

            ApplyExploreFilterIfNeeded();
        }, DispatcherPriority.Background);
    }

    private bool ApplyExploreFilterRecursive(ArchiveNode node, string filter)
    {
        if (string.IsNullOrEmpty(filter) && string.IsNullOrEmpty(_exploreTagFilterId))
        {
            node.IsVisibleByFilter = true;
            foreach (var child in node.Children)
            {
                ApplyExploreFilterRecursive(child, filter);
            }

            return true;
        }

        if (node.IsFolder)
        {
            // Filtering needs descendant visibility; force-load this level for lazy nodes.
            ((IArchiveNodeHost)this).EnsureChildrenLoaded(node);
        }

        bool textMatch = string.IsNullOrEmpty(filter)
            || node.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        bool anyChildVisible = false;
        foreach (var child in node.Children)
        {
            if (ApplyExploreFilterRecursive(child, filter))
            {
                anyChildVisible = true;
            }
        }

        bool visible;
        if (!string.IsNullOrEmpty(_exploreTagFilterId))
        {
            if (node.IsFolder)
            {
                visible = textMatch && anyChildVisible;
            }
            else
            {
                var storageKey = ResolveTagStorageKey(node.FullPath);
                var hasTag = !string.IsNullOrEmpty(storageKey) &&
                             _exploreTagFilterCache is { } cache &&
                             cache.TryGetValue(storageKey, out var idSet) &&
                             idSet.Contains(_exploreTagFilterId!);
                visible = textMatch && hasTag;
            }
        }
        else
        {
            visible = textMatch || anyChildVisible;
        }

        node.IsVisibleByFilter = visible;
        return visible;
    }

    private void InvalidateExploreTagFilterCache() => _exploreTagFilterCache = null;

    private void EnsureExploreTagFilterCache()
    {
        if (_exploreTagFilterCache is not null || Root is null)
        {
            return;
        }

        _exploreTagFilterCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        BuildExploreTagFilterCacheRecursive(Root);
    }

    private void BuildExploreTagFilterCacheRecursive(ArchiveNode node)
    {
        if (node.IsFolder)
        {
            ((IArchiveNodeHost)this).EnsureChildrenLoaded(node);
            foreach (var child in node.Children)
            {
                BuildExploreTagFilterCacheRecursive(child);
            }

            return;
        }

        var storageKey = ResolveTagStorageKey(node.FullPath);
        if (string.IsNullOrEmpty(storageKey))
        {
            return;
        }

        var tags = ((IArchiveNodeHost)this).GetEffectiveTags(node.FullPath);
        _exploreTagFilterCache![storageKey] = new HashSet<string>(tags.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshExploreTagFilterCacheEntry(string archivePath)
    {
        if (_exploreTagFilterCache is null)
        {
            return;
        }

        var storageKey = ResolveTagStorageKey(archivePath);
        if (string.IsNullOrEmpty(storageKey))
        {
            return;
        }

        var tags = ((IArchiveNodeHost)this).GetEffectiveTags(archivePath);
        _exploreTagFilterCache[storageKey] = new HashSet<string>(tags.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
    }

    private static void ClearChildrenRecursive(ArchiveNode node)
    {
        foreach (var child in node.Children)
        {
            ClearChildrenRecursive(child);
        }

        node.Children.Clear();
    }

    private void NotifyOverrideChangedForPaths(HashSet<string> paths)
    {
        if (paths.Count == 0 || Root is null)
        {
            return;
        }

        NotifyOverrideChangedRecursive(Root, paths);
    }

    private static void NotifyOverrideChangedRecursive(ArchiveNode node, HashSet<string> paths)
    {
        if (paths.Contains(node.FullPath))
        {
            node.NotifyOverrideChanged();
        }

        foreach (var child in node.Children)
        {
            NotifyOverrideChangedRecursive(child, paths);
        }
    }

    private bool HasVisiblePngUnder(string folderPath)
    {
        if (_folderVisibilityCache.TryGetValue(folderPath, out var cached))
        {
            return cached;
        }

        if (Data is null)
        {
            return false;
        }

        var visible = ComputeFolderVisible(Data, folderPath);
        _folderVisibilityCache[folderPath] = visible;
        return visible;
    }

    private bool ComputeFolderVisible(ScannedArchiveData data, string folderPath)
    {
        var queue = new Queue<string>();
        queue.Enqueue(folderPath);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var children = data.GetChildren(parent);
            if (children is null)
            {
                continue;
            }

            foreach (var c in children)
            {
                if (c.IsFolder)
                {
                    queue.Enqueue(c.FullPath);
                }
                else if (GetEffectiveOverrideForPath(c.FullPath) != false)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsIgnoredOptifineFolder(string fullPath)
    {
        var segments = fullPath.Split('/');
        if (segments.Length < 4)
        {
            return false;
        }

        if (!segments[0].Equals("assets", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!segments[2].Equals("optifine", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IgnoredOptifineFolders.Contains(segments[3]);
    }

    public ArchiveNode? FindNodeByFullPath(string fullPath)
    {
        if (Root is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(fullPath))
        {
            return Root;
        }

        var current = Root;
        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = (IArchiveNodeHost)this;
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
            {
                return null;
            }

            current = next;
        }
        return current;
    }

    public static ArchiveNode? FindChildByName(ArchiveNode parent, string name)
    {
        foreach (var c in parent.Children)
        {
            if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }
        return null;
    }
}
