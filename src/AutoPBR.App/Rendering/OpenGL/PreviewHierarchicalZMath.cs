using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CPU-side Hi-Z projection helpers kept in lockstep with <c>genesis_indirect_compact.comp</c>
/// occlusion tests (standard depth 0=near … 1=far).
/// </summary>
internal static class PreviewHierarchicalZMath
{
    public const float DepthEpsilon = 1e-4f;

    public readonly record struct ScreenBounds(
        float MinU,
        float MaxU,
        float MinV,
        float MaxV,
        float NearestDepth,
        bool Valid);

    public static ScreenBounds ProjectSphere(
        Vector3 center,
        float radius,
        in Matrix4x4 viewProj)
    {
        if (!(radius >= 0f) || !float.IsFinite(radius) ||
            !float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z))
        {
            return default;
        }

        var minU = float.PositiveInfinity;
        var maxU = float.NegativeInfinity;
        var minV = float.PositiveInfinity;
        var maxV = float.NegativeInfinity;
        var nearestDepth = float.PositiveInfinity;
        var any = false;

        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                center.X + (((i & 1) != 0) ? radius : -radius),
                center.Y + (((i & 2) != 0) ? radius : -radius),
                center.Z + (((i & 4) != 0) ? radius : -radius));
            var clip = Vector4.Transform(new Vector4(corner, 1f), viewProj);
            if (clip.W <= 1e-6f)
            {
                return default;
            }

            var invW = 1f / clip.W;
            var ndcX = clip.X * invW;
            var ndcY = clip.Y * invW;
            var ndcZ = clip.Z * invW;
            var u = ndcX * 0.5f + 0.5f;
            var v = ndcY * 0.5f + 0.5f;
            var depth = ndcZ * 0.5f + 0.5f;
            minU = MathF.Min(minU, u);
            maxU = MathF.Max(maxU, u);
            minV = MathF.Min(minV, v);
            maxV = MathF.Max(maxV, v);
            nearestDepth = MathF.Min(nearestDepth, depth);
            any = true;
        }

        if (!any)
        {
            return default;
        }

        minU = Math.Clamp(minU, 0f, 1f);
        maxU = Math.Clamp(maxU, 0f, 1f);
        minV = Math.Clamp(minV, 0f, 1f);
        maxV = Math.Clamp(maxV, 0f, 1f);
        nearestDepth = Math.Clamp(nearestDepth, 0f, 1f);
        if (maxU < minU || maxV < minV)
        {
            return default;
        }

        return new ScreenBounds(minU, maxU, minV, maxV, nearestDepth, Valid: true);
    }

    public static int SelectMipLevel(float minU, float maxU, float minV, float maxV, int width, int height, int maxLevel)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        maxLevel = Math.Max(0, maxLevel);
        var pixelW = MathF.Max(1f, (maxU - minU) * width);
        var pixelH = MathF.Max(1f, (maxV - minV) * height);
        var size = MathF.Max(pixelW, pixelH);
        var level = size <= 1f ? 0 : (int)MathF.Ceiling(MathF.Log2(size)) - 2;
        return Math.Clamp(level, 0, maxLevel);
    }

    public static bool IsOccluded(float nearestDepth, float regionMaxDepth) =>
        nearestDepth > regionMaxDepth + DepthEpsilon;
}
