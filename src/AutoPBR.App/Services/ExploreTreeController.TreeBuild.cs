using AutoPBR.App.Models;
using AutoPBR.Core;

using Avalonia.Threading;

namespace AutoPBR.App.Services;

internal sealed partial class ExploreTreeController
{
    /// <summary>Set scanned data and build the tree root. Call on UI thread. Returns the new root (also assign to VM's ScannedArchiveRoot). Loads persisted tag overrides for this pack.</summary>
    /// <param name="data">Scanned index (single archive or merged batch folder).</param>
    /// <param name="packPath">Single-pack .zip/.jar path, or when <paramref name="data"/>.<see cref="ScannedArchiveData.IsBatch"/> is true, the scanned folder path (used for tag persistence).</param>
    public ArchiveNode SetData(ScannedArchiveData data, string packPath)
    {
        _tagRefreshCts?.Cancel();
        _refreshDisplayTagsDebounceCts?.Cancel();
        _refreshDisplayTagsDebounceCts = null;
        _exploreFilterRefreshDebounceCts?.Cancel();
        _exploreFilterRefreshDebounceCts?.Dispose();
        _exploreFilterRefreshDebounceCts = null;
        Interlocked.Increment(ref _effectiveTagEpoch);
        ClearBatchPackIconCache();
        Data = data;
        _scannedArchivePath = data.IsBatch ? data.BatchFolderPath ?? packPath : packPath;
        _tagAdded.Clear();
        _tagRemoved.Clear();
        _effectiveTagCache.Clear();
        _effectiveTagIdsByStorageKey.Clear();
        _optifineFolderMaterialHintIdsByRuleKey.Clear();
        _effectiveTagComputeInFlight.Clear();
        _finalSemanticTagPaths.Clear();
        var snapshot = TagOverridesPersistence.Load(_scannedArchivePath);
        if (snapshot?.Overrides is not null)
        {
            foreach (var kv in snapshot.Overrides)
            {
                if (kv.Value.Added is { Count: > 0 })
                {
                    _tagAdded[kv.Key] = new HashSet<string>(kv.Value.Added, StringComparer.OrdinalIgnoreCase);
                }

                if (kv.Value.Removed is { Count: > 0 })
                {
                    _tagRemoved[kv.Key] = new HashSet<string>(kv.Value.Removed, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        TryLoadEffectiveTagsCache();
        _debugSink?.Invoke("Explore: scan data loaded, tag cache restored if signature matched.");
        Root = new ArchiveNode("", "", true, null, this);
        ((IArchiveNodeHost)this).EnsureChildrenLoaded(Root);
        InvalidateExploreTagFilterCache();
        return Root;
    }

    /// <summary>Clear all state; call when user clears archive or starts a new scan.</summary>
    public void Clear()
    {
        _tagRefreshCts?.Cancel();
        _refreshDisplayTagsDebounceCts?.Cancel();
        _refreshDisplayTagsDebounceCts = null;
        _exploreFilterRefreshDebounceCts?.Cancel();
        _exploreFilterRefreshDebounceCts?.Dispose();
        _exploreFilterRefreshDebounceCts = null;
        Interlocked.Increment(ref _effectiveTagEpoch);
        ClearBatchPackIconCache();
        Root = null;
        Data = null;
        _scannedArchivePath = null;
        _pathOverrides.Clear();
        _tagAdded.Clear();
        _tagRemoved.Clear();
        _effectiveTagCache.Clear();
        _effectiveTagIdsByStorageKey.Clear();
        _optifineFolderMaterialHintIdsByRuleKey.Clear();
        _effectiveTagComputeInFlight.Clear();
        _finalSemanticTagPaths.Clear();
        _folderVisibilityCache.Clear();
        InvalidateExploreTagFilterCache();
    }

    public bool HaveScanForCurrentPack(string? packPath) =>
        Data is not null &&
        !Data.IsBatch &&
        _scannedArchivePath is not null &&
        !string.IsNullOrEmpty(packPath) &&
        string.Equals(Path.GetFullPath(_scannedArchivePath), Path.GetFullPath(packPath), StringComparison.OrdinalIgnoreCase);

    bool? IArchiveNodeHost.GetOverride(string fullPath) =>
        _pathOverrides.GetValueOrDefault(fullPath);

    void IArchiveNodeHost.SetOverride(string fullPath, bool? value)
    {
        if (value.HasValue)
        {
            _pathOverrides[fullPath] = value;
        }
        else
        {
            _pathOverrides.TryRemove(fullPath, out _);
        }
    }

    void IArchiveNodeHost.EnsureChildrenLoaded(ArchiveNode node)
    {
        if (Data is null || node.Children.Count > 0)
        {
            return;
        }

        var children = Data.GetChildren(node.FullPath);
        if (children is null)
        {
            return;
        }

        var deferredTagRefresh = new List<ArchiveNode>();
        foreach (var entry in children)
        {
            if (entry.IsFolder)
            {
                if (IsIgnoredOptifineFolder(entry.FullPath))
                {
                    continue;
                }

                if (!HasVisiblePngUnder(entry.FullPath))
                {
                    continue;
                }
            }
            else
            {
                if (GetEffectiveOverrideForPath(entry.FullPath) == false)
                {
                    continue;
                }
            }

            var isBatchPackRoot = Data.IsBatch && string.IsNullOrEmpty(node.FullPath) && entry.IsFolder;
            var child = new ArchiveNode(entry.Name, entry.FullPath, entry.IsFolder, node, this, isBatchPackRoot);
            if (!entry.IsFolder)
            {
                deferredTagRefresh.Add(child);
            }

            if (isBatchPackRoot)
            {
                if (TryGetCachedBatchPackIcon(entry.FullPath, out var icon))
                {
                    child.PackIcon = icon;
                }
                else
                {
                    QueueBatchPackIconLoad(entry.FullPath);
                }
            }
            node.Children.Add(child);
        }

        // Filter walks call EnsureChildrenLoaded for every folder; nested filter/notify would
        // re-enter O(n) times and thrash the virtualized Explore list (clicks/selection fail).
        if (!_isApplyingExploreFilter)
        {
            ApplyExploreFilterIfNeeded();
        }

        if (deferredTagRefresh.Count > 0)
        {
            _ = QueueBackgroundEffectiveTagComputeBatchAsync(deferredTagRefresh.Select(static n => n.FullPath).ToList());
            // Spread tag row work across frames so a folder with hundreds of textures stays responsive.
            const int chunk = 40;
            void PostChunk(int start)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var end = Math.Min(start + chunk, deferredTagRefresh.Count);
                    for (var j = start; j < end; j++)
                    {
                        deferredTagRefresh[j].RefreshDisplayTags();
                    }

                    if (end < deferredTagRefresh.Count)
                    {
                        PostChunk(end);
                    }
                }, DispatcherPriority.Background);
            }

            PostChunk(0);
        }

        if (!_isApplyingExploreFilter)
        {
            ((IArchiveNodeHost)this).NotifyExploreStructureChanged();
        }
    }

    public bool? GetEffectiveOverrideForPath(string fullPath)
    {
        var path = fullPath;
        while (!string.IsNullOrEmpty(path))
        {
            if (_pathOverrides.TryGetValue(path, out var v) && v.HasValue)
            {
                return v;
            }

            var slash = path.LastIndexOf('/');
            if (slash < 0)
            {
                break;
            }

            path = path[..slash];
        }
        return null;
    }

    public void ApplyExploreOverridesToIgnoreSet(HashSet<string> ignore)
    {
        if (Data is null || Data.IsBatch)
        {
            return;
        }

        foreach (var fullPath in Data.EnumerateAllFilePaths())
        {
            var key = ArchivePathToTextureKey(fullPath);
            if (key is null)
            {
                continue;
            }

            var effective = GetEffectiveOverrideForPath(fullPath);
            if (!effective.HasValue)
            {
                continue;
            }

            if (effective.Value)
            {
                ignore.Remove(key);
            }
            else
            {
                ignore.Add(key);
            }
        }
    }

    public static string? ArchivePathToTextureKey(string fullPath)
    {
        var parts = fullPath.Replace('\\', '/').Split('/');
        if (parts.Length < 4 || !parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

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

    public HashSet<string> GetTextureTypeFolderPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Data is null)
        {
            return seen;
        }

        foreach (var fullPath in Data.EnumerateAllFilePaths())
        {
            var segments = fullPath.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!segments[i].Equals("textures", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= segments.Length)
                {
                    continue;
                }

                var typeName = segments[i + 1];
                if (!TextureTypeFolderNames.Contains(typeName))
                {
                    continue;
                }

                var folderPath = string.Join("/", segments.Take(i + 2));
                seen.Add(folderPath);
            }
        }
        return seen;
    }

