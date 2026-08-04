namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Session-bounded gate for render-thread diagnostics whose state can oscillate while
/// asynchronous GPU resources are being published. Each semantic state is reported once
/// and the hard capacity prevents diagnostic identity churn from flooding the UI log.
/// </summary>
internal sealed class PreviewCloudDiagnosticGate
{
    private readonly int _capacity;
    private readonly HashSet<string> _reportedIdentities =
        new(StringComparer.Ordinal);

    public PreviewCloudDiagnosticGate(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int ReportedCount => _reportedIdentities.Count;

    public bool TryReport(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (_reportedIdentities.Contains(identity) ||
            _reportedIdentities.Count >= _capacity)
        {
            return false;
        }

        return _reportedIdentities.Add(identity);
    }
}
