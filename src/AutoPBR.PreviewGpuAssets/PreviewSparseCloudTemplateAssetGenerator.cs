using System.Security.Cryptography;

namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Deterministic CQ4 envelope generator. Integer ellipsoidal buoyant growth lobes are merged,
/// boundary-eroded, reduced to one connected component, and paired with an exact conservative
/// Chebyshev empty-space distance field.
/// </summary>
public static class PreviewSparseCloudTemplateAssetGenerator
{
    private const int CoordinateScale = 1024;
    private const int MetricScale = 4096;

    public static PreviewSparseCloudTemplateAssetPayload Generate(
        PreviewSparseCloudTemplateAssetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var isV2 = descriptor.Version == PreviewSparseCloudTemplateAssetContractV2.AssetVersion;
        if (isV2)
        {
            if (!PreviewSparseCloudTemplateAssetContractV2.Assets.Contains(descriptor))
            {
                throw new ArgumentException(
                    "Descriptor does not belong to the CQ4 envelope v2 ABI.",
                    nameof(descriptor));
            }
        }
        else if (!PreviewSparseCloudTemplateAssetContract.Assets.Contains(descriptor))
        {
            throw new ArgumentException(
                "Descriptor does not belong to the CQ4 envelope v1 ABI.",
                nameof(descriptor));
        }

        var density = isV2 ? GenerateDensityV2(descriptor) : GenerateDensity(descriptor);
        RetainLargestConnectedComponent(density);
        var distance = GenerateConservativeDistance(density);
        var rg = new byte[descriptor.ByteLength];
        for (var index = 0; index < density.Length; index++)
        {
            rg[index * 2] = density[index];
            rg[index * 2 + 1] = distance[index];
        }

        return new PreviewSparseCloudTemplateAssetPayload(descriptor, rg);
    }

    public static IReadOnlyList<PreviewSparseCloudTemplateAssetPayload> GenerateAll() =>
        GenerateAllFor(PreviewSparseCloudTemplateAssetContract.Assets);

    public static IReadOnlyList<PreviewSparseCloudTemplateAssetPayload> GenerateAllV2() =>
        GenerateAllFor(PreviewSparseCloudTemplateAssetContractV2.Assets);

    private static System.Collections.ObjectModel.ReadOnlyCollection<PreviewSparseCloudTemplateAssetPayload>
        GenerateAllFor(IReadOnlyList<PreviewSparseCloudTemplateAssetDescriptor> assets) =>
        Array.AsReadOnly(
            assets
                .AsParallel()
                .AsOrdered()
                .Select(Generate)
                .ToArray());

    public static string ComputeSha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool HasExpectedHash(
        PreviewSparseCloudTemplateAssetDescriptor descriptor,
        ReadOnlySpan<byte> data) =>
        ValidatePayloadForVersion(descriptor, data, out _) &&
        descriptor.ExpectedSha256.Length == 64 &&
        string.Equals(
            ComputeSha256Hex(data),
            descriptor.ExpectedSha256,
            StringComparison.Ordinal);

    public static bool ValidatePayloadForVersion(
        PreviewSparseCloudTemplateAssetDescriptor descriptor,
        ReadOnlySpan<byte> payload,
        out string reason) =>
        descriptor.Version == PreviewSparseCloudTemplateAssetContractV2.AssetVersion
            ? PreviewSparseCloudTemplateAssetContractV2.ValidatePayload(descriptor, payload, out reason)
            : PreviewSparseCloudTemplateAssetContract.ValidatePayload(descriptor, payload, out reason);

