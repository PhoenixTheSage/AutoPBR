using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using System.Numerics;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudClipmapControllerTests
{
    [Fact]
    public void ClipmapProfiles_MatchDocumentedWorldCoverageAndAddressing()
    {
        Assert.Equal(new Int3(512, 256, 512),
            PreviewSparseCloudVolumeContract.WorldCoverage(0));
        Assert.Equal(new Int3(2048, 1024, 2048),
            PreviewSparseCloudVolumeContract.WorldCoverage(1));
        Assert.Equal(new Int3(8192, 4096, 8192),
            PreviewSparseCloudVolumeContract.WorldCoverage(2));
        Assert.Equal(16f, PreviewSparseCloudVolumeContract.BrickWorldSize(0));
        Assert.Equal(64f, PreviewSparseCloudVolumeContract.BrickWorldSize(1));
        Assert.Equal(256f, PreviewSparseCloudVolumeContract.BrickWorldSize(2));

        for (var index = 0;
             index < PreviewSparseCloudVolumeContract.PageTableEntryCount;
             index++)
        {
            Assert.Equal(
                index,
                PreviewSparseCloudVolumeContract.PageTableLinearIndex(
                    PreviewSparseCloudVolumeContract.PageTableCoordinate(
                        index)));
        }
    }

    [Fact]
    public void Update_InitializesSnappedOriginsAndCapsEnteringRequests()
    {
        var controller = new PreviewSparseCloudClipmapController();

        var update = controller.Update(
            new Vector3(0f, 20f, 0f),
            Vector3.UnitZ,
            cloudVerticalCenterWorldY: 30f,
            ReadOnlySpan<Vector4>.Empty,
            frame: 0);

        Assert.True(update.OriginChanged);
        Assert.False(update.Teleport);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame,
            update.Entering.Count);
        Assert.Equal(update.Entering.Count, update.RequestedCount);
        Assert.Equal(new Int3(-16, -7, -16), controller.GetOrigin(0));
        Assert.Equal(new Int3(-16, -8, -16), controller.GetOrigin(1));
        Assert.Equal(new Int3(-16, -8, -16), controller.GetOrigin(2));
        Assert.Equal(
            new PreviewSparseCloudLogicalBrickKey(0, 0, 1, 0),
            update.Entering[0]);
        Assert.All(update.Entering, key => Assert.Equal(0, key.ClipmapLevel));
        Assert.Equal(
            PreviewSparseCloudVolumeContract.RequestedPage,
            controller.GetPageValue(update.Entering[0]));
        Assert.Equal(1, controller.TableRevision);
    }

    [Fact]
    public void Update_IsDeterministicAndDoesNotDuplicatePendingRequests()
    {
        var first = new PreviewSparseCloudClipmapController();
        var second = new PreviewSparseCloudClipmapController();
        var camera = new Vector3(12.5f, 24f, -6.5f);

        var firstUpdate = first.Update(
            camera,
            Vector3.Normalize(new Vector3(0.2f, 0.1f, 1f)),
            32f,
            ReadOnlySpan<Vector4>.Empty,
            7);
        var secondUpdate = second.Update(
            camera,
            Vector3.Normalize(new Vector3(0.2f, 0.1f, 1f)),
            32f,
            ReadOnlySpan<Vector4>.Empty,
            7);

        Assert.Equal(firstUpdate.Entering, secondUpdate.Entering);
        var next = first.Update(
            camera,
            Vector3.Normalize(new Vector3(0.2f, 0.1f, 1f)),
            32f,
            ReadOnlySpan<Vector4>.Empty,
            8);
        Assert.Empty(firstUpdate.Entering.Intersect(next.Entering));
        Assert.Equal(192, next.RequestedCount);
        Assert.Equal(2, next.TableRevision);
    }

    [Fact]
    public void Update_SnapsOnlyAfterCrossingLevelBrickBoundary()
    {
        var controller = new PreviewSparseCloudClipmapController();
        controller.Update(
            new Vector3(0f, 20f, 0f),
            Vector3.UnitX,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            0);
        var initialL0 = controller.GetOrigin(0);
        var initialL1 = controller.GetOrigin(1);

        var withinBrick = controller.Update(
            new Vector3(15.99f, 20f, 0f),
            Vector3.UnitX,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            1);
        Assert.False(withinBrick.OriginChanged);
        Assert.Equal(initialL0, controller.GetOrigin(0));

        var crossed = controller.Update(
            new Vector3(16.01f, 20f, 0f),
            Vector3.UnitX,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            2);
        Assert.True(crossed.OriginChanged);
        Assert.Equal(initialL0.X + 1, controller.GetOrigin(0).X);
        Assert.Equal(initialL1, controller.GetOrigin(1));
        Assert.InRange(
            crossed.Entering.Count,
            0,
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame);
    }

    [Fact]
    public void ResidentMapping_SurvivesOverlappingRecenterAndUsesPageEncoding()
    {
        var controller = new PreviewSparseCloudClipmapController();
        var initialized = controller.Update(
            new Vector3(0f, 20f, 0f),
            Vector3.UnitX,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            0);
        var key = initialized.Entering[0];

        Assert.True(controller.TryMarkResident(key, 7));
        Assert.Equal(
            PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(7),
            controller.GetPageValue(key));
        var residentRevision = controller.TableRevision;

        var recentered = controller.Update(
            new Vector3(16.01f, 20f, 0f),
            Vector3.UnitX,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            1);
        Assert.True(recentered.OriginChanged);
        Assert.DoesNotContain(key, recentered.Retired);
        Assert.Equal(1, controller.ResidentCount);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(7),
            controller.GetPageValue(key));
        Assert.True(controller.TableRevision > residentRevision);
    }

    [Fact]
    public void Update_LargeTeleportRetiresOldFootprintAndRemainsBounded()
    {
        var controller = new PreviewSparseCloudClipmapController();
        controller.Update(
            Vector3.Zero,
            Vector3.UnitZ,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            0);
        controller.Update(
            Vector3.Zero,
            Vector3.UnitZ,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            1);

        var teleported = controller.Update(
            new Vector3(20_000f, 20f, -20_000f),
            Vector3.UnitZ,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            2);

        Assert.True(teleported.OriginChanged);
        Assert.True(teleported.Teleport);
        Assert.Equal(192, teleported.Retired.Count);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.TeleportEnteringBricksPerFrame,
            teleported.Entering.Count);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.TeleportEnteringBricksPerFrame,
            teleported.RequestedCount);
        Assert.Equal(1, controller.TeleportCount);
        Assert.All(
            teleported.Retired,
            key => Assert.Equal(
                PreviewSparseCloudVolumeContract.UnmappedPage,
                controller.GetPageValue(key)));
    }

    [Fact]
    public void Update_OriginChangeCapsEnteringBelowSteadyStateBudget()
    {
        var controller = new PreviewSparseCloudClipmapController();
        controller.Update(
            new Vector3(0f, 20f, 0f),
            Vector3.UnitX,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            0);

        var recentered = controller.Update(
            new Vector3(16.01f, 20f, 0f),
            Vector3.UnitX,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            1);

        Assert.True(recentered.OriginChanged);
        Assert.False(recentered.Teleport);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.OriginChangedEnteringBricksPerFrame,
            recentered.Entering.Count);
    }

    [Fact]
    public void Update_FillsCameraCenteredPendingWindowAtFrameCapThenSettles()
    {
        var controller = new PreviewSparseCloudClipmapController();
        PreviewSparseCloudClipmapUpdate update = default;
        // Windowed pending rebuild intentionally does not enumerate the entire
        // 32×16×32×3 page tables; fill the camera-centered window instead.
        for (var frame = 0; frame < 256; frame++)
        {
            update = controller.Update(
                Vector3.Zero,
                Vector3.UnitZ,
                30f,
                ReadOnlySpan<Vector4>.Empty,
                frame);
            if (update.Entering.Count == 0)
            {
                break;
            }

            Assert.InRange(
                update.Entering.Count,
                1,
                PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame);
        }

        Assert.True(controller.RequestedCount >=
            PreviewSparseCloudActivationPolicy.MinimumResidentBricksForActivation);
        Assert.Equal(0, controller.PendingCount);
        Assert.True(
            controller.RequestedCount <
            PreviewSparseCloudVolumeContract.PageTableEntryCount *
            PreviewSparseCloudVolumeContract.ClipmapCount);
        var settled = controller.Update(
            Vector3.Zero,
            Vector3.UnitZ,
            30f,
            ReadOnlySpan<Vector4>.Empty,
            update.Frame + 1);
        Assert.Empty(settled.Entering);
        Assert.Equal(update.TableRevision, settled.TableRevision);
    }
}