    private static bool GetProcessValueForTextureFolder(string folderPath, bool processBlocks, bool processItems, bool processEntity, bool processParticles)
    {
        var seg = folderPath.Split('/');
        var last = seg.Length > 0 ? seg[^1] : "";
        if (last.Equals("block", StringComparison.OrdinalIgnoreCase) || last.Equals("blocks", StringComparison.OrdinalIgnoreCase))
        {
            return processBlocks;
        }

        if (last.Equals("item", StringComparison.OrdinalIgnoreCase) || last.Equals("items", StringComparison.OrdinalIgnoreCase))
        {
            return processItems;
        }

        if (last.Equals("entity", StringComparison.OrdinalIgnoreCase))
        {
            return processEntity;
        }

        if (last.Equals("particle", StringComparison.OrdinalIgnoreCase))
        {
            return processParticles;
        }

        return true;
    }

    /// <summary>Warm up visibility cache for first few levels. Run on background thread.</summary>
    public void PrewarmFolderVisibilityCache(CancellationToken cancellationToken)
    {
        var data = Data;
        if (data is null)
        {
            return;
        }

        const int maxDepth = 3;
        var queue = new Queue<(string path, int depth)>();
        queue.Enqueue(("", 0));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parent, depth) = queue.Dequeue();
            var children = data.GetChildren(parent);
            if (children is null)
            {
                continue;
            }

            foreach (var c in children)
            {
                if (!c.IsFolder)
                {
                    continue;
                }

                if (depth < maxDepth)
                {
                    queue.Enqueue((c.FullPath, depth + 1));
                }

                if (!_folderVisibilityCache.ContainsKey(c.FullPath))
                {
                    _folderVisibilityCache[c.FullPath] = ComputeFolderVisible(data, c.FullPath);
                }
            }
        }
    }

    /// <summary>Apply block/item/entity/particle overrides from process flags and refresh tree. Returns node to restore focus (same path) or null.</summary>
    public ArchiveNode? ApplyTextureTypeOverridesToExplore(string? previousFocusPath, bool processBlocks, bool processItems, bool processEntity, bool processParticles)
    {
        var paths = GetTextureTypeFolderPaths();
        if (paths.Count == 0)
        {
            return null;
        }

        _folderVisibilityCache.Clear();
        foreach (var path in paths)
        {
            var include = GetProcessValueForTextureFolder(path, processBlocks, processItems, processEntity, processParticles);
            _pathOverrides[path] = include;
        }
        NotifyOverrideChangedForPaths(paths);
        RefreshExploreTreeFilter();
        if (string.IsNullOrEmpty(previousFocusPath))
        {
            return null;
        }

        return FindNodeByFullPath(previousFocusPath);
    }
}