    private static byte[] GenerateDensity(
        PreviewSparseCloudTemplateAssetDescriptor descriptor)
    {
        var lobes = BuildLobes(descriptor);
        var density = new byte[
            PreviewSparseCloudTemplateAssetContract.Width *
            PreviewSparseCloudTemplateAssetContract.Height *
            PreviewSparseCloudTemplateAssetContract.Depth];
        var isCumulus =
            descriptor.Family != PreviewSparseCloudTemplateFamily.Stratus;
        for (var z = 0; z < PreviewSparseCloudTemplateAssetContract.Depth; z++)
        {
            var pz = NormalizeHorizontal(z, PreviewSparseCloudTemplateAssetContract.Depth);
            for (var y = 0; y < PreviewSparseCloudTemplateAssetContract.Height; y++)
            {
                if (isCumulus &&
                    y < PreviewSparseCloudTemplateAssetContract.CumulusBaseLayer)
                {
                    continue;
                }

                var py = NormalizeVertical(y);
                for (var x = 0; x < PreviewSparseCloudTemplateAssetContract.Width; x++)
                {
                    var px = NormalizeHorizontal(x, PreviewSparseCloudTemplateAssetContract.Width);
                    var strongest = 0;
                    var second = 0;
                    foreach (var lobe in lobes)
                    {
                        var contribution = EvaluateLobe(px, py, pz, lobe);
                        if (contribution > strongest)
                        {
                            second = strongest;
                            strongest = contribution;
                        }
                        else if (contribution > second)
                        {
                            second = contribution;
                        }
                    }

                    var merged = Math.Min(255, strongest + second / 5);
                    var erosion = (int)(Hash(
                        x >> 1,
                        y >> 1,
                        z >> 1,
                        descriptor.Seed) >> 27);
                    var value = Math.Max(0, merged - erosion);
                    if (value < 8)
                    {
                        value = 0;
                    }

                    if (isCumulus &&
                        y == PreviewSparseCloudTemplateAssetContract.CumulusBaseLayer)
                    {
                        var baseRadiusX = descriptor.Family switch
                        {
                            PreviewSparseCloudTemplateFamily.CumulusHumilis => 250,
                            PreviewSparseCloudTemplateFamily.CumulusMediocris => 225,
                            _ => 190,
                        };
                        var baseRadiusZ = baseRadiusX * (92 + descriptor.Variant * 4) / 100;
                        var baseMetric =
                            (long)px * px * MetricScale / (baseRadiusX * baseRadiusX) +
                            (long)pz * pz * MetricScale / (baseRadiusZ * baseRadiusZ);
                        if (baseMetric < MetricScale)
                        {
                            var baseDensity =
                                18 + (int)((MetricScale - baseMetric) * 30 / MetricScale);
                            value = Math.Max(value, baseDensity);
                        }
                    }

                    density[VoxelIndex(x, y, z)] = (byte)value;
                }
            }
        }

        return density;
    }

    /// <summary>
    /// CA2.4 v2 morphology. Reuses the v1 lobe layouts but offsets the whole mass off-center,
    /// leans upper lobes with height, carves a missing ring sector out of the outer skirts, and
    /// replaces the symmetric cumulus base ellipse with an asymmetric one. The isosurface is
    /// softened slightly relative to v1 so the extra carving cannot fragment the envelope.
    /// </summary>
    private static byte[] GenerateDensityV2(
        PreviewSparseCloudTemplateAssetDescriptor descriptor)
    {
        var asymmetry = BuildAsymmetry(descriptor);
        var lobes = ApplyAsymmetryToLobes(BuildLobes(descriptor), asymmetry);
        var density = new byte[
            PreviewSparseCloudTemplateAssetContract.Width *
            PreviewSparseCloudTemplateAssetContract.Height *
            PreviewSparseCloudTemplateAssetContract.Depth];
        var isCumulus =
            descriptor.Family != PreviewSparseCloudTemplateFamily.Stratus;
        for (var z = 0; z < PreviewSparseCloudTemplateAssetContract.Depth; z++)
        {
            var pz = NormalizeHorizontal(z, PreviewSparseCloudTemplateAssetContract.Depth);
            for (var y = 0; y < PreviewSparseCloudTemplateAssetContract.Height; y++)
            {
                if (isCumulus &&
                    y < PreviewSparseCloudTemplateAssetContract.CumulusBaseLayer)
                {
                    continue;
                }

                var py = NormalizeVertical(y);
                for (var x = 0; x < PreviewSparseCloudTemplateAssetContract.Width; x++)
                {
                    var px = NormalizeHorizontal(x, PreviewSparseCloudTemplateAssetContract.Width);
                    var strongest = 0;
                    var second = 0;
                    foreach (var lobe in lobes)
                    {
                        var contribution = EvaluateLobe(px, py, pz, lobe);
                        if (contribution > strongest)
                        {
                            second = strongest;
                            strongest = contribution;
                        }
                        else if (contribution > second)
                        {
                            second = contribution;
                        }
                    }

                    var merged = Math.Min(255, strongest + second / 5);
                    var erosion = (int)(Hash(
                        x >> 1,
                        y >> 1,
                        z >> 1,
                        descriptor.Seed) >> 28);
                    var value = Math.Max(0, merged - erosion);

                    var isBaseLayer = isCumulus &&
                        y == PreviewSparseCloudTemplateAssetContract.CumulusBaseLayer;
                    if (!isBaseLayer &&
                        IsInNotchSector(px, pz, asymmetry))
                    {
                        value = value * (100 - asymmetry.NotchDepthPercent) / 100;
                    }

                    if (value < 6)
                    {
                        value = 0;
                    }

                    if (isBaseLayer)
                    {
                        var baseRadiusX = descriptor.Family switch
                        {
                            PreviewSparseCloudTemplateFamily.CumulusHumilis => 250,
                            PreviewSparseCloudTemplateFamily.CumulusMediocris => 225,
                            _ => 190,
                        };
                        var baseRadiusZ = baseRadiusX * (92 + descriptor.Variant * 4) / 100;
                        var baseOffsetX = px - asymmetry.OffsetX / 2;
                        var baseOffsetZ = pz - asymmetry.OffsetZ / 2;
                        var baseRadiusXEffective = baseOffsetX >= 0
                            ? baseRadiusX * (100 + asymmetry.BaseAsymmetryPercent) / 100
                            : baseRadiusX * (100 - asymmetry.BaseAsymmetryPercent) / 100;
                        var baseMetric =
                            (long)baseOffsetX * baseOffsetX * MetricScale /
                            (baseRadiusXEffective * baseRadiusXEffective) +
                            (long)baseOffsetZ * baseOffsetZ * MetricScale /
                            (baseRadiusZ * baseRadiusZ);
                        if (baseMetric < MetricScale)
                        {
                            var baseDensity =
                                18 + (int)((MetricScale - baseMetric) * 30 / MetricScale);
                            value = Math.Max(value, baseDensity);
                        }
                    }

                    density[VoxelIndex(x, y, z)] = (byte)value;
                }
            }
        }

        return density;
    }

