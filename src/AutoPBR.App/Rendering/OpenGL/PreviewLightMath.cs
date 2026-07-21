using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Helpers for converting Genesis Shadows Phase 2 light controls (yaw/pitch in degrees)
/// into world-space directions and view matrices used by the directional shadow pass.
///
/// Convention:
///  - Yaw rotates around world +Y; yaw = 0 means the light propagates along world +Z.
///  - Pitch tilts vertically; pitch &lt; 0 means the sun is above the horizon (light shines downward),
///    matching the prior <c>uLightDir = (-0.35, -0.85, -0.4)</c> default.
///  - The returned vector is the direction the light propagates (away from the sun); the
///    fragment shader negates it to get the incoming light direction L.
/// </summary>
internal static class PreviewLightMath
{
    /// <summary>
    /// Converts yaw/pitch (degrees) into a unit-length world-space light propagation direction.
    /// Round-trips with <see cref="LightYawPitchFromDirection"/> for any non-degenerate input.
    /// </summary>
    public static Vector3 LightDirectionFromYawPitch(double yawDegrees, double pitchDegrees)
    {
        var yaw = (float)(yawDegrees * (Math.PI / 180.0));
        var pitch = (float)(pitchDegrees * (Math.PI / 180.0));
        var cp = MathF.Cos(pitch);
        var sp = MathF.Sin(pitch);
        var sy = MathF.Sin(yaw);
        var cy = MathF.Cos(yaw);
        var dir = new Vector3(cp * sy, sp, cp * cy);
        var len = dir.Length();
        return len > 1e-6f ? dir / len : new Vector3(0f, -1f, 0f);
    }

    /// <summary>
    /// Inverse of <see cref="LightDirectionFromYawPitch"/>. Useful for tests confirming the
    /// representation is loss-free for any unit vector that isn't a pole.
    /// </summary>
    public static (double YawDegrees, double PitchDegrees) LightYawPitchFromDirection(Vector3 lightDir)
    {
        var d = lightDir.LengthSquared();
        if (d < 1e-12f)
        {
            return (0.0, 0.0);
        }

        var n = lightDir / MathF.Sqrt(d);
        var pitch = MathF.Asin(Math.Clamp(n.Y, -1f, 1f));
        var yaw = MathF.Atan2(n.X, n.Z);
        return (yaw * (180.0 / Math.PI), pitch * (180.0 / Math.PI));
    }

    /// <summary>
    /// Picks a stable up vector for the light view matrix; falls back from world +Y to world +Z
    /// when the light points straight up or down (avoiding a degenerate cross product).
    /// </summary>
    public static Vector3 PickShadowViewUp(Vector3 lightDir)
    {
        var ay = Math.Abs(lightDir.Y);
        return ay > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
    }

    /// <summary>
    /// Converts the sun/moon cycle direction into the visible direct-light source.
    /// During the day this is sunlight; once the sun is below the horizon it becomes
    /// reflected moonlight from the antipodal moon.
    /// </summary>
    public static Vector3 SceneLightDirectionFromCelestialCycle(Vector3 celestialLightDir)
    {
        var len2 = celestialLightDir.LengthSquared();
        if (len2 < 1e-12f)
        {
            return new Vector3(0f, -1f, 0f);
        }

        var dir = celestialLightDir / MathF.Sqrt(len2);
        return dir.Y > 0f ? -dir : dir;
    }

