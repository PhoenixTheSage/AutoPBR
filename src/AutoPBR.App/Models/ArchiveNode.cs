using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoPBR.App.Models;

/// <summary>Lazy node in the scanned archive tree. Children are loaded on expand; override state is stored in the host.</summary>
public partial class ArchiveNode : ObservableObject
{
    private readonly IArchiveNodeHost? _host;

    public string Name { get; }
    public string FullPath { get; }
    public bool IsFolder { get; }
    public ArchiveNode? Parent { get; set; }
    public ObservableCollection<ArchiveNode> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>When true, this node is shown in the tree; when false, hidden by the Resource Explorer search filter.</summary>
    [ObservableProperty]
    private bool _isVisibleByFilter = true;

    /// <summary>Include/exclude override: null = use rules, true = include, false = exclude. Stored in host, not in node.</summary>
    public bool? ManualOverride
    {
        get => _host?.GetOverride(FullPath);
        set
        {
            if (_host is null)
                return;
            _host.SetOverride(FullPath, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Call when host overrides were updated externally so the checkbox binding re-reads ManualOverride.</summary>
    public void NotifyOverrideChanged() => OnPropertyChanged(nameof(ManualOverride));

    public ArchiveNode(string name, string fullPath, bool isFolder, ArchiveNode? parent, IArchiveNodeHost? host)
    {
        Name = name;
        FullPath = fullPath;
        IsFolder = isFolder;
        Parent = parent;
        _host = host;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!IsFolder)
            return;
        if (value && _host is not null)
        {
            // Load this folder's children if needed.
            if (Children.Count == 0)
                _host.EnsureChildrenLoaded(this);

            // Also pre-load one level of children for immediate subfolders
            // so their expand/collapse arrows are visible right away.
            foreach (var child in Children)
            {
                if (child.IsFolder)
                    _host.EnsureChildrenLoaded(child);
            }
        }
    }
}
