namespace AutoPBR.App.Lang;

/// <summary>Localized GPU preview initialization phase strings.</summary>
public static class PreviewGpuInitPhases
{
    public static string Starting => Resources.GetString("Status_GpuPreviewStarting");
    public static string PreparingShaderSources => Resources.GetString("Status_GpuPreviewPreparingShaderSources");
    public static string Preparing => Resources.GetString("Status_GpuPreviewPreparing");
    public static string ClearingShaderCache => Resources.GetString("Status_GpuPreviewClearingShaderCache");
    public static string CompilingMainShaders => Resources.GetString("Status_GpuPreviewCompilingMainShaders");
    public static string CompilingShadowShaders => Resources.GetString("Status_GpuPreviewCompilingShadowShaders");
    public static string CreatingShadowMaps => Resources.GetString("Status_GpuPreviewCreatingShadowMaps");
    public static string UploadingMeshes => Resources.GetString("Status_GpuPreviewUploadingMeshes");
    public static string InitializingLineOverlay => Resources.GetString("Status_GpuPreviewInitializingLineOverlay");
    public static string InitializingSkyDome => Resources.GetString("Status_GpuPreviewInitializingSkyDome");
    public static string InitializingAtmosphere => Resources.GetString("Status_GpuPreviewInitializingAtmosphere");
    public static string Finalizing => Resources.GetString("Status_GpuPreviewFinalizing");
    public static string LoadingGodRays => Resources.GetString("Status_GpuPreviewLoadingGodRays");
    public static string LoadingClouds => Resources.GetString("Status_GpuPreviewLoadingClouds");
    public static string LoadingTaa => Resources.GetString("Status_GpuPreviewLoadingTaa");
    public static string LoadingScreenSpaceAo => Resources.GetString("Status_GpuPreviewLoadingScreenSpaceAo");
    public static string Ready => Resources.GetString("Status_GpuPreviewReady");
    public static string PreviewReady => Resources.GetString("Status_GpuPreviewPreviewReady");
    public static string CoreReady => Resources.GetString("Status_GpuPreviewCoreReady");

    public static string BootstrapPhase(int step) => step switch
    {
        0 => CompilingMainShaders,
        1 => CompilingShadowShaders,
        2 => CreatingShadowMaps,
        3 => UploadingMeshes,
        4 => InitializingLineOverlay,
        5 => InitializingSkyDome,
        6 => InitializingAtmosphere,
        >= 7 => Finalizing,
        _ => Preparing,
    };
}
