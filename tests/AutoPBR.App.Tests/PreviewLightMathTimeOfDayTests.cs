using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewLightMathTimeOfDayTests
{
    [Theory]
    [InlineData(6, 0.0)]
    [InlineData(12, -58.0, 0.5)]
    [InlineData(18, 0.0)]
    [InlineData(0, 58.0, 0.5)]
    public void LightYawPitchFromTimeOfDay_MatchesExpectedSolarElevation(double hours, double expectedPitch, double epsilon = 0.75)
    {
        var (_, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(hours);
        Assert.Equal(expectedPitch, pitch, epsilon);
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(12.0)]
    [InlineData(18.0)]
    [InlineData(0.0)]
    public void TimeOfDayFromLightYawPitch_RoundtripsKeyHours(double hours)
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(hours);
        var roundTrip = PreviewLightMath.TimeOfDayFromLightYawPitch(yaw, pitch, hours);
        Assert.Equal(hours, roundTrip, 0.75);
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(12.0)]
    [InlineData(18.0)]
    [InlineData(0.0)]
    public void TimeOfDay_ToYawPitch_ProducesUnitLightDirection(double hours)
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(hours);
        var dir = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        Assert.InRange(dir.Length(), 0.999f, 1.001f);
    }

    [Fact]
    public void TimeOfDay_NoonLightPointsDownward()
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(12.0);
        var dir = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        Assert.True(dir.Y < 0f);
        _ = yaw;
        _ = pitch;
    }

    [Fact]
    public void TimeOfDay_MidnightLightPointsUpward()
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(0.0);
        var dir = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        Assert.True(dir.Y > 0f);
        _ = yaw;
        _ = pitch;
    }

    [Fact]
    public void SceneLightDirection_UsesSunByDayAndMoonByNight()
    {
        var (noonYaw, noonPitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(12.0);
        var noonCycleDir = PreviewLightMath.LightDirectionFromYawPitch(noonYaw, noonPitch);
        var noonSceneDir = PreviewLightMath.SceneLightDirectionFromCelestialCycle(noonCycleDir);
        Assert.Equal(noonCycleDir, noonSceneDir);
        Assert.True((-noonSceneDir).Y > 0f);

        var (midnightYaw, midnightPitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(0.0);
        var midnightCycleDir = PreviewLightMath.LightDirectionFromYawPitch(midnightYaw, midnightPitch);
        var midnightSceneDir = PreviewLightMath.SceneLightDirectionFromCelestialCycle(midnightCycleDir);
        Assert.Equal(-midnightCycleDir, midnightSceneDir);
        Assert.True((-midnightSceneDir).Y > 0f);
    }

    [Fact]
    public void SceneLightColor_DimsAndCoolsMoonlight()
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(0.0);
        var cycleDir = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        var moonColor = PreviewLightMath.SceneLightColorFromCelestialCycle(cycleDir, new(1f, 1f, 1f));

        Assert.InRange(moonColor.X, 0.02f, 0.12f);
        Assert.True(moonColor.Z > moonColor.X);
    }

    [Fact]
    public void SceneLightColor_MoonWorldLightIntensityScalesOnlyMoonlight()
    {
        var (midnightYaw, midnightPitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(0.0);
        var midnightDir = PreviewLightMath.LightDirectionFromYawPitch(midnightYaw, midnightPitch);
        var baseMoon = PreviewLightMath.SceneLightColorFromCelestialCycle(midnightDir, new(1f, 1f, 1f), 1f);
        var boostedMoon = PreviewLightMath.SceneLightColorFromCelestialCycle(midnightDir, new(1f, 1f, 1f), 3f);
        Assert.True(boostedMoon.X > baseMoon.X * 2.9f);

        var (noonYaw, noonPitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(12.0);
        var noonDir = PreviewLightMath.LightDirectionFromYawPitch(noonYaw, noonPitch);
        var dayColor = PreviewLightMath.SceneLightColorFromCelestialCycle(noonDir, new(1f, 0.9f, 0.8f), 8f);
        Assert.Equal(new(1f, 0.9f, 0.8f), dayColor);
    }

    [Fact]
    public void SceneLightColor_BlendsAcrossSunriseWithoutHardSnap()
    {
        var sun = new System.Numerics.Vector3(1f, 0.9f, 0.8f);
        float LumaAt(double hours)
        {
            var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(hours);
            var dir = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
            var c = PreviewLightMath.SceneLightColorFromCelestialCycle(dir, sun);
            return 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        }

        var before = LumaAt(5.85);
        var at = LumaAt(6.00);
        var after = LumaAt(6.15);
        var sunLuma = 0.2126f * sun.X + 0.7152f * sun.Y + 0.0722f * sun.Z;
        // Hard switch used to jump moonlight (~0.05) to full sun (~0.9) in one sample.
        Assert.InRange(at / Math.Max(before, 1e-4f), 0.75f, 1.6f);
        Assert.InRange(after / Math.Max(at, 1e-4f), 0.75f, 1.6f);
        Assert.True(after > before);
        Assert.True(at > before * 0.9f);
        Assert.True(at < sunLuma * 0.95f);
    }

    [Fact]
    public void EffectiveTimeOfDayHours_AdvancesWithRenderTime()
    {
        var settings = new PreviewRenderSettings
        {
            TimeOfDayHours = 12f,
            AnimateTimeOfDay = true,
            TimeOfDaySpeed = 2f
        };

        Assert.Equal(14f, PreviewLightMath.EffectiveTimeOfDayHours(PreviewRenderSettingsSnapshot.From(settings), 1.0), 0.01f);
    }

    [Fact]
    public void EffectiveLightYawPitch_UsesStaticYawPitchWhenAnimationOff()
    {
        var settings = new PreviewRenderSettings
        {
            LightYawDegrees = -20f,
            LightPitchDegrees = -40f,
            AnimateTimeOfDay = false
        };

        var (yaw, pitch) = PreviewLightMath.EffectiveLightYawPitch(PreviewRenderSettingsSnapshot.From(settings), 99.0);
        Assert.Equal(-20.0, yaw);
        Assert.Equal(-40.0, pitch);
    }
}
