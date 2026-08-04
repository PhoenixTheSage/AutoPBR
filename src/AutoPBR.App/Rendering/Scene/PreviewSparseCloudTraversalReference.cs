using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ4.5 CPU oracle for fixed-ray tests. It mirrors active-page fallback, trilinear bordered
/// atlas sampling, 10% cascade blending, and brick-clipped conservative-distance stepping.
/// </summary>
internal static class PreviewSparseCloudTraversalReference
{
    public const float CascadeBlendFraction = 0.10f;
    public const int MaximumTraversalIterations = 64;

    public static PreviewSparseCloudResolvedDensity Resolve(
        IReadOnlyList<ushort[]> activePageTables,
        IReadOnlyList<Int3> activeOrigins,
        ReadOnlySpan<byte> atlasRg,
        Vector3 worldPosition,
        float shellDensity)
    {
        ValidateInputs(activePageTables, activeOrigins, atlasRg);
        var result = new PreviewSparseCloudResolvedDensity(
            Density: shellDensity,
            SafeDistanceWorld: 0f,
            VoxelWorldSize: PreviewSparseCloudVolumeContract.Level2VoxelWorldSize,
            SelectedLevel: -1,
            ShellWeight: 1f,
            Resident: false);

        for (var level = PreviewSparseCloudVolumeContract.ClipmapCount - 1;
             level >= 0;
             level--)
        {
            if (!TrySampleLevel(
                    activePageTables[level],
                    activeOrigins[level],
                    atlasRg,
                    level,
                    worldPosition,
                    out var sample))
            {
                continue;
            }

            var density = Lerp(
                result.Density,
                sample.Density,
                sample.EdgeWeight * sample.FaceWeight);
            var shellWeight =
                result.ShellWeight * (1f - sample.EdgeWeight);
            var safeDistance = shellWeight <= 1e-3f
                ? result.SafeDistanceWorld > 0f
                    ? Math.Min(
                        result.SafeDistanceWorld,
                        sample.DistanceWorld)
                    : sample.DistanceWorld
                : 0f;
            result = new PreviewSparseCloudResolvedDensity(
                density,
                safeDistance,
                sample.VoxelWorldSize,
                level,
                shellWeight,
                Resident: true);
        }

        return result;
    }

    public static PreviewSparseCloudReferenceTrace Trace(
        IReadOnlyList<ushort[]> activePageTables,
        IReadOnlyList<Int3> activeOrigins,
        ReadOnlySpan<byte> atlasRg,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float tStart,
        float tEnd,
        float fineStepWorld,
        float shellDensity = 0f)
    {
        var direction = NormalizeOrDefault(rayDirection, Vector3.UnitX);
        var t = Math.Max(tStart, 0f);
        var pageSteps = 0;
        var distanceSteps = 0;
        var fineSteps = 0;
        var fallbackQueries = 0;
        for (var iteration = 0;
             iteration < MaximumTraversalIterations && t < tEnd;
             iteration++)
        {
            var position = rayOrigin + direction * t;
            var resolved = Resolve(
                activePageTables,
                activeOrigins,
                atlasRg,
                position,
                shellDensity);
            if (resolved.ShellWeight > 1e-3f || !resolved.Resident)
            {
                fallbackQueries++;
                return new PreviewSparseCloudReferenceTrace(
                    Hit: true,
                    T: t,
                    Density: resolved.Density,
                    SelectedLevel: resolved.SelectedLevel,
                    ShellWeight: resolved.ShellWeight,
                    PageSteps: pageSteps,
                    DistanceSteps: distanceSteps,
                    FineSteps: fineSteps,
                    FallbackQueries: fallbackQueries);
            }

            var voxelSize = Math.Max(resolved.VoxelWorldSize, 0.001f);
            if (resolved.Density > 0.5f / 255f ||
                resolved.SafeDistanceWorld <= voxelSize + 1e-4f)
            {
                fineSteps++;
                return new PreviewSparseCloudReferenceTrace(
                    Hit: true,
                    T: t,
                    Density: resolved.Density,
                    SelectedLevel: resolved.SelectedLevel,
                    ShellWeight: resolved.ShellWeight,
                    PageSteps: pageSteps,
                    DistanceSteps: distanceSteps,
                    FineSteps: fineSteps,
                    FallbackQueries: fallbackQueries);
            }

            var boundaryDistance = DistanceToBrickBoundary(
                position,
                direction,
                voxelSize *
                PreviewSparseCloudVolumeContract.LogicalBrickInteriorSize);
            var distanceStep = Math.Max(
                voxelSize * 0.5f,
                resolved.SafeDistanceWorld * 0.8f);
            var boundaryCrossing =
                boundaryDistance <= distanceStep + 1e-4f;
            float advance;
            if (boundaryCrossing)
            {
                pageSteps++;
                advance = boundaryDistance + voxelSize * 1e-3f;
            }
            else
            {
                distanceSteps++;
                advance = Math.Max(
                    Math.Min(distanceStep, boundaryDistance),
                    Math.Min(
                        Math.Max(fineStepWorld, voxelSize * 0.5f),
                        voxelSize));
            }

            t += Math.Max(advance, voxelSize * 1e-4f);
        }

        if (t < tEnd)
        {
            var continuation = Resolve(
                activePageTables,
                activeOrigins,
                atlasRg,
                rayOrigin + direction * t,
                shellDensity);
            return new PreviewSparseCloudReferenceTrace(
                Hit: true,
                T: t,
                Density: continuation.Density,
                SelectedLevel: continuation.SelectedLevel,
                ShellWeight: continuation.ShellWeight,
                PageSteps: pageSteps,
                DistanceSteps: distanceSteps,
                FineSteps: fineSteps + 1,
                FallbackQueries: fallbackQueries);
        }

        return new PreviewSparseCloudReferenceTrace(
            Hit: false,
            T: tEnd,
            Density: 0f,
            SelectedLevel: -1,
            ShellWeight: 1f,
            PageSteps: pageSteps,
            DistanceSteps: distanceSteps,
            FineSteps: fineSteps,
            FallbackQueries: fallbackQueries);
    }

