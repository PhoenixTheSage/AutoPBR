namespace AutoPBR.App.Services;

/// <summary>
/// Named log categories under <see cref="LogService.LogsDirectory"/>. Verbose preview/shader diagnostics
/// land in categorical files; the in-app log UI only receives user-relevant indicators.
/// </summary>
public enum AppLogCategory
{
    Session,
    PreviewGl,
    Shaders,
    Terrain,
    Clouds,
    GpuTimings,
    Entities,
    Emergency,
}

/// <summary>
/// Persists diagnostics under %AppData%\Roaming\AutoPBR\logs.
/// <para>
/// <see cref="AppendEmergencyDiagnostic"/> is exception/fault only (not a session transcript).
/// Healthy runs previously left <c>AutoPBR_emergency.log</c> untouched for days; session start
/// now appends a banner so the file's mtime proves the sink is alive.
/// </para>
/// </summary>
internal static class LogService
{
    private const int MaxLogFiles = 10;
    private const long MaxEmergencyLogBytes = 2 * 1024 * 1024;
    private const long MaxCategoryLogBytes = 8 * 1024 * 1024;
    private static readonly object EmergencyLogSync = new();
    private static readonly object CategoryLogSync = new();
    private static readonly object SessionSync = new();
    private static bool _sessionStarted;

    public static string LogsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPBR", "logs");

    public static string EmergencyLogPath => Path.Combine(LogsDirectory, "AutoPBR_emergency.log");

    public static string GetCategoryLogPath(AppLogCategory category) =>
        Path.Combine(LogsDirectory, category switch
        {
            AppLogCategory.Session => "session.log",
            AppLogCategory.PreviewGl => "preview-gl.log",
            AppLogCategory.Shaders => "shaders.log",
            AppLogCategory.Terrain => "terrain.log",
            AppLogCategory.Clouds => "clouds.log",
            AppLogCategory.GpuTimings => "gpu-timings.log",
            AppLogCategory.Entities => "entities.log",
            AppLogCategory.Emergency => "AutoPBR_emergency.log",
            _ => "session.log",
        });

    /// <summary>
    /// Marks process start in session + emergency logs so emergency mtime stays trustworthy
    /// even when no faults occur. Safe to call repeatedly; only the first call writes.
    /// </summary>
    public static void EnsureSessionStarted()
    {
        lock (SessionSync)
        {
            if (_sessionStarted)
            {
                return;
            }

            _sessionStarted = true;
        }

        var stamp = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var pid = Environment.ProcessId;
        var banner =
            $"Session start pid={pid} utc={stamp} " +
            $"os={Environment.OSVersion} " +
            $"framework={Environment.Version}";
        Write(AppLogCategory.Session, banner);
        AppendEmergencyDiagnostic("Session start", banner + Environment.NewLine +
            "Note: this file records faults and session heartbeats only; " +
            "shader/preview detail is in shaders.log, preview-gl.log, terrain.log, clouds.log, " +
            "gpu-timings.log, and entities.log under the same folder.");
    }

