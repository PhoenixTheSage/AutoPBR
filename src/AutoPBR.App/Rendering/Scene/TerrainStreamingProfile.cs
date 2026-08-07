using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.Scene;

public readonly record struct TerrainStreamingProfile(
    PreviewTerrainStreamingMode Mode,
    int CacheReadConcurrency,
    int BakeConcurrency,
    int MaxInflightItems,
    long MaxInflightBytes,
    long MaxReadyBytes,
    long UploadBytesPerFrame,
    long CoverageUploadBytesPerFrame,
    long MeshArenaBytes,
    long TransitionReserveBytes,
    int MeshArenaPageBytes,
    int TransferSegmentBytes,
    int TransferSegmentCount,
    long MemoryCacheBytes,
    long DiskCacheBytes,
    double TerrainStreamCpuBudgetMs)
{
    public static TerrainStreamingProfile Resolve(
        PreviewTerrainStreamingMode requested,
        int processorCount,
        long dedicatedVramBytes,
        bool persistentTransferSupported)
    {
        processorCount = Math.Max(1, processorCount);
        var resolved = requested == PreviewTerrainStreamingMode.Auto
            ? ResolveAutomaticMode(processorCount, dedicatedVramBytes, persistentTransferSupported)
            : requested;

        var gib = 1024L * 1024L * 1024L;
        var mib = 1024 * 1024;
        var knownVram = dedicatedVramBytes > 0;
        var safeVram = knownVram
            ? Math.Max(256L * mib, dedicatedVramBytes)
            : 4L * gib;

        var profile = resolved switch
        {
            PreviewTerrainStreamingMode.Low => new TerrainStreamingProfile(
                resolved, 1, 1, 8, 96L * mib, 48L * mib,
                2L * mib, 4L * mib,
                Math.Min(512L * mib, safeVram / 5),
                64L * mib, 32 * 1024, 4 * mib, 3,
                128L * mib, 2L * gib, 0.5),
            PreviewTerrainStreamingMode.High => new TerrainStreamingProfile(
                resolved,
                Math.Clamp(processorCount / 4, 2, 4),
                Math.Clamp(processorCount / 3, 2, 6),
                32, 768L * mib, 384L * mib,
                12L * mib, 20L * mib,
                Math.Min(4L * gib, safeVram * 2 / 5),
                512L * mib, 64 * 1024, 16 * mib, 5,
                768L * mib, 12L * gib, 2.0),
            _ => new TerrainStreamingProfile(
                PreviewTerrainStreamingMode.Balanced,
                Math.Clamp(processorCount / 6, 1, 2),
                Math.Clamp(processorCount / 4, 2, 4),
                16, 320L * mib, 160L * mib,
                6L * mib, 10L * mib,
                Math.Min(1536L * mib, safeVram * 3 / 10),
                192L * mib, 64 * 1024, 8 * mib, 4,
                384L * mib, 6L * gib, 1.0),
        };

        if (persistentTransferSupported)
        {
            return profile;
        }

        return profile with
        {
            UploadBytesPerFrame = Math.Min(profile.UploadBytesPerFrame, 4L * mib),
            CoverageUploadBytesPerFrame = Math.Min(profile.CoverageUploadBytesPerFrame, 6L * mib),
            TransferSegmentBytes = 4 * mib,
            TransferSegmentCount = 3,
        };
    }

    private static PreviewTerrainStreamingMode ResolveAutomaticMode(
        int processorCount,
        long dedicatedVramBytes,
        bool persistentTransferSupported)
    {
        var gib = 1024L * 1024L * 1024L;
        if (processorCount <= 4 ||
            (dedicatedVramBytes > 0 && dedicatedVramBytes < 4L * gib) ||
            !persistentTransferSupported)
        {
            return PreviewTerrainStreamingMode.Low;
        }

        if (processorCount >= 12 && dedicatedVramBytes >= 10L * gib)
        {
            return PreviewTerrainStreamingMode.High;
        }

        return PreviewTerrainStreamingMode.Balanced;
    }
}

public readonly record struct TerrainAdaptiveBudget(
    long UploadBytes,
    int BakeConcurrency,
    bool AllowSpeculation,
    double Scale);

/// <summary>
/// Slow-increase/fast-decrease controller. Coverage work receives the profile's
/// dedicated coverage allowance even when discretionary streaming is throttled.
/// </summary>
public sealed class TerrainAdaptiveBudgetController
{
    private readonly TerrainStreamingProfile _profile;
    private double _scale = 1.0;
    private double _nextIncreaseSeconds;
    private double _downgradeCooldownUntilSeconds;

    public TerrainAdaptiveBudgetController(TerrainStreamingProfile profile)
    {
        _profile = profile;
    }

    public TerrainAdaptiveBudget Current =>
        BuildBudget(coverageDebt: 0);

    public TerrainAdaptiveBudget Update(
        double nowSeconds,
        double terrainStreamCpuP95Ms,
        bool stagingBackpressured,
        bool memoryPressured,
        int coverageDebt)
    {
        var overBudget = terrainStreamCpuP95Ms > _profile.TerrainStreamCpuBudgetMs ||
                         stagingBackpressured ||
                         memoryPressured;
        if (overBudget)
        {
            _scale = Math.Max(0.20, _scale * 0.65);
            _downgradeCooldownUntilSeconds = nowSeconds + 5.0;
            _nextIncreaseSeconds = Math.Max(_nextIncreaseSeconds, nowSeconds + 2.0);
            return BuildBudget(coverageDebt);
        }

        if (nowSeconds >= _nextIncreaseSeconds &&
            nowSeconds >= _downgradeCooldownUntilSeconds)
        {
            _scale = Math.Min(1.0, _scale + 0.05);
            _nextIncreaseSeconds = nowSeconds + 2.0;
        }

        return BuildBudget(coverageDebt);
    }

    private TerrainAdaptiveBudget BuildBudget(int coverageDebt)
    {
        var discretionary = (long)Math.Floor(_profile.UploadBytesPerFrame * _scale);
        var bytes = coverageDebt > 0
            ? Math.Max(discretionary, _profile.CoverageUploadBytesPerFrame)
            : discretionary;
        var workers = Math.Clamp(
            (int)Math.Ceiling(_profile.BakeConcurrency * _scale),
            1,
            _profile.BakeConcurrency);
        return new TerrainAdaptiveBudget(
            Math.Max(256 * 1024L, bytes),
            workers,
            AllowSpeculation: coverageDebt == 0 && _scale >= 0.75,
            _scale);
    }
}