    public static float ComputeClipmapEdgeWeight(
        Vector3 worldPosition,
        Int3 origin,
        int clipmapLevel)
    {
        var brickWorldSize =
            PreviewSparseCloudVolumeContract.BrickWorldSize(clipmapLevel);
        var pagePosition =
            worldPosition / brickWorldSize -
            origin.ToVector3();
        var dimensions =
            PreviewSparseCloudVolumeContract.PageTableDimensions.ToVector3();
        var edge = Vector3.Min(pagePosition, dimensions - pagePosition);
        var edgeDistance = Math.Min(edge.X, Math.Min(edge.Y, edge.Z));
        var blendPages =
            Math.Min(
                PreviewSparseCloudVolumeContract.PageTableWidth,
                Math.Min(
                    PreviewSparseCloudVolumeContract.PageTableHeight,
                    PreviewSparseCloudVolumeContract.PageTableDepth)) *
            CascadeBlendFraction;
        return SmoothStep(0f, Math.Max(blendPages, 0.001f), edgeDistance);
    }

    private static bool TrySampleLevel(
        ReadOnlySpan<ushort> pageTable,
        Int3 origin,
        ReadOnlySpan<byte> atlasRg,
        int level,
        Vector3 worldPosition,
        out PreviewSparseCloudReferenceLevelSample sample)
    {
        var voxelSize =
            PreviewSparseCloudVolumeContract.VoxelWorldSize(level);
        var brickWorldSize =
            PreviewSparseCloudVolumeContract.BrickWorldSize(level);
        var logicalPagePosition = worldPosition / brickWorldSize;
        var logicalPage = new Int3(
            FloorToInt(logicalPagePosition.X),
            FloorToInt(logicalPagePosition.Y),
            FloorToInt(logicalPagePosition.Z));
        var localPage = new Int3(
            logicalPage.X - origin.X,
            logicalPage.Y - origin.Y,
            logicalPage.Z - origin.Z);
        if (localPage.X < 0 ||
            localPage.X >= PreviewSparseCloudVolumeContract.PageTableWidth ||
            localPage.Y < 0 ||
            localPage.Y >= PreviewSparseCloudVolumeContract.PageTableHeight ||
            localPage.Z < 0 ||
            localPage.Z >= PreviewSparseCloudVolumeContract.PageTableDepth)
        {
            sample = default;
            return false;
        }

        var pageValue = pageTable[
            PreviewSparseCloudVolumeContract.PageTableLinearIndex(localPage)];
        if (!PreviewSparseCloudVolumeContract.TryDecodePhysicalBrickIndex(
                pageValue,
                out var physicalIndex))
        {
            sample = default;
            return false;
        }

        var logicalVoxel = worldPosition / voxelSize;
        var brickLocalVoxel = new Vector3(
            logicalVoxel.X -
            logicalPage.X *
            PreviewSparseCloudVolumeContract.LogicalBrickInteriorSize,
            logicalVoxel.Y -
            logicalPage.Y *
            PreviewSparseCloudVolumeContract.LogicalBrickInteriorSize,
            logicalVoxel.Z -
            logicalPage.Z *
            PreviewSparseCloudVolumeContract.LogicalBrickInteriorSize);
        brickLocalVoxel = new Vector3(
            Math.Clamp(brickLocalVoxel.X, 0f, 7.999f),
            Math.Clamp(brickLocalVoxel.Y, 0f, 7.999f),
            Math.Clamp(brickLocalVoxel.Z, 0f, 7.999f));
        var atlasBrick =
            PreviewSparseCloudVolumeContract.PhysicalBrickAtlasCoordinate(
                physicalIndex);
        var atlasBase =
            atlasBrick.ToVector3() *
            PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        // CPU oracle stores texel indices directly (integer = texel sample). Keep
        // samples inside [base, base+9] so they match the GL clamp that prevents
        // LINEAR bleed into the next packed atlas brick.
        var atlasPosition = Vector3.Clamp(
            atlasBase +
            new Vector3(PreviewSparseCloudVolumeContract.PhysicalBrickBorderSize) +
            brickLocalVoxel,
            atlasBase,
            atlasBase + new Vector3(
                PreviewSparseCloudVolumeContract.PhysicalBrickSize - 1));
        var rg = SampleAtlasTrilinear(atlasRg, atlasPosition);
        sample = new PreviewSparseCloudReferenceLevelSample(
            Density: rg.X,
            DistanceWorld: rg.Y * 255f * voxelSize,
            VoxelWorldSize: voxelSize,
            EdgeWeight: ComputeClipmapEdgeWeight(
                worldPosition,
                origin,
                level),
            FaceWeight: level == 0
                ? FaceResidentFactor(
                    pageTable,
                    localPage,
                    brickLocalVoxel)
                : 1f,
            Level: level,
            PhysicalBrickIndex: physicalIndex);
        return true;
    }

