using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CQ4.6 identity for one atomically published sparse-density view. A candidate is valid only
/// when the active page tables describe the controller's latest residency plan and no atlas
/// generation or page publication can still change that plan behind the consumer.
/// </summary>
internal readonly record struct PreviewSparseCloudSamplingIdentity(
    int AtlasGenerationId,
    int PageTableGenerationId,
    int PlanRevision,
    Int3 OriginL0,
    Int3 OriginL1,
    Int3 OriginL2,
    int ResidentCount,
    int Signature)
{
    public bool IsValid =>
        AtlasGenerationId > 0 &&
        PageTableGenerationId > 0 &&
        PlanRevision > 0 &&
        ResidentCount > 0 &&
        Signature != 0;

    public string FormatDiagnostic() =>
        IsValid
            ? $"identity-{Signature:X8}/atlas-{AtlasGenerationId}/" +
              $"tables-{PageTableGenerationId}/plan-{PlanRevision}/" +
              $"resident-{ResidentCount}/origins-" +
              $"{FormatOrigin(OriginL0)}-{FormatOrigin(OriginL1)}-{FormatOrigin(OriginL2)}"
            : "identity-unavailable";

    public static bool TryCreate(
        int atlasGenerationId,
        bool atlasGenerationPending,
        int pageTableGenerationId,
        int publishedPlanRevision,
        bool pagePublicationPending,
        int controllerPlanRevision,
        int residentCount,
        Int3 originL0,
        Int3 originL1,
        Int3 originL2,
        out PreviewSparseCloudSamplingIdentity identity,
        out string reason)
    {
        identity = default;
        // In-flight atlas writes are safe: their physical bricks remain REQUESTED in the
        // published table until completion. The identity advances only after completion
        // changes the controller plan and that revised table is published.
        _ = atlasGenerationPending;

        // A newer build table may be fenced while the current active table remains valid.
        // Its captured publication metadata, not the controller's newer draft, owns sampling.
        _ = pagePublicationPending;

        if (atlasGenerationId <= 0)
        {
            reason = "atlas-generation-unavailable";
            return false;
        }

        if (pageTableGenerationId <= 0)
        {
            reason = "page-publication-unavailable";
            return false;
        }

        if (publishedPlanRevision <= 0 ||
            controllerPlanRevision < publishedPlanRevision)
        {
            reason =
                $"plan-regressed-{publishedPlanRevision}-" +
                $"{controllerPlanRevision}";
            return false;
        }

        if (residentCount <= 0)
        {
            reason = "no-resident-bricks";
            return false;
        }

        var signature = ComputeSignature(
            atlasGenerationId,
            pageTableGenerationId,
            publishedPlanRevision,
            originL0,
            originL1,
            originL2,
            residentCount);
        identity = new PreviewSparseCloudSamplingIdentity(
            atlasGenerationId,
            pageTableGenerationId,
            publishedPlanRevision,
            originL0,
            originL1,
            originL2,
            residentCount,
            signature);
        reason = "ready";
        return true;
    }

    private static int ComputeSignature(
        int atlasGenerationId,
        int pageTableGenerationId,
        int planRevision,
        Int3 originL0,
        Int3 originL1,
        Int3 originL2,
        int residentCount)
    {
        unchecked
        {
            uint hash = 2166136261u;
            Add(ref hash, atlasGenerationId);
            Add(ref hash, pageTableGenerationId);
            Add(ref hash, planRevision);
            Add(ref hash, originL0.X);
            Add(ref hash, originL0.Y);
            Add(ref hash, originL0.Z);
            Add(ref hash, originL1.X);
            Add(ref hash, originL1.Y);
            Add(ref hash, originL1.Z);
            Add(ref hash, originL2.X);
            Add(ref hash, originL2.Y);
            Add(ref hash, originL2.Z);
            Add(ref hash, residentCount);
            var signature = (int)(hash & 0x7fffffffu);
            return signature == 0 ? 1 : signature;
        }
    }

    private static void Add(ref uint hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 16777619u;
        }
    }

    private static string FormatOrigin(Int3 origin) =>
        $"{origin.X},{origin.Y},{origin.Z}";
}

