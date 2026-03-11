namespace AutoPBR.App.Models;

/// <summary>Result of scanning an archive: child index (path -> immediate children) and total file count. No full tree in memory.</summary>
public sealed class ScannedArchiveData
{
    public IReadOnlyDictionary<string, IReadOnlyList<ArchiveChildEntry>> ChildIndex { get; }
    public int FileCount { get; }

    public ScannedArchiveData(IReadOnlyDictionary<string, IReadOnlyList<ArchiveChildEntry>> childIndex, int fileCount)
    {
        ChildIndex = childIndex;
        FileCount = fileCount;
    }

    public IReadOnlyList<ArchiveChildEntry>? GetChildren(string parentPath) =>
        ChildIndex.TryGetValue(parentPath, out var list) ? list : null;

    /// <summary>Enumerate all file paths (not directories) in the archive by walking the index.</summary>
    public IEnumerable<string> EnumerateAllFilePaths()
    {
        var queue = new Queue<string>();
        queue.Enqueue("");
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var children = GetChildren(parent);
            if (children is null)
                continue;
            foreach (var c in children)
            {
                if (c.IsFolder)
                    queue.Enqueue(c.FullPath);
                else
                    yield return c.FullPath;
            }
        }
    }
}
