using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudPopulationContractTests
{
    [Fact]
    public void CellPlacement_IsDeterministicJitteredAndBounded()
    {
        var parentSpan = PreviewCloudPopulationContract.ParentCellSpan(175f);
        var first = PreviewCloudPopulationContract.CellCenter(
            -17,
            29,
            parentSpan,
            PreviewCloudPopulationContract.ParentSalt);
        var second = PreviewCloudPopulationContract.CellCenter(
            -17,
            29,
            parentSpan,
            PreviewCloudPopulationContract.ParentSalt);

        Assert.Equal(first, second);
        Assert.Equal(192.5f, parentSpan, precision: 4);

        var observedOffset = false;
        for (var z = -24; z <= 24; z++)
        {
            for (var x = -24; x <= 24; x++)
            {
                var center = PreviewCloudPopulationContract.CellCenter(
                    x,
                    z,
                    parentSpan,
                    PreviewCloudPopulationContract.ParentSalt);
                var normalizedX = center.X / parentSpan - (x + 0.5f);
                var normalizedZ = center.Y / parentSpan - (z + 0.5f);
                Assert.InRange(
                    normalizedX,
                    -PreviewCloudPopulationContract.MaximumJitterInCells,
                    PreviewCloudPopulationContract.MaximumJitterInCells);
                Assert.InRange(
                    normalizedZ,
                    -PreviewCloudPopulationContract.MaximumJitterInCells,
                    PreviewCloudPopulationContract.MaximumJitterInCells);
                observedOffset |=
                    MathF.Abs(normalizedX) > 0.02f ||
                    MathF.Abs(normalizedZ) > 0.02f;
            }
        }

        Assert.True(observedOffset);
    }

    [Fact]
    public void ParentAndSatelliteScales_StayInSeparateDocumentedBands()
    {
        var observedParentMinimum = float.PositiveInfinity;
        var observedParentMaximum = float.NegativeInfinity;
        var observedSatelliteMinimum = float.PositiveInfinity;
        var observedSatelliteMaximum = float.NegativeInfinity;

        for (var z = -32; z <= 32; z++)
        {
            for (var x = -32; x <= 32; x++)
            {
                var parent = PreviewCloudPopulationContract.CellScale(
                    x,
                    z,
                    PreviewCloudPopulationContract.ParentSalt,
                    satellite: false);
                var satellite = PreviewCloudPopulationContract.CellScale(
                    x,
                    z,
                    PreviewCloudPopulationContract.SatelliteSalt,
                    satellite: true);
                Assert.InRange(
                    parent,
                    PreviewCloudPopulationContract.ParentMinimumScale,
                    PreviewCloudPopulationContract.ParentMaximumScale);
                Assert.InRange(
                    satellite,
                    PreviewCloudPopulationContract.SatelliteMinimumScale,
                    PreviewCloudPopulationContract.SatelliteMaximumScale);
                observedParentMinimum = Math.Min(observedParentMinimum, parent);
                observedParentMaximum = Math.Max(observedParentMaximum, parent);
                observedSatelliteMinimum = Math.Min(observedSatelliteMinimum, satellite);
                observedSatelliteMaximum = Math.Max(observedSatelliteMaximum, satellite);
            }
        }

        Assert.True(observedParentMaximum - observedParentMinimum > 0.40f);
        Assert.True(observedSatelliteMaximum - observedSatelliteMinimum > 0.30f);
        Assert.Equal(
            PreviewCloudPopulationContract.ParentCellSpan(175f) *
            PreviewCloudPopulationContract.SatelliteSpanRatio,
            PreviewCloudPopulationContract.SatelliteCellSpan(175f),
            precision: 4);
    }

    [Fact]
    public void SparseEnvelopeTransforms_AreDeterministicAsymmetricAndBounded()
    {
        var firstRotation = PreviewCloudPopulationContract.CellRotationRadians(
            -11,
            7,
            PreviewCloudPopulationContract.ParentSalt);
        var secondRotation = PreviewCloudPopulationContract.CellRotationRadians(
            -11,
            7,
            PreviewCloudPopulationContract.ParentSalt);
        Assert.Equal(firstRotation, secondRotation);
        Assert.InRange(firstRotation, 0f, MathF.Tau);

        var observedRotation = false;
        var observedAnisotropy = false;
        for (var z = -24; z <= 24; z++)
        {
            for (var x = -24; x <= 24; x++)
            {
                var parentAspect = PreviewCloudPopulationContract.CellAspect(
                    x,
                    z,
                    PreviewCloudPopulationContract.ParentSalt,
                    satellite: false);
                var satelliteAspect = PreviewCloudPopulationContract.CellAspect(
                    x,
                    z,
                    PreviewCloudPopulationContract.SatelliteSalt,
                    satellite: true);
                var parentLean = PreviewCloudPopulationContract.CellLean(
                    x,
                    z,
                    PreviewCloudPopulationContract.ParentSalt,
                    satellite: false);
                var satelliteLean = PreviewCloudPopulationContract.CellLean(
                    x,
                    z,
                    PreviewCloudPopulationContract.SatelliteSalt,
                    satellite: true);

                Assert.InRange(
                    parentAspect,
                    PreviewCloudPopulationContract.ParentMinimumAspect,
                    PreviewCloudPopulationContract.ParentMaximumAspect);
                Assert.InRange(
                    satelliteAspect,
                    PreviewCloudPopulationContract.SatelliteMinimumAspect,
                    PreviewCloudPopulationContract.SatelliteMaximumAspect);
                Assert.InRange(
                    parentLean.Length(),
                    PreviewCloudPopulationContract.ParentMinimumLean - 1e-5f,
                    PreviewCloudPopulationContract.ParentMaximumLean + 1e-5f);
                Assert.InRange(
                    satelliteLean.Length(),
                    PreviewCloudPopulationContract.SatelliteMinimumLean - 1e-5f,
                    PreviewCloudPopulationContract.SatelliteMaximumLean + 1e-5f);
                observedRotation |=
                    PreviewCloudPopulationContract.CellRotationRadians(
                        x,
                        z,
                        PreviewCloudPopulationContract.ParentSalt) > 0.25f;
                observedAnisotropy |= MathF.Abs(parentAspect - 1f) > 0.10f;
            }
        }

        Assert.True(observedRotation);
        Assert.True(observedAnisotropy);
    }

    [Fact]
    public void WeatherProbabilities_BiasMoistCumulusTowardAttachedSatellites()
    {
        var dryParent =
            PreviewCloudPopulationContract.ParentProbability(0f, 0.2f);
        var moistParent =
            PreviewCloudPopulationContract.ParentProbability(1f, 0.2f);
        var drySatellite =
            PreviewCloudPopulationContract.SatelliteProbability(0f, 0.2f, 0f);
        var liftedCumulus =
            PreviewCloudPopulationContract.SatelliteProbability(1f, 0.1f, 1f);
        var liftedStratus =
            PreviewCloudPopulationContract.SatelliteProbability(1f, 0.95f, 1f);

        Assert.InRange(dryParent, 0.23f, 0.25f);
        Assert.True(moistParent > dryParent);
        Assert.Equal(0f, drySatellite);
        Assert.True(liftedCumulus > liftedStratus);
        Assert.InRange(liftedCumulus, 0f, 0.78f);
        Assert.Equal(0.75f, PreviewCloudPopulationContract.SoftUnion(0.5f, 0.5f));
    }

    [Fact]
    public void Shaders_ShareCa2PopulationIdentityAndPreserveV1TemplateAbi()
    {
        var shaderRoot = RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders");
        var populationSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "cloud_population.glsl"));
        var densitySource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "volumetric_clouds_density_maps.glsl"));
        var sparseSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_sparse_cloud_brick_generate.comp"));
        var backendSource = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));

        Assert.Contains("cpParentCellSpan", populationSource, StringComparison.Ordinal);
        Assert.Contains("cpSatelliteCellSpan", populationSource, StringComparison.Ordinal);
        Assert.Contains("cpCellRotation", populationSource, StringComparison.Ordinal);
        Assert.Contains("cpCellAspect", populationSource, StringComparison.Ordinal);
        Assert.Contains("cpCellLean", populationSource, StringComparison.Ordinal);
        Assert.Contains("cpSoftUnion", populationSource, StringComparison.Ordinal);
        Assert.Contains("cloud_population.glsl", densitySource, StringComparison.Ordinal);
        Assert.Contains(
            "if (densityAssetVersion >= 2)",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains("cpPopulationMask(", densitySource, StringComparison.Ordinal);
        Assert.Contains("cloud_population.glsl", sparseSource, StringComparison.Ordinal);
        Assert.Contains("if (uHasWeatherMap == 0)", sparseSource, StringComparison.Ordinal);
        Assert.Contains("float lower = smoothstep(0.0, 0.08, unitHeight);", sparseSource, StringComparison.Ordinal);
        Assert.Contains("float rotation = cpCellRotation", sparseSource, StringComparison.Ordinal);
        Assert.Contains("float aspect = cpCellAspect", sparseSource, StringComparison.Ordinal);
        Assert.Contains("vec2 lean = cpCellLean", sparseSource, StringComparison.Ordinal);
        Assert.Contains("float domainApron = smoothstep(", sparseSource, StringComparison.Ordinal);
        Assert.Contains("for (int z = -1; z <= 1; ++z)", sparseSource, StringComparison.Ordinal);
        Assert.Contains("satelliteUnion * attachment", sparseSource, StringComparison.Ordinal);
        Assert.DoesNotContain("uVolumeSize * 0.75", sparseSource, StringComparison.Ordinal);
        Assert.Contains("CQ44_TEMPLATE_WIDTH = 32", sparseSource, StringComparison.Ordinal);
        Assert.Contains("CQ44_TEMPLATE_HEIGHT = 24", sparseSource, StringComparison.Ordinal);
        Assert.Contains("CQ44_TEMPLATE_DEPTH = 32", sparseSource, StringComparison.Ordinal);
        Assert.Contains(
            "ca2-dual-scale-asymmetric-v2-templates",
            backendSource,
            StringComparison.Ordinal);
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
