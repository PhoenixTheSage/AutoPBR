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



        var shadowDistance = Math.Clamp(
            frame.Settings.ShadowDistance > 0f ? frame.Settings.ShadowDistance : ShadowDistanceDefault,
            ShadowDistanceMin,
            ShadowDistanceMax);
        frame.ShadowDistance = shadowDistance;
        frame.ShadowFadeStart = shadowDistance * ShadowDistanceFadeFraction;
        frame.CascadeSplitWorldDistance = shadowDistance * ShadowCascadeNearFraction;
        frame.CascadeMidSplitWorldDistance = shadowDistance * ShadowCascadeMidFraction;
        frame.CascadeBlendWorldWidth = Math.Max(ShadowCascadeBlendWidth, shadowDistance * 0.025f);

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

        frame.ShadowCascadesActive = frame.Settings is { EnableShadowCascades: true, EnableShadows: true } &&
                                     _shadowTargetCascadeNear is not null &&
                                     _shadowTargetCascadeMid is not null;

        frame.ShadowAvailable = frame.Settings.EnableShadows && _shadowProgram?.IsValid == true && _shadowTarget is not null;
        if (!frame.ShadowAvailable)
        {
            return;
        }

        var casterPad = PreviewStageConstants.TerrainChunkSize;
        var nearCasterDist = nearHalf + casterPad;
        var midCasterDist = midHalf + casterPad;
        var farCasterDist = shadowDistance + casterPad;

        if (frame.Settings.ShowGroundMesh && HasTerrainChunksToDraw)
        {
            PrepareTerrainShadowCasterSelections(
                frame.Eye,
                frame.ShadowVpNear,
                frame.ShadowVpMid,
                frame.ShadowVp,
                nearCasterDist,
                midCasterDist,
                farCasterDist,
                cascadesActive: frame.ShadowCascadesActive);
        }
        else
        {
            _terrainShadowSelectedNear.Clear();
            _terrainShadowSelectedMid.Clear();
            _terrainShadowSelectedFar.Clear();
        }

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
                    frame.ShadowVpNear,
                    _shadowTargetCascadeNear!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: nearFactor,
                    polygonOffsetUnits: nearUnits,
                    terrainSelection: _terrainShadowSelectedNear);
                RenderShadowCascadeSlice(
                    ref frame,
                    frame.ShadowVpMid,
                    _shadowTargetCascadeMid!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: 0.4f * frame.ShadowBiasScale,
                    polygonOffsetUnits: 0.9f * frame.ShadowBiasScale,
                    terrainSelection: _terrainShadowSelectedMid);
                RenderShadowCascadeSlice(
                    ref frame,
                    frame.ShadowVp,
                    _shadowTarget!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: 0.45f * frame.ShadowBiasScale,
                    polygonOffsetUnits: 1.0f * frame.ShadowBiasScale,
                    terrainSelection: _terrainShadowSelectedFar);
            }
            else
            {
                var factor = frame.Settings.ShowGroundMesh ? 0.5f * frame.ShadowBiasScale : 1.25f;
                var units = frame.Settings.ShowGroundMesh ? 1.0f * frame.ShadowBiasScale : 2.5f;
                RenderShadowCascadeSlice(
                    ref frame,
                    frame.ShadowVp,
                    _shadowTarget!,
                    ref entityBoneUniformsApplied,
                    polygonOffsetFactor: factor,
                    polygonOffsetUnits: units,
                    terrainSelection: _terrainShadowSelectedFar);
            }
        }
        finally
        {
            FinishShadowSubjectGpuUploads();
        }
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
                    PreviewStageConstants.GroundPlaneWorldY - PreviewStageConstants.TerrainFillDepth,
                    _terrainEnvCeilingY,
                    PreviewShadowFrustum.TerrainShadowMinXzHalfExtent);
                min = worldMin;
                max = worldMax;
                return true;
            }

            PreviewShadowFrustum.SeedTerrainShadowBounds(
                focusXz: new Vector3(frame.Eye.X, 0f, frame.Eye.Z),
                groundFloorY: PreviewStageConstants.GroundPlaneWorldY - PreviewStageConstants.TerrainFillDepth,
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
        var groundY = PreviewStageConstants.GroundPlaneWorldY;
        var anyChunk = false;

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

        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: Vector3.Zero,
            groundFloorY: _terrainEnvFloorY,
            groundCeilingY: _terrainEnvCeilingY,
            xzHalfExtent: PreviewShadowFrustum.TerrainShadowMinXzHalfExtent,
            out var stageMin,
            out var stageMax);
        PreviewShadowFrustum.EncapsulateAabb(ref min, ref max, stageMin, stageMax);

        foreach (var chunk in _terrainGpuChunks.Values)
        {
            if (chunk.Mesh.IndexCount <= 0)
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
            anyChunk = true;
        }

        if (!anyChunk)
        {
            return true;
        }

        PreviewShadowFrustum.ExpandBoundsForGroundReceiver(
            ref min,
            ref max,
            _terrainEnvFloorY,
            _terrainEnvCeilingY,
            coverage);
        return true;
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
        _shadowSubjectUploadsPrepared = false;
        _shadowSubjectUseMaterialDrawRecords = false;
        _shadowSubjectUseIndirectDrawCommands = false;
        _shadowSubjectUseMaterialTextureArrays = false;

        if (!frame.Settings.DrawPreviewSubject ||
            _mesh is not { IndexCount: > 0 } ||
            frame.BlockModel is null ||
            frame.BlockSlots is not { Length: > 0 })
        {
            return;
        }

        _shadowSubjectUseMaterialDrawRecords = TryUploadGenesisMaterialDrawRecords(ref frame);
        _shadowSubjectUseIndirectDrawCommands = TryUploadGenesisIndirectDrawCommands(frame.BlockModel);
        _shadowSubjectUseMaterialTextureArrays =
            TryEnsureMaterialTextureArrays(ref frame, _shadowSubjectUseMaterialDrawRecords, out _);
        _shadowSubjectUploadsPrepared = true;
    }

    private void FinishShadowSubjectGpuUploads()
    {
        if (_shadowSubjectUploadsPrepared && _shadowSubjectUseMaterialDrawRecords)
        {
            MarkGenesisMaterialDrawRecordsSubmitted();
        }

        _shadowSubjectUploadsPrepared = false;
        _shadowSubjectUseMaterialDrawRecords = false;
        _shadowSubjectUseIndirectDrawCommands = false;
        _shadowSubjectUseMaterialTextureArrays = false;
    }

    private void RenderShadowCascadeSlice(
        ref GlRenderFrame frame,
        Matrix4x4 shadowVp,
        GlShadowMapTarget target,
        ref bool entityBoneUniformsApplied,
        float polygonOffsetFactor,
        float polygonOffsetUnits,
        List<int> terrainSelection)
    {
        target.BeginShadowPass(polygonOffsetFactor, polygonOffsetUnits);
        SetMatrixOnProgramLoc(_shadowProgram!, _shadowUniformLocs.LightViewProj, shadowVp);
        DrawShadowCasters(
            ref frame,
            shadowVp,
            ref entityBoneUniformsApplied,
            terrainSelection);
        target.EndShadowPass();
    }

    private void DrawShadowCasters(
        ref GlRenderFrame frame,
        Matrix4x4 shadowViewProjection,
        ref bool entityBoneUniformsApplied,
        List<int> terrainSelection)
    {
        var su = _shadowUniformLocs;

        if (frame.Settings.ShowGroundMesh && terrainSelection.Count > 0)
        {
            // Voxel terrain needs closed contact shadows; front-face cull (acne trick for smooth
            // meshes) detaches shadow silhouettes from block edges (peter-panning).
            var restoreCull = frame.Gl.IsEnabled(EnableCap.CullFace);
            frame.Gl.Disable(EnableCap.CullFace);

            SetIntOnProgramLoc(_shadowProgram!, su.SceneKind, 0);
            SetIntOnProgramLoc(_shadowProgram!, su.EntityAlphaMode, 0);
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
            }

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

                var gpuCulledDrawn =
                    batchGroupCount > 1 &&
                    TryDrawGpuCulledBatchGroup(
                        blockModel,
                        batchIndex,
                        batchGroupCount,
                        shadowViewProjection,
                        frame.Eye,
                        frame.ModelMatrix,
                        _shadowProgram!,
                        "shadow");
                if (!gpuCulledDrawn)
                {
                    DrawPreviewBatchRange(
                        batch,
                        batchIndex,
                        patches: false,
                        useIndirectDrawCommands,
                        useMultiDrawGroups: batchGroupCount > 1,
                        groupCount: batchGroupCount);
                }

                batchIndex += batchGroupCount - 1;

            }



            _mesh.UnbindVertexArray();

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


