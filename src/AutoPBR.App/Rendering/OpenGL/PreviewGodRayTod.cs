using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Day / dusk / night appearance for screen-space god rays derived from sun elevation.
/// Avoids a full duplicate settings tree while still transitioning shaft strength and tint.
/// </summary>
internal readonly record struct PreviewGodRayTodAppearance(
    float StrengthScale,
    Vector3 ScatterTint,
    float SkyWashFloor,
    float TerrainShaftScale,
    float EnergyKnee,
    bool IsMoon);

internal static class PreviewGodRayTod
{
    /// <summary>Warm daylight shaft scatter.</summary>
    public static readonly Vector3 DayTint = new(1.00f, 0.94f, 0.82f);

    /// <summary>Low-sun warm rim for dusk/dawn.</summary>
    public static readonly Vector3 DuskTint = new(1.00f, 0.72f, 0.48f);

    /// <summary>
    /// Cinematic moonlight — steely blue/cyan (Purkinje / film convention), not candy-blue.
    /// Real moonlight is nearer neutral; blue signals night to viewers.
    /// </summary>
    public static readonly Vector3 NightTint = new(0.58f, 0.76f, 1.00f);

    public const float MoonHorizonBandY = 0.04f;

    public static PreviewGodRayTodAppearance Evaluate(Vector3 worldLightDir)
    {
        var towardSun = -worldLightDir;
        var len2 = towardSun.LengthSquared();
        if (len2 < 1e-12f)
        {
            return new PreviewGodRayTodAppearance(
                StrengthScale: 0.4f,
                ScatterTint: NightTint,
                SkyWashFloor: 0.5f,
                TerrainShaftScale: 0.32f,
                EnergyKnee: 1.15f,
                IsMoon: true);
        }

        towardSun /= MathF.Sqrt(len2);
        var isMoon = towardSun.Y < MoonHorizonBandY;
        var elev = towardSun.Y;

        // Soft day/dusk/night weights from solar elevation (moon path forces night).
        var day = Smoothstep(-0.05f, 0.28f, elev);
        var night = 1f - Smoothstep(-0.18f, 0.06f, elev);
        var dusk = Smoothstep(-0.12f, 0.0f, elev) * (1f - Smoothstep(0.0f, 0.22f, elev));
        if (isMoon)
        {
            night = Math.Max(night, 0.92f);
            day = Math.Min(day, 0.08f);
            dusk = Math.Min(dusk, 0.18f);
        }

        var wSum = Math.Max(day + night + dusk, 1e-5f);
        day /= wSum;
        night /= wSum;
        dusk /= wSum;

        // Night: weaker overall; terrain shafts especially thinned (dark ground washes easily).
        // Sky wash floor rises at night so shafts can continue over sky/clouds.
        var strengthScale = day * 1.00f + dusk * 0.72f + night * 0.38f;
        var tint = day * DayTint + dusk * DuskTint + night * NightTint;
        var skyWashFloor = day * 0.02f + dusk * 0.14f + night * 0.52f;
        var terrainShaftScale = day * 0.72f + dusk * 0.48f + night * 0.28f;
        var energyKnee = day * 0.85f + dusk * 0.95f + night * 1.25f;

        return new PreviewGodRayTodAppearance(
            StrengthScale: strengthScale,
            ScatterTint: tint,
            SkyWashFloor: skyWashFloor,
            TerrainShaftScale: terrainShaftScale,
            EnergyKnee: energyKnee,
            IsMoon: isMoon);
    }

    /// <summary>
    /// Scene light × TOD shaft tint for froxel inject/integrate (terrain fog + volume shafts).
    /// </summary>
    public static Vector3 ResolveVolumeShaftLightColor(Vector3 sceneLightColor, Vector3 worldLightDir)
    {
        var tod = Evaluate(worldLightDir);
        var baseColor = sceneLightColor.LengthSquared() < 1e-10f ? Vector3.One : sceneLightColor;
        return baseColor * tod.ScatterTint;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / Math.Max(edge1 - edge0, 1e-6f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
