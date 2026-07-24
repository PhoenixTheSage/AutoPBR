using System.Numerics;

using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Core.Models;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private const float ShadowCascadeNearMaxHalfExtent = 8f;
    private const float ShadowCascadeFarMaxHalfExtent = 36f;
    private const float ShadowCascadeBlendWidth = 1.5f;

    private void GlRenderPassShadow(ref GlRenderFrame frame)
    {
        // Settings updates can mark targets dirty on the UI thread; rebuild only here (GL current).
        if (_shadowTargetsDirty || (frame.Settings.EnableShadows && _shadowTarget is null))
        {
            EnsureShadowMapTargets(frame.Gl, frame.Settings);
        }

        var (yaw, pitch) = PreviewLightMath.EffectiveLightYawPitch(frame.Settings, frame.RenderTime);
        frame.WorldLightDir = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);
        frame.LightDir = PreviewLightMath.SceneLightDirectionFromCelestialCycle(frame.WorldLightDir);

        if (frame.Settings.EnableAtmosphericSky)
        {
            EnsureAtmosphereLuts(frame.Gl, frame.WorldLightDir, frame.Settings);
        }

        frame.ModelMatrix = Matrix4x4.CreateRotationY((float)frame.Rotation);
        if (frame.Scene.SceneKind == PreviewSceneKind.ItemPlane)
        {
            frame.ModelMatrix = Matrix4x4.Identity;
        }
        else if (frame.BlockModel is { EnableRenderTimeAnimation: true, AnimationPreset: "entity_emulated", } &&
                 frame.Settings is { EnableEntityAnimation: true, EnableLegacyEntityWobble: true })
        {
            var animT = frame.EntityEmulatedAnimClock;
            var amp = Math.Clamp(frame.Settings.EntityAnimationAmplitude, 0f, 2f);
            var bob = Matrix4x4.CreateTranslation(0f, MathF.Sin(animT * 2.2f) * (0.035f * amp), 0f);
            var yawWobble = Matrix4x4.CreateRotationY(MathF.Sin(animT * 0.9f) * (0.22f * amp));
            var roll = Matrix4x4.CreateRotationZ(MathF.Sin(animT * 1.6f) * (0.03f * amp));
            frame.ModelMatrix = roll * yawWobble * bob * frame.ModelMatrix;
        }

        // Light/model setup is shared with the scene pass. Skip caster fit/cull when shadows are off.
        if (!frame.Settings.EnableShadows)
        {
            frame.ShadowAvailable = false;
            frame.ShadowCascadesActive = false;
            frame.ShadowBiasScale = 1f;
            return;
        }

        var shadowDistance = Math.Clamp(
            frame.Settings.ShadowDistance > 0f ? frame.Settings.ShadowDistance : ShadowDistanceDefault,
            ShadowDistanceMin,
            ShadowDistanceMax);
        frame.ShadowDistance = shadowDistance;
        frame.ShadowFadeStart = shadowDistance * ShadowDistanceFadeFraction;
        frame.CascadeSplitWorldDistance = shadowDistance * ShadowCascadeNearFraction;
        frame.CascadeMidSplitWorldDistance = shadowDistance * ShadowCascadeMidFraction;
        frame.CascadeBlendWorldWidth = Math.Max(ShadowCascadeBlendWidth, shadowDistance * 0.025f);

        var nearHalf = Math.Clamp(
            shadowDistance * ShadowCascadeNearFraction,
            4f,
            ShadowCascadeNearMaxHalfExtent);
        var midHalf = Math.Clamp(
            shadowDistance * ShadowCascadeMidFraction,
            nearHalf + 1f,
            shadowDistance);
        var farHalf = frame.Settings.ShowGroundMesh
            ? Math.Min(shadowDistance, ResolveTerrainShadowFarCoverageHalfExtent())
            : Math.Min(shadowDistance, ShadowCascadeFarMaxHalfExtent);
        farHalf = Math.Max(farHalf, midHalf);
        midHalf = Math.Min(midHalf, farHalf);

        if (TryGetShadowCasterBoundsForFrame(ref frame, out var boundsMin, out var boundsMax))
        {
            if (frame.Settings.ShowGroundMesh)
            {
                PreviewShadowFrustum.SeedTerrainShadowBounds(
                    focusXz: new Vector3(frame.Eye.X, 0f, frame.Eye.Z),
                    groundFloorY: _terrainEnvFloorY,
                    groundCeilingY: _terrainEnvCeilingY,
                    xzHalfExtent: farHalf,
                    out var camMin,
                    out var camMax);
                PreviewShadowFrustum.EncapsulateAabb(ref boundsMin, ref boundsMax, camMin, camMax);
                PreviewShadowFrustum.SeedTerrainShadowBounds(
                    focusXz: Vector3.Zero,
                    groundFloorY: _terrainEnvFloorY,
                    groundCeilingY: _terrainEnvCeilingY,
                    xzHalfExtent: PreviewShadowFrustum.TerrainShadowMinXzHalfExtent,
                    out var stageMin,
                    out var stageMax);
                PreviewShadowFrustum.EncapsulateAabb(ref boundsMin, ref boundsMax, stageMin, stageMax);
            }

            frame.ShadowVp = PreviewShadowFrustum.BuildDirectionalViewProj(
                frame.LightDir,
                boundsMin,
                boundsMax,
                Matrix4x4.Identity,
                maxHalfExtent: farHalf);

            BuildCameraCenteredCascadeAabb(
                ref frame,
                nearHalf,
                out var nearMin,
                out var nearMax);
            frame.ShadowVpNear = PreviewShadowFrustum.BuildDirectionalViewProj(
                frame.LightDir,
                nearMin,
                nearMax,
                Matrix4x4.Identity,
                maxHalfExtent: nearHalf);

            BuildCameraCenteredCascadeAabb(
                ref frame,
                midHalf,
                out var midMin,
                out var midMax);
            frame.ShadowVpMid = PreviewShadowFrustum.BuildDirectionalViewProj(
                frame.LightDir,
                midMin,
                midMax,
                Matrix4x4.Identity,
                maxHalfExtent: midHalf);

            frame.ShadowBiasScale = frame.Settings.ShowGroundMesh
                ? Math.Clamp(nearHalf / Math.Max(farHalf, 1f), 0.08f, 1f)
                : 1f;
        }
        else
        {
            frame.ShadowVp = BuildShadowViewProjFallback(frame.LightDir, farHalf * 0.5f);
            frame.ShadowVpNear = BuildShadowViewProjFallback(frame.LightDir, nearHalf * 0.5f);
            frame.ShadowVpMid = BuildShadowViewProjFallback(frame.LightDir, midHalf * 0.5f);
            frame.ShadowBiasScale = 1f;
        }

        frame.ShadowCascadesActive = frame.Settings.EnableShadowCascades &&
                                     _shadowTargetCascadeNear is not null &&
                                     _shadowTargetCascadeMid is not null;

        frame.ShadowAvailable = _shadowProgram?.IsValid == true && _shadowTarget is not null;
        if (!frame.ShadowAvailable)
        {
            frame.ShadowCascadesActive = false;
            return;
        }

        var casterPad = PreviewStageConstants.TerrainChunkSize;
        var nearCasterDist = nearHalf + casterPad;
        var midCasterDist = midHalf + casterPad;
        var farCasterDist = shadowDistance + casterPad;
        var maxCasterHeight = MathF.Max(1f, _terrainEnvCeilingY - _terrainEnvFloorY);
        var nearInclusionPad = ResolveShadowCasterInclusionPadding(frame.LightDir, nearHalf, maxCasterHeight);
        var midInclusionPad = ResolveShadowCasterInclusionPadding(frame.LightDir, midHalf, maxCasterHeight);
        var farInclusionPad = ResolveShadowCasterInclusionPadding(frame.LightDir, farHalf, maxCasterHeight);

        if (frame.Settings.ShowGroundMesh && HasTerrainChunksToDraw)
        {
            using (BeginCpuTimerScope(GlGpuTimerScope.ShadowTerrainCull))
            {
                PrepareTerrainShadowCasterSelections(
                    frame.Eye,
                    frame.ShadowVpNear,
                    frame.ShadowVpMid,
                    frame.ShadowVp,
                    nearCasterDist,
                    midCasterDist,
                    farCasterDist,
                    cascadesActive: frame.ShadowCascadesActive,
                    inclusionPad: MathF.Max(nearInclusionPad, MathF.Max(midInclusionPad, farInclusionPad)));
            }
        }
        else
        {
            _terrainShadowSelectedNear.Clear();
            _terrainShadowSelectedMid.Clear();
            _terrainShadowSelectedFar.Clear();
        }

        var restore = GlShadowPassRestoreState.FromFrame(frame);
        BeginShadowCasterPass(ref frame);
        PrepareShadowSubjectGpuUploads(ref frame);
        var entityBoneUniformsApplied = false;
        try
        {
            if (frame.ShadowCascadesActive)
            {
                var nearFactor = frame.Settings.ShowGroundMesh ? 0.35f : 1.0f;
                var nearUnits = frame.Settings.ShowGroundMesh ? 0.75f : 2.0f;
                RenderShadowCascadeSlice(
                    ref frame,
                    restore,
                    frame.ShadowVpNear,
                    _shadowTargetCascadeNear!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: nearFactor,
                    polygonOffsetUnits: nearUnits,
                    terrainSelection: _terrainShadowSelectedNear,
                    inclusionPad: nearInclusionPad);
                RenderShadowCascadeSlice(
                    ref frame,
                    restore,
                    frame.ShadowVpMid,
                    _shadowTargetCascadeMid!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: 0.4f * frame.ShadowBiasScale,
                    polygonOffsetUnits: 0.9f * frame.ShadowBiasScale,
                    terrainSelection: _terrainShadowSelectedMid,
                    inclusionPad: midInclusionPad);
                RenderShadowCascadeSlice(
                    ref frame,
                    restore,
                    frame.ShadowVp,
                    _shadowTarget!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: 0.45f * frame.ShadowBiasScale,
                    polygonOffsetUnits: 1.0f * frame.ShadowBiasScale,
                    terrainSelection: _terrainShadowSelectedFar,
                    inclusionPad: farInclusionPad);
            }
            else
            {
                var factor = frame.Settings.ShowGroundMesh ? 0.5f * frame.ShadowBiasScale : 1.25f;
                var units = frame.Settings.ShowGroundMesh ? 1.0f * frame.ShadowBiasScale : 2.5f;
                RenderShadowCascadeSlice(
                    ref frame,
                    restore,
                    frame.ShadowVp,
                    _shadowTarget!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: factor,
                    polygonOffsetUnits: units,
                    terrainSelection: _terrainShadowSelectedFar,
                    inclusionPad: farInclusionPad);
            }
        }
        finally
        {
            // Shared subject uploads are submitted after the scene pass consumes them.
            _shadowSubjectUploadsPrepared = false;
        }
    }

    /// <summary>
    /// Modest sphere growth so off-camera casters that still shadow receivers stay in the light
    /// frustum. Kept small: large pads previously pulled the far LOD ring into the near cascade.
    /// </summary>
    internal static float ResolveShadowCasterInclusionPadding(
        Vector3 lightDir,
        float cascadeHalfExtent,
        float maxCasterHeight)
    {
        var absY = MathF.Max(MathF.Abs(lightDir.Y), 0.25f);
        var heightPad = MathF.Min(MathF.Max(0f, maxCasterHeight), 24f) / absY * 0.15f;
        var extentPad = MathF.Max(0f, cascadeHalfExtent) * 0.08f;
        var pad = MathF.Max(heightPad, extentPad);
        var cap = Math.Clamp(cascadeHalfExtent * 0.25f, 1f, 6f);
        return Math.Clamp(pad, 0f, cap);
    }

    private void BuildCameraCenteredCascadeAabb(
        ref GlRenderFrame frame,
        float xzHalfExtent,
        out Vector3 min,
        out Vector3 max)
    {
        if (frame.Settings.ShowGroundMesh)
        {
            PreviewShadowFrustum.SeedTerrainShadowBounds(
                focusXz: new Vector3(frame.Eye.X, 0f, frame.Eye.Z),
                groundFloorY: _terrainEnvFloorY,
                groundCeilingY: _terrainEnvCeilingY,
                xzHalfExtent: xzHalfExtent,
                out min,
                out max);
            if (TryGetSubjectShadowCasterBounds(ref frame, out var subjectMin, out var subjectMax))
            {
                PreviewShadowFrustum.EncapsulateTransformedAabb(
                    subjectMin,
                    subjectMax,
                    frame.ModelMatrix,
                    ref min,
                    ref max);
            }

            return;
        }

        if (TryGetSubjectShadowCasterBounds(ref frame, out var localSubjectMin, out var localSubjectMax))
        {
            min = new Vector3(float.PositiveInfinity);
            max = new Vector3(float.NegativeInfinity);
            PreviewShadowFrustum.EncapsulateTransformedAabb(
                localSubjectMin,
                localSubjectMax,
                frame.ModelMatrix,
                ref min,
                ref max);
            return;
        }

        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: new Vector3(frame.Eye.X, 0f, frame.Eye.Z),
            groundFloorY: -1f,
            groundCeilingY: 1f,
            xzHalfExtent: xzHalfExtent,
            out min,
            out max);
    }

    private float ResolveTerrainShadowFarCoverageHalfExtent()
    {
        var ring = _terrainStreamer?.LodRingWorldRadius
                   ?? (PreviewStageConstants.TerrainDefaultChunkViewDistance +
                       PreviewStageConstants.TerrainLodRingChunks) *
                      (float)PreviewStageConstants.TerrainChunkSize;
        return Math.Clamp(
            ring * 1.05f,
            PreviewShadowFrustum.TerrainShadowMinXzHalfExtent,
            PreviewShadowFrustum.TerrainShadowFarMaxHalfExtent);
    }

    private bool TryGetShadowCasterBoundsForFrame(ref GlRenderFrame frame, out Vector3 min, out Vector3 max)
    {
        var worldMin = new Vector3(float.PositiveInfinity);
        var worldMax = new Vector3(float.NegativeInfinity);
        var hasSubject = false;

        if (TryGetSubjectShadowCasterBounds(ref frame, out var subjectMin, out var subjectMax))
        {
            PreviewShadowFrustum.EncapsulateTransformedAabb(
                subjectMin,
                subjectMax,
                frame.ModelMatrix,
                ref worldMin,
                ref worldMax);
            hasSubject = true;
        }

        if (frame.Settings.ShowGroundMesh)
        {
            if (TryEncapsulateTerrainShadowWorldBounds(frame.Eye, ref worldMin, ref worldMax))
            {
                min = worldMin;
                max = worldMax;
                return true;
            }

            // Terrain not resident yet — keep a stage-sized pad + relief ceiling so idle ground still casts.
            if (hasSubject)
            {
                PreviewShadowFrustum.ExpandBoundsForGroundReceiver(
                    ref worldMin,
                    ref worldMax,
                    PreviewStageConstants.GroundPlaneWorldY +
                    PreviewStageConstants.TerrainSolidFloorRelativeY,
                    _terrainEnvCeilingY,
                    PreviewShadowFrustum.TerrainShadowMinXzHalfExtent);
                min = worldMin;
                max = worldMax;
                return true;
            }

            PreviewShadowFrustum.SeedTerrainShadowBounds(
                focusXz: new Vector3(frame.Eye.X, 0f, frame.Eye.Z),
                groundFloorY: PreviewStageConstants.GroundPlaneWorldY +
                              PreviewStageConstants.TerrainSolidFloorRelativeY,
                groundCeilingY: _terrainEnvCeilingY,
                xzHalfExtent: PreviewShadowFrustum.TerrainShadowMinXzHalfExtent,
                out min,
                out max);
            return true;
        }

        if (!hasSubject)
        {
            min = default;
            max = default;
            return false;
        }

        min = worldMin;
        max = worldMax;
        return true;
    }

    private bool TryGetSubjectShadowCasterBounds(ref GlRenderFrame frame, out Vector3 min, out Vector3 max)
    {
        if (TryGetCachedShadowCasterBounds(out min, out max))
        {
            return true;
        }

        ReadOnlySpan<float> verts;
        int stride;
        if (frame.BlockModel?.InterleavedVertices is { Length: > 0 } subjectVerts)
        {
            verts = subjectVerts;
            stride = frame.BlockModel.VertexStrideFloats > 0
                ? frame.BlockModel.VertexStrideFloats
                : PreviewMesh.FloatsPerVertex;
        }
        else if (frame.Scene.Meshes is { Count: > 0 } meshes &&
                 meshes[0].InterleavedVertices is { Length: > 0 } sceneVerts)
        {
            verts = sceneVerts;
            stride = PreviewMesh.FloatsPerVertex;
        }
        else
        {
            min = default;
            max = default;
            return false;
        }

        return TryComputeVertexBounds(verts, stride, out min, out max);
    }

    /// <summary>
    /// Unions resident terrain (Full + Lod) into the shadow AABB so far-cascade coverage is world-wide
    /// for streamed chunks rather than a small camera neighborhood.
    /// </summary>
    private bool TryEncapsulateTerrainShadowWorldBounds(Vector3 eye, ref Vector3 min, ref Vector3 max)
    {
        if (_terrainGpuChunks.Count == 0)
        {
            return false;
        }

        var coverage = ResolveTerrainShadowFarCoverageHalfExtent();
        if (_terrainShadowWorldAabbValid)
        {
            PreviewShadowFrustum.SeedTerrainShadowBounds(
                focusXz: new Vector3(eye.X, 0f, eye.Z),
                groundFloorY: _terrainEnvFloorY,
                groundCeilingY: _terrainEnvCeilingY,
                xzHalfExtent: coverage,
                out var eyeMin,
                out var eyeMax);
            if (float.IsPositiveInfinity(min.X))
            {
                min = eyeMin;
                max = eyeMax;
            }
            else
            {
                PreviewShadowFrustum.EncapsulateAabb(ref min, ref max, eyeMin, eyeMax);
            }

            PreviewShadowFrustum.EncapsulateAabb(
                ref min,
                ref max,
                _terrainShadowWorldAabbMin,
                _terrainShadowWorldAabbMax);
            PreviewShadowFrustum.ExpandBoundsForGroundReceiver(
                ref min,
                ref max,
                _terrainEnvFloorY,
                _terrainEnvCeilingY,
                coverage);
            return true;
        }

        var groundY = PreviewStageConstants.GroundPlaneWorldY;
        var anyChunk = false;
        var cacheMin = new Vector3(float.PositiveInfinity);
        var cacheMax = new Vector3(float.NegativeInfinity);

        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: new Vector3(eye.X, 0f, eye.Z),
            groundFloorY: _terrainEnvFloorY,
            groundCeilingY: _terrainEnvCeilingY,
            xzHalfExtent: coverage,
            out var seedMin,
            out var seedMax);
        if (float.IsPositiveInfinity(min.X))
        {
            min = seedMin;
            max = seedMax;
        }
        else
        {
            PreviewShadowFrustum.EncapsulateAabb(ref min, ref max, seedMin, seedMax);
        }

        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: Vector3.Zero,
            groundFloorY: _terrainEnvFloorY,
            groundCeilingY: _terrainEnvCeilingY,
            xzHalfExtent: PreviewShadowFrustum.TerrainShadowMinXzHalfExtent,
            out var stageMin,
            out var stageMax);
        PreviewShadowFrustum.EncapsulateAabb(ref min, ref max, stageMin, stageMax);
        PreviewShadowFrustum.EncapsulateAabb(ref cacheMin, ref cacheMax, stageMin, stageMax);

        foreach (var chunk in _terrainGpuChunks.Values)
        {
            if (chunk.IndexCount <= 0)
            {
                continue;
            }

            var r = MathF.Max(chunk.BoundsRadius, 0.5f);
            var chunkMin = new Vector3(
                chunk.BoundsCenter.X - r,
                groundY + chunk.MinRelativeHeight - 1f,
                chunk.BoundsCenter.Z - r);
            var chunkMax = new Vector3(
                chunk.BoundsCenter.X + r,
                groundY + chunk.MaxRelativeHeight,
                chunk.BoundsCenter.Z + r);
            PreviewShadowFrustum.EncapsulateAabb(ref min, ref max, chunkMin, chunkMax);
            PreviewShadowFrustum.EncapsulateAabb(ref cacheMin, ref cacheMax, chunkMin, chunkMax);
            anyChunk = true;
        }

        if (!anyChunk)
        {
            return true;
        }

        _terrainShadowWorldAabbMin = cacheMin;
        _terrainShadowWorldAabbMax = cacheMax;
        _terrainShadowWorldAabbValid = true;

        PreviewShadowFrustum.ExpandBoundsForGroundReceiver(
            ref min,
            ref max,
            _terrainEnvFloorY,
            _terrainEnvCeilingY,
            coverage);
        return true;
    }

    private void InvalidateTerrainShadowWorldAabbCache()
    {
        _terrainShadowWorldAabbValid = false;
    }

    private static Matrix4x4 BuildShadowViewProjFallback(Vector3 worldLightDir, float orthoHalfExtent)
    {
        const float shadowBoom = 4.0f;
        const float shadowNear = shadowBoom - 2.5f;
        const float shadowFar = shadowBoom + 2.5f;
        var shadowTargetPos = Vector3.Zero;
        var shadowEye = shadowTargetPos - worldLightDir * shadowBoom;
        var shadowUp = PreviewLightMath.PickShadowViewUp(worldLightDir);
        var shadowView = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(shadowEye, shadowTargetPos, shadowUp);
        var shadowProj = PreviewGlMatrices.CreateOrthographicOpenGlRowStorage(
            -orthoHalfExtent, orthoHalfExtent,
            -orthoHalfExtent, orthoHalfExtent,
            shadowNear, shadowFar);
        return shadowProj * shadowView;
    }

    private bool _shadowCasterCullFaceEnabled;

    private void BeginShadowCasterPass(ref GlRenderFrame frame)
    {
        frame.Gl.Enable(EnableCap.DepthTest);
        frame.Gl.DepthFunc(GLEnum.Lequal);
        frame.Gl.DepthMask(true);

        if (ShouldCullSolidBackFaces(frame.Scene.SceneKind, frame.BlockModel, frame.Settings))
        {
            frame.Gl.Enable(EnableCap.CullFace);
            frame.Gl.CullFace(GLEnum.Front);
            frame.Gl.FrontFace(GLEnum.Ccw);
            _shadowCasterCullFaceEnabled = true;
        }
        else
        {
            frame.Gl.Disable(EnableCap.CullFace);
            _shadowCasterCullFaceEnabled = false;
        }

        _shadowProgram!.Use();
        var su = _shadowUniformLocs;
        SetMatrixOnProgramLoc(_shadowProgram, su.Model, Matrix4x4.Identity);
        SetIntOnProgramLoc(_shadowProgram, su.SceneKind, 0);
        SetIntOnProgramLoc(_shadowProgram, su.EntityAlphaMode, 0);
        SetIntOnProgramLoc(_shadowProgram, su.GenesisUseMaterialDrawRecord, 0);
        SetIntOnProgramLoc(_shadowProgram, su.GenesisDrawRecordIndex, 0);
        ApplyEntitySkinningUniforms(_shadowProgram, 0, 0, 0f);
        if (frame.EntityBonePaletteUploaded)
        {
            BindEntityBoneSkinningUboBlocks();
        }
    }

    private bool _shadowSubjectUploadsPrepared;
    private bool _shadowSubjectUseMaterialDrawRecords;
    private bool _shadowSubjectUseIndirectDrawCommands;
    private bool _shadowSubjectUseMaterialTextureArrays;

    private void PrepareShadowSubjectGpuUploads(ref GlRenderFrame frame)
    {
        EnsureFrameSubjectGpuUploads(ref frame);
        _shadowSubjectUseMaterialDrawRecords = _frameSubjectUseMaterialDrawRecords;
        _shadowSubjectUseIndirectDrawCommands = _frameSubjectUseIndirectDrawCommands;
        _shadowSubjectUseMaterialTextureArrays = _frameSubjectUseMaterialTextureArrays;
        _shadowSubjectUploadsPrepared = _frameSubjectGpuUploadsReady;
    }

    private void EnsureFrameSubjectGpuUploads(ref GlRenderFrame frame)
    {
        if (_frameSubjectGpuUploadsReady)
        {
            return;
        }

        _frameSubjectUseMaterialDrawRecords = false;
        _frameSubjectUseIndirectDrawCommands = false;
        _frameSubjectUseMaterialTextureArrays = false;

        if (!frame.Settings.DrawPreviewSubject ||
            _mesh is not { IndexCount: > 0 } ||
            frame.BlockModel is null ||
            frame.BlockSlots is not { Length: > 0 })
        {
            _frameSubjectGpuUploadsReady = true;
            return;
        }

        _frameSubjectUseMaterialDrawRecords = TryUploadGenesisMaterialDrawRecords(ref frame);
        _frameSubjectUseIndirectDrawCommands = TryUploadGenesisIndirectDrawCommands(frame.BlockModel);
        _frameSubjectUseMaterialTextureArrays =
            TryEnsureMaterialTextureArrays(ref frame, _frameSubjectUseMaterialDrawRecords, out _);
        _frameSubjectGpuUploadsReady = true;
    }

    private void FinishFrameSubjectGpuUploads()
    {
        if (_frameSubjectGpuUploadsReady && _frameSubjectUseMaterialDrawRecords)
        {
            MarkGenesisMaterialDrawRecordsSubmitted();
        }

        _frameSubjectGpuUploadsReady = false;
        _frameSubjectUseMaterialDrawRecords = false;
        _frameSubjectUseIndirectDrawCommands = false;
        _frameSubjectUseMaterialTextureArrays = false;
        _shadowSubjectUploadsPrepared = false;
        _shadowSubjectUseMaterialDrawRecords = false;
        _shadowSubjectUseIndirectDrawCommands = false;
        _shadowSubjectUseMaterialTextureArrays = false;
    }

    private void RenderShadowCascadeSlice(
        ref GlRenderFrame frame,
        in GlShadowPassRestoreState restore,
        Matrix4x4 shadowVp,
        GlShadowMapTarget target,
        ref bool entityBoneUniformsApplied,
        float polygonOffsetFactor,
        float polygonOffsetUnits,
        List<int> terrainSelection,
        float inclusionPad)
    {
        if (ShouldSkipShadowCascadeSlice(ref frame, shadowVp, terrainSelection, inclusionPad))
        {
            return;
        }

        target.BeginShadowPass(restore, polygonOffsetFactor, polygonOffsetUnits);
        SetMatrixOnProgramLoc(_shadowProgram!, _shadowUniformLocs.LightViewProj, shadowVp);
        DrawShadowCasters(
            ref frame,
            ref entityBoneUniformsApplied,
            terrainSelection,
            shadowVp,
            inclusionPad);
        target.EndShadowPass();
    }

    private bool ShouldSkipShadowCascadeSlice(
        ref GlRenderFrame frame,
        Matrix4x4 shadowVp,
        List<int> terrainSelection,
        float inclusionPad)
    {
        if (terrainSelection.Count > 0 || _terrainShadowGpuIndirectReady)
        {
            return false;
        }

        if (!frame.Settings.DrawPreviewSubject ||
            _mesh is not { IndexCount: > 0 } ||
            frame.BlockModel is null)
        {
            return true;
        }

        Span<Vector4> lightPlanes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        PreviewFrustumPlanes.Extract(shadowVp, lightPlanes);
        return PreviewDrawBatchFrustumCull.IsSubjectFullyCulled(
            frame.BlockModel.DrawBatches,
            lightPlanes,
            frame.Eye,
            frame.ModelMatrix,
            inclusionPad);
    }

    private void DrawShadowCasters(
        ref GlRenderFrame frame,
        ref bool entityBoneUniformsApplied,
        List<int> terrainSelection,
        Matrix4x4 shadowVp,
        float inclusionPad)
    {
        var su = _shadowUniformLocs;

        if (frame.Settings.ShowGroundMesh &&
            (terrainSelection.Count > 0 || _terrainShadowGpuIndirectReady))
        {
            // Voxel terrain needs closed contact shadows; front-face cull (acne trick for smooth
            // meshes) detaches shadow silhouettes from block edges (peter-panning).
            var restoreCull = _shadowCasterCullFaceEnabled;
            frame.Gl.Disable(EnableCap.CullFace);

            SetIntOnProgramLoc(_shadowProgram!, su.SceneKind, 0);
            SetIntOnProgramLoc(_shadowProgram!, su.EntityAlphaMode, 0);
            SetFloatOnProgramLoc(_shadowProgram!, su.AlphaCutoff, frame.Settings.AlphaCutoff);
            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialDrawRecord, 0);
            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialTextureArray, 0);
            if (_grassGroundAlbedo is not null)
            {
                frame.Gl.ActiveTexture(TextureUnit.Texture0);
                _grassGroundAlbedo.Bind(0);
                SetIntOnProgramLoc(_shadowProgram!, su.Albedo, 0);
            }

            BindFallbackShadowMaterialTextureArrayIfPresent(su);
            DrawPreparedTerrainShadowCasters(terrainSelection);

            if (restoreCull)
            {
                frame.Gl.Enable(EnableCap.CullFace);
            }
        }

        if (!frame.Settings.DrawPreviewSubject || _mesh is not { IndexCount: > 0 })
        {
            return;
        }

        SetMatrixOnProgramLoc(_shadowProgram!, su.Model, frame.ModelMatrix);

        if (frame.Scene.SceneKind == PreviewSceneKind.ItemPlane)
        {
            ApplyEntitySkinningUniforms(_shadowProgram!, 0, 0, 0f);
            SetIntOnProgramLoc(_shadowProgram!, su.SceneKind, 1);
            SetIntOnProgramLoc(_shadowProgram!, su.EntityAlphaMode, 0);
            SetFloatOnProgramLoc(_shadowProgram!, su.AlphaCutoff, frame.Settings.AlphaCutoff);
            SetIntOnProgramLoc(_shadowProgram!, su.ItemAlphaBlend, frame.Settings.ItemUseAlphaBlend ? 1 : 0);
            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialTextureArray, 0);
            frame.Gl.ActiveTexture(TextureUnit.Texture0);
            _albedo!.Bind(0);
            SetIntOnProgramLoc(_shadowProgram!, su.Albedo, 0);
        }
        else
        {
            SetIntOnProgramLoc(_shadowProgram!, su.SceneKind, 0);
        }

        if (frame.BlockModel is not null && frame.BlockSlots is { Length: > 0 })
        {
            if (frame.EntityAlphaModeUniform != 0)
            {
                SetFloatOnProgramLoc(_shadowProgram!, su.AlphaCutoff, frame.Settings.AlphaCutoff);
            }

            SetIntOnProgramLoc(_shadowProgram!, su.EntityAlphaMode, frame.EntityAlphaModeUniform);
            var uploadedMaterialIndex = -1;
            var blockModel = frame.BlockModel;
            var blockSlots = frame.BlockSlots;
            var useMaterialDrawRecords = _shadowSubjectUseMaterialDrawRecords;
            var useIndirectDrawCommands = _shadowSubjectUseIndirectDrawCommands;
            var useMaterialTextureArrays = _shadowSubjectUseMaterialTextureArrays;
            if (useMaterialDrawRecords)
            {
                BindGenesisMaterialDrawRecordBuffer();
            }

            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialDrawRecord, useMaterialDrawRecords ? 1 : 0);
            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialTextureArray, useMaterialTextureArrays ? 1 : 0);
            if (useMaterialTextureArrays)
            {
                BindShadowPassMaterialTextureArray(su);
                // Keep sampler2D albedo complete for array-capable shadow programs.
                var primary = Math.Clamp(blockModel.PrimaryMaterialIndex, 0, blockSlots.Length - 1);
                UploadMaterial(frame.Gl, blockSlots[primary], nearest: true);
                _albedo!.Bind(0);
                SetIntOnProgramLoc(_shadowProgram!, su.Albedo, 0);
                BindFallbackShadowMaterialTextureArrayIfPresent(su);
            }

            Span<Vector4> lightFrustum = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
            PreviewFrustumPlanes.Extract(shadowVp, lightFrustum);
            var subjectFullyCulled = PreviewDrawBatchFrustumCull.IsSubjectFullyCulled(
                blockModel.DrawBatches,
                lightFrustum,
                frame.Eye,
                frame.ModelMatrix,
                inclusionPad);

            if (!subjectFullyCulled)
            {
                _mesh.BindVertexArray();

                for (var batchIndex = 0; batchIndex < blockModel.DrawBatches.Length; batchIndex++)
                {
                    var batch = blockModel.DrawBatches[batchIndex];
                    if ((uint)batch.MaterialIndex >= (uint)blockSlots.Length)
                    {
                        continue;
                    }

                    if (batch.LayerPolicy.ShadowMode == PreviewDrawLayerShadowMode.Skip)
                    {
                        continue;
                    }

                    SetIntOnProgramLoc(_shadowProgram!, su.GenesisDrawRecordIndex, useMaterialDrawRecords ? batchIndex : 0);

                    if (batch.MaterialIndex != uploadedMaterialIndex && !useMaterialTextureArrays)
                    {
                        UploadMaterial(frame.Gl, blockSlots[batch.MaterialIndex], nearest: true);
                        uploadedMaterialIndex = batch.MaterialIndex;
                        frame.Gl.ActiveTexture(TextureUnit.Texture0);
                        _albedo!.Bind(0);
                        SetIntOnProgramLoc(_shadowProgram!, su.Albedo, 0);
                    }

                    if (!entityBoneUniformsApplied)
                    {
                        ApplyEntityBoneSkinningUniformsBeforeDraw(
                            _shadowProgram!,
                            _shadowEntityUniformLocs,
                            blockModel,
                            blockModel.EntityGpuMeshSpaceLiftY,
                            frame.EntityBoneSnapshotValid,
                            frame.EntityBoneSnapshotCount,
                            frame.Settings.EnableEntityAnimation,
                            frame.EntityBonePaletteUploaded,
                            "shadow",
                            bindBoneUboBlocks: !frame.EntityBonePaletteUploaded);
                        entityBoneUniformsApplied = true;
                    }

                    var batchGroupCount = CountShadowPassMultiDrawGroup(
                        blockModel.DrawBatches,
                        batchIndex,
                        blockSlots.Length,
                        useIndirectDrawCommands && CanUseGenesisMultiDrawGroups(useMaterialDrawRecords),
                        allowMaterialChanges: useMaterialTextureArrays);

                    // Light frustum (+ inclusion pad): keep off-camera casters that still shadow the view.
                    var gpuCulledDrawn =
                        batchGroupCount > 1 &&
                        TryDrawGpuCulledBatchGroup(
                            blockModel,
                            batchIndex,
                            batchGroupCount,
                            shadowVp,
                            frame.Eye,
                            frame.ModelMatrix,
                            _shadowProgram!,
                            "shadow",
                            boundsPadding: inclusionPad);
                    if (!gpuCulledDrawn)
                    {
                        DrawCpuFrustumCulledBatchGroup(
                            blockModel,
                            batchIndex,
                            batchGroupCount,
                            lightFrustum,
                            frame.Eye,
                            frame.ModelMatrix,
                            patches: false,
                            useIndirectDrawCommands,
                            boundsPadding: inclusionPad);
                    }

                    batchIndex += batchGroupCount - 1;
                }

                _mesh.UnbindVertexArray();
            }

            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialDrawRecord, 0);
            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialTextureArray, 0);
            SetIntOnProgramLoc(_shadowProgram!, su.GenesisDrawRecordIndex, 0);
        }
        else
        {
            var alphaMode = frame.EntityAlphaModeUniform;
            if (alphaMode != 0)
            {
                SetFloatOnProgramLoc(_shadowProgram!, su.AlphaCutoff, frame.Settings.AlphaCutoff);
                frame.Gl.ActiveTexture(TextureUnit.Texture0);
                _albedo!.Bind(0);
                SetIntOnProgramLoc(_shadowProgram!, su.Albedo, 0);
            }

            SetIntOnProgramLoc(_shadowProgram!, su.GenesisUseMaterialTextureArray, 0);
            SetIntOnProgramLoc(_shadowProgram!, su.EntityAlphaMode, alphaMode);
            ApplyEntitySkinningUniforms(_shadowProgram!, 0, 0, 0f);
            _mesh.Draw();
        }
    }
}
