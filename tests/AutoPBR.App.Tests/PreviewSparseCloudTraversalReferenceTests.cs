using System.Numerics;

using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudTraversalReferenceTests
{
    [Fact]
    public void Resolve_PrefersFinestResidentAndFallsBackThroughRequestedPages()
    {
        var fixture = CreateFixture();
        Map(fixture.Pages[0], fixture.Origins[0], new(0, 0, 0, 0), 0);
        Map(fixture.Pages[1], fixture.Origins[1], new(1, 0, 0, 0), 1);
        FillDensityPlane(fixture.Atlas, physicalBrickIndex: 0, physicalX: 6);
        FillSolidBrick(fixture.Atlas, physicalBrickIndex: 1, density: 64);

        var finest = PreviewSparseCloudTraversalReference.Resolve(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            new Vector3(10f, 4f, 4f),
            shellDensity: 0.1f);
        Assert.True(finest.Resident);
        Assert.Equal(0, finest.SelectedLevel);
        Assert.InRange(finest.Density, 0.99f, 1f);
        Assert.InRange(finest.ShellWeight, 0f, 0.001f);

        SetPage(
            fixture.Pages[0],
            fixture.Origins[0],
            new(0, 0, 0, 0),
            PreviewSparseCloudVolumeContract.RequestedPage);
        var coarse = PreviewSparseCloudTraversalReference.Resolve(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            new Vector3(10f, 4f, 4f),
            shellDensity: 0.1f);
        Assert.True(coarse.Resident);
        Assert.Equal(1, coarse.SelectedLevel);
        Assert.InRange(coarse.Density, 0.24f, 0.26f);

        SetPage(
            fixture.Pages[1],
            fixture.Origins[1],
            new(1, 0, 0, 0),
            PreviewSparseCloudVolumeContract.UnmappedPage);
        var shell = PreviewSparseCloudTraversalReference.Resolve(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            new Vector3(10f, 4f, 4f),
            shellDensity: 0.37f);
        Assert.False(shell.Resident);
        Assert.Equal(-1, shell.SelectedLevel);
        Assert.InRange(shell.Density, 0.369f, 0.371f);
        Assert.Equal(1f, shell.ShellWeight);
    }

    [Fact]
    public void Trace_UsesConservativeDistanceWithoutSkippingDensity()
    {
        var fixture = CreateFixture();
        Map(fixture.Pages[0], fixture.Origins[0], new(0, 0, 0, 0), 0);
        FillDensityPlane(fixture.Atlas, physicalBrickIndex: 0, physicalX: 6);

        var trace = PreviewSparseCloudTraversalReference.Trace(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            rayOrigin: new Vector3(0f, 4f, 4f),
            rayDirection: Vector3.UnitX,
            tStart: 0f,
            tEnd: 16f,
            fineStepWorld: 1f);

        Assert.True(trace.Hit);
        Assert.Equal(0, trace.SelectedLevel);
        Assert.InRange(trace.T, 0f, 10f);
        Assert.True(trace.DistanceSteps > 0);
        Assert.True(trace.FineSteps > 0);
        Assert.Equal(0, trace.FallbackQueries);
    }

    [Fact]
    public void Trace_StopsAtFallbackInsteadOfSkippingUnknownDensity()
    {
        var fixture = CreateFixture();
        SetPage(
            fixture.Pages[0],
            fixture.Origins[0],
            new(0, 0, 0, 0),
            PreviewSparseCloudVolumeContract.RequestedPage);

        var trace = PreviewSparseCloudTraversalReference.Trace(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            rayOrigin: new Vector3(2f, 4f, 4f),
            rayDirection: Vector3.UnitX,
            tStart: 0f,
            tEnd: 16f,
            fineStepWorld: 1f,
            shellDensity: 0.42f);

        Assert.True(trace.Hit);
        Assert.Equal(0f, trace.T);
        Assert.Equal(-1, trace.SelectedLevel);
        Assert.Equal(1, trace.FallbackQueries);
        Assert.InRange(trace.Density, 0.419f, 0.421f);
    }

    [Fact]
    public void Resolve_SoftensTowardCoarserWhenNeighborPageIsMissing()
    {
        var fixture = CreateFixture();
        Map(fixture.Pages[0], fixture.Origins[0], new(0, 0, 0, 0), 0);
        Map(fixture.Pages[1], fixture.Origins[1], new(0, 0, 0, 0), 1);
        FillSolidBrick(fixture.Atlas, physicalBrickIndex: 0, density: 255);
        FillSolidBrick(fixture.Atlas, physicalBrickIndex: 1, density: 64);
        // Leave +X neighbor unmapped so the face toward x=16 must fade L0→L1.

        var interior = PreviewSparseCloudTraversalReference.Resolve(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            new Vector3(8f, 4f, 4f),
            shellDensity: 0f);
        var nearMissingFace = PreviewSparseCloudTraversalReference.Resolve(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            new Vector3(15.5f, 4f, 4f),
            shellDensity: 0f);

        Assert.Equal(0, interior.SelectedLevel);
        Assert.InRange(interior.Density, 0.99f, 1f);
        Assert.Equal(0, nearMissingFace.SelectedLevel);
        Assert.True(
            nearMissingFace.Density < interior.Density - 0.15f,
            $"expected face fade toward L1, interior={interior.Density}, face={nearMissingFace.Density}");
    }

    [Fact]
    public void CascadeBlend_IsTenPercentAndContinuousAtBoundary()
    {
        var origin = new Int3(-16, -8, -16);
        var brickSize = PreviewSparseCloudVolumeContract.BrickWorldSize(0);
        var boundary = origin.ToVector3() * brickSize;
        var atBoundary =
            PreviewSparseCloudTraversalReference.ComputeClipmapEdgeWeight(
                boundary,
                origin,
                0);
        var insideBand =
            PreviewSparseCloudTraversalReference.ComputeClipmapEdgeWeight(
                boundary + new Vector3(brickSize * 0.8f),
                origin,
                0);
        var beyondBand =
            PreviewSparseCloudTraversalReference.ComputeClipmapEdgeWeight(
                boundary + new Vector3(brickSize * 1.6f),
                origin,
                0);

        Assert.Equal(0f, atBoundary);
        Assert.InRange(insideBand, 0.49f, 0.51f);
        Assert.InRange(beyondBand, 0.999f, 1f);
    }

    [Fact]
    public void Resolve_HandlesNegativeLogicalCoordinates()
    {
        var fixture = CreateFixture();
        var key = new PreviewSparseCloudLogicalBrickKey(0, -1, 0, -1);
        Map(fixture.Pages[0], fixture.Origins[0], key, 0);
        FillSolidBrick(fixture.Atlas, physicalBrickIndex: 0, density: 200);

        var resolved = PreviewSparseCloudTraversalReference.Resolve(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            new Vector3(-8f, 4f, -8f),
            shellDensity: 0f);

        Assert.True(resolved.Resident);
        Assert.Equal(0, resolved.SelectedLevel);
        Assert.InRange(resolved.Density, 0.78f, 0.79f);
    }

    [Fact]
    public void Trace_IterationBudgetFailsOpenWithoutDiscardingRayTail()
    {
        var fixture = CreateFixture(originsAtZero: true);
        Array.Fill(
            fixture.Pages[0],
            PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(0));
        FillEmptyBrick(
            fixture.Atlas,
            physicalBrickIndex: 0,
            distance: PreviewSparseCloudBrickGenerationContract
                .MaximumConservativeDistance);
        var brickWorld =
            PreviewSparseCloudVolumeContract.BrickWorldSize(0);
        var startPages = new Vector3(1.7f);
        var direction = Vector3.Normalize(
            new Vector3(1f, 0.43f, 0.97f));
        var trace = PreviewSparseCloudTraversalReference.Trace(
            fixture.Pages,
            fixture.Origins,
            fixture.Atlas,
            rayOrigin: startPages * brickWorld,
            rayDirection: direction,
            tStart: 0f,
            tEnd: 1_000f,
            fineStepWorld: 1f);

        Assert.True(trace.Hit);
        Assert.True(trace.T < 1_000f);
        Assert.Equal(
            PreviewSparseCloudTraversalReference.MaximumTraversalIterations,
            trace.PageSteps + trace.DistanceSteps);
        Assert.True(trace.FineSteps > 0);
        Assert.Equal(0, trace.FallbackQueries);
    }

    private static TraversalFixture CreateFixture(
        bool originsAtZero = false)
    {
        var pages = Enumerable.Range(
                0,
                PreviewSparseCloudVolumeContract.ClipmapCount)
            .Select(_ => new ushort[
                PreviewSparseCloudVolumeContract.PageTableEntryCount])
            .ToArray();
        Int3[] origins =
        [
            originsAtZero ? default : new(-16, -8, -16),
            originsAtZero ? default : new(-16, -8, -16),
            originsAtZero ? default : new(-16, -8, -16),
        ];
        var atlas = new byte[
            checked((int)PreviewSparseCloudVolumeContract.AtlasByteLength)];
        return new TraversalFixture(pages, origins, atlas);
    }

    private static void Map(
        ushort[] pageTable,
        Int3 origin,
        PreviewSparseCloudLogicalBrickKey key,
        int physicalBrickIndex) =>
        SetPage(
            pageTable,
            origin,
            key,
            PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(
                physicalBrickIndex));

    private static void SetPage(
        ushort[] pageTable,
        Int3 origin,
        PreviewSparseCloudLogicalBrickKey key,
        ushort value)
    {
        var local = new Int3(
            key.X - origin.X,
            key.Y - origin.Y,
            key.Z - origin.Z);
        pageTable[
            PreviewSparseCloudVolumeContract.PageTableLinearIndex(local)] =
            value;
    }

    private static void FillDensityPlane(
        byte[] atlas,
        int physicalBrickIndex,
        int physicalX)
    {
        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    WriteAtlasTexel(
                        atlas,
                        physicalBrickIndex,
                        x,
                        y,
                        z,
                        density: x == physicalX ? (byte)255 : (byte)0,
                        distance: checked((byte)Math.Abs(x - physicalX)));
                }
            }
        }
    }

    private static void FillSolidBrick(
        byte[] atlas,
        int physicalBrickIndex,
        byte density)
    {
        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    WriteAtlasTexel(
                        atlas,
                        physicalBrickIndex,
                        x,
                        y,
                        z,
                        density,
                        distance: 0);
                }
            }
        }
    }

    private static void FillEmptyBrick(
        byte[] atlas,
        int physicalBrickIndex,
        int distance)
    {
        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        var encodedDistance = checked((byte)distance);
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    WriteAtlasTexel(
                        atlas,
                        physicalBrickIndex,
                        x,
                        y,
                        z,
                        density: 0,
                        distance: encodedDistance);
                }
            }
        }
    }

    private static void WriteAtlasTexel(
        byte[] atlas,
        int physicalBrickIndex,
        int x,
        int y,
        int z,
        byte density,
        byte distance)
    {
        var atlasSize = PreviewSparseCloudVolumeContract.AtlasTexelSize;
        var brickSize = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        var brick =
            PreviewSparseCloudVolumeContract.PhysicalBrickAtlasCoordinate(
                physicalBrickIndex);
        var atlasX = brick.X * brickSize + x;
        var atlasY = brick.Y * brickSize + y;
        var atlasZ = brick.Z * brickSize + z;
        var index =
            ((atlasZ * atlasSize + atlasY) * atlasSize + atlasX) * 2;
        atlas[index] = density;
        atlas[index + 1] = distance;
    }

    private sealed record TraversalFixture(
        ushort[][] Pages,
        Int3[] Origins,
        byte[] Atlas);
}
