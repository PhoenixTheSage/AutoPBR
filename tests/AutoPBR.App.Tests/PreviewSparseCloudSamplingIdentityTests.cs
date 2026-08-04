using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudSamplingIdentityTests
{
    private static readonly Int3 OriginL0 = new(-16, -8, 4);
    private static readonly Int3 OriginL1 = new(-8, -4, 2);
    private static readonly Int3 OriginL2 = new(-4, -2, 1);

    [Fact]
    public void MatchingPublishedResidencyPlan_CreatesStableIdentity()
    {
        var first = CreateReady();
        var second = CreateReady();

        Assert.True(first.IsValid);
        Assert.Equal(first, second);
        Assert.NotEqual(0, first.Signature);
        Assert.Contains("atlas-7", first.FormatDiagnostic());
        Assert.Contains("tables-3", first.FormatDiagnostic());
    }

    [Fact]
    public void InFlightUnpublishedBricks_DoNotInvalidateActivePlan()
    {
        var created = PreviewSparseCloudSamplingIdentity.TryCreate(
            atlasGenerationId: 7,
            atlasGenerationPending: true,
            pageTableGenerationId: 3,
            publishedPlanRevision: 11,
            pagePublicationPending: false,
            controllerPlanRevision: 11,
            residentCount: 24,
            originL0: OriginL0,
            originL1: OriginL1,
            originL2: OriginL2,
            out var identity,
            out var reason);

        Assert.True(created);
        Assert.True(identity.IsValid);
        Assert.Equal("ready", reason);
    }

    [Fact]
    public void NewerPendingPagePublication_PreservesCurrentActiveIdentity()
    {
        var created = PreviewSparseCloudSamplingIdentity.TryCreate(
            atlasGenerationId: 7,
            atlasGenerationPending: true,
            pageTableGenerationId: 3,
            publishedPlanRevision: 11,
            pagePublicationPending: true,
            controllerPlanRevision: 12,
            residentCount: 24,
            originL0: OriginL0,
            originL1: OriginL1,
            originL2: OriginL2,
            out var identity,
            out var reason);

        Assert.True(created);
        Assert.True(identity.IsValid);
        Assert.Equal("ready", reason);
    }

    [Theory]
    [InlineData(0, 3, 11, false, 11, 24, "atlas-generation-unavailable")]
    [InlineData(7, 0, 11, false, 11, 24, "page-publication-unavailable")]
    [InlineData(7, 3, 11, false, 10, 24, "plan-regressed-11-10")]
    [InlineData(7, 3, 11, false, 11, 0, "no-resident-bricks")]
    public void IncompletePublication_DoesNotCreateSamplingIdentity(
        int atlasGeneration,
        int tableGeneration,
        int publishedRevision,
        bool publicationPending,
        int controllerRevision,
        int residentCount,
        string expectedReason)
    {
        var created = PreviewSparseCloudSamplingIdentity.TryCreate(
            atlasGeneration,
            atlasGenerationPending: false,
            tableGeneration,
            publishedRevision,
            publicationPending,
            controllerRevision,
            residentCount,
            OriginL0,
            OriginL1,
            OriginL2,
            out var identity,
            out var reason);

        Assert.False(created);
        Assert.False(identity.IsValid);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void GenerationOrOriginChange_ChangesTemporalIdentity()
    {
        var baseline = CreateReady();
        Assert.True(PreviewSparseCloudSamplingIdentity.TryCreate(
            atlasGenerationId: 8,
            atlasGenerationPending: false,
            pageTableGenerationId: 4,
            publishedPlanRevision: 12,
            pagePublicationPending: false,
            controllerPlanRevision: 12,
            residentCount: 25,
            originL0: new Int3(OriginL0.X + 1, OriginL0.Y, OriginL0.Z),
            originL1: OriginL1,
            originL2: OriginL2,
            out var changed,
            out _));

        Assert.NotEqual(baseline.Signature, changed.Signature);
        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Activation_RequiresBothLightingCascadesAtExactIdentity()
    {
        var identity = CreateReady(residentCount: 480);

        Assert.True(PreviewSparseCloudActivationPolicy.CanActivate(
            identity,
            nearLightingGenerated: true,
            nearLightingIdentity: identity.Signature,
            farLightingGenerated: true,
            farLightingIdentity: identity.Signature,
            residentCount: 480,
            requestedCount: 480,
            hasPendingGeneration: false));
        Assert.False(PreviewSparseCloudActivationPolicy.CanActivate(
            identity,
            nearLightingGenerated: true,
            nearLightingIdentity: identity.Signature,
            farLightingGenerated: false,
            farLightingIdentity: identity.Signature,
            residentCount: 480,
            requestedCount: 480,
            hasPendingGeneration: false));
        Assert.False(PreviewSparseCloudActivationPolicy.CanActivate(
            identity,
            nearLightingGenerated: true,
            nearLightingIdentity: identity.Signature,
            farLightingGenerated: true,
            farLightingIdentity: identity.Signature + 1,
            residentCount: 480,
            requestedCount: 480,
            hasPendingGeneration: false));
    }

    [Fact]
    public void Activation_RejectsColdFirstEnteringBatch()
    {
        var identity = CreateReady(residentCount: 96);

        Assert.False(PreviewSparseCloudActivationPolicy.IsResidencyReady(
            residentCount: 96,
            requestedCount: 96,
            hasPendingGeneration: false));
        Assert.False(PreviewSparseCloudActivationPolicy.CanActivate(
            identity,
            nearLightingGenerated: true,
            nearLightingIdentity: identity.Signature,
            farLightingGenerated: true,
            farLightingIdentity: identity.Signature,
            residentCount: 96,
            requestedCount: 96,
            hasPendingGeneration: false));
        Assert.Contains(
            "warming-residency/96-of-96/need-480",
            PreviewSparseCloudActivationPolicy.FormatResidencyWarmupDiagnostic(
                96,
                96,
                hasPendingGeneration: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_RequiresCoverageWhileEnteringBatchesCatchUp()
    {
        Assert.False(PreviewSparseCloudActivationPolicy.IsResidencyReady(
            residentCount: 480,
            requestedCount: 576,
            hasPendingGeneration: false));
        Assert.True(PreviewSparseCloudActivationPolicy.IsResidencyReady(
            residentCount: 480,
            requestedCount: 520,
            hasPendingGeneration: false));
        Assert.False(PreviewSparseCloudActivationPolicy.IsResidencyReady(
            residentCount: 480,
            requestedCount: 480,
            hasPendingGeneration: true));
    }

    [Fact]
    public void LightingMatchesIdentity_IsIndependentOfResidencyWarmup()
    {
        var identity = CreateReady(residentCount: 96);

        Assert.True(PreviewSparseCloudActivationPolicy.LightingMatchesIdentity(
            identity,
            nearLightingGenerated: true,
            nearLightingIdentity: identity.Signature,
            farLightingGenerated: true,
            farLightingIdentity: identity.Signature));
    }

    [Fact]
    public void SoftHold_AllowsSameOriginResidencyGrowth()
    {
        var active = CreateReady(residentCount: 480);
        Assert.True(PreviewSparseCloudSamplingIdentity.TryCreate(
            atlasGenerationId: 7,
            atlasGenerationPending: false,
            pageTableGenerationId: 4,
            publishedPlanRevision: 12,
            pagePublicationPending: false,
            controllerPlanRevision: 12,
            residentCount: 504,
            originL0: OriginL0,
            originL1: OriginL1,
            originL2: OriginL2,
            out var prepared,
            out _));

        Assert.True(PreviewSparseCloudActivationPolicy.CanSoftHold(active, prepared));
        Assert.False(PreviewSparseCloudActivationPolicy.CanSoftHold(
            active,
            prepared with { OriginL0 = new Int3(OriginL0.X + 1, OriginL0.Y, OriginL0.Z) }));
    }

    private static PreviewSparseCloudSamplingIdentity CreateReady(
        int residentCount = 24)
    {
        Assert.True(PreviewSparseCloudSamplingIdentity.TryCreate(
            atlasGenerationId: 7,
            atlasGenerationPending: false,
            pageTableGenerationId: 3,
            publishedPlanRevision: 11,
            pagePublicationPending: false,
            controllerPlanRevision: 11,
            residentCount,
            originL0: OriginL0,
            originL1: OriginL1,
            originL2: OriginL2,
            out var identity,
            out var reason),
            reason);
        return identity;
    }
}
