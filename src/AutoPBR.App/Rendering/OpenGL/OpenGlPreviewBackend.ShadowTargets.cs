using AutoPBR.App.Rendering.Abstractions;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    internal const int ShadowLodNearResolution = 4096;
    internal const int ShadowLodMidResolution = 2048;
    internal const int ShadowLodFarResolution = 1024;
    internal const int ShadowLodSingleResolution = 2048;

    internal const float ShadowDistanceMin = 32f;
    internal const float ShadowDistanceMax = 256f;
    internal const float ShadowDistanceDefault = 128f;
    internal const float ShadowDistanceFadeFraction = 0.85f;

    /// <summary>Near band ends at this fraction of ShadowDistance.</summary>
    internal const float ShadowCascadeNearFraction = 0.15f;

    /// <summary>Mid band ends at this fraction of ShadowDistance.</summary>
    internal const float ShadowCascadeMidFraction = 0.45f;

    /// <summary>Must run with a current GL context (bootstrap / render thread).</summary>
    private void EnsureShadowMapTargets(GL gl, in PreviewRenderSettingsSnapshot settings)
    {
        _shadowTargetsDirty = false;
        if (!settings.EnableShadows)
        {
            DisposeShadowMapTargets();
            return;
        }

        var wantCascades = settings.EnableShadowCascades;
        var nearRes = wantCascades
            ? Math.Clamp(settings.ShadowMapResolution > 0 ? settings.ShadowMapResolution : ShadowLodNearResolution, 256, 4096)
            : ShadowLodSingleResolution;
        if (wantCascades)
        {
            nearRes = ShadowLodNearResolution;
        }

        var midRes = ShadowLodMidResolution;
        var farRes = wantCascades ? ShadowLodFarResolution : ShadowLodSingleResolution;

        var needsRebuild =
            _shadowTarget is null ||
            _shadowTargetsWantCascades != wantCascades ||
            _shadowTargetsFarRes != farRes ||
            (wantCascades && (_shadowTargetCascadeNear is null || _shadowTargetsNearRes != nearRes)) ||
            (wantCascades && (_shadowTargetCascadeMid is null || _shadowTargetsMidRes != midRes)) ||
            (!wantCascades && _shadowTargetCascadeNear is not null);

        if (!needsRebuild)
        {
            return;
        }

        DisposeShadowMapTargets();
        try
        {
            _shadowTarget = new GlShadowMapTarget(gl, farRes, _useOpenGlEs);
            _shadowTargetsFarRes = farRes;
            _shadowTargetsWantCascades = wantCascades;
            if (wantCascades)
            {
                _shadowTargetCascadeNear = new GlShadowMapTarget(gl, nearRes, _useOpenGlEs);
                _shadowTargetCascadeMid = new GlShadowMapTarget(gl, midRes, _useOpenGlEs);
                _shadowTargetsNearRes = nearRes;
                _shadowTargetsMidRes = midRes;
                EmitDiagnostic(
                    $"[3D preview] Shadow maps: near {nearRes}, mid {midRes}, far {farRes}");
            }
            else
            {
                EmitDiagnostic($"[3D preview] Shadow map: {farRes}x{farRes} (single)");
            }
        }
        catch (Exception ex)
        {
            DisposeShadowMapTargets();
            EmitDiagnostic("[3D preview] Shadow target init failed: " + ex.Message);
        }
    }

    private void DisposeShadowMapTargets()
    {
        _shadowTarget?.Dispose();
        _shadowTarget = null;
        _shadowTargetCascadeNear?.Dispose();
        _shadowTargetCascadeNear = null;
        _shadowTargetCascadeMid?.Dispose();
        _shadowTargetCascadeMid = null;
        _shadowTargetsNearRes = 0;
        _shadowTargetsMidRes = 0;
        _shadowTargetsFarRes = 0;
        _shadowTargetsWantCascades = false;
    }
}
