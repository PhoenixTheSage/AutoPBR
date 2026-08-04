using System.Runtime.InteropServices;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ4.4 CPU/GLSL ABI for one bounded sparse-cloud generation dispatch.
/// Two ivec4 values keep every request naturally aligned for std430 SSBO reads.
/// </summary>
internal static class PreviewSparseCloudBrickGenerationContract
{
    public const uint CompletionPending = 0u;
    public const uint CompletionMagic = 0xC044B11Cu;
    public const int TemplateTextureLayerCount = 12 * 32;
    public const int MaximumConservativeDistance = 32;

    public static PreviewSparseCloudBrickGenerationRecord CreateRecord(
        PreviewSparseCloudLogicalBrickKey key,
        int physicalBrickIndex)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        _ = PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(
            physicalBrickIndex);
        return new PreviewSparseCloudBrickGenerationRecord
        {
            LogicalX = key.X,
            LogicalY = key.Y,
            LogicalZ = key.Z,
            ClipmapLevel = key.ClipmapLevel,
            PhysicalBrickIndex = physicalBrickIndex,
            StableSeed = unchecked((int)StableHash(key)),
            Reserved0 = 0,
            Reserved1 = 0,
        };
    }

    public static uint StableHash(PreviewSparseCloudLogicalBrickKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        unchecked
        {
            var hash = 2166136261u;
            hash = (hash ^ (uint)key.ClipmapLevel) * 16777619u;
            hash = (hash ^ (uint)key.X) * 16777619u;
            hash = (hash ^ (uint)key.Y) * 16777619u;
            hash = (hash ^ (uint)key.Z) * 16777619u;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            return hash;
        }
    }

    /// <summary>
    /// Validates the discrete atlas contract used by CQ4.5 traversal. Occupied texels must
    /// encode zero. Empty texels may encode only a downward-biased Chebyshev distance.
    /// CQ4.5 caps the workgroup-local transform at 32 voxels. Traversal independently clips
    /// every decoded skip to the current logical-brick boundary.
    /// </summary>
    public static bool ValidateConservativeBrick(
        ReadOnlySpan<byte> rg,
        out string reason)
    {
        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        var texelCount = size * size * size;
        if (rg.Length != texelCount * 2)
        {
            reason = $"length-{rg.Length}-expected-{texelCount * 2}";
            return false;
        }

        var occupied = new List<Int3>();
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var index = ((z * size + y) * size + x) * 2;
                    if (rg[index] != 0)
                    {
                        occupied.Add(new Int3(x, y, z));
                        if (rg[index + 1] != 0)
                        {
                            reason = $"occupied-distance-nonzero-{x}-{y}-{z}";
                            return false;
                        }
                    }
                }
            }
        }

        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var index = ((z * size + y) * size + x) * 2;
                    if (rg[index] != 0)
                    {
                        continue;
                    }

                    var encoded = rg[index + 1];
                    if (encoded > MaximumConservativeDistance)
                    {
                        reason = $"distance-over-cq4.5-cap-{encoded}-{x}-{y}-{z}";
                        return false;
                    }

                    if (occupied.Count == 0)
                    {
                        continue;
                    }

                    var exact = occupied.Min(candidate =>
                        Math.Max(
                            Math.Abs(candidate.X - x),
                            Math.Max(
                                Math.Abs(candidate.Y - y),
                                Math.Abs(candidate.Z - z))));
                    if (encoded > exact)
                    {
                        reason =
                            $"distance-overestimate-{encoded}-exact-{exact}-" +
                            $"{x}-{y}-{z}";
                        return false;
                    }
                }
            }
        }

        reason = occupied.Count == 0
            ? "valid-empty"
            : $"valid-occupied-{occupied.Count}";
        return true;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PreviewSparseCloudBrickGenerationRecord
{
    public int LogicalX;
    public int LogicalY;
    public int LogicalZ;
    public int ClipmapLevel;
    public int PhysicalBrickIndex;
    public int StableSeed;
    public int Reserved0;
    public int Reserved1;

    public readonly PreviewSparseCloudLogicalBrickKey Key =>
        new(ClipmapLevel, LogicalX, LogicalY, LogicalZ);
}