    /// <summary>
    /// Cool, dim reflected moonlight when the moon is the visible source.
    /// Blends across a small horizon band so sunrise/sunset do not hard-switch full sun vs moon
    /// (aerial fog and other luminance gates used to snap dark↔bright at 06:00 / 18:00).
    /// </summary>
    public static Vector3 SceneLightColorFromCelestialCycle(
        Vector3 celestialLightDir,
        Vector3 sunColor,
        float moonWorldLightIntensity = 1f)
    {
        var len2 = celestialLightDir.LengthSquared();
        if (len2 < 1e-12f)
        {
            return sunColor;
        }

        var dir = celestialLightDir / MathF.Sqrt(len2);
        // Celestial light Y negative => sun above horizon. Blend ~±20° (~80 min) around the limb
        // so fog/light luminance gates fade instead of snapping at 06:00 / 18:00.
        var dayAmt = 1f - Smoothstep(-0.36f, 0.36f, dir.Y);

        // Moon endpoint stays defined on the day side (elevation clamped to 0) so the lerp is continuous.
        var moonElevation = Math.Clamp(MathF.Max(dir.Y, 0f), 0f, 1f);
        var reflectedStrength = (0.05f + 0.13f * MathF.Pow(moonElevation, 0.65f)) *
                                Math.Clamp(moonWorldLightIntensity, 0f, 8f);
        var moonTint = new Vector3(0.58f, 0.66f, 0.86f);
        var moonColor = sunColor * moonTint * reflectedStrength;
        return Vector3.Lerp(moonColor, sunColor, dayAmt);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Maps clock time (0–24 h) to sun yaw/pitch. 6:00 ≈ sunrise, 12:00 ≈ solar noon, 18:00 ≈ sunset, 0:00/24:00 ≈ midnight.
    /// </summary>
    public static (double YawDegrees, double PitchDegrees) LightYawPitchFromTimeOfDay(
        double hours,
        double maxSunElevationDegrees = 58.0)
    {
        hours = NormalizeHours(hours);
        var phase = (hours - 6.0) / 24.0 * Math.PI * 2.0;
        var sunElevation = maxSunElevationDegrees * Math.Sin(phase);
        var pitch = Math.Clamp(-sunElevation, -89.0, 89.0);
        var yaw = (hours - 6.0) / 24.0 * 360.0;
        if (yaw > 180.0)
        {
            yaw -= 360.0;
        }
        else if (yaw < -180.0)
        {
            yaw += 360.0;
        }

        return (yaw, pitch);
    }

    /// <summary>
    /// Approximate inverse of <see cref="LightYawPitchFromTimeOfDay"/> for UI sync when yaw/pitch are edited manually.
    /// </summary>
    public static double TimeOfDayFromLightYawPitch(
        double yawDegrees,
        double pitchDegrees,
        double referenceHours = 12.0,
        double maxSunElevationDegrees = 58.0)
    {
        var hourFromYaw = NormalizeHours(6.0 + yawDegrees / 360.0 * 24.0);
        var sunElev = Math.Clamp(-pitchDegrees, -maxSunElevationDegrees, maxSunElevationDegrees);
        var phase = Math.Asin(sunElev / maxSunElevationDegrees);
        var hourFromPitchA = NormalizeHours(6.0 + phase / (Math.PI * 2.0) * 24.0);
        var hourFromPitchB = NormalizeHours(18.0 - phase / (Math.PI * 2.0) * 24.0);
        var hourFromPitch = HoursDistance(hourFromPitchA, referenceHours) <= HoursDistance(hourFromPitchB, referenceHours)
            ? hourFromPitchA
            : hourFromPitchB;
        return NormalizeHours((hourFromYaw + hourFromPitch) * 0.5);
    }

    private static double NormalizeHours(double hours)
    {
        hours %= 24.0;
        if (hours < 0.0)
        {
            hours += 24.0;
        }

        return hours;
    }

    private static double HoursDistance(double a, double b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, 24.0 - d);
    }

    /// <summary>Clock hours for rendering, advancing when <see cref="Abstractions.PreviewRenderSettings.AnimateTimeOfDay"/> is on.</summary>
    public static float EffectiveTimeOfDayHours(in Abstractions.PreviewRenderSettingsSnapshot settings, double renderTime)
    {
        if (!settings.AnimateTimeOfDay)
        {
            return settings.TimeOfDayHours;
        }

        var hours = settings.TimeOfDayHours + renderTime * settings.TimeOfDaySpeed;
        return (float)NormalizeHours(hours);
    }

    /// <summary>Light yaw/pitch for shadow and sky passes; respects animated time-of-day.</summary>
    public static (double YawDegrees, double PitchDegrees) EffectiveLightYawPitch(
        in Abstractions.PreviewRenderSettingsSnapshot settings,
        double renderTime)
    {
        if (!settings.AnimateTimeOfDay)
        {
            return (settings.LightYawDegrees, settings.LightPitchDegrees);
        }

        return LightYawPitchFromTimeOfDay(EffectiveTimeOfDayHours(settings, renderTime));
    }
}