    /// <summary>
    /// Classifies a preview diagnostic, appends it to the matching category file, and returns
    /// whether the line should also appear in the in-app log UI.
    /// </summary>
    public static bool WritePreviewDiagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        EnsureSessionStarted();
        var category = ClassifyPreviewDiagnostic(message);
        Write(category, message);
        return IsUserVisiblePreviewDiagnostic(message);
    }

    /// <summary>Append a line to a categorical log file. Never throws.</summary>
    public static void Write(AppLogCategory category, string message)
    {
        if (category == AppLogCategory.Emergency)
        {
            AppendEmergencyDiagnostic("diagnostic", message);
            return;
        }

        try
        {
            lock (CategoryLogSync)
            {
                Directory.CreateDirectory(LogsDirectory);
                var path = GetCategoryLogPath(category);
                RotateIfOversize(path, MaxCategoryLogBytes);
                var timestamp = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write('[');
                writer.Write(timestamp);
                writer.Write("] ");
                writer.WriteLine(message);
                writer.Flush();
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

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
                RotateIfOversize(EmergencyLogPath, MaxEmergencyLogBytes);
                using var stream = new FileStream(
                    EmergencyLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write('[');
                writer.Write(timestamp);
                writer.Write("] ");
                writer.WriteLine(source);
                writer.WriteLine(detail);
                writer.WriteLine();
                writer.Flush();
                stream.Flush(flushToDisk: true);
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

    /// <summary>
    /// Routes preview diagnostic text into a named category file.
    /// Prefer explicit keywords over fuzzy matching so spammy residency/timing lines stay out of shaders.
    /// </summary>
    public static AppLogCategory ClassifyPreviewDiagnostic(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return AppLogCategory.PreviewGl;
        }

        // Order matters: more specific categories before the PreviewGl default.
        if (ContainsAny(message,
                "Terrain residency", "terrain ", "Terrain ", "LOD cache", "Lod ", "lod ",
                "chunk stream", "column board", "height atlas", "GPU full mesh", "GPU LOD"))
        {
            return AppLogCategory.Terrain;
        }

        if (ContainsAny(message,
                "Volumetric cloud", "volumetric cloud", "Cloud ", "cloud ", "froxel", "Volume ",
                "god-ray", "God-ray", "god ray", "Weather ", "CQ2", "CQ3"))
        {
            return AppLogCategory.Clouds;
        }

        if (ContainsAny(message,
                "P8 GPU", "GPU timings", "Occlusion debug", "Frame fingerprint",
                "luminance histogram", "image histogram", "GpuTimer", "timer quer"))
        {
            return AppLogCategory.GpuTimings;
        }

        if (ContainsAny(message,
                "Entity draw contract", "GPU runtime:", "GPU WARN:", "bone index",
                "Bone index", "Entity skinning", "entity GPU", "PreparedBone"))
        {
            return AppLogCategory.Entities;
        }

        if (ContainsAny(message,
                " shader", "shader:", "Shader", "program:", "link failed", "compile",
                "tessellation", "GLSL", "prewarm", "program binary"))
        {
            return AppLogCategory.Shaders;
        }

        return AppLogCategory.PreviewGl;
    }

    /// <summary>
    /// In-app log UI allowlist: failures, warnings, and a few high-signal path notices.
    /// Periodic residency/timing/fingerprint spam and dense CQ key=value dumps stay file-only.
    /// </summary>
    public static bool IsUserVisiblePreviewDiagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        // High-frequency / toggle-gated / dense dumps — never UI.
        // Note: cloud lighting lines embed computeFailure=none; do not let that substring leak them.
        if (ContainsAny(message,
                "Terrain residency",
                "P8 GPU timings:",
                "Occlusion debug:",
                "P5.3 Hi-Z",
                "Hi-Z occlusion culling enabled",
                "Frame fingerprint",
                "luminance histogram",
                "Entity draw contract:",
                "GPU runtime:",
                "bone index histogram",
                "Bone index histogram",
                "Flat continuous-world volumetric clouds",
                "cloudLightCache=",
                "computeFailure=",
                "planeReuse=",
                "lifecycle=cq",
                "historyConfidence=live-via-overlay",
                "first-report-snapshot",
                "nearGeneration=",
                "farGeneration=",
                "centerWeights=",
                "densityAssets=",
                "sparseTraversal=",
                "thinFeaturePreservation="))
        {
            return false;
        }

        // Dense semicolon key=value blobs (CQ/cloud/lighting dumps) stay file-only even if
        // they happen to contain words like Failure inside field names.
        if (LooksLikeDenseKeyValueDiagnosticDump(message))
        {
            return false;
        }

        // Failures / warnings / contained exceptions.
        // Avoid bare "failure"/"Failure"/"error"/"unavailable" — those match field names and mid-sentence prose
        // (e.g. computeFailure=none, "atlas is unavailable; ...").
        if (ContainsAny(message,
                " failed", "Failed", " failure ", " failure:", "Failure:",
                " exception", "Exception", " exception:",
                " error:", " error ", "Error:", "ERROR", " WARN", "GPU WARN",
                "unavailable;", "unavailable:", "unavailable (", "unavailable).",
                "incomplete",
                "disabled for this session", "fallback engaged", "Emergency log:",
                "init failed", "Init failed", "bootstrap failed", "link failed",
                "not resolved", "contained ("))
        {
            return true;
        }

        // One-shot major presentation path notices users care about.
        if (ContainsAny(message,
                "Desktop OpenGL 4.x sidecar active",
                "D3D11/WGL interop active",
                "Async PBO readback active",
                "shared-texture interop disabled",
                "OpenGL fallback",
                "PreviewOpenGlFallback",
                "shader cache",
                "Shader cache"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Heuristic for CQ / lighting-cache style dumps that are long and packed with <c>key=value;</c> pairs.
    /// </summary>
    internal static bool LooksLikeDenseKeyValueDiagnosticDump(string message)
    {
        if (message.Length < 220)
        {
            return false;
        }

        var semicolons = 0;
        var equals = 0;
        foreach (var ch in message)
        {
            if (ch == ';')
            {
                semicolons++;
            }
            else if (ch == '=')
            {
                equals++;
            }

            if (semicolons >= 8 && equals >= 8)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string message, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (message.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RotateIfOversize(string path, long maxBytes)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes)
            {
                return;
            }

            var backup = path + ".1";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(path, backup);
        }
        catch
        {
            // Best-effort rotation; append may still succeed on the original path.
        }
    }
}
