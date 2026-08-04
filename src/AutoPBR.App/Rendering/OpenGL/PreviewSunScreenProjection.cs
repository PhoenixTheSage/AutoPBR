using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Screen-space sun disc projection shared by the sun billboard, atmospheric sky bloom, and god rays.
/// </summary>
internal static class PreviewSunScreenProjection
{
    public const float SunDistance = 85f;
    public const float SunRadius = 5.5f;
    /// <summary>Moon angular diameter ~48% of the sun (real ~0.52° vs ~0.53°).</summary>
    public const float MoonRadius = 2.65f;
    public const float DiscUvMargin = 1.2f;
    public const float ShaftScale = 14f;
    public const float MinShaftRadiusUv = 0.11f;
    /// <summary>
    /// Soft ceiling on shaft cone radius (aspect-corrected UV). Projected edge distances explode
    /// when the sun sits near the horizon of the view frustum and would otherwise cover the whole
    /// frame with a broken march.
    /// </summary>
    public const float MaxShaftRadiusUv = 0.85f;

    /// <summary>
    /// Projects the sun billboard center and disc/cone radii in normalized viewport UV (origin bottom-left).
    /// </summary>
    public static void Compute(
        Vector3 eye,
        Vector3 lightPropagationDir,
        Matrix4x4 view,
        Matrix4x4 proj,
        float viewportAspect,
        float coneScale,
        float sunSizeScale,
        out Vector2 sunUv,
        out float sunDiscRadiusUv,
        out float sunConeRadiusUv,
        out float sunCosDiscEdge) =>
        _ = TryCompute(
            eye,
            lightPropagationDir,
            view,
            proj,
            viewportAspect,
            coneScale,
            sunSizeScale,
            out sunUv,
            out sunDiscRadiusUv,
            out sunConeRadiusUv,
            out sunCosDiscEdge);

