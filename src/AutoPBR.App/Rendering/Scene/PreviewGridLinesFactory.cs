using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>Interleaved line/quad vertices: vec3 position, vec4 rgba (for unlit line shader).</summary>
public static class PreviewGridLinesFactory
{
    public const int FloatsPerVertex = 7;

    /// <summary>
    /// XZ grid on a horizontal plane as thick quads (two triangles per segment).
    /// Drawn with <c>Triangles</c>; color is typically white and tinted by a shader uniform.
    /// </summary>
    public static float[] BuildGrid(
        float halfExtent,
        float step,
        float y,
        float cr,
        float cg,
        float cb,
        float ca,
        float lineHalfWidth = PreviewStageConstants.GridLineHalfWidth)
    {
        lineHalfWidth = Math.Max(1e-4f, lineHalfWidth);
        var list = new List<float>(2048);
        void Vertex(Vector3 p)
        {
            list.Add(p.X);
            list.Add(p.Y);
            list.Add(p.Z);
            list.Add(cr);
            list.Add(cg);
            list.Add(cb);
            list.Add(ca);
        }

        void AddThickLine(Vector3 p0, Vector3 p1)
        {
            var delta = p1 - p0;
            var lenSq = delta.LengthSquared();
            if (lenSq < 1e-12f)
            {
                return;
            }

            var dir = delta / MathF.Sqrt(lenSq);
            // Perpendicular in XZ so the ribbon sits on the ground plane.
            var perp = new Vector3(-dir.Z, 0f, dir.X) * lineHalfWidth;
            var a = p0 + perp;
            var b = p0 - perp;
            var c = p1 + perp;
            var d = p1 - perp;
            // Two triangles: a-b-c, b-d-c
            Vertex(a);
            Vertex(b);
            Vertex(c);
            Vertex(b);
            Vertex(d);
            Vertex(c);
        }

        for (var z = -halfExtent; z <= halfExtent + 1e-4f; z += step)
        {
            AddThickLine(new Vector3(-halfExtent, y, z), new Vector3(halfExtent, y, z));
        }

        for (var x = -halfExtent; x <= halfExtent + 1e-4f; x += step)
        {
            AddThickLine(new Vector3(x, y, -halfExtent), new Vector3(x, y, halfExtent));
        }

        return [.. list];
    }

    /// <summary>Three axis segments from origin along +X,+Y,+Z (model space).</summary>
    public static float[] BuildAxes(float halfLen, float rX, float gX, float bX, float rY, float gY, float bY,
        float rZ, float gZ, float bZ)
    {
        float[] Seg(float x0, float y0, float z0, float x1, float y1, float z1, float r, float g, float b) =>
            [
                x0, y0, z0, r, g, b, 1f,
                x1, y1, z1, r, g, b, 1f
            ];

        var o = new List<float>(42);
        o.AddRange(Seg(0, 0, 0, halfLen, 0, 0, rX, gX, bX));
        o.AddRange(Seg(0, 0, 0, 0, halfLen, 0, rY, gY, bY));
        o.AddRange(Seg(0, 0, 0, 0, 0, halfLen, rZ, gZ, bZ));
        return [.. o];
    }
}
