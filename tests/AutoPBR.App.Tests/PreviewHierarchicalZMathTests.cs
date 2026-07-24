using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewHierarchicalZMathTests
{
    [Fact]
    public void ProjectSphere_RejectsBehindCamera()
    {
        var viewProj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            MathF.PI / 3f, 1f, 0.1f, 100f) *
            PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(
                Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);

        var bounds = PreviewHierarchicalZMath.ProjectSphere(
            new Vector3(0f, 0f, 5f),
            0.25f,
            viewProj);

        Assert.False(bounds.Valid);
    }

    [Fact]
    public void ProjectSphere_ProducesScreenBoundsInFrontOfCamera()
    {
        var viewProj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            MathF.PI / 3f, 1f, 0.1f, 100f) *
            PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(
                Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);

        var bounds = PreviewHierarchicalZMath.ProjectSphere(
            new Vector3(0f, 0f, -5f),
            0.5f,
            viewProj);

        Assert.True(bounds.Valid);
        Assert.InRange(bounds.MinU, 0f, 1f);
        Assert.InRange(bounds.MaxU, 0f, 1f);
        Assert.True(bounds.MaxU >= bounds.MinU);
        Assert.True(bounds.MaxV >= bounds.MinV);
        Assert.InRange(bounds.NearestDepth, 0f, 1f);
    }

    [Fact]
    public void SelectMipLevel_GrowsWithScreenCoverage()
    {
        Assert.Equal(0, PreviewHierarchicalZMath.SelectMipLevel(0.5f, 0.5005f, 0.5f, 0.5005f, 1024, 1024, 10));
        // Full-frame coverage with mip bias -2 still lands near the top of the pyramid.
        Assert.True(PreviewHierarchicalZMath.SelectMipLevel(0f, 1f, 0f, 1f, 1024, 1024, 10) >= 7);
    }

    [Fact]
    public void IsOccluded_RequiresNearestBehindRegionMax()
    {
        Assert.True(PreviewHierarchicalZMath.IsOccluded(0.8f, 0.5f));
        Assert.False(PreviewHierarchicalZMath.IsOccluded(0.4f, 0.5f));
        Assert.False(PreviewHierarchicalZMath.IsOccluded(0.5f, 0.5f));
    }
}
