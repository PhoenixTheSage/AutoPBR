using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewGodRayTodTests
{
    [Fact]
    public void Evaluate_Noon_IsDayWarmAndTerrainAllowed()
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(12.0);
        var light = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        var tod = PreviewGodRayTod.Evaluate(light);

        Assert.False(tod.IsMoon);
        Assert.True(tod.StrengthScale > 0.85f);
        Assert.True(tod.TerrainShaftScale > 0.55f);
        Assert.True(tod.SkyWashFloor < 0.12f);
        Assert.True(tod.ScatterTint.X >= tod.ScatterTint.Z);
    }

    [Fact]
    public void Evaluate_Midnight_IsMoonCoolThinTerrainAndSkyAllowed()
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(0.0);
        var light = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        var tod = PreviewGodRayTod.Evaluate(light);

        Assert.True(tod.IsMoon);
        Assert.True(tod.StrengthScale < 0.55f);
        Assert.True(tod.TerrainShaftScale < 0.4f);
        Assert.True(tod.SkyWashFloor > 0.35f);
        Assert.True(tod.ScatterTint.Z > tod.ScatterTint.X);
        Assert.True(tod.EnergyKnee > 1.0f);
    }

    [Fact]
    public void Evaluate_StrengthScale_IsMonotoneFromNightTowardDay()
    {
        var (ny, np) = PreviewLightMath.LightYawPitchFromTimeOfDay(0.0);
        var (dy, dp) = PreviewLightMath.LightYawPitchFromTimeOfDay(12.0);
        var midnight = PreviewGodRayTod.Evaluate(PreviewLightMath.LightDirectionFromYawPitch(ny, np));
        var noon = PreviewGodRayTod.Evaluate(PreviewLightMath.LightDirectionFromYawPitch(dy, dp));

        Assert.True(noon.StrengthScale > midnight.StrengthScale);
        Assert.True(noon.TerrainShaftScale > midnight.TerrainShaftScale);
        Assert.True(noon.SkyWashFloor < midnight.SkyWashFloor);
    }

    [Fact]
    public void ResolveVolumeShaftLightColor_Noon_IsWarm()
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(12.0);
        var light = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        var color = PreviewGodRayTod.ResolveVolumeShaftLightColor(Vector3.One, light);
        Assert.True(color.X >= color.Z);
    }

    [Fact]
    public void ResolveVolumeShaftLightColor_Midnight_IsCool()
    {
        var (yaw, pitch) = PreviewLightMath.LightYawPitchFromTimeOfDay(0.0);
        var light = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        var color = PreviewGodRayTod.ResolveVolumeShaftLightColor(Vector3.One, light);
        Assert.True(color.Z > color.X);
    }
}
