using System.Text.Json.Serialization;

namespace AutoPBR.App.Models;

/// <summary>
/// Persisted window size, position, state, and preview panel splitter position.
/// Stored in AppData so layout is restored across sessions.
/// </summary>
public sealed class WindowLayoutState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 720;

    /// <summary>0 = Normal, 1 = Minimized, 2 = Maximized.</summary>
    [JsonPropertyName("WindowState")]
    public int State { get; set; }

    /// <summary>Preview column width in pixels (min 260, max 600).</summary>
    public double PreviewColumnWidth { get; set; } = 280;

    private static string LayoutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPBR", "window-layout.json");

    public static WindowLayoutState Load()
    {
        try
        {
            if (!File.Exists(LayoutPath))
                return new WindowLayoutState();

            var json = File.ReadAllText(LayoutPath);
            var state = System.Text.Json.JsonSerializer.Deserialize<WindowLayoutState>(json);
            if (state is null)
                return new WindowLayoutState();

            state.PreviewColumnWidth = Math.Clamp(state.PreviewColumnWidth, 260, 600);
            return state;
        }
        catch
        {
            return new WindowLayoutState();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(LayoutPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            PreviewColumnWidth = Math.Clamp(PreviewColumnWidth, 260, 600);
            var json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LayoutPath, json);
        }
        catch
        {
            // Best-effort; ignore persistence errors.
        }
    }
}
