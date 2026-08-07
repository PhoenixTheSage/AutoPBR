using System.Runtime.Loader;

using AutoPBR.App.Models;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Services;

using Avalonia;
using Avalonia.Threading;

namespace AutoPBR.App;

sealed class Program
{
    // Satellite assemblies (language resources) are moved to lang\[culture]\ in build output.
    private const string LangSubfolder = "lang";

    [STAThread]
    public static void Main(string[] args)
    {
        LogService.EnsureSessionStarted();
        RegisterEmergencyExceptionLogging();
        RegisterSatelliteAssemblyResolver();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RegisterEmergencyExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var detail = eventArgs.ExceptionObject is Exception ex
                ? ex.ToString()
                : eventArgs.ExceptionObject?.ToString() ?? "Unknown unmanaged/untyped exception.";
            LogService.AppendEmergencyDiagnostic(
                eventArgs.IsTerminating ? "AppDomain terminating exception" : "AppDomain unhandled exception",
                detail);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            LogService.AppendEmergencyDiagnostic("Unobserved task exception", eventArgs.Exception.ToString());
    }

    /// <summary>
    /// Must run after Avalonia platform setup — touching <see cref="Dispatcher.UIThread"/> beforehand
    /// installs a null dispatcher and makes <c>MainLoop</c> throw <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    private static void RegisterDispatcherExceptionLogging()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            LogService.AppendEmergencyDiagnostic("Avalonia dispatcher exception", eventArgs.Exception.ToString());
    }

    /// <summary>Load satellite assemblies from lang\[culture]\ so language folders stay grouped in build output.</summary>
    private static void RegisterSatelliteAssemblyResolver()
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is null || !name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var culture = name.CultureName;
            if (string.IsNullOrEmpty(culture))
            {
                return null;
            }

            var baseDir = AppContext.BaseDirectory;
            var path = Path.Combine(baseDir, LangSubfolder, culture, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var settings = UserSettings.Load();
        return PreviewOpenGlPlatformConfigurator.Configure(
                AppBuilder.Configure<App>().UsePlatformDetect(),
                settings)
            .WithInterFont()
            // Avalonia framework noise stays out of the console; Warning+ only.
            .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
            .AfterSetup(_ => RegisterDispatcherExceptionLogging());
    }
}