    private static float FaceResidentFactor(
        ReadOnlySpan<ushort> pageTable,
        Int3 localPage,
        Vector3 brickLocalVoxel)
    {
        const float fade = 1.25f;
        var factor = 1f;
        var size = PreviewSparseCloudVolumeContract.LogicalBrickInteriorSize;
        var dimensions = PreviewSparseCloudVolumeContract.PageTableDimensions;

        factor = Math.Min(
            factor,
            FaceAxisFactor(
                pageTable,
                new Int3(localPage.X - 1, localPage.Y, localPage.Z),
                dimensions,
                brickLocalVoxel.X,
                fade));
        factor = Math.Min(
            factor,
            FaceAxisFactor(
                pageTable,
                new Int3(localPage.X + 1, localPage.Y, localPage.Z),
                dimensions,
                size - brickLocalVoxel.X,
                fade));
        factor = Math.Min(
            factor,
            FaceAxisFactor(
                pageTable,
                new Int3(localPage.X, localPage.Y - 1, localPage.Z),
                dimensions,
                brickLocalVoxel.Y,
                fade));
        factor = Math.Min(
            factor,
            FaceAxisFactor(
                pageTable,
                new Int3(localPage.X, localPage.Y + 1, localPage.Z),
                dimensions,
                size - brickLocalVoxel.Y,
                fade));
        factor = Math.Min(
            factor,
            FaceAxisFactor(
                pageTable,
                new Int3(localPage.X, localPage.Y, localPage.Z - 1),
                dimensions,
                brickLocalVoxel.Z,
                fade));
        factor = Math.Min(
            factor,
            FaceAxisFactor(
                pageTable,
                new Int3(localPage.X, localPage.Y, localPage.Z + 1),
                dimensions,
                size - brickLocalVoxel.Z,
                fade));
        return factor;
    }

    private static float FaceAxisFactor(
        ReadOnlySpan<ushort> pageTable,
        Int3 neighbor,
        Int3 dimensions,
        float distanceFromFace,
        float fade)
    {
        if (neighbor.X < 0 || neighbor.X >= dimensions.X ||
            neighbor.Y < 0 || neighbor.Y >= dimensions.Y ||
            neighbor.Z < 0 || neighbor.Z >= dimensions.Z)
        {
            return 1f;
        }

        var page = pageTable[
            PreviewSparseCloudVolumeContract.PageTableLinearIndex(neighbor)];
        if (PreviewSparseCloudVolumeContract.TryDecodePhysicalBrickIndex(
                page,
                out _))
        {
            return 1f;
        }

        return SmoothStep(0f, fade, distanceFromFace);
    }