internal readonly record struct GlSparseCloudSamplingBindings(
    uint AtlasTexture,
    uint PageTableL0,
    uint PageTableL1,
    uint PageTableL2,
    Int3 OriginL0,
    Int3 OriginL1,
    Int3 OriginL2,
    int IdentitySignature)
{
    public bool IsValid =>
        AtlasTexture != 0 &&
        PageTableL0 != 0 &&
        PageTableL1 != 0 &&
        PageTableL2 != 0 &&
        IdentitySignature != 0;
}

internal static class PreviewSparseCloudActivationPolicy
{
    /// <summary>
    /// CQ4 publishes an identity after the first entering batch
    /// (<see cref="PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame"/> =
    /// 96). Activating then leaves most of the frustum on the procedural shell
    /// while a few resident template bricks punch through as cubish cells.
    /// Keep Cinematic on High-equivalent procedural density until residency is
    /// warm enough that sparse is a quality uplift, not a pattern replacement.
    /// </summary>
    public const int MinimumResidentBricksForActivation = 480;

    /// <summary>
    /// Reject activation while requested pages are still racing ahead of
    /// completed residents (another entering batch staged but not yet mapped).
    /// </summary>
    public const float MinimumResidentCoverageRatio = 0.90f;

    public static bool IsResidencyReady(
        int residentCount,
        int requestedCount,
        bool hasPendingGeneration)
    {
        if (hasPendingGeneration ||
            residentCount < MinimumResidentBricksForActivation ||
            requestedCount <= 0)
        {
            return false;
        }

        return residentCount >=
            requestedCount * MinimumResidentCoverageRatio;
    }

    public static string FormatResidencyWarmupDiagnostic(
        int residentCount,
        int requestedCount,
        bool hasPendingGeneration)
    {
        if (IsResidencyReady(
                residentCount,
                requestedCount,
                hasPendingGeneration))
        {
            return $"residency-ready/{residentCount}-of-{requestedCount}";
        }

        if (hasPendingGeneration)
        {
            return $"warming-residency/{residentCount}-of-{requestedCount}/" +
                   $"need-{MinimumResidentBricksForActivation}/pending-generation";
        }

        return $"warming-residency/{residentCount}-of-{requestedCount}/" +
               $"need-{MinimumResidentBricksForActivation}";
    }

    /// <summary>
    /// An already-active sparse view may keep sampling while a newer prepared
    /// identity on the same atlas/origins warms lighting. Origin changes cannot
    /// soft-hold because published page tables address a different footprint.
    /// </summary>
    public static bool CanSoftHold(
        in PreviewSparseCloudSamplingIdentity active,
        in PreviewSparseCloudSamplingIdentity prepared) =>
        active.IsValid &&
        prepared.IsValid &&
        active.AtlasGenerationId == prepared.AtlasGenerationId &&
        active.OriginL0 == prepared.OriginL0 &&
        active.OriginL1 == prepared.OriginL1 &&
        active.OriginL2 == prepared.OriginL2;

    public static bool LightingMatchesIdentity(
        in PreviewSparseCloudSamplingIdentity identity,
        bool nearLightingGenerated,
        int nearLightingIdentity,
        bool farLightingGenerated,
        int farLightingIdentity) =>
        identity.IsValid &&
        nearLightingGenerated &&
        farLightingGenerated &&
        nearLightingIdentity == identity.Signature &&
        farLightingIdentity == identity.Signature;

    /// <summary>
    /// Convenience overload that treats the identity's resident count as both the
    /// published and requested totals with no in-flight generation.
    /// </summary>
    public static bool CanActivate(
        in PreviewSparseCloudSamplingIdentity identity,
        bool nearLightingGenerated,
        int nearLightingIdentity,
        bool farLightingGenerated,
        int farLightingIdentity) =>
        CanActivate(
            identity,
            nearLightingGenerated,
            nearLightingIdentity,
            farLightingGenerated,
            farLightingIdentity,
            residentCount: identity.ResidentCount,
            requestedCount: identity.ResidentCount,
            hasPendingGeneration: false);

    public static bool CanActivate(
        in PreviewSparseCloudSamplingIdentity identity,
        bool nearLightingGenerated,
        int nearLightingIdentity,
        bool farLightingGenerated,
        int farLightingIdentity,
        int residentCount,
        int requestedCount,
        bool hasPendingGeneration) =>
        IsResidencyReady(
            residentCount,
            requestedCount,
            hasPendingGeneration) &&
        LightingMatchesIdentity(
            identity,
            nearLightingGenerated,
            nearLightingIdentity,
            farLightingGenerated,
            farLightingIdentity);
}
