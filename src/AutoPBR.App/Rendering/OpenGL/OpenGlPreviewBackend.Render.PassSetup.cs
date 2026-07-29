using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend
{
    private void GlRenderPassSetup(ref GlRenderFrame frame)
    {
        SyncEntityPreviewDebugRevision(frame.BlockModel);

        // When idle animation is off, clear spacing so the next enable always rebakes immediately (avoids one throttled skip).
        if (!frame.Settings.EnableEntityAnimation)
        {
            _lastEmulatedEntityRebakeRenderTime = double.NegativeInfinity;
        }

        frame.EntityEmulatedPreview = IsEntityEmulatedPreview(frame.BlockModel);
        frame.EntityRebakeCtx = frame.BlockModel?.EmulatedRebake;
        frame.EntityEmulatedMaterialsOk = frame.EntityEmulatedPreview &&
            frame.EntityRebakeCtx is not null &&
            frame.BlockSlots is { Length: > 0 } &&
            frame.BlockModel!.Materials.Length == frame.EntityRebakeCtx.OrderedTextureZipPaths.Length;

        frame.EntityEmulatedAnimClock = 0f;
        frame.EntityEmulatedPauseEdge = false;
        // Keep in sync with TryBuildStaticMesh / GPU bone fill: frame.RenderTime * speed * amp (see TryRebakeMesh / TryFillEmulatedEntityBoneMatrices).
        // Must not depend on materials being ready; otherwise frame.ModelMatrix wobble uses a different phase than bones when amp != 1.
        if (frame.EntityEmulatedPreview && frame.BlockModel is not null && frame.EntityRebakeCtx is not null)
        {
            frame.EntityEmulatedAnimClock = PreviewRenderPassSetup.ResolveEntityEmulatedAnimClock(
                frame,
                ref _prevPauseEntityIdleAnimation,
                ref _frozenEntityIdleAnimClock,
                out frame.EntityEmulatedPauseEdge);
        }

        frame.UploadedLiveEntityAnim = false;
        var setupAnimMotion = frame.Settings.EnableEntityAnimation;
        if (!setupAnimMotion)
        {
            frame.EntityEmulatedAnimClock = 0f;
        }

        var bindPoseRebakeKey = PreviewRenderPassSetup.ResolveEntityBindRebakeKey(
            setupAnimMotion,
            frame.EntityRebakeCtx);
        var bindPoseCommitted = bindPoseRebakeKey is not null &&
            string.Equals(bindPoseRebakeKey, _entityBindPoseCommittedKey, StringComparison.Ordinal);
        var parityCatalogCpuBindReady = frame.EntityRebakeCtx is not null &&
            frame.BlockModel is not null &&
            PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(frame.EntityRebakeCtx.AssetArchivePath) &&
            PreviewRenderPassSetup.IsParityCatalogCpuBindReady(
                setupAnimMotion,
                frame.BlockModel,
                bindPoseCommitted);
        if (parityCatalogCpuBindReady)
        {
            // Committed parity-catalog CPU mesh: do not let the generic fallback path clobber the VBO
            // with pack-converter or unit-cube geometry when TryCommit is intentionally skipped this frame.
            frame.UploadedLiveEntityAnim = true;
            if (frame.MeshDirty &&
                frame.BlockModel is
                {
                    InterleavedVertices.Length: > 0,
                    Indices.Length: > 0
                } committedCpu &&
                committedCpu.InterleavedVertices.Length % PreviewMesh.FloatsPerVertex == 0)
            {
                UploadPreviewMesh(committedCpu.InterleavedVertices, committedCpu.Indices);
                lock (_sync)
                {
                    _meshDirty = false;
                }
            }
        }
        var needsBindPoseMesh = PreviewRenderPassSetup.NeedsBindPoseMesh(
            parityCatalogCpuBindReady,
            frame.MeshDirty,
            bindPoseCommitted,
            frame.BlockModel);
        EntityRebakeResult? bindGpuPreparation = null;
        EntityRebakeResult? bindCpuPreparation = null;
        if (frame.EntityEmulatedMaterialsOk &&
            frame.BlockModel is not null &&
            frame.EntityRebakeCtx is not null &&
            !frame.Settings.EnableEntityAnimation &&
            needsBindPoseMesh &&
            !PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(
                frame.EntityRebakeCtx.AssetArchivePath))
        {
            var asyncBindKey = bindPoseRebakeKey ??
                PreviewRenderPassSetup.BuildEntityGpuBindRebakeKey(
                    frame.EntityRebakeCtx);
            if (string.Equals(
                    asyncBindKey,
                    _emulatedGpuSkinPrepFailedKey,
                    StringComparison.Ordinal))
            {
                if (TryTakeEntityPreparationResult(
                        EntityRebakeWorkKind.CpuRebake,
                        asyncBindKey,
                        out var cpuResult))
                {
                    if (cpuResult.Success)
                    {
                        ApplyEntityRebakeContextResult(
                            frame.EntityRebakeCtx,
                            cpuResult);
                    }

                    bindCpuPreparation = cpuResult;
                }
                else
                {
                    EnqueueEntityRebakeRequest(
                        ref frame,
                        setupAnimMotion: false,
                        requestKey: asyncBindKey,
                        workKind: EntityRebakeWorkKind.CpuRebake,
                        animationTimeSeconds: 0f);
                }
            }
            else if (TryTakeEntityPreparationResult(
                         EntityRebakeWorkKind.GpuSkinPrepare,
                         asyncBindKey,
                         out var gpuResult))
            {
                if (gpuResult.Success)
                {
                    ApplyEntityRebakeContextResult(
                        frame.EntityRebakeCtx,
                        gpuResult);
                    bindGpuPreparation = gpuResult;
                }
                else
                {
                    _emulatedGpuSkinPrepFailedKey = asyncBindKey;
                    EnqueueEntityRebakeRequest(
                        ref frame,
                        setupAnimMotion: false,
                        requestKey: asyncBindKey,
                        workKind: EntityRebakeWorkKind.CpuRebake,
                        animationTimeSeconds: 0f);
                }
            }
            else
            {
                EnqueueEntityRebakeRequest(
                    ref frame,
                    setupAnimMotion: false,
                    requestKey: asyncBindKey,
                    workKind: EntityRebakeWorkKind.GpuSkinPrepare,
                    animationTimeSeconds: 0f);
            }
        }

        if (frame.EntityEmulatedMaterialsOk &&
            frame.BlockModel is not null &&
            frame.EntityRebakeCtx is not null &&
            !frame.Settings.EnableEntityAnimation &&
            needsBindPoseMesh &&
            PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(frame.EntityRebakeCtx.AssetArchivePath) &&
            TryCommitParityCatalogCpuBindPoseMesh(
                ref frame,
                bindPoseRebakeKey,
                setupAnimMotion))
        {
        }
        else if (frame.EntityEmulatedMaterialsOk &&
            frame.BlockModel is not null &&
            frame.EntityRebakeCtx is not null &&
            !frame.Settings.EnableEntityAnimation &&
            needsBindPoseMesh &&
            !PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(frame.EntityRebakeCtx.AssetArchivePath) &&
            bindGpuPreparation is
            {
                Success: true,
                InterleavedVertices: not null,
                Indices: not null,
                DrawBatches: not null,
            })
        {
            var bindGpuVerts = bindGpuPreparation.InterleavedVertices;
            var bindGpuIdx = bindGpuPreparation.Indices;
            var bindGpuBatches = bindGpuPreparation.DrawBatches;
            var bindGpuBoneCount = bindGpuPreparation.GpuBoneCount;
            var bindGpuLift = bindGpuPreparation.GpuMeshSpaceLiftY;
            _emulatedGpuSkinPrepFailedKey = null;
            frame.EntityRebakeCtx.GpuPreparedBoneCount = bindGpuBoneCount;
            frame.EntityRebakeCtx.GpuBoundCpuMeshFingerprint =
                frame.EntityRebakeCtx.PackConverterCpuMeshFingerprint;
            var bindGpuBounds = PreviewGpuSkinnedBounds.TryBuild(
                bindGpuBatches,
                bindGpuVerts,
                bindGpuIdx,
                EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex);
            var bindGpuSubject = new PreviewModelSubject
            {
                InterleavedVertices = bindGpuVerts,
                Indices = bindGpuIdx,
                DrawBatches = bindGpuBatches,
                Materials = frame.BlockModel.Materials,
                PrimaryMaterialIndex = frame.BlockModel.PrimaryMaterialIndex,
                Sprite2DFoliageTarget = frame.BlockModel.Sprite2DFoliageTarget,
                EnableRenderTimeAnimation = frame.BlockModel.EnableRenderTimeAnimation,
                AnimationPreset = frame.BlockModel.AnimationPreset,
                EmulatedRebake = frame.BlockModel.EmulatedRebake,
                GpuEntityBoneSkinning = true,
                VertexStrideFloats = EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex,
                EntityGpuMeshSpaceLiftY = bindGpuLift,
                EntityGpuVerticesInPreviewSpace = false,
                GpuSkinnedBounds = bindGpuBounds,
                EntityPreviewAnchorOffset = frame.BlockModel.EntityPreviewAnchorOffset,
                EntityPreviewPlacementApplied = true,
                MeshProvenance = frame.EntityRebakeCtx.MeshProvenance ?? frame.BlockModel.MeshProvenance
            };
            frame.BlockModel = bindGpuSubject;
            UploadPreviewMesh(
                bindGpuVerts,
                bindGpuIdx,
                EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex);
            frame.UploadedLiveEntityAnim = true;
            if (bindPoseRebakeKey is not null)
            {
                _entityBindPoseCommittedKey = bindPoseRebakeKey;
            }

            lock (_sync)
            {
                _blockModelSubject = bindGpuSubject;
                if (frame.MeshDirty)
                {
                    _meshDirty = false;
                }
            }

            EmitEntityBindPosePrepDiagnostic(
                bindPoseRebakeKey,
                bindGpuBoneCount,
                bindGpuVerts.Length / EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex,
                bindGpuIdx.Length,
                bindGpuLift,
                setupAnimMotion: false);
            EmitParityCatalogPlacementDiagnostic(
                frame.BlockModel,
                frame.EntityRebakeCtx,
                setupAnimMotion,
                gpuSkinning: true,
                frame.EntityEmulatedAnimClock);
        }
        else if (frame.EntityEmulatedMaterialsOk &&
            frame.BlockModel is not null &&
            frame.EntityRebakeCtx is not null &&
            !frame.Settings.EnableEntityAnimation &&
            needsBindPoseMesh &&
            !PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(frame.EntityRebakeCtx.AssetArchivePath) &&
            bindCpuPreparation is
            {
                Success: true,
                InterleavedVertices: not null,
                Indices: not null,
                DrawBatches: not null,
            })
        {
            var bindCpuVerts = bindCpuPreparation.InterleavedVertices;
            var bindCpuIdx = bindCpuPreparation.Indices;
            var bindCpuBatches = bindCpuPreparation.DrawBatches;
            _emulatedGpuSkinPrepFailedKey = null;
            frame.EntityRebakeCtx.GpuPreparedBoneCount = null;
            frame.EntityRebakeCtx.GpuBindPoseInverseLocalToParent = null;
            frame.EntityRebakeCtx.GpuBindPoseBonePalette = null;
            frame.EntityRebakeCtx.GpuBindPoseInterleavedVertices = null;
            var bindCpuSubject = new PreviewModelSubject
            {
                InterleavedVertices = bindCpuVerts,
                Indices = bindCpuIdx,
                DrawBatches = bindCpuBatches,
                Materials = frame.BlockModel.Materials,
                PrimaryMaterialIndex = frame.BlockModel.PrimaryMaterialIndex,
                Sprite2DFoliageTarget = frame.BlockModel.Sprite2DFoliageTarget,
                EnableRenderTimeAnimation = frame.BlockModel.EnableRenderTimeAnimation,
                AnimationPreset = frame.BlockModel.AnimationPreset,
                EmulatedRebake = frame.BlockModel.EmulatedRebake,
                GpuEntityBoneSkinning = false,
                VertexStrideFloats = 0,
                EntityGpuMeshSpaceLiftY = 0f,
                EntityPreviewAnchorOffset = frame.BlockModel.EntityPreviewAnchorOffset,
                EntityPreviewPlacementApplied = true,
                MeshProvenance = frame.EntityRebakeCtx.MeshProvenance ?? frame.BlockModel.MeshProvenance
            };
            frame.BlockModel = bindCpuSubject;
            UploadPreviewMesh(bindCpuVerts, bindCpuIdx);
            frame.UploadedLiveEntityAnim = true;
            if (bindPoseRebakeKey is not null)
            {
                _entityBindPoseCommittedKey = bindPoseRebakeKey;
            }

            lock (_sync)
            {
                _blockModelSubject = bindCpuSubject;
                if (frame.MeshDirty)
                {
                    _meshDirty = false;
                }
            }

            EmitDiagnostic(
                $"[3D preview] Emulated entity CPU bind-pose mesh (GPU prep failed, animation off): verts={bindCpuVerts.Length / PreviewMesh.FloatsPerVertex}, indices={bindCpuIdx.Length}.");
            EmitParityCatalogPlacementDiagnostic(
                frame.BlockModel,
                frame.EntityRebakeCtx,
                setupAnimMotion,
                gpuSkinning: false,
                frame.EntityEmulatedAnimClock);
        }
        else if (frame.EntityEmulatedMaterialsOk &&
                 frame.Settings.EnableEntityAnimation &&
                 frame.BlockModel is not null &&
                 frame.EntityRebakeCtx is not null)
        {
            var rebakeKey = PreviewRenderPassSetup.BuildEntityGpuBindRebakeKey(frame.EntityRebakeCtx);
            if (!string.Equals(rebakeKey, _emulatedRebakeSubjectKey, StringComparison.Ordinal))
            {
                _emulatedRebakeSubjectKey = rebakeKey;
                _lastEmulatedEntityRebakeRenderTime = double.NegativeInfinity;
            }

            if (frame.MeshDirty)
            {
                _emulatedGpuSkinPrepFailedKey = null;
            }

            var gpuBindCacheMissing = frame.EntityRebakeCtx.GpuPreparedBoneCount is null or 0 ||
                                      frame.EntityRebakeCtx.GpuBindPoseInterleavedVertices is null;
            var gpuPreparationPreviouslyFailed = string.Equals(
                rebakeKey,
                _emulatedGpuSkinPrepFailedKey,
                StringComparison.Ordinal);
            var shouldTryGpuLayout = !gpuPreparationPreviouslyFailed &&
                (frame.MeshDirty ||
                 gpuBindCacheMissing ||
                 !frame.BlockModel.GpuEntityBoneSkinning);
            EntityRebakeResult? gpuPreparation = null;
            var gpuPreparationCompletedFailure = false;
            if (shouldTryGpuLayout)
            {
                if (TryTakeEntityPreparationResult(
                        EntityRebakeWorkKind.GpuSkinPrepare,
                        rebakeKey,
                        out var result))
                {
                    if (result.Success)
                    {
                        ApplyEntityRebakeContextResult(
                            frame.EntityRebakeCtx,
                            result);
                        gpuPreparation = result;
                    }
                    else
                    {
                        gpuPreparationCompletedFailure = true;
                    }
                }
                else
                {
                    EnqueueEntityRebakeRequest(
                        ref frame,
                        setupAnimMotion,
                        requestKey: rebakeKey,
                        workKind: EntityRebakeWorkKind.GpuSkinPrepare);
                }
            }

            if (gpuPreparation is
                {
                    Success: true,
                    InterleavedVertices: not null,
                    Indices: not null,
                    DrawBatches: not null,
                })
            {
                var gpuVerts = gpuPreparation.InterleavedVertices;
                var gpuIdx = gpuPreparation.Indices;
                var gpuBatches = gpuPreparation.DrawBatches;
                var gpuBoneCount = gpuPreparation.GpuBoneCount;
                var gpuLift = gpuPreparation.GpuMeshSpaceLiftY;
                _emulatedGpuSkinPrepFailedKey = null;
                frame.EntityRebakeCtx!.GpuPreparedBoneCount = gpuBoneCount;
                frame.EntityRebakeCtx.GpuBoundCpuMeshFingerprint =
                    frame.EntityRebakeCtx.PackConverterCpuMeshFingerprint;
                var gpuSkinnedBounds = PreviewGpuSkinnedBounds.TryBuild(
                    gpuBatches!,
                    gpuVerts!,
                    gpuIdx!,
                    EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex);
                var liftedGpu = new PreviewModelSubject
                {
                    InterleavedVertices = gpuVerts!,
                    Indices = gpuIdx!,
                    DrawBatches = gpuBatches!,
                    Materials = frame.BlockModel!.Materials,
                    PrimaryMaterialIndex = frame.BlockModel.PrimaryMaterialIndex,
                    Sprite2DFoliageTarget = frame.BlockModel.Sprite2DFoliageTarget,
                    EnableRenderTimeAnimation = frame.BlockModel.EnableRenderTimeAnimation,
                    AnimationPreset = frame.BlockModel.AnimationPreset,
                    EmulatedRebake = frame.BlockModel.EmulatedRebake,
                    GpuEntityBoneSkinning = true,
                    VertexStrideFloats = EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex,
                    EntityGpuMeshSpaceLiftY = gpuLift,
                    EntityGpuVerticesInPreviewSpace = false,
                    GpuSkinnedBounds = gpuSkinnedBounds,
                    EntityPreviewAnchorOffset = frame.BlockModel.EntityPreviewAnchorOffset,
                    EntityPreviewPlacementApplied = true,
                    MeshProvenance = frame.EntityRebakeCtx.MeshProvenance ?? frame.BlockModel.MeshProvenance
                };
                frame.BlockModel = liftedGpu;
                UploadPreviewMesh(gpuVerts!, gpuIdx!, EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex);
                frame.UploadedLiveEntityAnim = true;
                lock (_sync)
                {
                    _blockModelSubject = liftedGpu;
                    if (frame.MeshDirty)
                    {
                        _meshDirty = false;
                    }
                }

                EmitDiagnostic(
                    $"[3D preview] Emulated entity GPU skinned mesh: bones={gpuBoneCount}, verts={gpuVerts!.Length / EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex}, indices={gpuIdx!.Length}.");
                EmitParityCatalogPlacementDiagnostic(
                    frame.BlockModel,
                    frame.EntityRebakeCtx,
                    setupAnimMotion,
                    gpuSkinning: true,
                    frame.EntityEmulatedAnimClock);
            }
            else
            {
                if (gpuPreparationCompletedFailure)
                {
                    _emulatedGpuSkinPrepFailedKey = rebakeKey;
                    frame.EntityRebakeCtx!.GpuPreparedBoneCount = null;
                    frame.EntityRebakeCtx.GpuBindPoseInverseLocalToParent = null;
                    frame.EntityRebakeCtx.GpuBindPoseBonePalette = null;
                    frame.EntityRebakeCtx.GpuBindPoseInterleavedVertices = null;
                }

                var shouldEnqueueCpuRebake = (frame.MeshDirty ||
                    frame.EntityEmulatedPauseEdge ||
                    frame.RenderTime - _lastEmulatedEntityRebakeRenderTime >= MinEmulatedEntityRebakeIntervalSeconds) &&
                    !frame.BlockModel!.GpuEntityBoneSkinning &&
                    (!shouldTryGpuLayout || gpuPreparationCompletedFailure);
                if (shouldEnqueueCpuRebake)
                {
                    if (TryConsumeEntityRebakeResult(ref frame, rebakeKey))
                    {
                        var rebaked = frame.BlockModel!;
                        var rebakeDiagKey =
                            $"{rebakeKey}|verts={rebaked.InterleavedVertices.Length / PreviewMesh.FloatsPerVertex}|idx={rebaked.Indices.Length}";
                        if (!string.Equals(rebakeDiagKey, _entityCpuRebakeDiagKey, StringComparison.Ordinal))
                        {
                            _entityCpuRebakeDiagKey = rebakeDiagKey;
                            EmitDiagnostic(
                                $"[3D preview] Emulated entity mesh rebake: verts={rebaked.InterleavedVertices.Length / PreviewMesh.FloatsPerVertex}, indices={rebaked.Indices.Length}.");
                        }

                        EmitParityCatalogPlacementDiagnostic(
                            frame.BlockModel,
                            frame.EntityRebakeCtx,
                            setupAnimMotion,
                            gpuSkinning: false,
                            frame.EntityEmulatedAnimClock);
                    }
                    else
                    {
                        EnqueueEntityRebakeRequest(
                            ref frame,
                            setupAnimMotion,
                            requestKey: rebakeKey);
                    }
                }
            }
        }

        // Mesh upload must happen before either pass so depth-only and main pass share the same VBO.
        var needsGpuStrideResync = frame.BlockModel is
        {
            GpuEntityBoneSkinning: true,
            VertexStrideFloats: > 0 and var subjectStride,
            InterleavedVertices.Length: > 0,
            Indices.Length: > 0
        } && subjectStride != _lastMeshUploadStride;
        if ((frame.MeshDirty || needsGpuStrideResync) && !frame.UploadedLiveEntityAnim)
        {
            // Keep mesh dirty until we actually have geometry to upload; render can start a few frames
            // before frame.Scene meshes are ready, and clearing this flag too early leaves the GPU buffer empty.
            if (!frame.Settings.DrawPreviewSubject)
            {
                var empty = PreviewMeshFactory.CreateEmptySubjectPlaceholder();
                UploadPreviewMesh(empty.InterleavedVertices, empty.Indices);
            }
            else if (frame.BlockModel is
            {
                GpuEntityBoneSkinning: true,
                VertexStrideFloats: > 0,
                InterleavedVertices.Length: > 0,
                Indices.Length: > 0
            } gpuSkinned
                     && gpuSkinned.InterleavedVertices.Length % gpuSkinned.VertexStrideFloats == 0)
            {
                // Scene meshes are always 12-float PreviewMesh; GPU-skinned entities use a wider stride (bone index).
                // Never upload the frame.Scene copy here or the VAO stride would desync from genesis.vert skinning.
                UploadPreviewMesh(
                    gpuSkinned.InterleavedVertices,
                    gpuSkinned.Indices,
                    gpuSkinned.VertexStrideFloats);
                EmitMeshUploadDiagnostic(
                    $"[3D preview] Mesh upload: frame.Scene={frame.Scene.SceneKind}, sourceCount={frame.Scene.Meshes.Count}, verts={gpuSkinned.InterleavedVertices.Length / gpuSkinned.VertexStrideFloats}, indices={gpuSkinned.Indices.Length}, strideFloats={gpuSkinned.VertexStrideFloats} (GPU-skinned subject).");
            }
            else if (frame.EntityEmulatedPreview && frame.EntityRebakeCtx is not null)
            {
                // Pack-converter CPU mesh must never reach the GPU for parity-catalog entities; only
                // TryCommitParityCatalogCpuBindPoseMesh / GPU bind paths may upload entity geometry.
                var deferKey = frame.EntityRebakeCtx.AssetArchivePath;
                if (!string.Equals(deferKey, _entityMeshUploadDeferredDiagKey, StringComparison.Ordinal))
                {
                    _entityMeshUploadDeferredDiagKey = deferKey;
                    EmitDiagnostic(
                        "[3D preview] Entity mesh upload deferred (awaiting TryRebakeMesh commit; pack-converter mesh is not uploaded to GPU).");
                }
            }
            else if (frame.BlockModel is
            {
                GpuEntityBoneSkinning: false,
                EntityPreviewPlacementApplied: true,
                InterleavedVertices.Length: > 0,
                Indices.Length: > 0
            } cpuPlaced
                     && cpuPlaced.InterleavedVertices.Length % PreviewMesh.FloatsPerVertex == 0)
            {
                UploadPreviewMesh(cpuPlaced.InterleavedVertices, cpuPlaced.Indices);
                EmitMeshUploadDiagnostic(
                    $"[3D preview] Mesh upload: pack-converter CPU subject, verts={cpuPlaced.InterleavedVertices.Length / PreviewMesh.FloatsPerVertex}, indices={cpuPlaced.Indices.Length}.");
            }
            else if (frame.Scene.SceneKind == PreviewSceneKind.ItemPlane)
            {
                var uploadMesh = ItemPreviewSceneFactory.CreateMesh(frame.Settings, frame.Material);
                UploadPreviewMesh(uploadMesh.InterleavedVertices, uploadMesh.Indices);
                var meshKind = uploadMesh.Name == "sprite_voxels" ? "sprite voxels" : "item plane";
                var voxelCount = uploadMesh.OpaqueVoxelCount > 0
                    ? uploadMesh.OpaqueVoxelCount
                    : uploadMesh.Name == "sprite_voxels"
                        ? uploadMesh.VertexCount / 24
                        : 0;
                EmitMeshUploadDiagnostic(
                    voxelCount > 0
                        ? $"[3D preview] Mesh upload: {meshKind} (itemFlat={(frame.Settings.ItemFlatSpritePreview ? 1 : 0)}, thickness={frame.Settings.SpriteThickness:0.###}, voxels={voxelCount}), verts={uploadMesh.VertexCount}, indices={uploadMesh.Indices.Length}."
                        : $"[3D preview] Mesh upload: {meshKind} (itemFlat={(frame.Settings.ItemFlatSpritePreview ? 1 : 0)}, planes={frame.Settings.SpritePlaneCount}, thickness={frame.Settings.SpriteThickness:0.###}), verts={uploadMesh.VertexCount}, indices={uploadMesh.Indices.Length}.");
                lock (_sync)
                {
                    _meshDirty = false;
                }
            }
            else if (frame.Scene.Meshes.Count > 0 &&
                     !(frame.EntityEmulatedPreview && frame.EntityRebakeCtx is not null))
            {
                var uploadMesh = frame.Scene.Meshes[0];
                UploadPreviewMesh(uploadMesh.InterleavedVertices, uploadMesh.Indices);
                EmitMeshUploadDiagnostic(
                    $"[3D preview] Mesh upload: frame.Scene={frame.Scene.SceneKind}, sourceCount={frame.Scene.Meshes.Count}, verts={uploadMesh.VertexCount}, indices={uploadMesh.Indices.Length}.");
            }
            else
            {
                // Defensive fallback: if frame.Scene mesh population races with the first render frame,
                // synthesize a canonical mesh so preview never goes blank.
                var uploadMesh = frame.Scene.SceneKind == PreviewSceneKind.ItemPlane
                    ? ItemPreviewSceneFactory.CreateMesh(frame.Settings, frame.Material)
                    : PreviewMeshFactory.CreateUnitCube();
                EmitMeshUploadDiagnostic($"[3D preview] Fallback mesh upload used ({frame.Scene.SceneKind}).");
                UploadPreviewMesh(uploadMesh.InterleavedVertices, uploadMesh.Indices);
            }

            if (!(frame.EntityEmulatedPreview && frame.EntityRebakeCtx is not null))
            {
                lock (_sync)
                {
                    _meshDirty = false;
                }
            }
        }

        if (frame.MaterialDirty)
        {
            var materialUploadComplete = true;
            if (frame.BlockModel is null || frame.BlockSlots is null)
            {
                materialUploadComplete = TryUploadMaterialAsync(
                    frame.Gl,
                    frame.Material,
                    frame.Settings.NearestTextureFilter);
            }

            if (materialUploadComplete)
            {
                lock (_sync)
                {
                    _materialDirty = false;
                }
            }
        }

        bool groundMaterialDirty;
        PreviewMaterial[]? groundSlots;
        bool groundOverlayCutout;
        bool[]? groundSlotCutout;
        bool terrainGrassBakeDirty;
        PreviewTerrainGrassBakeSettings terrainGrassBake;
        bool terrainVegetationDirty;
        PreviewTerrainVegetationBakePlan? terrainVegetation;
        bool terrainWorldGenDirty;
        PreviewTerrainWorldGenSettings terrainWorldGen;
        lock (_sync)
        {
            groundMaterialDirty = _grassGroundMaterialDirty;
            groundSlots = _grassGroundSlotMaterials;
            groundOverlayCutout = _grassGroundOverlayCutout;
            groundSlotCutout = _grassGroundSlotCutout;
            terrainGrassBakeDirty = _terrainGrassBakeSettingsDirty;
            terrainGrassBake = _terrainGrassBakeSettings;
            terrainVegetationDirty = _terrainVegetationBakePlanDirty;
            terrainVegetation = _terrainVegetationBakePlan;
            terrainWorldGenDirty = _terrainWorldGenSettingsDirty;
            terrainWorldGen = _terrainWorldGenSettings;
        }

        if (groundMaterialDirty)
        {
            UploadGroundMaterials(frame.Gl, groundSlots, groundOverlayCutout, nearest: true, groundSlotCutout);
            lock (_sync)
            {
                _grassGroundMaterialDirty = false;
            }
        }

        if (terrainGrassBakeDirty)
        {
            ApplyTerrainGrassBakeSettings(terrainGrassBake);
            lock (_sync)
            {
                _terrainGrassBakeSettingsDirty = false;
            }
        }

        if (terrainVegetationDirty)
        {
            ApplyTerrainVegetationBakePlan(terrainVegetation);
            lock (_sync)
            {
                _terrainVegetationBakePlanDirty = false;
            }
        }

        if (terrainWorldGenDirty)
        {
            ApplyTerrainWorldGenSettings(terrainWorldGen);
            lock (_sync)
            {
                _terrainWorldGenSettingsDirty = false;
            }
        }

        frame.EntityBoneSnapshotValid = false;
        frame.EntityBoneSnapshotCount = 0;
        frame.EntityBonePaletteUploaded = false;
        _lastEntityBoneSnapshotCount = 0;
        string? boneFillHint = null;
        var boneFillOk = false;
        using (BeginCpuTimerScope(GlGpuTimerScope.SetupBones))
        {
        if (frame.BlockModel is { GpuEntityBoneSkinning: true, EmulatedRebake: { } ebBone })
        {
            if (setupAnimMotion)
            {
                boneFillOk = EntityEmulatedPreviewRebaker.TryFillEmulatedEntityBoneMatrices(
                    ebBone,
                    frame.EntityEmulatedAnimClock,
                    _entityBoneScratch.AsSpan(),
                    out frame.EntityBoneSnapshotCount,
                    applyGeometryIrSetupAnimMotion: true);
                if (!boneFillOk)
                {
                    boneFillHint = "anim_bone_fill_failed";
                }
            }
            else if (ebBone.GpuBindPoseBonePalette is { Length: > 0 } cachedBind)
            {
                var copyCount = Math.Min(cachedBind.Length, _entityBoneScratch.Length);
                cachedBind.AsSpan(0, copyCount).CopyTo(_entityBoneScratch);
                frame.EntityBoneSnapshotCount = copyCount;
                boneFillOk = copyCount > 0;
                if (!boneFillOk)
                {
                    boneFillHint = "cached_bind_palette_empty";
                }
            }
            else
            {
                boneFillOk = EntityEmulatedPreviewRebaker.TryFillEmulatedEntityBoneMatrices(
                    ebBone,
                    0f,
                    _entityBoneScratch.AsSpan(),
                    out frame.EntityBoneSnapshotCount,
                    applyGeometryIrSetupAnimMotion: false);
                if (!boneFillOk)
                {
                    boneFillHint = "bind_bone_fill_failed";
                }
            }

            frame.EntityBoneSnapshotValid = boneFillOk && frame.EntityBoneSnapshotCount > 0;
            _lastEntityBoneSnapshotCount = frame.EntityBoneSnapshotCount;
            if (setupAnimMotion && frame.EntityBoneSnapshotValid)
            {
                var liveLift = EntityPreviewPlacement.ComputeLiveGpuLiftY(
                    frame.BlockModel.InterleavedVertices,
                    _entityBoneScratch.AsSpan(0, frame.EntityBoneSnapshotCount),
                    frame.EntityBoneSnapshotCount);
                if (MathF.Abs(liveLift - frame.BlockModel.EntityGpuMeshSpaceLiftY) > 1e-5f)
                {
                    frame.BlockModel = PreviewSubjectPlacement.CopySubjectWithVertices(
                        frame.BlockModel,
                        frame.BlockModel.InterleavedVertices,
                        liveLift);
                    lock (_sync)
                    {
                        _blockModelSubject = frame.BlockModel;
                    }
                }
            }
        }
        else if (!setupAnimMotion &&
                 frame.BlockModel is { GpuEntityBoneSkinning: true, EntityGpuVerticesInPreviewSpace: false } &&
                 frame.EntityRebakeCtx is not null &&
                 MathF.Abs(frame.BlockModel.EntityGpuMeshSpaceLiftY - frame.EntityRebakeCtx.LastGroundLiftY) > 1e-5f)
        {
            frame.BlockModel = PreviewSubjectPlacement.CopySubjectWithVertices(
                frame.BlockModel,
                frame.BlockModel.InterleavedVertices,
                frame.EntityRebakeCtx.LastGroundLiftY);
            lock (_sync)
            {
                _blockModelSubject = frame.BlockModel;
            }
        }
        }

        using (BeginCpuTimerScope(GlGpuTimerScope.SetupBounds))
        {
            UpdateEntityGpuSkinnedBounds(ref frame);
        }

        var bonePaletteUploaded = false;
        if (frame.BlockModel is { GpuEntityBoneSkinning: true } blockModel && _entityBoneUbo != 0)
        {
            if (frame.EntityBoneSnapshotValid &&
                frame.EntityBoneSnapshotCount > 0)
            {
                if (ShouldUploadEntityBonePalette(frame.EntityBoneSnapshotCount, setupAnimMotion))
                {
                    UploadPreviousEntitySkinningBoneMatrices(frame.Gl);
                    UploadEntitySkinningBoneMatrices(frame.Gl, frame.EntityBoneSnapshotCount);
                    SnapshotUploadedEntityBonePalette(frame.EntityBoneSnapshotCount);
                }

                bonePaletteUploaded = _entityBonePaletteLastUploadedCount > 0;
            }

            TryApplyForceEntityCpuSkinningUpload(ref frame, setupAnimMotion);
            if (frame.Settings.ForceEntityCpuSkinning)
            {
                bonePaletteUploaded = false;
            }

            if (TryResolveEntitySkinningDrawState(
                    blockModel,
                    blockModel.EntityGpuMeshSpaceLiftY,
                    frame.EntityBoneSnapshotValid,
                    frame.EntityBoneSnapshotCount,
                    setupAnimMotion,
                    out var previewSpaceVerts,
                    out var bindMesh,
                    out var gpuSkinning,
                    out var boneCount,
                    out var liftY))
            {
                _lastUploadedPreviewSpaceVerts = previewSpaceVerts > 0.5f ? 1 : 0;
                _lastUploadedBindMesh = bindMesh > 0.5f ? 1 : 0;
                _lastUploadedGpuSkinning = gpuSkinning;
                _lastUploadedBoneCount = boneCount;
                _lastUploadedLiftY = liftY;
                // Uniforms are applied in PassScene/PassShadow after program.Use().
            }
            else
            {
                _lastUploadedPreviewSpaceVerts = 0;
                _lastUploadedBindMesh = 0;
                _lastUploadedGpuSkinning = 0;
                _lastUploadedBoneCount = 0;
                _lastUploadedLiftY = 0f;
            }

            EmitEntityGpuRuntimeDiagnostic(
                frame.BlockModel,
                frame.EntityRebakeCtx,
                setupAnimMotion,
                frame.EntityEmulatedAnimClock,
                boneFillOk,
                bonePaletteUploaded,
                boneFillHint);
            frame.EntityBonePaletteUploaded = bonePaletteUploaded;
        }
        else if (frame.EntityEmulatedPreview && frame.BlockModel is { GpuEntityBoneSkinning: false })
        {
            _lastUploadedPreviewSpaceVerts = 1;
            _lastUploadedBindMesh = 0;
            _lastUploadedGpuSkinning = 0;
            _lastUploadedBoneCount = 0;
            _lastUploadedLiftY = 0f;
        }

        ApplyEffectiveFrameRenderFlags(ref frame);
    }

    private static void ApplyEffectiveFrameRenderFlags(ref GlRenderFrame frame)
    {
        frame.EntityAlphaModeUniform = PreviewSubjectAlphaPolicy.ResolveAlphaModeUniform(
            frame.Scene.SceneKind,
            frame.EntityEmulatedPreview,
            frame.Settings.EntityAlphaMode);
        frame.EntityBlendDraw =
            frame.EntityEmulatedPreview &&
            frame.Scene.SceneKind == PreviewSceneKind.BlockModel &&
            frame.Settings.EntityAlphaMode == PreviewEntityAlphaMode.Blend;
        frame.EnableParallaxEff = PreviewEntityEmulatedShaderGating.EffectiveParallax(
            frame.Settings.EnableParallax, frame.EntityEmulatedPreview, frame.Settings.EnableEntityParallax);
        frame.EnableParallaxAoEff = PreviewEntityEmulatedShaderGating.EffectiveParallaxAo(
            frame.Settings.EnableParallaxAo, frame.EntityEmulatedPreview, frame.Settings.EnableEntityParallax);
        frame.EnableNormalMapEff = PreviewEntityEmulatedShaderGating.EffectiveNormalMap(
            frame.Settings.EnableNormalMap, frame.EntityEmulatedPreview, frame.Settings.EnableEntityLabPbrShading);
        frame.EnableSpecularMapEff = PreviewEntityEmulatedShaderGating.EffectiveSpecularMap(
            frame.Settings.EnableSpecularMap, frame.EntityEmulatedPreview, frame.Settings.EnableEntityLabPbrShading);
        frame.EnableParallaxShadowEff = PreviewEntityEmulatedShaderGating.EffectiveParallaxShadow(
            frame.Settings.EnableParallaxShadow, frame.EntityEmulatedPreview, frame.Settings.EnableEntityParallax);
        frame.EnableTessellationDisplacementEff = PreviewEntityEmulatedShaderGating.EffectiveTessellationDisplacement(
            frame.Settings.EnableTessellationDisplacement, frame.EntityEmulatedPreview);
    }

    private void UpdateEntityGpuSkinnedBounds(ref GlRenderFrame frame)
    {
        if (frame.BlockModel is not
            {
                GpuEntityBoneSkinning: true,
                GpuSkinnedBounds: { } skinnedBounds
            } model)
        {
            return;
        }

        if (!frame.EntityBoneSnapshotValid || frame.EntityBoneSnapshotCount <= 0)
        {
            PreviewGpuSkinnedBounds.ClearDrawBatchBounds(model.DrawBatches);
            return;
        }

        skinnedBounds.UpdateDrawBatchBounds(
            model.DrawBatches,
            _entityBoneScratch.AsSpan(0, frame.EntityBoneSnapshotCount),
            frame.EntityBoneSnapshotCount,
            model.EntityGpuMeshSpaceLiftY);
    }

    private string? _parityCatalogCpuBindDiagKey;
    private string? _parityCatalogCpuBindFailDiagKey;
    private string? _entityMeshUploadDeferredDiagKey;
    private string? _lastMeshUploadDiagKey;

    private void EmitMeshUploadDiagnostic(string message)
    {
        if (string.Equals(message, _lastMeshUploadDiagKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastMeshUploadDiagKey = message;
        EmitDiagnostic(message);
    }

    private bool TryCommitParityCatalogCpuBindPoseMesh(
        ref GlRenderFrame frame,
        string? bindPoseRebakeKey,
        bool setupAnimMotion)
    {
        if (frame.BlockModel is null || frame.EntityRebakeCtx is null)
        {
            return false;
        }

        var requestKey = bindPoseRebakeKey ??
            PreviewRenderPassSetup.BuildParityCatalogCpuBindCommitKey(
                frame.EntityRebakeCtx);
        if (!TryTakeEntityPreparationResult(
                EntityRebakeWorkKind.CpuRebake,
                requestKey,
                out var result))
        {
            EnqueueEntityRebakeRequest(
                ref frame,
                setupAnimMotion: false,
                requestKey: requestKey,
                workKind: EntityRebakeWorkKind.CpuRebake,
                animationTimeSeconds: 0f);
            return false;
        }

        if (!result.Success ||
            result.InterleavedVertices is not { } cpuVerts ||
            result.Indices is not { } cpuIdx ||
            result.DrawBatches is not { } cpuBatches)
        {
            var failKey = frame.EntityRebakeCtx.AssetArchivePath;
            if (!string.Equals(failKey, _parityCatalogCpuBindFailDiagKey, StringComparison.Ordinal))
            {
                _parityCatalogCpuBindFailDiagKey = failKey;
                EmitDiagnostic(
                    $"[3D preview] Parity-catalog CPU bind-pose rebake failed for {failKey}; GPU upload deferred (no pack-converter fallback).");
            }

            return false;
        }

        ApplyEntityRebakeContextResult(
            frame.EntityRebakeCtx,
            result);

        _entityMeshUploadDeferredDiagKey = null;

        frame.EntityRebakeCtx.PackConverterCpuMeshFingerprint =
            PreviewMeshGeometryFingerprint.ComputeCpuPreviewMesh(
                cpuVerts,
                PreviewMesh.FloatsPerVertex);

        _emulatedGpuSkinPrepFailedKey = null;
        ClearEntityGpuBindCache(frame.EntityRebakeCtx);
        var cpuSubject = new PreviewModelSubject
        {
            InterleavedVertices = cpuVerts,
            Indices = cpuIdx,
            DrawBatches = cpuBatches,
            Materials = frame.BlockModel.Materials,
            PrimaryMaterialIndex = frame.BlockModel.PrimaryMaterialIndex,
            Sprite2DFoliageTarget = frame.BlockModel.Sprite2DFoliageTarget,
            EnableRenderTimeAnimation = frame.BlockModel.EnableRenderTimeAnimation,
            AnimationPreset = frame.BlockModel.AnimationPreset,
            EmulatedRebake = frame.BlockModel.EmulatedRebake,
            GpuEntityBoneSkinning = false,
            VertexStrideFloats = 0,
            EntityGpuMeshSpaceLiftY = 0f,
            EntityGpuVerticesInPreviewSpace = true,
            EntityPreviewAnchorOffset = frame.BlockModel.EntityPreviewAnchorOffset,
            EntityPreviewPlacementApplied = true,
            MeshProvenance = frame.EntityRebakeCtx.MeshProvenance ?? frame.BlockModel.MeshProvenance
        };
        frame.BlockModel = cpuSubject;
        UploadPreviewMesh(cpuVerts, cpuIdx);
        frame.UploadedLiveEntityAnim = true;
        _entityBindPoseCommittedKey = PreviewRenderPassSetup.BuildParityCatalogCpuBindCommitKey(frame.EntityRebakeCtx);

        lock (_sync)
        {
            _blockModelSubject = cpuSubject;
            if (frame.MeshDirty)
            {
                _meshDirty = false;
            }
        }

        EmitParityCatalogPlacementDiagnostic(
            frame.BlockModel,
            frame.EntityRebakeCtx,
            setupAnimMotion,
            gpuSkinning: false,
            frame.EntityEmulatedAnimClock);

        var diagKey = bindPoseRebakeKey ?? frame.EntityRebakeCtx.AssetArchivePath;
        if (!string.Equals(diagKey, _parityCatalogCpuBindDiagKey, StringComparison.Ordinal))
        {
            _parityCatalogCpuBindDiagKey = diagKey;
            _parityCatalogCpuBindFailDiagKey = null;
            var rebakeUvFp = PreviewMeshGeometryFingerprint.ComputeCpuPreviewMeshUvFingerprint(
                cpuVerts,
                PreviewMesh.FloatsPerVertex);
            EmitDiagnostic(
                $"[3D preview] Parity-catalog CPU bind-pose mesh: verts={cpuVerts.Length / PreviewMesh.FloatsPerVertex}, indices={cpuIdx.Length}, uvFp={rebakeUvFp}.");
        }

        return true;
    }

    private void TryApplyForceEntityCpuSkinningUpload(ref GlRenderFrame frame, bool setupAnimMotion)
    {
        if (!frame.Settings.ForceEntityCpuSkinning ||
            frame.BlockModel is not { EmulatedRebake: { } rebake })
        {
            return;
        }

        var requestKey =
            "force-cpu|" +
            PreviewRenderPassSetup.BuildEntityGpuBindRebakeKey(rebake) +
            $"|setup={setupAnimMotion}";
        if (!TryTakeEntityPreparationResult(
                EntityRebakeWorkKind.CpuRebake,
                requestKey,
                out var result))
        {
            EnqueueEntityRebakeRequest(
                ref frame,
                setupAnimMotion,
                requestKey: requestKey,
                animationTimeSeconds:
                    setupAnimMotion ? frame.EntityEmulatedAnimClock : 0f);
            return;
        }

        if (!result.Success ||
            result.InterleavedVertices is not { } baked ||
            result.Indices is not { } indices ||
            result.DrawBatches is not { } batches ||
            baked.Length == 0 ||
            indices.Length == 0)
        {
            return;
        }

        ApplyEntityRebakeContextResult(rebake, result);

        UploadPreviewMesh(baked, indices);
        var cpuSubject = new PreviewModelSubject
        {
            InterleavedVertices = baked,
            Indices = indices,
            DrawBatches = batches,
            Materials = frame.BlockModel.Materials,
            PrimaryMaterialIndex = frame.BlockModel.PrimaryMaterialIndex,
            Sprite2DFoliageTarget = frame.BlockModel.Sprite2DFoliageTarget,
            EnableRenderTimeAnimation = frame.BlockModel.EnableRenderTimeAnimation,
            AnimationPreset = frame.BlockModel.AnimationPreset,
            EmulatedRebake = frame.BlockModel.EmulatedRebake,
            GpuEntityBoneSkinning = false,
            VertexStrideFloats = 0,
            EntityGpuMeshSpaceLiftY = 0f,
            EntityGpuVerticesInPreviewSpace = false,
            EntityPreviewAnchorOffset = frame.BlockModel.EntityPreviewAnchorOffset,
            EntityPreviewPlacementApplied = true,
            MeshProvenance = frame.BlockModel.MeshProvenance
        };
        frame.BlockModel = cpuSubject;
        lock (_sync)
        {
            _blockModelSubject = cpuSubject;
        }

        if (setupAnimMotion)
        {
            EnqueueEntityRebakeRequest(
                ref frame,
                setupAnimMotion,
                requestKey: requestKey,
                animationTimeSeconds: frame.EntityEmulatedAnimClock);
        }

        var norm = rebake.AssetArchivePath.Replace('\\', '/').TrimStart('/');
        if (!EntityTextureParityCatalog.IsCatalogued(norm))
        {
            return;
        }

        var diagKey = $"{norm}|anim={(setupAnimMotion ? 1 : 0)}|cpu=1";
        if (string.Equals(diagKey, _forceEntityCpuSkinningDiagKey, StringComparison.Ordinal))
        {
            return;
        }

        _forceEntityCpuSkinningDiagKey = diagKey;
        EmitDiagnostic(
            $"[3D preview] ForceEntityCpuSkinning: path={norm} anim={(setupAnimMotion ? 1 : 0)} " +
            $"verts={baked.Length / PreviewMesh.FloatsPerVertex} (12-float TryRebakeMesh preview-space VBO).");
    }

    private int _entityPreviewDebugRevision = -1;

    private void SyncEntityPreviewDebugRevision(PreviewModelSubject? subject)
    {
        if (!EntityPreviewDebugSettings.RequiresMeshRebuild(_entityPreviewDebugRevision))
        {
            return;
        }

        _entityPreviewDebugRevision = EntityPreviewDebugSettings.Revision;
        _entityBindPoseCommittedKey = null;
        _emulatedGpuSkinPrepFailedKey = null;
        _emulatedRebakeSubjectKey = null;
        _parityPlacementDiagKey = null;
        _entityCpuRebakeDiagKey = null;
        ResetEntityGpuRuntimeDiagState();
        if (subject?.EmulatedRebake is { } rebake)
        {
            rebake.GpuPreparedBoneCount = null;
            rebake.GpuBindPoseInverseLocalToParent = null;
            rebake.GpuBindPoseBonePalette = null;
            rebake.GpuBindPoseInterleavedVertices = null;
            rebake.GpuBoundCpuMeshFingerprint = null;
        }

        lock (_sync)
        {
            _meshDirty = true;
        }
    }
}
