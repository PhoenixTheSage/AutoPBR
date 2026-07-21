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

    private const float ShadowCascadeSplitDistance = 6f;
    private const float ShadowCascadeBlendWidth = 1.5f;



    private void GlRenderPassShadow(ref GlRenderFrame frame)

    {

        var (yaw, pitch) = PreviewLightMath.EffectiveLightYawPitch(frame.Settings, frame.RenderTime);

        frame.WorldLightDir = PreviewLightMath.LightDirectionFromYawPitch(yaw, pitch);

        frame.LightDir = PreviewLightMath.SceneLightDirectionFromCelestialCycle(frame.WorldLightDir);

        if (frame.Settings.EnableAtmosphericSky)

        {

            EnsureAtmosphereLuts(frame.Gl, frame.WorldLightDir, frame.Settings);

        }



        frame.CascadeSplitWorldDistance = ShadowCascadeSplitDistance;
        frame.CascadeBlendWorldWidth = ShadowCascadeBlendWidth;



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



        if (TryGetShadowCasterBoundsForFrame(ref frame, out var boundsMin, out var boundsMax))

        {

            frame.ShadowVp = PreviewShadowFrustum.BuildDirectionalViewProj(

                frame.LightDir,

                boundsMin,

                boundsMax,

                frame.ModelMatrix,

                maxHalfExtent: ShadowCascadeFarMaxHalfExtent);

            frame.ShadowVpNear = PreviewShadowFrustum.BuildDirectionalViewProj(

                frame.LightDir,

                boundsMin,

                boundsMax,

                frame.ModelMatrix,

                maxHalfExtent: ShadowCascadeNearMaxHalfExtent);

        }

        else

        {

            frame.ShadowVp = BuildShadowViewProjFallback(frame.LightDir, ShadowCascadeFarMaxHalfExtent * 0.5f);

            frame.ShadowVpNear = BuildShadowViewProjFallback(frame.LightDir, ShadowCascadeNearMaxHalfExtent * 0.5f);

        }



        frame.ShadowCascadesActive = frame.Settings is { EnableShadowCascades: true, EnableShadows: true } &&

                                     _shadowTargetCascadeNear is not null;



        frame.ShadowAvailable = frame.Settings.EnableShadows && _shadowProgram?.IsValid == true && _shadowTarget is not null;

        if (!frame.ShadowAvailable)

        {

            return;

        }



        BeginShadowCasterPass(ref frame);

        var entityBoneUniformsApplied = false;

        if (frame.ShadowCascadesActive)

        {

            RenderShadowCascadeSlice(ref frame, frame.ShadowVpNear, _shadowTargetCascadeNear!, ref entityBoneUniformsApplied);

            RenderShadowCascadeSlice(ref frame, frame.ShadowVp, _shadowTarget!, ref entityBoneUniformsApplied);

        }

        else

        {

            RenderShadowCascadeSlice(ref frame, frame.ShadowVp, _shadowTarget!, ref entityBoneUniformsApplied);

        }

    }



    private bool TryGetShadowCasterBoundsForFrame(ref GlRenderFrame frame, out Vector3 min, out Vector3 max)

    {

        if (TryGetCachedShadowCasterBounds(out min, out max))

        {

            if (frame.Settings.ShowGroundMesh)

            {

                PreviewShadowFrustum.ExpandBoundsForGroundReceiver(ref min, ref max, PreviewStageConstants.GridWorldY);

            }



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



        if (!TryComputeVertexBounds(verts, stride, out min, out max))

        {

            return false;

        }



        if (frame.Settings.ShowGroundMesh)

        {

            PreviewShadowFrustum.ExpandBoundsForGroundReceiver(ref min, ref max, PreviewStageConstants.GridWorldY);

        }



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



    private void RenderShadowCascadeSlice(

        ref GlRenderFrame frame,

        Matrix4x4 shadowVp,

        GlShadowMapTarget target,

        ref bool entityBoneUniformsApplied)

    {

        target.BeginShadowPass();

        SetMatrixOnProgramLoc(_shadowProgram!, _shadowUniformLocs.LightViewProj, shadowVp);

        DrawShadowCasters(ref frame, shadowVp, ref entityBoneUniformsApplied);

        target.EndShadowPass();

    }



    private void DrawShadowCasters(
        ref GlRenderFrame frame,
        Matrix4x4 shadowViewProjection,
        ref bool entityBoneUniformsApplied)

    {

        var su = _shadowUniformLocs;

        if (frame.Settings.ShowGroundMesh && HasTerrainChunksToDraw)
        {
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
            DrawGroundTerrainChunks(
                frame.Gl,
                shadowViewProjection,
                frame.Eye,
                patches: false,
                enableParallaxSetting: false,
                setParallaxEnabled: static _ => { },
                shadowFullOnly: true);
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
            var useMaterialDrawRecords = TryUploadGenesisMaterialDrawRecords(ref frame);
            var useIndirectDrawCommands = TryUploadGenesisIndirectDrawCommands(blockModel);
            var useMaterialTextureArrays =
                TryEnsureMaterialTextureArrays(ref frame, useMaterialDrawRecords, out _);
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
            if (useMaterialDrawRecords)
            {
                MarkGenesisMaterialDrawRecordsSubmitted();
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