    private static AsymmetryProfile BuildAsymmetry(
        PreviewSparseCloudTemplateAssetDescriptor descriptor)
    {
        var random = new DeterministicRandom((uint)descriptor.Seed ^ 0x5A5AA5A5u);
        var offsetSignX = random.Next(0, 2) == 0 ? -1 : 1;
        var offsetSignZ = random.Next(0, 2) == 0 ? -1 : 1;
        var offsetX = offsetSignX * random.Next(95, 176);
        var offsetZ = offsetSignZ * random.Next(95, 176);
        var leanSignX = random.Next(0, 2) == 0 ? -1 : 1;
        var leanSignZ = random.Next(0, 2) == 0 ? -1 : 1;
        var leanX = leanSignX * random.Next(55, 116);
        var leanZ = leanSignZ * random.Next(55, 116);
        var notchAngleDeg = random.Next(0, 360);
        var notchWidthDeg = random.Next(75, 126);
        var notchDepthPercent = random.Next(55, 86);
        var baseAsymmetryPercent = random.Next(10, 19) * (offsetSignX >= 0 ? 1 : -1);
        return new AsymmetryProfile(
            offsetX,
            offsetZ,
            leanX,
            leanZ,
            notchAngleDeg,
            notchWidthDeg,
            notchDepthPercent,
            baseAsymmetryPercent);
    }

    private static List<Lobe> ApplyAsymmetryToLobes(
        List<Lobe> lobes,
        in AsymmetryProfile asymmetry)
    {
        var result = new List<Lobe>(lobes.Count);
        foreach (var lobe in lobes)
        {
            var heightFraction = Math.Clamp(lobe.Y, 0, CoordinateScale);
            var leanAmountX = asymmetry.LeanX * heightFraction / CoordinateScale;
            var leanAmountZ = asymmetry.LeanZ * heightFraction / CoordinateScale;
            result.Add(lobe with
            {
                X = lobe.X + asymmetry.OffsetX + leanAmountX,
                Z = lobe.Z + asymmetry.OffsetZ + leanAmountZ,
            });
        }

        return result;
    }

    /// <summary>
    /// True outside a protected core radius when the voxel's angle around the volume center
    /// falls inside the descriptor's deterministic notch sector. Protecting the core keeps the
    /// connected humid mass intact while the notch removes an outer skirt wedge.
    /// </summary>
    private static bool IsInNotchSector(int px, int pz, in AsymmetryProfile asymmetry)
    {
        const int ProtectedCoreRadius = 260;
        var radiusSquared = (long)px * px + (long)pz * pz;
        if (radiusSquared < (long)ProtectedCoreRadius * ProtectedCoreRadius)
        {
            return false;
        }

        var angleDegrees = Math.Atan2(pz, px) * (180.0 / Math.PI);
        if (angleDegrees < 0)
        {
            angleDegrees += 360.0;
        }

        var delta = Math.Abs(((angleDegrees - asymmetry.NotchAngleDeg + 540.0) % 360.0) - 180.0);
        return delta <= asymmetry.NotchWidthDeg / 2.0;
    }

