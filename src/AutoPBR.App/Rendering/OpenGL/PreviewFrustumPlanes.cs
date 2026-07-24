using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

internal static class PreviewFrustumPlanes
{
    public const int PlaneCount = 6;

    /// <summary>
    /// Extract frustum planes from a CPU view-projection that matches the preview's GL upload path.
    /// <see cref="OpenGlPreviewBackend"/> uploads <c>Transpose(cpu)</c> with <c>transpose=false</c>,
    /// so the shader's column-vector <c>clip = uProj * uView * world</c> is equivalent to
    /// <c>clip = (proj * view) * world</c> on the CPU matrix. Gribb–Hartmann therefore reads
    /// matrix <b>rows</b> (not columns).
    /// </summary>
    public static void Extract(Matrix4x4 viewProjection, Span<Vector4> destination)
    {
        if (destination.Length < PlaneCount)
        {
            throw new ArgumentException("Frustum destination must hold six planes.", nameof(destination));
        }

        var r1 = new Vector4(viewProjection.M11, viewProjection.M12, viewProjection.M13, viewProjection.M14);
        var r2 = new Vector4(viewProjection.M21, viewProjection.M22, viewProjection.M23, viewProjection.M24);
        var r3 = new Vector4(viewProjection.M31, viewProjection.M32, viewProjection.M33, viewProjection.M34);
        var r4 = new Vector4(viewProjection.M41, viewProjection.M42, viewProjection.M43, viewProjection.M44);

        destination[0] = Normalize(r4 + r1); // left
        destination[1] = Normalize(r4 - r1); // right
        destination[2] = Normalize(r4 + r2); // bottom
        destination[3] = Normalize(r4 - r2); // top
        destination[4] = Normalize(r4 + r3); // near
        destination[5] = Normalize(r4 - r3); // far
    }

    /// <summary>Extract into a frame-owned plane buffer.</summary>
    public static void Extract(Matrix4x4 viewProjection, ref PreviewFrustumPlaneBuffer destination)
    {
        Span<Vector4> planes = destination;
        Extract(viewProjection, planes);
    }

    /// <summary>True when the sphere is not completely outside any frustum plane.</summary>
    public static bool SphereIntersects(ReadOnlySpan<Vector4> planes, Vector3 center, float radius)
    {
        var r = Math.Max(0f, radius);
        for (var i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            var dist = p.X * center.X + p.Y * center.Y + p.Z * center.Z + p.W;
            if (dist < -r)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector4 Normalize(Vector4 plane)
    {
        var length = new Vector3(plane.X, plane.Y, plane.Z).Length();
        return length > 1e-7f && float.IsFinite(length) ? plane / length : Vector4.Zero;
    }
}
