namespace AutoPBR.App.Models;

/// <summary>Lightweight entry for one path in the archive (name, path, is-folder). Used for the child index only.</summary>
public readonly record struct ArchiveChildEntry(string Name, string FullPath, bool IsFolder);