    private readonly record struct AsymmetryProfile(
        int OffsetX,
        int OffsetZ,
        int LeanX,
        int LeanZ,
        int NotchAngleDeg,
        int NotchWidthDeg,
        int NotchDepthPercent,
        int BaseAsymmetryPercent);

    private static List<Lobe> BuildLobes(
        PreviewSparseCloudTemplateAssetDescriptor descriptor)
    {
        var lobes = new List<Lobe>(12);
        var random = new DeterministicRandom((uint)descriptor.Seed);
        switch (descriptor.Family)
        {
            case PreviewSparseCloudTemplateFamily.CumulusHumilis:
                lobes.Add(Jittered(0, 245, 0, 290, 205, 255, 248, ref random));
                AddRing(lobes, count: 5, centerY: 235, radius: 195,
                    lobeRadius: 170, verticalRadius: 150, strength: 230, ref random);
                break;
            case PreviewSparseCloudTemplateFamily.CumulusMediocris:
                lobes.Add(Jittered(0, 335, 0, 250, 300, 240, 252, ref random));
                AddRing(lobes, count: 6, centerY: 310, radius: 185,
                    lobeRadius: 155, verticalRadius: 190, strength: 232, ref random);
                lobes.Add(Jittered(
                    random.Next(-70, 71),
                    545,
                    random.Next(-55, 56),
                    175,
                    210,
                    170,
                    238,
                    ref random));
                break;
            case PreviewSparseCloudTemplateFamily.CumulusCongestus:
                lobes.Add(Jittered(0, 225, 0, 205, 190, 205, 250, ref random));
                lobes.Add(Jittered(-35, 410, 25, 190, 220, 185, 252, ref random));
                lobes.Add(Jittered(35, 610, -20, 170, 230, 170, 250, ref random));
                lobes.Add(Jittered(-10, 805, 10, 145, 190, 145, 242, ref random));
                AddRing(lobes, count: 4, centerY: 320, radius: 165,
                    lobeRadius: 125, verticalRadius: 145, strength: 220, ref random);
                break;
            case PreviewSparseCloudTemplateFamily.Stratus:
                lobes.Add(Jittered(0, 350, 0, 470, 105, 455, 220, ref random));
                AddRing(lobes, count: 8, centerY: 350, radius: 300,
                    lobeRadius: 230, verticalRadius: 90, strength: 205, ref random);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(descriptor));
        }

