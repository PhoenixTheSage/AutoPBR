using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend
{
    private const int MainPassShadowFarUnit = 4;
    private const int MainPassShadowNearUnit = 5;
    private const int MainPassSkyLutUnit = 6;

    private void GlRenderPassScene(ref GlRenderFrame frame)
    {
        if (_program is null || _albedo is null || _normal is null || _spec is null || _height is null || _mesh is null)
        {
            return;
        }

        EnsureGenesisProgramForFrame(ref frame);
        if (_program is not { IsValid: true })
        {
            return;
        }

        SyncGodRayToggleState(frame.Settings);
        SyncVolumetricToggleState(frame.Settings);

        frame.GodRayCaptureActive = TryBeginGodRaySceneRender(ref frame);

        // Restore main-pass framebuffer + viewport (BeginShadowPass snapshots & EndShadowPass restores
        // the GL viewport, but binding our actual default FBO again is cheap and explicit).
        if (!frame.GodRayCaptureActive)
        {
            if (frame.DefaultFbo != 0)
            {
                frame.Gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)frame.DefaultFbo);
            }
            else
            {
                frame.Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            }
        }

        var sceneVpX = frame.GodRayCaptureActive ? 0 : frame.VpX;
        var sceneVpY = frame.GodRayCaptureActive ? 0 : frame.VpY;
        var sceneVpW = frame.GodRayCaptureActive && frame.SceneCaptureW > 0 ? frame.SceneCaptureW : frame.Vw;
        var sceneVpH = frame.GodRayCaptureActive && frame.SceneCaptureH > 0 ? frame.SceneCaptureH : frame.Vh;
        frame.Gl.Viewport(sceneVpX, sceneVpY, (uint)sceneVpW, (uint)sceneVpH);
        frame.Gl.Disable(EnableCap.ScissorTest);
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

        // Camera must exist before sky / sun projection / froxel placement.
        var cam = frame.Scene.Camera;
        if (frame.FlyCamActive)
        {
            ComposeFlyEye(frame.FlyPosition, frame.FlyYaw, frame.FlyPitch, out frame.Eye, out frame.LookTarget);
        }
        else
        {
            ComposeOrbitEye(frame.OrbitBaseTarget, frame.OrbitPan, frame.OrbitYaw, frame.OrbitPitch, frame.OrbitDistance,
                out frame.Eye, out frame.LookTarget);
        }
        var aspect = frame.Vw / (float)Math.Max(frame.Vh, 1);
        var nearPlane = cam.NearPlane;
        var farPlane = cam.FarPlane;
        if (frame.Scene.SceneKind != PreviewSceneKind.ItemPlane)
        {
            // Idle / empty-subject: still frame depth around the stage so terrain is not clipped.
            var hasSubject = TryGetSubjectBoundsLocked(out var subjectMin, out var subjectMax);
            if (!hasSubject)
            {
                subjectMin = new Vector3(-0.5f, PreviewStageConstants.GridWorldY, -0.5f);
                subjectMax = new Vector3(0.5f, PreviewStageConstants.GridWorldY + 1f, 0.5f);
            }

            var envHalf = frame.Settings is { ShowBackgroundGrid: false, ShowGroundMesh: false }
                ? 0f
                : frame.Settings.ShowGroundMesh
                    ? TerrainEnvironmentHalfExtent
                    : PreviewStageConstants.GridHalfExtent;
            var envFloor = frame.Settings.ShowGroundMesh
                ? _terrainEnvFloorY
                : PreviewStageConstants.GridWorldY;
            var envCeiling = frame.Settings.ShowGroundMesh
                ? _terrainEnvCeilingY
                : PreviewStageConstants.GridWorldY + 0.05f;
            (nearPlane, farPlane) = PreviewCameraDepthRange.ForOrbitPreview(
                subjectMin,
                subjectMax,
                frame.OrbitDistance,
                frame.Eye,
                environmentHalfExtent: envHalf,
                environmentFloorY: envFloor,
                environmentCeilingY: envCeiling);
        }

        frame.Proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            cam.FieldOfViewDegrees * (MathF.PI / 180f),
            aspect,
            nearPlane,
            farPlane);
        frame.UnjitteredProj = frame.Proj;
        frame.PreviewTaaJitterNdc = Vector2.Zero;
        SyncPreviewTaaToggleState(frame.Settings);
        if (IsPreviewTaaActive(frame.Settings))
        {
            var jitterW = frame.GodRayCaptureActive && frame.SceneCaptureW > 0 ? frame.SceneCaptureW : frame.Vw;
            var jitterH = frame.GodRayCaptureActive && frame.SceneCaptureH > 0 ? frame.SceneCaptureH : frame.Vh;
            frame.PreviewTaaJitterNdc = CurrentPreviewTaaJitter(jitterW, jitterH, frame.Settings);
            frame.Proj = PreviewGlMatrices.ApplyProjectionJitter(
                frame.Proj,
                frame.PreviewTaaJitterNdc);
        }

        frame.View = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(frame.Eye, frame.LookTarget, Vector3.UnitY);
        frame.NearPlane = nearPlane;
        frame.FarPlane = farPlane;

        if (frame.WorldLightDir.LengthSquared() < 1e-8f)
        {
            frame.WorldLightDir = frame.Scene.Light.Direction.LengthSquared() < 1e-8f
                ? new Vector3(-0.35f, -0.85f, -0.4f)
                : Vector3.Normalize(frame.Scene.Light.Direction);
        }
        frame.LightDir = PreviewLightMath.SceneLightDirectionFromCelestialCycle(frame.WorldLightDir);

        var lutSkyReady = _atmoLutsValid && _atmoSkyViewTex != 0;
        var drawSky = frame.Settings.EnableAtmosphericSky && _atmoQuadVao != 0 &&
                      (_atmoSkyProgram is { IsValid: true } || _proceduralSkyProgram is { IsValid: true });
        if (drawSky)
        {
            frame.Gl.ClearColor(0.01f, 0.012f, 0.02f, 1f);
        }
        else
        {
            frame.Gl.ClearColor(0.12f, 0.12f, 0.14f, 1f);
        }

        frame.Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (drawSky)
        {
            frame.Gl.Disable(EnableCap.DepthTest);
            frame.Gl.DepthMask(false);
            DrawAtmosphereSky(frame.Gl, ref frame, lutSkyReady);
            frame.Gl.DepthMask(true);
            frame.Gl.Enable(EnableCap.DepthTest);
            frame.Gl.Clear(ClearBufferMask.DepthBufferBit);
        }

        // Sun disc + aureole are rendered by the sky shader (skySunDiscAureole); only the moon
        // remains a billboard, drawn before opaque geometry so depth testing hides it.
        frame.Gl.Enable(EnableCap.DepthTest);
        frame.Gl.DepthFunc(GLEnum.Lequal);
        DrawMoonBillboard(frame.Gl, frame.Proj, frame.View, frame.Eye, frame.WorldLightDir,
            farPlane,
            frame.Settings.AtmosphereMoonDiscStrength,
            frame.Settings.AtmosphereMoonDiscSize,
            frame.Settings.AtmosphereMoonGlowStrength,
            frame.Settings.AtmosphereMoonTextureSharpness,
            ShouldCullSolidBackFaces(frame.Scene.SceneKind, frame.BlockModel, frame.Settings));

        _program.Use();
        var u = _mainUniformLocs;
        if (frame.SettingsRevision != _lastMainPassAppliedSettingsRevision)
        {
            ApplyMainPassPerSettingsUniforms(ref frame, u);
            _lastMainPassAppliedSettingsRevision = frame.SettingsRevision;
        }

        var taaCurrentViewProj = frame.UnjitteredProj * frame.View;
        SetMatrixLoc(u.TaaCurrViewProj, taaCurrentViewProj);
        SetMatrixLoc(u.PrevViewProj, ResolvePreviewTaaPrevViewProj(taaCurrentViewProj));
        SetMatrixLoc(u.View, frame.View);
        SetMatrixLoc(u.Proj, frame.Proj);
        ApplyTerrainAerialFogUniforms(ref frame, u);
        SetMatrixLoc(u.LightViewProj, frame.ShadowVp);
        SetMatrixLoc(u.LightViewProjNear, frame.ShadowVpNear);

        SetVec3Loc(u.CameraPos, frame.Eye);
        SetVec3Loc(u.LightDir, frame.LightDir);
        SetVec3Loc(u.LightColor, PreviewLightMath.SceneLightColorFromCelestialCycle(
            frame.WorldLightDir,
            frame.Scene.Light.Color,
            frame.Settings.MoonWorldLightIntensity));

        // genesis.frag always declares shadow + sky samplers; pin unique units every main-pass bind so
        // GLES/ANGLE never leaves sampler2DShadow on unit 0 alongside uAlbedo.
        var shadowEnabledForShader = frame.ShadowAvailable;
        SetIntLoc(u.EnableShadowMap, shadowEnabledForShader ? 1 : 0);
        SetIntLoc(u.EnableShadowCascades, frame.ShadowCascadesActive ? 1 : 0);
        SetFloatLoc(u.CascadeSplitDistance, frame.CascadeSplitWorldDistance);
        SetFloatLoc(u.CascadeBlendWidth, frame.CascadeBlendWorldWidth);
        BindAndPinMainPassGlobalTextures(ref frame, u);

        // LabPBR grass plane under the grid; one texture tile per world unit (nearest + repeat).
        if (frame.Settings.ShowGroundMesh &&
            _grassGroundReady && _grassGroundAlbedo is not null)
        {
            TickTerrainStreaming(ref frame);
        }

        if (frame.Settings.ShowGroundMesh &&
            _grassGroundReady && _grassGroundAlbedo is not null && HasTerrainChunksToDraw)
        {
            var restoreCull = frame.Gl.IsEnabled(EnableCap.CullFace);
            frame.Gl.Enable(EnableCap.CullFace);
            frame.Gl.CullFace(TriangleFace.Back);
            SetMatrixLoc(u.Model, Matrix4x4.Identity);
            SetMatrixLoc(u.PrevModel, Matrix4x4.Identity);
            var groundParallax = frame.Settings.EnableParallax && _grassGroundHasHeight;
            var groundNormal = frame.Settings.EnableNormalMap && _grassGroundHasNormal;
            var groundSpec = frame.Settings.EnableSpecularMap && _grassGroundHasSpecular;
            SetIntLoc(u.EnableParallax, groundParallax ? 1 : 0);
            SetFloatLoc(u.ParallaxUvScale, 1f);
            SetVec2Loc(u.TextureAtlasScale, Vector2.One);
            SetIntLoc(u.EnableParallaxAo, groundParallax && frame.Settings.EnableParallaxAo ? 1 : 0);
            SetIntLoc(u.EnableParallaxShadow, groundParallax && frame.Settings.EnableParallaxShadow ? 1 : 0);
            SetIntLoc(u.EnableNormalMap, groundNormal ? 1 : 0);
            SetIntLoc(u.EnableSpecularMap, groundSpec ? 1 : 0);
            SetIntLoc(u.EnableTessellationDisplacement, 0);
            SetIntLoc(u.SceneKind, 0);
            SetIntLoc(u.IsGroundPass, 1);
            SetIntLoc(u.EntityAlphaMode, 0);
            SetIntLoc(u.GenesisUseMaterialDrawRecord, 0);
            SetIntLoc(u.GenesisUseMaterialTextureArray, 0);
            SetIntLoc(u.GenesisDrawRecordIndex, 0);
            ApplyEntitySkinningUniforms(_program, 0, 0, 0f);
            SetIntLoc(u.HasNormal, _grassGroundHasNormal ? 1 : 0);
            SetIntLoc(u.HasSpecular, _grassGroundHasSpecular ? 1 : 0);
            SetIntLoc(u.HasHeight, _grassGroundHasHeight ? 1 : 0);
            SetVec2Loc(u.ParallaxHeightTexSize, _grassGroundHasHeight && _grassGroundMaterial is not null
                ? new Vector2(Math.Max(1, _grassGroundMaterial.Width), Math.Max(1, _grassGroundMaterial.Height))
                : Vector2.One);
            _grassGroundAlbedo.Bind(0);
            _grassGroundNormal!.Bind(1);
            _grassGroundSpec!.Bind(2);
            _grassGroundHeight!.Bind(3);
            SetIntLoc(u.Albedo, 0);
            SetIntLoc(u.Normal, 1);
            SetIntLoc(u.Specular, 2);
            SetIntLoc(u.Height, 3);
            // Idle/non-array draws still need complete sampler2DArray units when the active
            // Genesis program was compiled with GENESIS_MATERIAL_TEXTURE_ARRAYS.
            BindFallbackMaterialTextureArraysIfPresent(u);
            var viewProjection = frame.UnjitteredProj * frame.View;
            var enableParallaxAo = frame.Settings.EnableParallaxAo;
            var enableParallaxShadow = frame.Settings.EnableParallaxShadow;
            // Tessellation programs require patch primitives even when displacement is disabled.
            DrawGroundTerrainChunks(
                frame.Gl,
                viewProjection,
                frame.Eye,
                patches: _mainProgramUsesTessellation,
                enableParallaxSetting: groundParallax,
                setParallaxEnabled: pom =>
                {
                    SetIntLoc(u.EnableParallax, pom ? 1 : 0);
                    SetIntLoc(u.EnableParallaxAo, pom && enableParallaxAo ? 1 : 0);
                    SetIntLoc(u.EnableParallaxShadow, pom && enableParallaxShadow ? 1 : 0);
                });
            SetIntLoc(u.IsGroundPass, 0);
            if (!restoreCull)
            {
                frame.Gl.Disable(EnableCap.CullFace);
            }
        }

        if (frame.Settings.ShowBackgroundGrid && _lineProgram?.IsValid == true &&
            _gridVertexCount > 0)
        {
            DrawBackgroundGrid(frame.Gl, frame.Proj, frame.View);
            // DrawBackgroundGrid binds the line program; restore main frame.Material program before mesh uniforms.
            _program.Use();
            BindAndPinMainPassGlobalTextures(ref frame, u);
        }

        SetMatrixLoc(u.Model, frame.ModelMatrix);
        SetMatrixLoc(u.PrevModel, ResolvePreviewTaaPrevSubjectModel(frame.ModelMatrix));
        SetFloatLoc(u.ParallaxUvScale, 1f);
        SetVec2Loc(u.ParallaxHeightTexSize, Vector2.One);
        SetVec2Loc(u.TextureAtlasScale, Vector2.One);
        SetIntLoc(u.EnableParallax, frame.EnableParallaxEff ? 1 : 0);
        SetIntLoc(u.EnableParallaxAo, frame.EnableParallaxAoEff ? 1 : 0);
        SetIntLoc(u.EnableParallaxShadow, frame.EnableParallaxShadowEff ? 1 : 0);
        SetIntLoc(u.EnableNormalMap, frame.EnableNormalMapEff ? 1 : 0);
        SetIntLoc(u.EnableSpecularMap, frame.EnableSpecularMapEff ? 1 : 0);
        SetIntLoc(u.SceneKind, frame.Scene.SceneKind == PreviewSceneKind.ItemPlane ? 1 : 0);
        SetIntLoc(u.IsGroundPass, 0);
        SetIntLoc(u.EntityAlphaMode, frame.EntityAlphaModeUniform);
        SetIntLoc(u.GenesisUseMaterialDrawRecord, 0);
        SetIntLoc(u.GenesisUseMaterialTextureArray, 0);
        SetIntLoc(u.GenesisDrawRecordIndex, 0);

        if (!frame.Settings.DrawPreviewSubject || _mesh.IndexCount <= 0)
        {
            if (!_loggedZeroIndex && frame.Settings.DrawPreviewSubject && _mesh.IndexCount <= 0)
            {
                EmitDiagnostic(
                    $"[3D preview] Draw skipped: index buffer empty (frame.Scene={frame.Scene.SceneKind}, sceneMeshCount={frame.Scene.Meshes.Count}, frame.MeshDirty={frame.MeshDirty}).");
                _loggedZeroIndex = true;
            }
        }
        else if (frame.BlockModel is not null && frame.BlockSlots is { Length: > 0 })
        {
            if (!_loggedMeshReady)
            {
                var subjectTag = frame.BlockModel.EmulatedRebake is not null
                    ? frame.BlockModel.EntityGpuVerticesInPreviewSpace ? "parity-cpu-rebake" : "entity"
                    : "block-model";
                EmitDiagnostic(
                    $"[3D preview] Draw ready: indexCount={_mesh.IndexCount}, subject={subjectTag}, frame.Scene={frame.Scene.SceneKind}, lightYaw={frame.Settings.LightYawDegrees:F1}, lightPitch={frame.Settings.LightPitchDegrees:F1}.");
                _loggedMeshReady = true;
                EmitDepthLayerDiagnostic(frame.BlockModel, nearPlane, farPlane, frame.Gl);
            }
            var blendWasEnabled = frame.Gl.IsEnabled(EnableCap.Blend);
            var activeBatchBlend = blendWasEnabled;
            var uploadedMaterialIndex = -1;
            var entityBoneUniformsApplied = false;
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

            SetIntLoc(u.GenesisUseMaterialDrawRecord, useMaterialDrawRecords ? 1 : 0);
            SetIntLoc(u.GenesisUseMaterialTextureArray, useMaterialTextureArrays ? 1 : 0);
            if (useMaterialTextureArrays)
            {
                BindMainPassMaterialTextureArrays(u);
            }
            if (frame.EntityBonePaletteUploaded)
            {
                BindEntityBoneSkinningUboBlocks();
            }

            _mesh.BindVertexArray();
            for (var batchIndex = 0; batchIndex < blockModel.DrawBatches.Length; batchIndex++)
            {
                var batch = blockModel.DrawBatches[batchIndex];
                if ((uint)batch.MaterialIndex >= (uint)blockSlots.Length)
                {
                    continue;
                }

                var batchUsesTranslucentOverlay =
                    batch.LayerPolicy.Kind == PreviewDepthLayerKind.TranslucentOverlay;
                var batchAlphaMode = batchUsesTranslucentOverlay
                    ? (int)PreviewEntityAlphaMode.Blend
                    : frame.EntityAlphaModeUniform;
                var batchBlend = frame.EntityBlendDraw || batchUsesTranslucentOverlay;
                if (batchBlend)
                {
                    if (!activeBatchBlend)
                    {
                        frame.Gl.Enable(EnableCap.Blend);
                        frame.Gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    }

                    activeBatchBlend = true;
                }
                else if (activeBatchBlend)
                {
                    frame.Gl.Disable(EnableCap.Blend);
                    activeBatchBlend = false;
                }

                SetIntLoc(u.EntityAlphaMode, batchAlphaMode);
                SetIntLoc(u.GenesisDrawRecordIndex, useMaterialDrawRecords ? batchIndex : 0);

                var slot = blockSlots[batch.MaterialIndex];
                var materialChanged = batch.MaterialIndex != uploadedMaterialIndex;
                if (materialChanged && !useMaterialTextureArrays)
                {
                    UploadMaterial(frame.Gl, slot, nearest: true);
                    uploadedMaterialIndex = batch.MaterialIndex;
                    BindSubjectMaterialTextures();
                    PinMainPassMaterialSamplerUniforms(u);
                }

                var bHasN = slot.NormalRgba is { Length: > 0 };
                var bHasS = slot.SpecularRgba is { Length: > 0 };
                var bHasH = slot.HeightRgba is { Length: > 0 };
                var batchAllowsParallax = !frame.EntityEmulatedPreview || batch.EnableParallax;
                var batchParallax = frame.EnableParallaxEff && batchAllowsParallax && bHasH;
                SetIntLoc(u.EnableParallax, batchParallax ? 1 : 0);
                SetIntLoc(u.EnableParallaxAo, batchParallax && frame.EnableParallaxAoEff ? 1 : 0);
                SetIntLoc(u.EnableParallaxShadow, batchParallax && frame.EnableParallaxShadowEff ? 1 : 0);
                SetFloatLoc(u.ParallaxUvScale, frame.EntityEmulatedPreview
                    ? EntityParallaxUvScale(slot)
                    : 1f);
                SetVec2Loc(u.TextureAtlasScale, frame.EntityEmulatedPreview
                    ? EntityTextureAtlasScale(slot)
                    : Vector2.One);
                SetIntLoc(u.EnableTessellationDisplacement,
                    _mainProgramUsesTessellation &&
                    frame.EnableTessellationDisplacementEff &&
                    batchAllowsParallax &&
                    bHasH
                        ? 1
                        : 0);
                SetIntLoc(u.HasNormal, bHasN ? 1 : 0);
                SetIntLoc(u.HasSpecular, bHasS ? 1 : 0);
                SetIntLoc(u.HasHeight, bHasH ? 1 : 0);
                SetVec2Loc(u.ParallaxHeightTexSize, bHasH
                    ? new Vector2(Math.Max(1, slot.Width), Math.Max(1, slot.Height))
                    : Vector2.One);
                if (!entityBoneUniformsApplied)
                {
                    ApplyEntityBoneSkinningUniformsBeforeDraw(
                        _program,
                        _mainEntityUniformLocs,
                        blockModel,
                        blockModel.EntityGpuMeshSpaceLiftY,
                        frame.EntityBoneSnapshotValid,
                        frame.EntityBoneSnapshotCount,
                        frame.Settings.EnableEntityAnimation,
                        frame.EntityBonePaletteUploaded,
                        "main",
                        bindBoneUboBlocks: !frame.EntityBonePaletteUploaded);
                    entityBoneUniformsApplied = true;
                }

                var batchGroupCount = CountMainPassMultiDrawGroup(
                    blockModel.DrawBatches,
                    batchIndex,
                    blockSlots.Length,
                    frame.EntityBlendDraw,
                    useIndirectDrawCommands &&
                    CanUseGenesisMultiDrawGroups(useMaterialDrawRecords),
                    allowMaterialChanges: useMaterialTextureArrays);

                using (OpenGlPreviewLayerDepthState.Apply(frame.Gl, batch.LayerPolicy))
                {
                    if (EntityPreviewDebugSettings.ShowDepthLayerDebug)
                    {
                        SetVec3Loc(
                            u.PreviewLayerDebugTint,
                            PreviewDrawLayerPolicy.GetDebugTint(batch.LayerPolicy.Kind));
                    }

                    var gpuCulledDrawn =
                        batchGroupCount > 1 &&
                        TryDrawGpuCulledBatchGroup(
                            blockModel,
                            batchIndex,
                            batchGroupCount,
                            frame.UnjitteredProj * frame.View,
                            frame.Eye,
                            frame.ModelMatrix,
                            _program!,
                            batchBlend ? "main-alpha" : "main",
                            patches: _mainProgramUsesTessellation,
                            preserveOrder: batchBlend,
                            boundsPadding: _mainProgramUsesTessellation
                                ? Math.Clamp(frame.Settings.TessellationDisplacementStrength, 0f, 0.20f)
                                : 0f);
                    if (!gpuCulledDrawn)
                    {
                        DrawPreviewBatchRange(
                            batch,
                            batchIndex,
                            _mainProgramUsesTessellation,
                            useIndirectDrawCommands,
                            useMultiDrawGroups: batchGroupCount > 1,
                            groupCount: batchGroupCount);
                    }
                }

                batchIndex += batchGroupCount - 1;
            }

            _mesh.UnbindVertexArray();

            if (useMaterialDrawRecords)
            {
                MarkGenesisMaterialDrawRecordsSubmitted();
            }

            if (frame.EntityBonePaletteUploaded)
            {
                _entityBoneUpload?.MarkSubmitted();
                _entityPrevBoneUpload?.MarkSubmitted();
                _entityNormalBoneUpload?.MarkSubmitted();
            }

            if (!blendWasEnabled)
            {
                frame.Gl.Disable(EnableCap.Blend);
            }
            else
            {
                frame.Gl.Enable(EnableCap.Blend);
            }

            SetIntLoc(u.EntityAlphaMode, frame.EntityAlphaModeUniform);
            SetIntLoc(u.GenesisUseMaterialDrawRecord, 0);
            SetIntLoc(u.GenesisUseMaterialTextureArray, 0);
            SetIntLoc(u.GenesisDrawRecordIndex, 0);
        }
        else
        {
            var hasN = frame.Material?.NormalRgba is { Length: > 0 };
            var hasS = frame.Material?.SpecularRgba is { Length: > 0 };
            var hasH = frame.Material?.HeightRgba is { Length: > 0 };
            SetFloatLoc(u.ParallaxUvScale, 1f);
            SetVec2Loc(u.TextureAtlasScale, Vector2.One);
            SetIntLoc(u.EnableTessellationDisplacement,
                _mainProgramUsesTessellation && frame.EnableTessellationDisplacementEff && hasH ? 1 : 0);
            SetIntLoc(u.HasNormal, hasN ? 1 : 0);
            SetIntLoc(u.HasSpecular, hasS ? 1 : 0);
            SetIntLoc(u.HasHeight, hasH ? 1 : 0);
            SetVec2Loc(u.ParallaxHeightTexSize, hasH && frame.Material is not null
                ? new Vector2(Math.Max(1, frame.Material.Width), Math.Max(1, frame.Material.Height))
                : Vector2.One);
            _albedo.Bind(0);
            _normal.Bind(1);
            _spec.Bind(2);
            _height.Bind(3);
            SetIntLoc(u.Albedo, 0);
            SetIntLoc(u.Normal, 1);
            SetIntLoc(u.Specular, 2);
            SetIntLoc(u.Height, 3);
            SetIntLoc(u.GenesisUseMaterialTextureArray, 0);
            if (!_loggedMeshReady)
            {
                EmitDiagnostic(
                    $"[3D preview] Draw ready: indexCount={_mesh.IndexCount}, frame.Scene={frame.Scene.SceneKind}, lightYaw={frame.Settings.LightYawDegrees:F1}, lightPitch={frame.Settings.LightPitchDegrees:F1}.");
                _loggedMeshReady = true;
            }

            ApplyEntitySkinningUniforms(_program, 0, 0, 0f);
            _mesh.Draw(_mainProgramUsesTessellation);
        }

        FinishGodRaySceneRender(ref frame);
    }

    private static float EntityParallaxUvScale(PreviewMaterial slot)
    {
        var atlasMax = Math.Max(slot.Width, slot.Height);
        if (atlasMax <= 16)
        {
            return 1f;
        }

        return Math.Clamp(16f / atlasMax, 0.02f, 1f);
    }

    private static Vector2 EntityTextureAtlasScale(PreviewMaterial slot) =>
        PreviewEntityTextureAtlasScale.Resolve(
            slot.Width,
            slot.Height,
            slot.BakeAtlasWidth,
            slot.BakeAtlasHeight);

    private void BindAndPinMainPassGlobalTextures(ref GlRenderFrame frame, MainProgramUniformLocs u)
    {
        PinMainPassMaterialSamplerUniforms(u);

        if (_shadowTarget is not null)
        {
            frame.Gl.ActiveTexture(TextureUnit.Texture0 + MainPassShadowFarUnit);
            frame.Gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        }

        if (frame.ShadowCascadesActive && _shadowTargetCascadeNear is not null)
        {
            frame.Gl.ActiveTexture(TextureUnit.Texture0 + MainPassShadowNearUnit);
            frame.Gl.BindTexture(TextureTarget.Texture2D, _shadowTargetCascadeNear.DepthTextureHandle);
        }
        else if (_shadowTarget is not null)
        {
            frame.Gl.ActiveTexture(TextureUnit.Texture0 + MainPassShadowNearUnit);
            frame.Gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        }

        if (_atmoSkyViewTex != 0)
        {
            frame.Gl.ActiveTexture(TextureUnit.Texture0 + MainPassSkyLutUnit);
            frame.Gl.BindTexture(TextureTarget.Texture2D, _atmoSkyViewTex);
        }

        if (u.ShadowMap >= 0)
        {
            SetIntLoc(u.ShadowMap, MainPassShadowFarUnit);
        }

        if (u.ShadowMapNear >= 0)
        {
            SetIntLoc(u.ShadowMapNear, MainPassShadowNearUnit);
        }

        if (u.AtmoSkyViewLut >= 0 && _atmoSkyViewTex != 0)
        {
            SetIntLoc(u.AtmoSkyViewLut, MainPassSkyLutUnit);
        }
    }

    private void BindSubjectMaterialTextures()
    {
        _albedo!.Bind(0);
        _normal!.Bind(1);
        _spec!.Bind(2);
        _height!.Bind(3);
    }

    private void PinMainPassMaterialSamplerUniforms(MainProgramUniformLocs u)
    {
        if (u.Albedo >= 0)
        {
            SetIntLoc(u.Albedo, 0);
        }

        if (u.Normal >= 0)
        {
            SetIntLoc(u.Normal, 1);
        }

        if (u.Specular >= 0)
        {
            SetIntLoc(u.Specular, 2);
        }

        if (u.Height >= 0)
        {
            SetIntLoc(u.Height, 3);
        }
    }

    private void ApplyTerrainAerialFogUniforms(ref GlRenderFrame frame, MainProgramUniformLocs u)
    {
        var fogStrength = frame.Settings.EnableAtmosphericSky && frame.Settings.ShowGroundMesh
            ? Math.Max(frame.Settings.AerialFogStrength, 0.85f)
            : (frame.Settings.EnableAtmosphericSky ? frame.Settings.AerialFogStrength : 0f);
        SetFloatLoc(u.AerialFogStrength, fogStrength);
        var hardR = _terrainStreamer?.HardRingWorldRadius
                    ?? PreviewStageConstants.TerrainDefaultChunkViewDistance *
                       (float)PreviewStageConstants.TerrainChunkSize;
        var lodR = _terrainStreamer?.LodRingWorldRadius
                   ?? hardR + PreviewStageConstants.TerrainLodRingChunks *
                              PreviewStageConstants.TerrainChunkSize;
        SetFloatLoc(u.TerrainFogStart, hardR * 0.85f);
        SetFloatLoc(u.TerrainFogEnd, lodR * 0.98f);
    }

    private void ApplyMainPassPerSettingsUniforms(ref GlRenderFrame frame, MainProgramUniformLocs u)
    {
        SetFloatLoc(u.Ambient, frame.Settings.AmbientStrength);
        SetFloatLoc(u.NormalStrength, frame.Settings.NormalStrength);
        SetFloatLoc(u.HeightStrength, frame.Settings.HeightStrength);
        SetFloatLoc(u.SpecularStrength, frame.Settings.SpecularStrength);
        SetFloatLoc(u.RoughnessScale, frame.Settings.RoughnessScale);
        SetFloatLoc(u.Exposure, frame.Settings.Exposure);
        SetIntLoc(u.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
        SetFloatLoc(u.ParallaxAoStrength, frame.Settings.ParallaxAoStrength);
        SetIntLoc(u.ParallaxTraceLayers, Math.Clamp(frame.Settings.ParallaxTraceLayers, 8, 128));
        SetIntLoc(u.ParallaxRefineSteps, Math.Clamp(frame.Settings.ParallaxRefineSteps, 0, 8));
        SetIntLoc(u.ParallaxShadowSamples, Math.Clamp(frame.Settings.ParallaxShadowSamples, 4, 64));
        SetFloatLoc(u.ParallaxShadowSoftness, Math.Clamp(frame.Settings.ParallaxShadowSoftness, 0f, 4f));
        SetFloatLoc(u.ParallaxMaxUvShift, Math.Clamp(frame.Settings.ParallaxMaxUvShift, 0.05f, 0.75f));
        SetFloatLoc(u.TessellationLevel, Math.Clamp(frame.Settings.TessellationLevel, 1f, 16f));
        SetFloatLoc(u.TessellationDisplacementStrength, Math.Clamp(frame.Settings.TessellationDisplacementStrength, 0f, 0.20f));
        SetFloatLoc(u.AlphaCutoff, frame.Settings.AlphaCutoff);
        SetIntLoc(u.ItemAlphaBlend, frame.Settings.ItemUseAlphaBlend ? 1 : 0);
        SetIntLoc(u.PreviewDepthLayerDebug, EntityPreviewDebugSettings.ShowDepthLayerDebug ? 1 : 0);
        SetVec3Loc(u.PreviewLayerDebugTint, Vector3.One);
        SetIntLoc(u.EnableSss, frame.Settings.EnableSss ? 1 : 0);
        SetIntLoc(u.EnableIbl, frame.Settings.EnableIbl ? 1 : 0);
        SetFloatLoc(u.SssStrength, frame.Settings.SssStrength);
        SetFloatLoc(u.IblStrength, frame.Settings.IblStrength);
        SetFloatLoc(u.EmissionStrength, frame.Settings.EmissionStrength);
        SetIntLoc(u.EnableAtmosphericSky, frame.Settings.EnableAtmosphericSky ? 1 : 0);
        SetFloatLoc(u.AtmosphereSunIntensity, frame.Settings.AtmosphereSunIntensity);
        SetVec3Loc(u.SkyTint, new Vector3(0.55f, 0.62f, 0.74f));
        SetVec3Loc(u.GroundTint, new Vector3(0.22f, 0.20f, 0.18f));
        SetFloatLoc(u.ShadowMinBias, frame.Settings.ShadowMinBias);
        SetFloatLoc(u.ShadowMaxBias, frame.Settings.ShadowMaxBias);
        SetFloatLoc(u.ShadowSoftnessTexels, Math.Clamp(frame.Settings.ShadowSoftnessTexels, 0f, 8f));
        var shadowRes = _shadowTarget?.Resolution ?? Math.Clamp(frame.Settings.ShadowMapResolution, 256, 4096);
        SetVec2Loc(u.ShadowTexelSize, new Vector2(1f / shadowRes, 1f / shadowRes));
    }
}
