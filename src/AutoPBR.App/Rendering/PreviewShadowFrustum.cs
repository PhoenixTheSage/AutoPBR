using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Rendering;

/// <summary>
/// Fits directional shadow ortho extents to preview subject bounds (large entities such as Ender Dragon)
/// and optionally streamed ground terrain so hills can self-shadow onto the pad / valleys.
/// </summary>
internal static class PreviewShadowFrustum
{
    private const float MinHalfExtent = 0.75f;
    private const float MaxHalfExtent = 256f;
    private const float DepthPadding = 2.5f;
    private const float ExtentPaddingFraction = 0.12f;

    /// <summary>
    /// Minimum XZ half-extent kept around the stage/camera when seeding terrain shadow bounds.
    /// Far coverage uses the streamed LOD ring (up to <see cref="TerrainShadowFarMaxHalfExtent"/>).
    /// </summary>
    public const float TerrainShadowMinXzHalfExtent = 48f;

    /// <summary>Upper clamp for far-cascade half-extent covering the streamed terrain ring.</summary>
    public const float TerrainShadowFarMaxHalfExtent = 256f;

    public static Matrix4x4 BuildDirectionalViewProj(
        Vector3 worldLightDir,
        Vector3 boundsMin,
        Vector3 boundsMax,
        Matrix4x4 worldFromModel,
        float minHalfExtent = MinHalfExtent,
        float maxHalfExtent = MaxHalfExtent)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        WriteAabbCorners(boundsMin, boundsMax, corners);
        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = Vector3.Transform(corners[i], worldFromModel);
        }

        var center = Vector3.Zero;
        foreach (var corner in corners)
        {
            center += corner;
        }

        center /= corners.Length;

        var up = PreviewLightMath.PickShadowViewUp(worldLightDir);
        var radius = 0f;
        foreach (var corner in corners)
        {
            radius = MathF.Max(radius, Vector3.Distance(corner, center));
        }

        var eyeDistance = Math.Clamp(radius + 6f, 8f, MathF.Max(maxHalfExtent * 2f, radius + 6f));
        var eye = center - worldLightDir * eyeDistance;
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, center, up);

        var minX = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        foreach (var corner in corners)
        {
            var lightSpace = TransformPointColumn(view, corner);
            minX = MathF.Min(minX, lightSpace.X);
            maxX = MathF.Max(maxX, lightSpace.X);
            minY = MathF.Min(minY, lightSpace.Y);
            maxY = MathF.Max(maxY, lightSpace.Y);
            minZ = MathF.Min(minZ, lightSpace.Z);
            maxZ = MathF.Max(maxZ, lightSpace.Z);
        }

        var halfX = (maxX - minX) * 0.5f;
        var halfY = (maxY - minY) * 0.5f;
        var half = MathF.Max(halfX, halfY);
        half *= 1f + ExtentPaddingFraction;
        half = Math.Clamp(MathF.Max(half, minHalfExtent), minHalfExtent, maxHalfExtent);

        var centerX = (minX + maxX) * 0.5f;
        var centerY = (minY + maxY) * 0.5f;
        var depthPad = MathF.Max(DepthPadding, half * 0.08f);
        var zNear = -maxZ - depthPad;
        var zFar = -minZ + depthPad;
        if (zFar - zNear < 1f)
        {
            var mid = (zNear + zFar) * 0.5f;
            zNear = mid - 0.5f;
            zFar = mid + 0.5f;
        }

        var proj = PreviewGlMatrices.CreateOrthographicOpenGlRowStorage(
            centerX - half,
            centerX + half,
            centerY - half,
            centerY + half,
            zNear,
            zFar);
        return proj * view;
    }

    internal static void ExpandBoundsForGroundReceiver(ref Vector3 min, ref Vector3 max, float groundY) =>
        ExpandBoundsForGroundReceiver(ref min, ref max, groundY, groundCeilingY: groundY, minXzHalfExtent: 0f);

    /// <summary>
    /// Expands caster/receiver AABB so flat ground and nearby terrain relief stay inside the light ortho.
    /// </summary>
    /// <param name="groundY">Ground floor (pad top / grid Y).</param>
    /// <param name="groundCeilingY">Highest terrain relief to keep in the shadow volume.</param>
    /// <param name="minXzHalfExtent">
    /// Minimum XZ half-extent from the AABB center (terrain self-shadow coverage). 0 keeps legacy subject pad only.
    /// </param>
    internal static void ExpandBoundsForGroundReceiver(
        ref Vector3 min,
        ref Vector3 max,
        float groundY,
        float groundCeilingY,
        float minXzHalfExtent)
    {
        min.Y = MathF.Min(min.Y, groundY);
        max.Y = MathF.Max(max.Y, groundCeilingY);
        var spanX = max.X - min.X;
        var spanZ = max.Z - min.Z;
        var pad = MathF.Max(spanX, spanZ) * 0.35f + 1.5f;
        pad = MathF.Max(pad, MathF.Max(0f, minXzHalfExtent));
        var cx = (min.X + max.X) * 0.5f;
        var cz = (min.Z + max.Z) * 0.5f;
        min.X = MathF.Min(min.X, cx - pad);
        max.X = MathF.Max(max.X, cx + pad);
        min.Z = MathF.Min(min.Z, cz - pad);
        max.Z = MathF.Max(max.Z, cz + pad);
    }

    /// <summary>Seeds a world-space AABB covering terrain self-shadow around a focus point (subject origin or camera).</summary>
    internal static void SeedTerrainShadowBounds(
        Vector3 focusXz,
        float groundFloorY,
        float groundCeilingY,
        float xzHalfExtent,
        out Vector3 min,
        out Vector3 max)
    {
        var half = MathF.Max(xzHalfExtent, MinHalfExtent);
        min = new Vector3(focusXz.X - half, groundFloorY, focusXz.Z - half);
        max = new Vector3(focusXz.X + half, groundCeilingY, focusXz.Z + half);
    }

    internal static void EncapsulateAabb(ref Vector3 min, ref Vector3 max, Vector3 otherMin, Vector3 otherMax)
    {
        min = Vector3.Min(min, otherMin);
        max = Vector3.Max(max, otherMax);
    }

    internal static void EncapsulateTransformedAabb(
        Vector3 localMin,
        Vector3 localMax,
        Matrix4x4 worldFromModel,
        ref Vector3 worldMin,
        ref Vector3 worldMax)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        WriteAabbCorners(localMin, localMax, corners);
        for (var i = 0; i < corners.Length; i++)
        {
            var world = Vector3.Transform(corners[i], worldFromModel);
            worldMin = Vector3.Min(worldMin, world);
            worldMax = Vector3.Max(worldMax, world);
        }
    }

    private static void WriteAabbCorners(Vector3 min, Vector3 max, Span<Vector3> corners)
    {
        corners[0] = new Vector3(min.X, min.Y, min.Z);
        corners[1] = new Vector3(max.X, min.Y, min.Z);
        corners[2] = new Vector3(min.X, max.Y, min.Z);
        corners[3] = new Vector3(max.X, max.Y, min.Z);
        corners[4] = new Vector3(min.X, min.Y, max.Z);
        corners[5] = new Vector3(max.X, min.Y, max.Z);
        corners[6] = new Vector3(min.X, max.Y, max.Z);
        corners[7] = new Vector3(max.X, max.Y, max.Z);
    }

    private static Vector3 TransformPointColumn(Matrix4x4 rowStorageMatrix, Vector3 point)
    {
        var column = Matrix4x4.Transpose(rowStorageMatrix);
        return Vector3.Transform(point, column);
    }
}