        return lobes;
    }

    private static void AddRing(
        List<Lobe> lobes,
        int count,
        int centerY,
        int radius,
        int lobeRadius,
        int verticalRadius,
        int strength,
        ref DeterministicRandom random)
    {
        ReadOnlySpan<(int X, int Z)> directions =
        [
            (1024, 0), (724, 724), (0, 1024), (-724, 724),
            (-1024, 0), (-724, -724), (0, -1024), (724, -724),
        ];
        var phase = random.Next(0, directions.Length);
        for (var index = 0; index < count; index++)
        {
            var direction = directions[
                (phase + index * directions.Length / count) % directions.Length];
            lobes.Add(Jittered(
                direction.X * radius / 1024,
                centerY + random.Next(-45, 46),
                direction.Z * radius / 1024,
                lobeRadius,
                verticalRadius,
                lobeRadius,
                strength,
                ref random));
        }
    }

    private static Lobe Jittered(
        int x,
        int y,
        int z,
        int radiusX,
        int radiusY,
        int radiusZ,
        int strength,
        ref DeterministicRandom random) =>
        new(
            x + random.Next(-35, 36),
            y + random.Next(-24, 25),
            z + random.Next(-35, 36),
            radiusX * random.Next(91, 110) / 100,
            radiusY * random.Next(91, 110) / 100,
            radiusZ * random.Next(91, 110) / 100,
            Math.Clamp(strength + random.Next(-8, 9), 1, 255));

    private static int EvaluateLobe(int x, int y, int z, in Lobe lobe)
    {
        var dx = x - lobe.X;
        var dy = y - lobe.Y;
        var dz = z - lobe.Z;
        var metric =
            (long)dx * dx * MetricScale / (lobe.RadiusX * lobe.RadiusX) +
            (long)dy * dy * MetricScale / (lobe.RadiusY * lobe.RadiusY) +
            (long)dz * dz * MetricScale / (lobe.RadiusZ * lobe.RadiusZ);
        if (metric >= MetricScale)
        {
            return 0;
        }

        var interior = MetricScale - metric;
        return (int)(interior * interior * lobe.Strength /
                     ((long)MetricScale * MetricScale));
    }

    private static void RetainLargestConnectedComponent(byte[] density)
    {
        var visited = new bool[density.Length];
        var largest = new List<int>();
        var queue = new Queue<int>();
        for (var start = 0; start < density.Length; start++)
        {
            if (density[start] == 0 || visited[start])
            {
                continue;
            }

            var component = new List<int>();
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                DecodeIndex(current, out var x, out var y, out var z);
                Visit(x - 1, y, z);
                Visit(x + 1, y, z);
                Visit(x, y - 1, z);
                Visit(x, y + 1, z);
                Visit(x, y, z - 1);
                Visit(x, y, z + 1);
            }

            if (component.Count > largest.Count)
            {
                largest = component;
            }

            void Visit(int x, int y, int z)
            {
                if (!InBounds(x, y, z))
                {
                    return;
                }

                var index = VoxelIndex(x, y, z);
                if (density[index] == 0 || visited[index])
                {
                    return;
                }

                visited[index] = true;
                queue.Enqueue(index);
            }
        }

        if (largest.Count == 0)
        {
            throw new InvalidDataException("Generated sparse cloud template is empty.");
        }

        var keep = new bool[density.Length];
        foreach (var index in largest)
        {
            keep[index] = true;
        }

        for (var index = 0; index < density.Length; index++)
        {
            if (!keep[index])
            {
                density[index] = 0;
            }
        }
    }

    private static byte[] GenerateConservativeDistance(byte[] density)
    {
        var distance = new byte[density.Length];
        Array.Fill(distance, byte.MaxValue);
        var queue = new Queue<int>(density.Length);
        for (var index = 0; index < density.Length; index++)
        {
            if (density[index] != 0)
            {
                distance[index] = 0;
                queue.Enqueue(index);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var candidate = Math.Min(
                PreviewSparseCloudTemplateAssetContract.MaximumEncodedDistance,
                distance[current] + 1);
            DecodeIndex(current, out var x, out var y, out var z);
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if ((dx == 0 && dy == 0 && dz == 0) ||
                            !InBounds(x + dx, y + dy, z + dz))
                        {
                            continue;
                        }

                        var neighbor = VoxelIndex(x + dx, y + dy, z + dz);
                        if (candidate >= distance[neighbor])
                        {
                            continue;
                        }

                        distance[neighbor] = (byte)candidate;
                        if (candidate <
                            PreviewSparseCloudTemplateAssetContract.MaximumEncodedDistance)
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }
        }

        return distance;
    }

    private static int NormalizeHorizontal(int coordinate, int size) =>
        ((coordinate * 2 - (size - 1)) * CoordinateScale) /
        Math.Max(1, (size - 1) * 2);

    private static int NormalizeVertical(int coordinate) =>
        coordinate * CoordinateScale /
        (PreviewSparseCloudTemplateAssetContract.Height - 1);

    private static int VoxelIndex(int x, int y, int z) =>
        (z * PreviewSparseCloudTemplateAssetContract.Height + y) *
        PreviewSparseCloudTemplateAssetContract.Width + x;

    private static void DecodeIndex(int index, out int x, out int y, out int z)
    {
        x = index % PreviewSparseCloudTemplateAssetContract.Width;
        index /= PreviewSparseCloudTemplateAssetContract.Width;
        y = index % PreviewSparseCloudTemplateAssetContract.Height;
        z = index / PreviewSparseCloudTemplateAssetContract.Height;
    }

    private static bool InBounds(int x, int y, int z) =>
        x >= 0 && x < PreviewSparseCloudTemplateAssetContract.Width &&
        y >= 0 && y < PreviewSparseCloudTemplateAssetContract.Height &&
        z >= 0 && z < PreviewSparseCloudTemplateAssetContract.Depth;

    private static uint Hash(int x, int y, int z, int seed)
    {
        var value = (uint)seed;
        value ^= (uint)x * 0x9E3779B9u;
        value ^= (uint)y * 0x85EBCA6Bu;
        value ^= (uint)z * 0xC2B2AE35u;
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }

    private readonly record struct Lobe(
        int X,
        int Y,
        int Z,
        int RadiusX,
        int RadiusY,
        int RadiusZ,
        int Strength);

    private struct DeterministicRandom(uint state)
    {
        private uint _state = state == 0 ? 0xA341316Cu : state;

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
                maximumExclusive,
                minimumInclusive);

            var value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return minimumInclusive +
                   (int)(value % (uint)(maximumExclusive - minimumInclusive));
        }
    }
}

public sealed record PreviewSparseCloudTemplateAssetPayload(
    PreviewSparseCloudTemplateAssetDescriptor Descriptor,
    byte[] Rg);