    /// <summary>
    /// Like <see cref="Compute"/> but returns false when the sun is behind the camera
    /// (view-space Z &gt;= 0), so screen-space shafts do not aim at a bogus UV.
    /// </summary>
    public static bool TryCompute(
        Vector3 eye,
        Vector3 lightPropagationDir,
        Matrix4x4 view,
        Matrix4x4 proj,
        float viewportAspect,
        float coneScale,
        float sunSizeScale,
        out Vector2 sunUv,
        out float sunDiscRadiusUv,
        out float sunConeRadiusUv,
        out float sunCosDiscEdge)
    {
        _ = eye;
        coneScale = Math.Max(coneScale, 0.05f);
        var sizeScale = Math.Clamp(sunSizeScale, 0.05f, 2f);
        var towardSun = -lightPropagationDir;
        var tls = towardSun.LengthSquared();
        if (tls < 1e-12f)
        {
            sunUv = new Vector2(0.5f, 0.5f);
            sunDiscRadiusUv = 0.025f;
            sunConeRadiusUv = MinShaftRadiusUv;
            sunCosDiscEdge = 0.999f;
            return false;
        }

        towardSun /= MathF.Sqrt(tls);
        if (!TryProjectWorldDirectionToUv(towardSun, view, proj, out sunUv))
        {
            sunDiscRadiusUv = 0.025f;
            sunConeRadiusUv = MinShaftRadiusUv * coneScale;
            sunCosDiscEdge = 0.999f;
            return false;
        }

        var worldUp = Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(worldUp, towardSun));
        if (right.LengthSquared() < 1e-10f)
        {
            right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, towardSun));
        }

        var edgeDir = towardSun * SunDistance + right * (SunRadius * sizeScale);
        var edgeLen2 = edgeDir.LengthSquared();
        sunCosDiscEdge = edgeLen2 < 1e-12f
            ? 0.999f
            : Math.Clamp(Vector3.Dot(towardSun, edgeDir / MathF.Sqrt(edgeLen2)), 0.85f, 0.999999f);

        // FOV-stable disc radius — projecting the disc edge to UV explodes when the sun is near
        // the frustum limb (high sun while looking at the horizon).
        sunDiscRadiusUv = AngularRadiusToUv(proj, SunRadius * sizeScale / SunDistance);
        sunConeRadiusUv = EnsureConeReachesViewport(
            sunUv,
            ClampConeRadius(
                Math.Max(sunDiscRadiusUv * ShaftScale * coneScale, MinShaftRadiusUv * coneScale),
                coneScale),
            viewportAspect,
            coneScale);
        return true;
    }

    /// <summary>Projects the antipodal moon disc (opposite the sun light propagation direction).</summary>
    public static void ComputeMoon(
        Vector3 eye,
        Vector3 lightPropagationDir,
        Matrix4x4 view,
        Matrix4x4 proj,
        float viewportAspect,
        out Vector2 moonUv,
        out float moonDiscRadiusUv,
        out float moonCosDiscEdge) =>
        _ = TryComputeMoon(
            eye,
            lightPropagationDir,
            view,
            proj,
            viewportAspect,
            out moonUv,
            out moonDiscRadiusUv,
            out moonCosDiscEdge);

    /// <summary>
    /// Like <see cref="ComputeMoon"/> but returns false when the moon is behind the camera.
    /// </summary>
    public static bool TryComputeMoon(
        Vector3 eye,
        Vector3 lightPropagationDir,
        Matrix4x4 view,
        Matrix4x4 proj,
        float viewportAspect,
        out Vector2 moonUv,
        out float moonDiscRadiusUv,
        out float moonCosDiscEdge)
    {
        _ = eye;
        _ = viewportAspect;
        var towardMoon = lightPropagationDir;
        var tlm = towardMoon.LengthSquared();
        if (tlm < 1e-12f)
        {
            moonUv = new Vector2(0.5f, 0.5f);
            moonDiscRadiusUv = 0.012f;
            moonCosDiscEdge = 0.9995f;
            return false;
        }

        towardMoon /= MathF.Sqrt(tlm);
        if (!TryProjectWorldDirectionToUv(towardMoon, view, proj, out moonUv))
        {
            moonDiscRadiusUv = 0.012f;
            moonCosDiscEdge = 0.9995f;
            return false;
        }

        var worldUp = Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(worldUp, towardMoon));
        if (right.LengthSquared() < 1e-10f)
        {
            right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, towardMoon));
        }

        var edgeDir = towardMoon * SunDistance + right * MoonRadius;
        var edgeLen2 = edgeDir.LengthSquared();
        moonCosDiscEdge = edgeLen2 < 1e-12f
            ? 0.9995f
            : Math.Clamp(Vector3.Dot(towardMoon, edgeDir / MathF.Sqrt(edgeLen2)), 0.92f, 0.99998f);

        moonDiscRadiusUv = AngularRadiusToUv(proj, MoonRadius / SunDistance);
        return true;
    }

    /// <summary>
    /// Projects a unit world-space direction to viewport UV as a point at infinity.
    /// Uses the camera basis embedded in <paramref name="view"/> with OpenGL perspective
    /// divide — not <see cref="Vector3.TransformNormal"/> + <c>proj</c>, which disagrees with
    /// these transpose-on-upload matrices and folds out-of-frustum directions onto the screen
    /// (god-ray shafts aiming at the wrong place).
    /// Returns false when the direction is beside/behind the camera.
    /// </summary>
    public static bool TryProjectWorldDirectionToUv(
        Vector3 worldDirUnit,
        Matrix4x4 view,
        Matrix4x4 proj,
        out Vector2 uv)
    {
        // CreateLookAtRhOpenGlRowStorage stores rows (right, up, -forward, ...).
        var right = new Vector3(view.M11, view.M12, view.M13);
        var up = new Vector3(view.M21, view.M22, view.M23);
        var negForward = new Vector3(view.M31, view.M32, view.M33);
        var viewX = Vector3.Dot(worldDirUnit, right);
        var viewY = Vector3.Dot(worldDirUnit, up);
        var viewZ = Vector3.Dot(worldDirUnit, negForward);
        // OpenGL view space: camera looks down -Z, so in-front directions have Z &lt; 0.
        if (viewZ >= -1e-5f)
        {
            uv = default;
            return false;
        }

        var invNegZ = 1f / -viewZ;
        // proj.M11 = 1/(aspect*tanHalfFovY), proj.M22 = 1/tanHalfFovY for PreviewGlMatrices.
        var ndcX = proj.M11 * viewX * invNegZ;
        var ndcY = proj.M22 * viewY * invNegZ;
        uv = new Vector2(ndcX * 0.5f + 0.5f, ndcY * 0.5f + 0.5f);
        return true;
    }

    /// <summary>
    /// Maps a small angular radius (radians approx via tan) to aspect-corrected UV radius using
    /// the perspective <paramref name="proj"/> Y focal length (<c>M22 = 1/tan(fovY/2)</c>).
    /// </summary>
    public static float AngularRadiusToUv(Matrix4x4 proj, float sinOrTanAngularApprox)
    {
        var tanHalfFovY = 1f / Math.Max(MathF.Abs(proj.M22), 1e-5f);
        var tanAngular = Math.Clamp(sinOrTanAngularApprox, 1e-6f, 0.45f);
        // NDC radius = tan(θ) / tan(fovY/2); UV radius is half of that.
        var discRadiusUv = 0.5f * (tanAngular / tanHalfFovY) * DiscUvMargin;
        return Math.Max(discRadiusUv, 0.004f);
    }

    public static float ClampConeRadius(float coneRadiusUv, float coneScale) =>
        Math.Min(coneRadiusUv, MaxShaftRadiusUv * Math.Max(coneScale, 1f));

    /// <summary>
    /// When the disc sits off-screen, a FOV-stable cone can fall short of the frame edge and
    /// kill all shafts. Expand just enough to keep a band of on-screen receivers toward the disc.
    /// </summary>
    public static float EnsureConeReachesViewport(
        Vector2 sunUv,
        float coneRadiusUv,
        float viewportAspect,
        float coneScale)
    {
        var nearestOnScreen = new Vector2(
            Math.Clamp(sunUv.X, 0f, 1f),
            Math.Clamp(sunUv.Y, 0f, 1f));
        var reach = AspectCorrectedUvDistance(sunUv, nearestOnScreen, viewportAspect);
        var edgeBand = MinShaftRadiusUv * Math.Max(coneScale, 1f);
        // Do not re-apply MaxShaftRadiusUv here — reach can exceed it when the disc is far
        // off-frame; clamping again would recreate the empty-cone bug.
        return Math.Max(coneRadiusUv, reach + edgeBand);
    }

    public static float AspectCorrectedUvDistance(Vector2 a, Vector2 b, float viewportAspect)
    {
        var aspect = Math.Max(viewportAspect, 1e-4f);
        var dx = (a.X - b.X) * aspect;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static Vector2 WorldToViewportUv(Vector3 worldPos, Matrix4x4 viewProjRow)
    {
        TryWorldToViewportUv(worldPos, viewProjRow, out var uv);
        return uv;
    }

    public static bool TryWorldToViewportUv(Vector3 worldPos, Matrix4x4 viewProjRow, out Vector2 uv)
    {
        var clip = Vector4.Transform(new Vector4(worldPos, 1f), viewProjRow);
        if (clip.W <= 1e-6f)
        {
            uv = new Vector2(0.5f, 0.5f);
            return false;
        }

        var invW = 1f / clip.W;
        var ndc = new Vector2(clip.X * invW, clip.Y * invW);
        uv = new Vector2(ndc.X * 0.5f + 0.5f, ndc.Y * 0.5f + 0.5f);
        return true;
    }
}
