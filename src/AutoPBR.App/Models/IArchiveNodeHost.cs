namespace AutoPBR.App.Models;

/// <summary>Provides override storage and lazy child loading for archive tree nodes. Implemented by the ViewModel.</summary>
public interface IArchiveNodeHost
{
    bool? GetOverride(string fullPath);
    void SetOverride(string fullPath, bool? value);
    void EnsureChildrenLoaded(ArchiveNode node);
}
