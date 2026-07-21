using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>Idle / no-subject preview stage so sky, ground terrain, and grid still render.</summary>
public static class PreviewStageSceneFactory
{
    public static PreviewScene CreateIdle(PreviewRenderSettings settings) =>
        CreateIdle(settings.LightYawDegrees, settings.LightPitchDegrees);

    public static PreviewScene CreateIdle(PreviewRenderSettingsSnapshot settings) =>
        CreateIdle(settings.LightYawDegrees, settings.LightPitchDegrees);

    public static PreviewScene CreateIdle(float lightYawDegrees, float lightPitchDegrees)
    {
        var lightDir = BlockPreviewSceneFactory.LightDirectionFromYawPitch(lightYawDegrees, lightPitchDegrees);
        return new PreviewScene(
            PreviewSceneKind.BlockCube,
            [PreviewMeshFactory.CreateEmptySubjectPlaceholder("idle_stage")],
            new PreviewCamera(),
            new PreviewLight { Direction = lightDir });
    }
}
