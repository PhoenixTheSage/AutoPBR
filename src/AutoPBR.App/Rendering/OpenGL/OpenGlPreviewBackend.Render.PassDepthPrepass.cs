using System.Numerics;

using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    /// <summary>
    /// Hi-Z prepass renders at 1/N of the scene resolution. Half-res is enough for conservative
    /// sphere occlusion and cuts fill + pyramid build cost ~4× vs full scene resolution.
    /// </summary>
    private const int HiZPrepassResolutionDivisor = 2;

    /// <summary>
    /// Opaque terrain depth prepass into a sampleable (often half-res) depth target, then Hi-Z pyramid.
    /// Skipped when the voxel DDA atlas is valid (DDA is primary; do not run Hi-Z alongside it).
    /// Depth is kept only for Hi-Z sampling — it is not blitted into the scene FBO.
    /// Subject and cutout/foliage are omitted: hills/cliffs are the useful occluders; redrawing the
    /// full opaque+cutout scene at camera resolution was the dominant Depth Prepass cost.
    /// </summary>
    private bool TryRunDepthPrepassAndHiZ(
        ref GlRenderFrame frame,
        int sceneVpX,
        int sceneVpY,
        int sceneVpW,
        int sceneVpH,
        int restoreDrawFbo)
    {
        frame.HiZReady = false;
        _hiZReadyThisFrame = false;
        if (!ShouldRunHiZPrepassThisFrame(frame.Settings))
        {
            return false;
        }

        var prepassW = Math.Max(1, sceneVpW / HiZPrepassResolutionDivisor);
        var prepassH = Math.Max(1, sceneVpH / HiZPrepassResolutionDivisor);
        if (!TryEnsureHierarchicalZResources(prepassW, prepassH))
        {
            return false;
        }

        using (BeginPassTimerScope(GlGpuTimerScope.DepthPrepass))
        {
            DrawOpaqueDepthPrepass(ref frame, sceneVpX, sceneVpY, sceneVpW, sceneVpH, restoreDrawFbo);
        }

        using (BeginPassTimerScope(GlGpuTimerScope.HiZ))
        {
            if (!TryBuildHierarchicalZFromPrepass())
            {
                return false;
            }
        }

        frame.HiZReady = true;
        _hiZReadyThisFrame = true;
        if (!_loggedHiZOcclusionEnabled)
        {
            _loggedHiZOcclusionEnabled = true;
            EmitDiagnostic(
                "[3D preview] P5.3 Hi-Z occlusion culling enabled: opaque-terrain half-res depth prepass + max-depth pyramid; " +
                "Hi-Z culls shaded subject batches when voxel DDA atlas is unavailable; " +
                "prepass omits subject/cutout and is not blitted (avoids early-Z holes); GLES/ANGLE and alpha stay frustum/LOD only.");
        }

        return true;
    }

    private void DrawOpaqueDepthPrepass(
        ref GlRenderFrame frame,
        int sceneVpX,
        int sceneVpY,
        int sceneVpW,
        int sceneVpH,
        int restoreDrawFbo)
    {
        if (_shadowProgram is not { IsValid: true } || _depthPrepassTarget is null)
        {
            return;
        }

        try
        {
            _depthPrepassTarget.BeginPrepass(restoreDrawFbo, sceneVpX, sceneVpY, sceneVpW, sceneVpH);
            BeginDepthPrepassCasterState(ref frame);
            // Must match shaded pass projection (including TAA jitter) or Hi-Z tests disagree with draws.
            SetMatrixOnProgramLoc(_shadowProgram, _shadowUniformLocs.LightViewProj, frame.RasterViewProj);

            // Opaque terrain only seeds Hi-Z so hills/cliffs occlude geometry behind them.
            // Shaded terrain is still frustum-drawn (not Hi-Z-filtered) so visible ground never vanishes.
            // Cutout/foliage and the preview subject are skipped — they cost many draws and rarely
            // fully cover batch spheres used by the compact Hi-Z test.
            if (frame.Settings.ShowGroundMesh &&
                _grassGroundReady &&
                _grassGroundAlbedo is not null &&
                HasTerrainChunksToDraw)
            {
                EnsureTerrainDrawCandidates();
                TerrainChunkDrawCull.Select(
                    _terrainDrawCandidates,
                    frame.CameraViewProj,
                    frame.Eye,
                    PreviewStageConstants.TerrainFrustumDrawFallbackCount,
                    fullOnly: false,
                    _terrainDrawSelected,
                    maxCasterDistanceXz: 0f);

                // Terrain casters keep both faces (matches prior prepass); no glIsEnabled sync.
                frame.Gl.Disable(EnableCap.CullFace);
                var su = _shadowUniformLocs;
                SetIntOnProgramLoc(_shadowProgram, su.SceneKind, 0);
                SetIntOnProgramLoc(_shadowProgram, su.EntityAlphaMode, 0);
                SetFloatOnProgramLoc(_shadowProgram, su.AlphaCutoff, frame.Settings.AlphaCutoff);
                SetIntOnProgramLoc(_shadowProgram, su.GenesisUseMaterialDrawRecord, 0);
                SetIntOnProgramLoc(_shadowProgram, su.GenesisUseMaterialTextureArray, 0);
                // Opaque batches need no albedo sample (shadow frag only discards on cutout/entity alpha).
                SetIntOnProgramLoc(_shadowProgram, su.Albedo, 0);
                BindFallbackShadowMaterialTextureArrayIfPresent(su);
                DrawTerrainCandidates(
                    _terrainDrawCandidates,
                    _terrainDrawChunkScratch,
                    _terrainDrawSelected,
                    patches: false,
                    enableParallaxSetting: false,
                    setParallaxEnabled: static _ => { },
                    shadowPass: true,
                    cameraPosition: frame.Eye,
                    opaqueOnly: true);
            }
        }
        finally
        {
            _depthPrepassTarget.EndPrepass();
        }
    }

    private void BeginDepthPrepassCasterState(ref GlRenderFrame frame)
    {
        frame.Gl.Enable(EnableCap.DepthTest);
        frame.Gl.DepthFunc(GLEnum.Lequal);
        frame.Gl.DepthMask(true);
        if (ShouldCullSolidBackFaces(frame.Scene.SceneKind, frame.BlockModel, frame.Settings))
        {
            frame.Gl.Enable(EnableCap.CullFace);
            frame.Gl.CullFace(GLEnum.Back);
            frame.Gl.FrontFace(GLEnum.Ccw);
        }
        else
        {
            frame.Gl.Disable(EnableCap.CullFace);
        }

        _shadowProgram!.Use();
        var su = _shadowUniformLocs;
        SetMatrixOnProgramLoc(_shadowProgram, su.Model, Matrix4x4.Identity);
        SetIntOnProgramLoc(_shadowProgram, su.SceneKind, 0);
        SetIntOnProgramLoc(_shadowProgram, su.EntityAlphaMode, 0);
        SetIntOnProgramLoc(_shadowProgram, su.GenesisUseMaterialDrawRecord, 0);
        SetIntOnProgramLoc(_shadowProgram, su.GenesisDrawRecordIndex, 0);
        ApplyEntitySkinningUniforms(_shadowProgram, 0, 0, 0f);
    }
}