    private static Vector2 SampleAtlasTrilinear(
        ReadOnlySpan<byte> atlasRg,
        Vector3 position)
    {
        var size = PreviewSparseCloudVolumeContract.AtlasTexelSize;
        var x0 = Math.Clamp(FloorToInt(position.X), 0, size - 1);
        var y0 = Math.Clamp(FloorToInt(position.Y), 0, size - 1);
        var z0 = Math.Clamp(FloorToInt(position.Z), 0, size - 1);
        var x1 = Math.Min(x0 + 1, size - 1);
        var y1 = Math.Min(y0 + 1, size - 1);
        var z1 = Math.Min(z0 + 1, size - 1);
        var fx = Math.Clamp(position.X - x0, 0f, 1f);
        var fy = Math.Clamp(position.Y - y0, 0f, 1f);
        var fz = Math.Clamp(position.Z - z0, 0f, 1f);

        var c00 = Vector2.Lerp(
            ReadAtlasTexel(atlasRg, size, x0, y0, z0),
            ReadAtlasTexel(atlasRg, size, x1, y0, z0),
            fx);
        var c10 = Vector2.Lerp(
            ReadAtlasTexel(atlasRg, size, x0, y1, z0),
            ReadAtlasTexel(atlasRg, size, x1, y1, z0),
            fx);
        var c01 = Vector2.Lerp(
            ReadAtlasTexel(atlasRg, size, x0, y0, z1),
            ReadAtlasTexel(atlasRg, size, x1, y0, z1),
            fx);
        var c11 = Vector2.Lerp(
            ReadAtlasTexel(atlasRg, size, x0, y1, z1),
            ReadAtlasTexel(atlasRg, size, x1, y1, z1),
            fx);
        return Vector2.Lerp(
            Vector2.Lerp(c00, c10, fy),
            Vector2.Lerp(c01, c11, fy),
            fz);
    }

    private static Vector2 ReadAtlasTexel(
        ReadOnlySpan<byte> atlasRg,
        int size,
        int x,
        int y,
        int z)
    {
        var index = ((z * size + y) * size + x) * 2;
        return new Vector2(
            atlasRg[index] / 255f,
            atlasRg[index + 1] / 255f);
    }

    private static float DistanceToBrickBoundary(
        Vector3 worldPosition,
        Vector3 direction,
        float brickWorldSize)
    {
        static float Axis(float position, float direction, float size)
        {
            if (Math.Abs(direction) <= 1e-6f)
            {
                return float.PositiveInfinity;
            }

            var brick = MathF.Floor(position / size);
            var boundary = (brick + (direction >= 0f ? 1f : 0f)) * size;
            return Math.Max((boundary - position) / direction, 0f);
        }

        return Math.Min(
            Axis(worldPosition.X, direction.X, brickWorldSize),
            Math.Min(
                Axis(worldPosition.Y, direction.Y, brickWorldSize),
                Axis(worldPosition.Z, direction.Z, brickWorldSize)));
    }

    private static void ValidateInputs(
        IReadOnlyList<ushort[]> pageTables,
        IReadOnlyList<Int3> origins,
        ReadOnlySpan<byte> atlasRg)
    {
        if (pageTables.Count !=
                PreviewSparseCloudVolumeContract.ClipmapCount ||
            origins.Count !=
                PreviewSparseCloudVolumeContract.ClipmapCount ||
            pageTables.Any(
                table =>
                    table.Length !=
                    PreviewSparseCloudVolumeContract.PageTableEntryCount))
        {
            throw new ArgumentException("CQ4.5 page-table input is incomplete.");
        }

        if (atlasRg.Length !=
            PreviewSparseCloudVolumeContract.AtlasByteLength)
        {
            throw new ArgumentException("CQ4.5 RG8 atlas input has the wrong length.");
        }
    }

    private static int FloorToInt(float value) =>
        checked((int)MathF.Floor(value));

    private static Vector3 NormalizeOrDefault(
        Vector3 value,
        Vector3 fallback) =>
        value.LengthSquared() > 1e-12f &&
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z)
            ? Vector3.Normalize(value)
            : fallback;

    private static float Lerp(float left, float right, float amount) =>
        left + (right - left) * amount;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp(
            (value - edge0) / Math.Max(edge1 - edge0, 1e-6f),
            0f,
            1f);
        return t * t * (3f - 2f * t);
    }
}

internal readonly record struct PreviewSparseCloudReferenceLevelSample(
    float Density,
    float DistanceWorld,
    float VoxelWorldSize,
    float EdgeWeight,
    float FaceWeight,
    int Level,
    int PhysicalBrickIndex);

internal readonly record struct PreviewSparseCloudResolvedDensity(
    float Density,
    float SafeDistanceWorld,
    float VoxelWorldSize,
    int SelectedLevel,
    float ShellWeight,
    bool Resident);

internal readonly record struct PreviewSparseCloudReferenceTrace(
    bool Hit,
    float T,
    float Density,
    int SelectedLevel,
    float ShellWeight,
    int PageSteps,
    int DistanceSteps,
    int FineSteps,
    int FallbackQueries);
