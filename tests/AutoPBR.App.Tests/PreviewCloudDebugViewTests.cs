using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudDebugViewTests
{
    [Fact]
    public void DebugViewEnum_PreservesPersistedValuesAndAppendsCq2Inspectors()
    {
        Assert.Equal(0, (int)PreviewCloudDebugView.Off);
        Assert.Equal(1, (int)PreviewCloudDebugView.WeatherCoverage);
        Assert.Equal(2, (int)PreviewCloudDebugView.FinalDensity);
        Assert.Equal(3, (int)PreviewCloudDebugView.WeatherCloudType);
        Assert.Equal(4, (int)PreviewCloudDebugView.WeatherDensityPotential);
        Assert.Equal(5, (int)PreviewCloudDebugView.WeatherConvection);
        Assert.Equal(6, (int)PreviewCloudDebugView.ShapeCoherentBody);
        Assert.Equal(9, (int)PreviewCloudDebugView.ShapeFineErosion);
        Assert.Equal(10, (int)PreviewCloudDebugView.DetailBroadBillow);
        Assert.Equal(13, (int)PreviewCloudDebugView.DetailCurlDistortion);
        Assert.Equal(14, (int)PreviewCloudDebugView.SelectedLod);
        Assert.Equal(15, (int)PreviewCloudDebugView.BaseDensity);
        Assert.Equal(16, (int)PreviewCloudDebugView.AssetProfile);
        Assert.Equal(17, Enum.GetValues<PreviewCloudDebugView>().Length);
    }

    [Fact]
    public void TraceShader_ExposesEveryCq2ChannelDensityLodAndProfileView()
    {
        var shader = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders",
            "genesis_clouds.frag"));

        Assert.Contains("CLOUD_DEBUG_WEATHER_COVERAGE = 1", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_WEATHER_CONVECTION = 5", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_SHAPE_R = 6", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_SHAPE_A = 9", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_DETAIL_R = 10", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_DETAIL_A = 13", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_SELECTED_LOD = 14", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_BASE_DENSITY = 15", shader, StringComparison.Ordinal);
        Assert.Contains("CLOUD_DEBUG_ASSET_PROFILE = 16", shader, StringComparison.Ordinal);
        Assert.Contains("shapeCoordinates.xyz,\n                    0.0", shader, StringComparison.Ordinal);
        Assert.Contains("textureLod(\n                    uDetailNoise,\n                    detailCoordinates.xyz,\n                    0.0)", shader,
            StringComparison.Ordinal);
        Assert.Contains("vcCloudBaseDensityFromWeather(", shader, StringComparison.Ordinal);
        Assert.Contains("vcCloudDensityEx(", shader, StringComparison.Ordinal);
        Assert.Contains("sampleFootprint,\n                    shapeCoordinates.w", shader, StringComparison.Ordinal);
        Assert.Contains("sampleFootprint,\n                    detailCoordinates.w", shader, StringComparison.Ordinal);
        Assert.Contains("uDensityAssetProfileCode", shader, StringComparison.Ordinal);
        Assert.Contains("cloudDebugAssetProfileColor(", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugViews_BypassHistoryRepairPresentationAndProceduralCirrus()
    {
        var backend = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));
        var shader = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders",
            "genesis_clouds.frag"));
        var upsample = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders",
            "genesis_clouds_upsample.frag"));

        Assert.Contains(
            "settings.CloudDisableTemporal || settings.CloudDebugView != PreviewCloudDebugView.Off",
            backend,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (frame.Settings.CloudDebugView != PreviewCloudDebugView.Off)",
            backend,
            StringComparison.Ordinal);
        Assert.Contains(
            "_cloudEdgeRepairDiagnostic = \"disabled by cloud debug view\"",
            backend,
            StringComparison.Ordinal);
        Assert.Contains("if (uDebugView != 0 && !slabHit)", shader, StringComparison.Ordinal);
        Assert.Contains("if (!debugViewActive)", shader, StringComparison.Ordinal);
        Assert.Contains(
            "frame.Settings.CloudDebugView == PreviewCloudDebugView.Off ? 1 : 0",
            backend,
            StringComparison.Ordinal);
        Assert.Contains("uApplyCloudEncoding > 0", upsample, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileInspector_MapsFallbackClassesAndLogsExactSelectionReason()
    {
        var backend = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));
        var shader = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders",
            "genesis_clouds.frag"));

        Assert.Contains("? 1\n                        : (allowV2 ? 3 : 2)", backend, StringComparison.Ordinal);
        Assert.Contains("profileCode: 3", backend, StringComparison.Ordinal);
        Assert.Contains("profileCode: 4", backend, StringComparison.Ordinal);
        Assert.Contains("_cloudDensityAssetProfileCode = 5", backend, StringComparison.Ordinal);
        Assert.Contains("densityAssets={_cloudDensityAssetDiagnostic}", backend, StringComparison.Ordinal);
        Assert.Contains("1=v2 bundled, 2=v1 compatibility policy, 3=v1 fallback", shader,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndUi_AcceptAllAppendedDebugModes()
    {
        var viewModel = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "ViewModels",
            "MainWindowViewModel.Preview.cs"));
        var synchronizer = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Services",
            "UserSettingsSynchronizer.cs"));

        Assert.Contains("Preview3DCloudDebugViewAssetProfile", viewModel, StringComparison.Ordinal);
        Assert.Contains("(int)PreviewCloudDebugView.AssetProfile", viewModel, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(synchronizer, "(int)PreviewCloudDebugView.AssetProfile"));
    }

    [Fact]
    public void TransactionalUpload_ReleasesEveryPartialTextureBeforeFallback()
    {
        var backend = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));

        Assert.Contains("finally", backend, StringComparison.Ordinal);
        Assert.Contains("shape?.Dispose();", backend, StringComparison.Ordinal);
        Assert.Contains("detail?.Dispose();", backend, StringComparison.Ordinal);
        Assert.Contains("weather?.Dispose();", backend, StringComparison.Ordinal);
        Assert.Contains("generated-v1", backend, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepositoryPath(params string[] segments)
    {
        var parts = new[]
        {
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
        }.Concat(segments).ToArray();
        return Path.GetFullPath(Path.Combine(parts));
    }
}
