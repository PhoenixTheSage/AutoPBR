namespace AutoPBR.App.Services;

/// <summary>Persists in-memory log lines to timestamped files under the app logs directory, with rotation (keep at most 10 files).</summary>
internal static class LogService
{
    private const int MaxLogFiles = 10;
    private const long MaxEmergencyLogBytes = 2 * 1024 * 1024;
    private static readonly object EmergencyLogSync = new();

    public static string LogsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPBR", "logs");

    public static string EmergencyLogPath => Path.Combine(LogsDirectory, "AutoPBR_emergency.log");

    /// <summary>
    /// Immediately appends a fatal/render fault without depending on the UI log lifetime.
    /// This path must never throw, including while the process is terminating.
    /// </summary>
    public static void AppendEmergencyDiagnostic(string source, string detail)
    {
        try
        {
            lock (EmergencyLogSync)
            {
                Directory.CreateDirectory(LogsDirectory);
                var timestamp = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                if (File.Exists(EmergencyLogPath) && new FileInfo(EmergencyLogPath).Length > MaxEmergencyLogBytes)
                {
                    File.WriteAllText(
                        EmergencyLogPath,
                        $"[{timestamp}] Emergency log truncated after {MaxEmergencyLogBytes} bytes.{Environment.NewLine}{Environment.NewLine}");
                }
                File.AppendAllText(
                    EmergencyLogPath,
                    $"[{timestamp}] {source}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Emergency diagnostics must not become a second failure during exception handling.
        }
    }

    /// <summary>Write lines to a new timestamped log file and remove older files beyond <see cref="MaxLogFiles"/>.</summary>
    public static void SaveToFile(IEnumerable<string> lines)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var fileName = $"AutoPBR_{timestamp}.log";
            var fullPath = Path.Combine(LogsDirectory, fileName);
            File.WriteAllLines(fullPath, lines);

            var files = Directory.GetFiles(LogsDirectory, "AutoPBR_*.log")
                .Where(path => !string.Equals(path, EmergencyLogPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(File.GetCreationTimeUtc)
                .ToList();
            while (files.Count > MaxLogFiles)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                try
                {
                    File.Delete(oldest);
                }
                catch
                {
                    /* ignore cleanup errors */
                }
            }
        }
        catch
        {
            // Logging should never crash the app; ignore IO errors.
        }
    }
}
