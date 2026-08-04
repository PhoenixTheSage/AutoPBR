using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CA2 CPU reference for the deterministic GLSL cloud-population contract.
/// It intentionally contains no renderer state: placement is a pure function
/// of cell coordinates, scale class, and weather material.
/// </summary>
internal static class PreviewCloudPopulationContract
{
    public const uint ParentSalt = 0xA511E9B3u;
    public const uint SatelliteSalt = 0x63D83595u;
    public const float SatelliteSpanRatio = 0.38f;
    public const float MaximumJitterInCells = 0.18f;
    public const float ParentMinimumScale = 0.72f;
    public const float ParentMaximumScale = 1.16f;
    public const float SatelliteMinimumScale = 0.58f;
    public const float SatelliteMaximumScale = 0.92f;
    public const float ParentMinimumAspect = 0.76f;
    public const float ParentMaximumAspect = 1.32f;
    public const float SatelliteMinimumAspect = 0.68f;
    public const float SatelliteMaximumAspect = 1.24f;
    public const float ParentMinimumLean = 0.045f;
    public const float ParentMaximumLean = 0.145f;
    public const float SatelliteMinimumLean = 0.035f;
    public const float SatelliteMaximumLean = 0.085f;

    public static float ParentCellSpan(float volumeSize) =>
        Math.Max(Math.Max(volumeSize, 8f) * 1.10f, 160f);

    public static float SatelliteCellSpan(float volumeSize) =>
        ParentCellSpan(volumeSize) * SatelliteSpanRatio;

    public static uint HashCell(int x, int z, uint salt)
    {
        unchecked
        {
            var hash = 2166136261u;
            hash = (hash ^ (uint)x) * 16777619u;
            hash = (hash ^ (uint)z) * 16777619u;
            hash = (hash ^ salt) * 16777619u;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            return hash;
        }
    }

    public static float Hash01(int x, int z, uint salt) =>
        (HashCell(x, z, salt) & 0x00FFFFFFu) / 16777215f;

    public static Vector2 CellCenter(
        int x,
        int z,
        float cellSpan,
        uint salt)
    {
        var jitterX = Hash01(x, z, salt ^ 0x68BC21EBu) - 0.5f;
        var jitterZ = Hash01(x, z, salt ^ 0x02E5BE93u) - 0.5f;
        return new Vector2(
            (x + 0.5f + jitterX * 0.36f) * cellSpan,
            (z + 0.5f + jitterZ * 0.36f) * cellSpan);
    }

    public static float CellScale(
        int x,
        int z,
        uint salt,
        bool satellite)
    {
        var value = Hash01(x, z, salt ^ 0x967A889Bu);
        return satellite
            ? Lerp(SatelliteMinimumScale, SatelliteMaximumScale, value)
            : Lerp(ParentMinimumScale, ParentMaximumScale, value);
    }

    public static float CellRotationRadians(int x, int z, uint salt) =>
        Hash01(x, z, salt ^ 0xC2B2AE35u) * MathF.Tau;

    public static float CellAspect(
        int x,
        int z,
        uint salt,
        bool satellite)
    {
        var value = Hash01(x, z, salt ^ 0x27D4EB2Fu);
        return satellite
            ? Lerp(SatelliteMinimumAspect, SatelliteMaximumAspect, value)
            : Lerp(ParentMinimumAspect, ParentMaximumAspect, value);
    }

    public static Vector2 CellLean(
        int x,
        int z,
        uint salt,
        bool satellite)
    {
        var angle =
            Hash01(x, z, salt ^ 0x165667B1u) * MathF.Tau;
        var amount = satellite
            ? Lerp(
                SatelliteMinimumLean,
                SatelliteMaximumLean,
                Hash01(x, z, salt ^ 0xD3A2646Cu))
            : Lerp(
                ParentMinimumLean,
                ParentMaximumLean,
                Hash01(x, z, salt ^ 0xFD7046C5u));
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * amount;
    }

    public static float ParentProbability(float coverage, float cloudType)
    {
        var moisture = SmoothStep(0.14f, 0.84f, Saturate(coverage));
        var stratusFill =
            SmoothStep(0.72f, 0.96f, Saturate(cloudType)) * 0.08f;
        return Math.Clamp(
            Lerp(0.24f, 0.82f, moisture) + stratusFill,
            0f,
            0.90f);
    }

    public static float SatelliteProbability(
        float coverage,
        float cloudType,
        float convection)
    {
        var moisture = SmoothStep(0.20f, 0.78f, Saturate(coverage));
        var cumulusBias = Lerp(0.72f, 0.36f, Saturate(cloudType));
        var lift =
            SmoothStep(0.40f, 0.90f, Saturate(convection)) * 0.12f;
        return Math.Clamp(moisture * cumulusBias + lift, 0f, 0.78f);
    }

    public static float SoftUnion(float a, float b)
    {
        a = Saturate(a);
        b = Saturate(b);
        return a + b - a * b;
    }

    private static float Saturate(float value) =>
        Math.Clamp(value, 0f, 1f);

    private static float Lerp(float a, float b, float t) =>
        a + (b - a) * t;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Saturate((value - edge0) / Math.Max(edge1 - edge0, 1e-6f));
        return t * t * (3f - 2f * t);
    }
}
