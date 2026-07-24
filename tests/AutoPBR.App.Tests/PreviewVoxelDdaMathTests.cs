using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewVoxelDdaMathTests
{
    [Fact]
    public void SolidBottomY_ReachesSharedFloor_ForTallColumns()
    {
        var bottom = PreviewVoxelDdaMath.SolidBottomY(20);
        Assert.Equal(PreviewStageConstants.TerrainSolidFloorRelativeY, bottom);
        Assert.True(PreviewVoxelDdaMath.IsSolidColumnLayer(20, bottom, 0));
        Assert.True(PreviewVoxelDdaMath.IsSolidColumnLayer(20, bottom, 20));
        Assert.False(PreviewVoxelDdaMath.IsSolidColumnLayer(20, bottom, 21));
    }

    [Fact]
    public void RayHitsSolid_BeforeSampleBehindWall()
    {
        // Wall of solids at x=2 for all y/z in the path.
        (int Surface, int Bottom) ColumnAt(int x, int z) =>
            x == 2 ? (10, -10) : (-100, 100); // empty elsewhere (bottom > surface)

        var camera = new Vector3(0.5f, PreviewStageConstants.GroundPlaneWorldY + 1.5f, 0.5f);
        var target = new Vector3(5.5f, PreviewStageConstants.GroundPlaneWorldY + 1.5f, 0.5f);
        var delta = target - camera;
        Assert.True(PreviewVoxelDdaMath.RayHitsSolidBefore(
            camera,
            delta,
            delta.Length(),
            ColumnAt));
    }

    [Fact]
    public void RayMisses_OverFlatPad()
    {
        (int Surface, int Bottom) ColumnAt(int x, int z) => (0, PreviewVoxelDdaMath.SolidBottomY(0));

        // Ray above the pad surface (relative layer 0 occupies GroundPlane..GroundPlane+1).
        var camera = new Vector3(0.5f, PreviewStageConstants.GroundPlaneWorldY + 3f, 0.5f);
        var target = new Vector3(8.5f, PreviewStageConstants.GroundPlaneWorldY + 3f, 0.5f);
        var delta = target - camera;
        Assert.False(PreviewVoxelDdaMath.RayHitsSolidBefore(
            camera,
            delta,
            delta.Length(),
            ColumnAt));
    }

    [Fact]
    public void SphereOcclusion_RequiresAllNearSamplesBlocked()
    {
        // Thin pillar at origin — a large sphere beside it should not cull (edge straddles).
        (int Surface, int Bottom) ThinPillar(int x, int z) =>
            x == 0 && z == 0 ? (20, -5) : (-100, 100);

        var camera = new Vector3(-4f, PreviewStageConstants.GroundPlaneWorldY + 2f, 0f);
        var center = new Vector3(4f, PreviewStageConstants.GroundPlaneWorldY + 2f, 0f);
        Assert.False(PreviewVoxelDdaMath.IsSphereOccludedByHeightfield(
            camera,
            center,
            radius: 1.5f,
            ThinPillar));

        // Full wall between camera and sphere.
        (int Surface, int Bottom) Wall(int x, int z) =>
            x == 0 ? (20, -20) : (-100, 100);

        Assert.True(PreviewVoxelDdaMath.IsSphereOccludedByHeightfield(
            camera,
            center,
            radius: 0.5f,
            Wall));
    }
}
