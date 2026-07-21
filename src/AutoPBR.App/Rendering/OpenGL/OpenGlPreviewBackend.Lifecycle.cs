using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

using AutoPBR.App.Lang;
using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend
{
    private bool TryUploadBundledGroundFallback(GL gl)
    {
        if (!PreviewBundledGroundMapsLoader.TryLoad(out var material))
        {
            EmitDiagnostic("[3D preview] Bundled grass ground fallback missing or invalid.");
            return false;
        }

        lock (_sync)
        {
            _grassGroundMaterial = material;
            _grassGroundMaterialDirty = true;
        }

        UploadGroundMaterial(gl, material, nearest: true);
        return true;
    }

    private void UploadGroundMaterial(GL gl, PreviewMaterial? material, bool nearest)
    {
        Debug.Assert(_grassGroundAlbedo is not null && _grassGroundNormal is not null &&
                     _grassGroundSpec is not null && _grassGroundHeight is not null);
        UploadMaterialToTextures(
            gl,
            material,
            nearest,
            _grassGroundAlbedo,
            _grassGroundNormal,
            _grassGroundSpec,
            _grassGroundHeight,
            out _grassGroundHasNormal,
            out _grassGroundHasSpecular,
            out _grassGroundHasHeight);
        // Bootstrap sets ready via bundled fallback; VM-driven SetGroundMaterial uploads must also
        // open the draw gate (idle 3D never reaches subject selection to "accidentally" fix this).
        _grassGroundReady = _grassGroundAlbedo is not null;
    }

    private void UploadMaterial(GL gl, PreviewMaterial? material, bool nearest)
    {
        Debug.Assert(_albedo is not null && _normal is not null && _spec is not null && _height is not null);
        UploadMaterialToTextures(gl, material, nearest, _albedo, _normal, _spec, _height, out _, out _, out _);
    }

    private void UploadMaterialToTextures(
        GL gl,
        PreviewMaterial? material,
        bool nearest,
        GlTexture2D albedo,
        GlTexture2D normal,
        GlTexture2D spec,
        GlTexture2D height,
        out bool hasNormal,
        out bool hasSpecular,
        out bool hasHeight)
    {
        hasNormal = false;
        hasSpecular = false;
        hasHeight = false;
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        try
        {
            if (material is null || material.AlbedoRgba.Length < 4)
            {
                albedo.UploadRgbaIfChanged(1, 1, [180, 180, 190, 255], nearest);
                normal.UploadRgbaIfChanged(1, 1, [128, 128, 255, 255], nearest);
                spec.UploadRgbaIfChanged(1, 1, [120, 60, 40, 255], nearest);
                height.UploadRgbaIfChanged(1, 1, [128, 128, 128, 255], nearest);
                return;
            }

            var albMem = material.AlbedoRgba;
            var (albW, albH) = ResolveRgbaDimensions(material.Width, material.Height, albMem.Length);
            var alb = albMem.Span;
            var albPx = albW * albH * 4;
            if (alb.Length < albPx)
            {
                albedo.UploadRgbaIfChanged(1, 1, [180, 180, 190, 255], nearest);
            }
            else
            {
                var albSpan = alb[..albPx];
                var albUpload = PrepareRgbaUploadSpan(albSpan, albW, albH, material.GlUploadFlipRows);
                albedo.UploadRgbaIfChanged(albW, albH, albUpload, nearest);
            }

            if (material.NormalRgba is { Length: >= 4 } nr)
            {
                var (nw, nh) = ResolveRgbaDimensions(material.Width, material.Height, nr.Length);
                var nPx = nw * nh * 4;
                if (nr.Length >= nPx)
                {
                    var nSpan = nr[..nPx].Span;
                    var nUpload = PrepareRgbaUploadSpan(nSpan, nw, nh, material.GlUploadFlipRows);
                    normal.UploadRgbaIfChanged(nw, nh, nUpload, nearest);
                    hasNormal = true;
                }
                else
                {
                    normal.UploadRgbaIfChanged(1, 1, [128, 128, 255, 255], nearest);
                }
            }
            else
            {
                normal.UploadRgbaIfChanged(1, 1, [128, 128, 255, 255], nearest);
            }

            if (material.SpecularRgba is { Length: >= 4 } sr)
            {
                var (sw, sh) = ResolveRgbaDimensions(material.Width, material.Height, sr.Length);
                var sPx = sw * sh * 4;
                if (sr.Length >= sPx)
                {
                    var sSpan = sr[..sPx].Span;
                    var sUpload = PrepareRgbaUploadSpan(sSpan, sw, sh, material.GlUploadFlipRows);
                    spec.UploadRgbaIfChanged(sw, sh, sUpload, nearest);
                    hasSpecular = true;
                }
                else
                {
                    spec.UploadRgbaIfChanged(1, 1, [120, 60, 40, 255], nearest);
                }
            }
            else
            {
                spec.UploadRgbaIfChanged(1, 1, [120, 60, 40, 255], nearest);
            }

            if (material.HeightRgba is { Length: >= 4 } hr)
            {
                var (hw, hh) = ResolveRgbaDimensions(material.Width, material.Height, hr.Length);
                var hPx = hw * hh * 4;
                if (hr.Length >= hPx)
                {
                    var hSpan = hr[..hPx].Span;
                    var hUpload = PrepareRgbaUploadSpan(hSpan, hw, hh, material.GlUploadFlipRows);
                    height.UploadRgbaIfChanged(hw, hh, hUpload, nearest);
                    hasHeight = true;
                }
                else
                {
                    height.UploadRgbaIfChanged(1, 1, [128, 128, 128, 255], nearest);
                }
            }
            else
            {
                height.UploadRgbaIfChanged(1, 1, [128, 128, 128, 255], nearest);
            }
        }
        finally
        {
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        }
    }

    private ReadOnlySpan<byte> PrepareRgbaUploadSpan(ReadOnlySpan<byte> rgba, int width, int height, bool flipRows)
    {
        if (!flipRows)
        {
            return rgba;
        }

        var needed = width * height * 4;
        if (_rgbaUploadScratch is null || _rgbaUploadScratch.Length < needed)
        {
            _rgbaUploadScratch = new byte[needed];
        }

        OpenGlRgbaUpload.CopyBottomRowFirst(rgba, width, height, _rgbaUploadScratch);
        return _rgbaUploadScratch.AsSpan(0, needed);
    }

    private static (int W, int H) ResolveRgbaDimensions(int declaredW, int declaredH, int byteLength)
    {
        if (byteLength < 4)
        {
            return (1, 1);
        }

        var texels = byteLength / 4;
        var w = declaredW;
        var h = declaredH;
        if (w > 0 && h > 0 && w * h == texels)
        {
            return (w, h);
        }

        var side = (int)Math.Sqrt(texels);
        if (side > 0 && side * side == texels)
        {
            return (side, side);
        }

        if (w > 0 && texels % w == 0)
        {
            var inferredH = texels / w;
            if (inferredH <= w * 4)
            {
                return (w, inferredH);
            }
        }

        if (h > 0 && texels % h == 0)
        {
            var inferredW = texels / h;
            if (inferredW <= h * 4)
            {
                return (inferredW, h);
            }
        }

        var s = side > 0 ? side : (int)Math.Sqrt(texels);
        while (s > 1 && texels % s != 0)
        {
            s--;
        }

        if (s < 1)
        {
            s = 1;
        }

        return (s, Math.Max(1, texels / s));
    }

    private void InitEntitySkinningBoneUbo(GL gl)
    {
        if (_program is not { IsValid: true })
        {
            return;
        }

        DisposeEntitySkinningUploadBuffers();
        DisposeGenesisMaterialDrawRecordBuffer();
        const string blockName = "EntitySkinningBones";
        const string prevBlockName = "EntityPrevSkinningBones";
        const string normalBlockName = "EntitySkinningNormals";
        _entitySkinningUsesSsbo = ShouldUseEntitySkinningSsbo();
        _entityBoneUboBlockBoundMain = false;
        _entityBoneUboBlockBoundShadow = false;
        Array.Clear(_entitySkinningUboScratch);
        Array.Clear(_entityPrevSkinningUboScratch);
        Array.Clear(_entityNormalSkinningUboScratch);
        var usePersistent = _glCapabilities?.CanUsePersistentUploadRing == true;
        var uploadTarget = _entitySkinningUsesSsbo ? (BufferTargetARB)0x90D2 : BufferTargetARB.UniformBuffer;
        var boneBindingPoint = _entitySkinningUsesSsbo
            ? EntitySkinningSsboBindingPoint
            : EntitySkinningUboBindingPoint;
        var prevBoneBindingPoint = _entitySkinningUsesSsbo
            ? EntityPrevSkinningSsboBindingPoint
            : EntityPrevSkinningUboBindingPoint;
        var normalBoneBindingPoint = _entitySkinningUsesSsbo
            ? EntityNormalSkinningSsboBindingPoint
            : EntityNormalSkinningUboBindingPoint;
        var offsetAlignmentPName = _entitySkinningUsesSsbo ? (GetPName)0x90DF : (GetPName)0x8A34;
        var bufferOffsetAlignment = Math.Max(16, gl.GetInteger(offsetAlignmentPName));
        _entityBoneUpload = new GlPersistentMappedUploadBuffer(
            gl,
            uploadTarget,
            boneBindingPoint,
            EntitySkinningUboTotalBytes,
            bufferOffsetAlignment,
            usePersistent);
        _entityPrevBoneUpload = new GlPersistentMappedUploadBuffer(
            gl,
            uploadTarget,
            prevBoneBindingPoint,
            EntitySkinningUboTotalBytes,
            bufferOffsetAlignment,
            usePersistent);
        _entityNormalBoneUpload = new GlPersistentMappedUploadBuffer(
            gl,
            uploadTarget,
            normalBoneBindingPoint,
            EntitySkinningUboMatrixBytes,
            bufferOffsetAlignment,
            usePersistent);
        _entityBoneUbo = _entityBoneUpload.Handle;
        _entityPrevBoneUbo = _entityPrevBoneUpload.Handle;
        _entityNormalBoneUbo = _entityNormalBoneUpload.Handle;
        _entityBoneUpload.Upload(_entitySkinningUboScratch);
        _entityPrevBoneUpload.Upload(_entityPrevSkinningUboScratch);
        _entityNormalBoneUpload.Upload(_entityNormalSkinningUboScratch);
        var bufferLabel = _entitySkinningUsesSsbo ? "SSBO" : "UBO";
        EmitDiagnostic("[3D preview] Entity bone uploads: " +
                       (_entityBoneUpload.UsesPersistentMapping
                           ? $"persistent mapped {bufferLabel} transport."
                           : $"BufferSubData {bufferLabel} transport."));

        if (_entitySkinningUsesSsbo)
        {
            _entityBoneUboBlockBoundMain = true;
            _entityBoneUboBlockBoundShadow = _shadowProgram is { IsValid: true };
            gl.BindBuffer(uploadTarget, 0);
            return;
        }

        var mainProg = _program.Program;
        var mainBlock = gl.GetUniformBlockIndex(mainProg, blockName);
        _entityBoneUboBlockBoundMain = mainBlock != uint.MaxValue;
        if (_entityBoneUboBlockBoundMain)
        {
            gl.UniformBlockBinding(mainProg, mainBlock, EntitySkinningUboBindingPoint);
        }

        var mainPrevBlock = gl.GetUniformBlockIndex(mainProg, prevBlockName);
        if (mainPrevBlock != uint.MaxValue)
        {
            gl.UniformBlockBinding(mainProg, mainPrevBlock, EntityPrevSkinningUboBindingPoint);
        }

        var mainNormalBlock = gl.GetUniformBlockIndex(mainProg, normalBlockName);
        if (mainNormalBlock != uint.MaxValue)
        {
            gl.UniformBlockBinding(mainProg, mainNormalBlock, EntityNormalSkinningUboBindingPoint);
        }

        if (_shadowProgram is { IsValid: true })
        {
            var shadowProg = _shadowProgram.Program;
            var shadowBlock = gl.GetUniformBlockIndex(shadowProg, blockName);
            _entityBoneUboBlockBoundShadow = shadowBlock != uint.MaxValue;
            if (_entityBoneUboBlockBoundShadow)
            {
                gl.UniformBlockBinding(shadowProg, shadowBlock, EntitySkinningUboBindingPoint);
            }
        }

        gl.BindBuffer(uploadTarget, 0);
    }

    private static EntitySkinningUniformLocs ResolveEntitySkinningUniformLocs(GlShaderProgram program) =>
        new(
            program.GetUniformLocation("uEntityPreviewSpaceVerts"),
            program.GetUniformLocation("uEntityBindMesh"),
            program.GetUniformLocation("uEntityGpuSkinning"),
            program.GetUniformLocation("uEntityBoneCount"),
            program.GetUniformLocation("uEntityMeshLiftY"),
            program.GetUniformLocation("uEntityPrevBonePaletteValid"));

    private void LogEntityShaderInitDiagnosticsOnce()
    {
        if (_loggedEntityShaderInit)
        {
            return;
        }

        _loggedEntityShaderInit = true;
        EmitDiagnostic(
            "[3D preview] Entity skinning buffer source: " +
            (_entitySkinningUsesSsbo ? "desktop SSBO matrix palettes." : "UBO matrix palettes."));
        EmitDiagnostic(EntityGpuShaderDiagnostics.FormatEntityShaderInitLine(
            "main",
            _mainEntityUniformLocs.PreviewSpaceVerts,
            _mainEntityUniformLocs.BindMesh,
            _mainEntityUniformLocs.GpuSkinning,
            _mainEntityUniformLocs.BoneCount,
            _mainEntityUniformLocs.MeshLiftY,
            _entityBoneUboBlockBoundMain));
        EmitDiagnostic(EntityGpuShaderDiagnostics.FormatEntityShaderInitLine(
            "shadow",
            _shadowEntityUniformLocs.PreviewSpaceVerts,
            _shadowEntityUniformLocs.BindMesh,
            _shadowEntityUniformLocs.GpuSkinning,
            _shadowEntityUniformLocs.BoneCount,
            _shadowEntityUniformLocs.MeshLiftY,
            _entityBoneUboBlockBoundShadow));
        if (!_mainEntityUniformLocs.IsComplete || !_shadowEntityUniformLocs.IsComplete)
        {
            EmitDiagnostic(
                "[3D preview] GPU WARN: entity scalar uniform locations incomplete at init; 13-float entity meshes may render exploded (anim-on and anim-off) until shaders reload.");
        }

        if (!_entityBoneUboBlockBoundMain || (_shadowProgram is { IsValid: true } && !_entityBoneUboBlockBoundShadow))
        {
            var bufferLabel = _entitySkinningUsesSsbo ? "SSBO" : "UBO";
            EmitDiagnostic(
                $"[3D preview] GPU WARN: EntitySkinningBones {bufferLabel} block not bound on one or more programs; anim-on bone multiply will not run.");
        }
    }

    private void BindEntityBoneSkinningUboBlocks()
    {
        if (_entityBoneUbo == 0)
        {
            return;
        }

        _entityBoneUpload?.BindBase();
        _entityPrevBoneUpload?.BindBase();
        _entityNormalBoneUpload?.BindBase();
    }

    private void ApplyEntityBoneSkinningUniformsBeforeDraw(
        GlShaderProgram? program,
        EntitySkinningUniformLocs locs,
        PreviewModelSubject? model,
        float meshSpaceLiftY,
        bool boneSnapshotValid,
        int boneSnapshotCount,
        bool setupAnimMotion,
        bool bonePaletteUploaded,
        string passLabel,
        bool bindBoneUboBlocks = true)
    {
        if (bindBoneUboBlocks && bonePaletteUploaded)
        {
            BindEntityBoneSkinningUboBlocks();
        }

        var resolveOk = TryApplyEntityBoneSkinningUniforms(
            program,
            locs,
            model,
            meshSpaceLiftY,
            boneSnapshotValid,
            boneSnapshotCount,
            setupAnimMotion);
        EmitEntityDrawContractDiagnostic(
            passLabel,
            locs,
            model,
            setupAnimMotion,
            boneSnapshotValid,
            boneSnapshotCount,
            bonePaletteUploaded,
            resolveOk);
    }

    private bool TryApplyEntityBoneSkinningUniforms(
        GlShaderProgram? program,
        EntitySkinningUniformLocs locs,
        PreviewModelSubject? model,
        float meshSpaceLiftY,
        bool boneSnapshotValid,
        int boneSnapshotCount,
        bool setupAnimMotion)
    {
        if (TryResolveEntitySkinningDrawState(
                model,
                meshSpaceLiftY,
                boneSnapshotValid,
                boneSnapshotCount,
                setupAnimMotion,
                out var previewSpaceVerts,
                out var bindMesh,
                out var gpuSkinning,
                out var boneCount,
                out var liftY))
        {
            ApplyEntitySkinningUniforms(program, locs, previewSpaceVerts, bindMesh, gpuSkinning, boneCount, liftY);
            ApplyEntityPrevBonePaletteUniform(program, locs, gpuSkinning != 0 && _entityPrevBoneSnapshotValid);
            _lastUploadedPreviewSpaceVerts = previewSpaceVerts > 0.5f ? 1 : 0;
            _lastUploadedBindMesh = bindMesh > 0.5f ? 1 : 0;
            _lastUploadedGpuSkinning = gpuSkinning;
            _lastUploadedBoneCount = boneCount;
            _lastUploadedLiftY = liftY;
            return true;
        }

        ApplyEntitySkinningUniforms(program, locs, 0f, 0f, 0f, 0f, 0f);
        ApplyEntityPrevBonePaletteUniform(program, locs, false);
        _lastUploadedPreviewSpaceVerts = 0;
        _lastUploadedBindMesh = 0;
        _lastUploadedGpuSkinning = 0;
        _lastUploadedBoneCount = 0;
        _lastUploadedLiftY = 0f;
        return false;
    }

    private void ApplyEntitySkinningUniforms(
        GlShaderProgram? program,
        EntitySkinningUniformLocs locs,
        float previewSpaceVerts,
        float bindMesh,
        float gpuSkinning,
        float boneCount,
        float meshLiftY)
    {
        if (program is not { IsValid: true })
        {
            return;
        }

        if (locs.PreviewSpaceVerts >= 0)
        {
            _gl!.Uniform1(locs.PreviewSpaceVerts, previewSpaceVerts);
        }

        if (locs.BindMesh >= 0)
        {
            _gl!.Uniform1(locs.BindMesh, bindMesh);
        }

        if (locs.GpuSkinning >= 0)
        {
            _gl!.Uniform1(locs.GpuSkinning, gpuSkinning);
        }

        if (locs.BoneCount >= 0)
        {
            _gl!.Uniform1(locs.BoneCount, boneCount);
        }

        if (locs.MeshLiftY >= 0)
        {
            _gl!.Uniform1(locs.MeshLiftY, meshLiftY);
        }
    }

    private void ApplyEntityPrevBonePaletteUniform(
        GlShaderProgram? program,
        EntitySkinningUniformLocs locs,
        bool valid)
    {
        if (program is not { IsValid: true } || locs.PrevBonePaletteValid < 0)
        {
            return;
        }

        _gl!.Uniform1(locs.PrevBonePaletteValid, valid ? 1f : 0f);
    }

    // ReSharper disable once UnusedMember.Local -- called from PassScene/PassShadow partials
    private void ApplyEntitySkinningUniforms(GlShaderProgram? program, int gpuSkinning, int boneCount, float meshLiftY)
    {
        var locs = program == _shadowProgram ? _shadowEntityUniformLocs : _mainEntityUniformLocs;
        ApplyEntitySkinningUniforms(
            program,
            locs,
            0f,
            0f,
            gpuSkinning,
            boneCount,
            meshLiftY);
        ApplyEntityPrevBonePaletteUniform(program, locs, false);
    }

    private static bool TryResolveEntitySkinningDrawState(
        PreviewModelSubject? model,
        float meshSpaceLiftY,
        bool boneSnapshotValid,
        int boneSnapshotCount,
        bool setupAnimMotion,
        out float previewSpaceVerts,
        out float bindMesh,
        out int gpuSkinning,
        out int boneCount,
        out float liftY)
    {
        previewSpaceVerts = 0f;
        bindMesh = 0f;
        gpuSkinning = 0;
        boneCount = 0;
        liftY = 0f;
        if (model is
            {
                GpuEntityBoneSkinning: false,
                EntityPreviewPlacementApplied: true,
                InterleavedVertices.Length: > 0
            })
        {
            // CPU-baked preview vertices (placement lift already in aPos). Never route through bind-mesh W()+lift.
            previewSpaceVerts = 1f;
            return true;
        }

        if (model is not { GpuEntityBoneSkinning: true, EmulatedRebake.GpuPreparedBoneCount: > 0 and var preparedCount })
        {
            return false;
        }

        if (model.EntityGpuVerticesInPreviewSpace)
        {
            previewSpaceVerts = 1f;
            gpuSkinning = 0;
            boneCount = 0;
            liftY = 0f;
            return true;
        }

        var paletteReady = boneSnapshotValid && boneSnapshotCount > 0;
        previewSpaceVerts = 0f;
        bindMesh = 1f;
        gpuSkinning = paletteReady && setupAnimMotion ? 1 : 0;
        boneCount = paletteReady ? boneSnapshotCount : preparedCount;
        liftY = meshSpaceLiftY;

        return true;
    }

    private readonly byte[] _entityBonePaletteLastUploadScratch = new byte[EntitySkinningUboMatrixBytes];
    private int _entityBonePaletteLastUploadedCount;

    private bool ShouldUploadEntityBonePalette(int boneSnapshotCount, bool setupAnimMotion)
    {
        if (!setupAnimMotion || boneSnapshotCount <= 0)
        {
            return false;
        }

        if (boneSnapshotCount != _entityBonePaletteLastUploadedCount)
        {
            return true;
        }

        PackEntitySkinningBoneMatrices(boneSnapshotCount);
        return !_entitySkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes)
            .SequenceEqual(_entityBonePaletteLastUploadScratch);
    }

    private void SnapshotUploadedEntityBonePalette(int boneSnapshotCount)
    {
        _entityBonePaletteLastUploadedCount = boneSnapshotCount;
        _entitySkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes)
            .CopyTo(_entityBonePaletteLastUploadScratch);
    }

    private void PackEntitySkinningBoneMatrices(int boneSnapshotCount)
    {
        var matrixFloats = MemoryMarshal.Cast<byte, float>(_entitySkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes));
        var n = Math.Clamp(boneSnapshotCount, 0, EntityGpuSkinningLimits.MaxBones);
        for (var i = 0; i < n; i++)
        {
            var m = _entityBoneScratch[i];
            MemoryMarshal.CreateReadOnlySpan(ref m.M11, 16).CopyTo(matrixFloats.Slice(i * 16, 16));
        }

        if (n < EntityGpuSkinningLimits.MaxBones)
        {
            matrixFloats.Slice(n * 16, (EntityGpuSkinningLimits.MaxBones - n) * 16).Clear();
        }
    }

    private void PackEntityNormalSkinningBoneMatrices(int boneSnapshotCount)
    {
        var matrixFloats = MemoryMarshal.Cast<byte, float>(_entityNormalSkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes));
        var n = Math.Clamp(boneSnapshotCount, 0, EntityGpuSkinningLimits.MaxBones);
        for (var i = 0; i < n; i++)
        {
            var bone = _entityBoneScratch[i];
            if (!Matrix4x4.Invert(bone, out var invBone))
            {
                invBone = Matrix4x4.Identity;
            }

            var normalBone = Matrix4x4.Transpose(invBone);
            MemoryMarshal.CreateReadOnlySpan(ref normalBone.M11, 16).CopyTo(matrixFloats.Slice(i * 16, 16));
        }

        if (n < EntityGpuSkinningLimits.MaxBones)
        {
            matrixFloats.Slice(n * 16, (EntityGpuSkinningLimits.MaxBones - n) * 16).Clear();
        }
    }

    private void UploadEntityNormalSkinningBoneMatrices(GL gl, int boneSnapshotCount)
    {
        if (_entityNormalBoneUbo == 0)
        {
            return;
        }

        PackEntityNormalSkinningBoneMatrices(boneSnapshotCount);
        _entityNormalBoneUpload?.Upload(_entityNormalSkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes));
    }

    private void UploadEntitySkinningBoneMatrices(GL gl, int boneSnapshotCount)
    {
        if (_entityBoneUbo == 0)
        {
            return;
        }

        PackEntitySkinningBoneMatrices(boneSnapshotCount);
        UploadEntityNormalSkinningBoneMatrices(gl, boneSnapshotCount);

        _entityBoneUpload?.Upload(_entitySkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes));
    }

    private void UploadPreviousEntitySkinningBoneMatrices(GL gl)
    {
        if (_entityPrevBoneUbo == 0)
        {
            return;
        }

        var matrixFloats = MemoryMarshal.Cast<byte, float>(_entityPrevSkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes));
        var n = Math.Clamp(_entityPrevBoneSnapshotCount, 0, EntityGpuSkinningLimits.MaxBones);
        for (var i = 0; i < n; i++)
        {
            var m = _entityPrevBoneScratch[i];
            MemoryMarshal.CreateReadOnlySpan(ref m.M11, 16).CopyTo(matrixFloats.Slice(i * 16, 16));
        }

        if (n < EntityGpuSkinningLimits.MaxBones)
        {
            matrixFloats.Slice(n * 16, (EntityGpuSkinningLimits.MaxBones - n) * 16).Clear();
        }

        _entityPrevBoneUpload?.Upload(_entityPrevSkinningUboScratch.AsSpan(0, EntitySkinningUboMatrixBytes));
    }

    private void CapturePreviousEntitySkinningBones()
    {
        if (_lastEntityBoneSnapshotCount <= 0)
        {
            _entityPrevBoneSnapshotValid = false;
            _entityPrevBoneSnapshotCount = 0;
            return;
        }

        _entityPrevBoneSnapshotCount = Math.Min(_lastEntityBoneSnapshotCount, EntityGpuSkinningLimits.MaxBones);
        _entityBoneScratch.AsSpan(0, _entityPrevBoneSnapshotCount)
            .CopyTo(_entityPrevBoneScratch.AsSpan(0, _entityPrevBoneSnapshotCount));
        _entityPrevBoneSnapshotValid = true;
    }

    private void InvalidatePreviousEntitySkinningBones()
    {
        _entityPrevBoneSnapshotValid = false;
        _entityPrevBoneSnapshotCount = 0;
    }

    /// <summary>Called from <see cref="AutoPBR.App.Controls.GlPbrPreviewControl.OnOpenGlInit"/> only.</summary>
    internal void GlInit(GlInterface glInterface) => BeginGlInit(glInterface);

    internal void GlInitNativeWglPresenter(GlInterface glInterface) => BeginNativeWglPresenterGlInit(glInterface);

    private void FinishGlInitLocked(GlInterface renderGlInterface, PreviewDesktopWglContext? sidecar)
    {
        try
        {
            _gl = GL.GetApi(renderGlInterface.GetProcAddress);
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            EmitDiagnostic("[3D preview] " + _lastError);
            return;
        }

        string versionStr;
        if (sidecar is not null)
        {
            versionStr = sidecar.VersionString;
        }
        else
        {
            versionStr = ReadGlVersionString(_gl);
        }

        _glVersionString = versionStr;
        _useOpenGlEs = sidecar is null &&
                       versionStr.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        _glCapabilities = PreviewGlCapabilities.FromGl(_gl, _useOpenGlEs, _glVersionString);
        _shaderToolchainPlan = GlShaderToolchainPlan.FromCapabilities(_glCapabilities, GlSpirVShaderManifest.Bundled.Count);
        _gpuBootstrap = new GpuBootstrapRunner();
        _gpuBootstrapAborted = false;
        RaiseGpuInitProgress(PreviewGpuInitPhases.Preparing, _settings);
    }

    private static string ReadGlVersionString(GL gl)
    {
        unsafe
        {
            var p = gl.GetString(StringName.Version);
            return p is null ? "(unknown)" : Marshal.PtrToStringUTF8((nint)p) ?? "(unknown)";
        }
    }

    /// <summary>Desktop WGL only: maps the preview VSync setting to native swap interval (ANGLE/GLES uses Avalonia pacing).</summary>
    internal void ConfigurePresentationVsync(GlInterface glInterface, bool enabled, int? displayRefreshHz = null)
    {
        lock (_sync)
        {
            if (_useOpenGlEs || _desktopWglSidecar is not null)
            {
                return;
            }

            var interval = enabled ? 1 : 0;
            var detectedRefreshHz = displayRefreshHz.GetValueOrDefault();
            if (_appliedWglSwapInterval == interval && _appliedWglDisplayRefreshHz == detectedRefreshHz)
            {
                return;
            }

            if (PreviewWglPresentation.TrySetSwapInterval(glInterface, interval))
            {
                _appliedWglSwapInterval = interval;
                _appliedWglDisplayRefreshHz = detectedRefreshHz;
                EmitDiagnostic(interval == 0
                    ? FormatWglSwapIntervalDiagnostic("off; uncapped presentation", detectedRefreshHz)
                    : FormatWglSwapIntervalDiagnostic("on; pacing to current display", detectedRefreshHz));
            }
            else if (_appliedWglSwapInterval == int.MinValue)
            {
                EmitDiagnostic("[3D preview] wglSwapIntervalEXT unavailable; preview FPS may follow display vsync.");
            }
        }
    }

    private static string FormatWglSwapIntervalDiagnostic(string mode, int detectedRefreshHz) =>
        detectedRefreshHz > 1
            ? $"[3D preview] WGL VSync {mode} ({detectedRefreshHz} Hz detected)."
            : $"[3D preview] WGL VSync {mode} (display refresh unknown).";

    /// <summary>Called from <see cref="AutoPBR.App.Controls.GlPbrPreviewControl.OnOpenGlDeinit"/> only.</summary>
    internal void GlDeinit(GlInterface glInterface)
    {
        _ = glInterface;
        PreviewDesktopWglContext? sidecar;
        lock (_sync)
        {
            _appliedWglSwapInterval = int.MinValue;
            _appliedWglDisplayRefreshHz = int.MinValue;
            _gpuAlive = false;
            _gpuBootstrap = null;
            _pendingShaderReload = false;
            sidecar = _desktopWglSidecar;
        }

        // Stop the async sidecar bootstrap worker before touching GL objects / the WGL context.
        var spin = 0;
        while (Volatile.Read(ref _sidecarBootstrapWorkerState) != 0 && spin < 5000)
        {
            Thread.Sleep(1);
            spin++;
        }

        if (sidecar is not null)
        {
            try
            {
                // Sidecar-owned GL objects must be deleted with the WGL context current.
                sidecar.Invoke(() =>
                {
                    using (sidecar.BindOnOwnerThread())
                    {
                        lock (_sync)
                        {
                            DisposeAllGpuObjectsLocked();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                EmitDiagnostic($"[3D preview] Sidecar GPU teardown: {ex.GetType().Name}: {ex.Message}");
                lock (_sync)
                {
                    AbandonGpuObjectReferencesLocked();
                }
            }

            lock (_sync)
            {
                DestroyDesktopWglSidecar();
                DisposeEntityRebakeWorker();
                _gl = null;
                _gpuInitTier = PreviewGpuInitTier.None;
                _shadowAwareGodRayInitAttempted = false;
                _gpuInitProgress = PreviewGpuInitProgress.Starting;
            }

            return;
        }

        lock (_sync)
        {
            DisposeAllGpuObjectsLocked();
            DisposeEntityRebakeWorker();
            _gl = null;
            _shaderToolchainPlan = null;
            _nativeWglPresenterActive = false;
            _gpuInitTier = PreviewGpuInitTier.None;
            _shadowAwareGodRayInitAttempted = false;
            _gpuInitProgress = PreviewGpuInitProgress.Starting;
        }
    }

    /// <summary>Deletes GL objects. Caller must hold <see cref="_sync"/> and have the owning GL context current.</summary>
    private void DisposeAllGpuObjectsLocked()
    {
        _mesh?.Dispose();
        _mesh = null;
        _groundMesh?.Dispose();
        _groundMesh = null;
        _groundChunkBatches = [];
        DisposeTerrainGpuChunks();
        _terrainStreamer?.Dispose();
        _terrainStreamer = null;
        _grassGroundAlbedo?.Dispose();
        _grassGroundAlbedo = null;
        _grassGroundNormal?.Dispose();
        _grassGroundNormal = null;
        _grassGroundSpec?.Dispose();
        _grassGroundSpec = null;
        _grassGroundHeight?.Dispose();
        _grassGroundHeight = null;
        _neutralNormal?.Dispose();
        _neutralNormal = null;
        _neutralSpec?.Dispose();
        _neutralSpec = null;
        _neutralHeight?.Dispose();
        _neutralHeight = null;
        _grassGroundReady = false;
        _albedo?.Dispose();
        _albedo = null;
        _normal?.Dispose();
        _normal = null;
        _spec?.Dispose();
        _spec = null;
        _height?.Dispose();
        _height = null;
        DisposeMaterialTextureArrays();
        DisposeGpuTimerProfiler();
        DestroyGenesisProgramCache();
        _program?.Dispose();
        _program = null;
        _shadowProgram?.Dispose();
        _shadowProgram = null;
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
        _shadowTargetsDirty = false;
        DestroyAtmosphereResources();
        DestroyImageHistogramResources();
        DestroyGodRayResources();
        DestroyVolumeResources();
        DestroyVolumetricCloudResources();
        DestroyPreviewTaaResources();
        DestroyMoonBillboard();
        DestroyLineOverlay();
        DestroyNativeWglOverlay();
        DestroySunDebugOverlay();
        _shaderCtx = null;
        _shaderToolchainPlan = null;

        DisposeEntitySkinningUploadBuffers();
        DisposeGenesisMaterialDrawRecordBuffer();
        DisposeGenesisIndirectDrawCommands();
    }

    /// <summary>Drops managed references without issuing GL deletes (context already gone).</summary>
    private void AbandonGpuObjectReferencesLocked()
    {
        _gl = null;
        _mesh = null;
        _groundMesh = null;
        _groundChunkBatches = [];
        _terrainGpuChunks.Clear();
        _terrainStreamer?.Dispose();
        _terrainStreamer = null;
        _grassGroundAlbedo = null;
        _grassGroundNormal = null;
        _grassGroundSpec = null;
        _grassGroundHeight = null;
        _neutralNormal = null;
        _neutralSpec = null;
        _neutralHeight = null;
        _grassGroundReady = false;
        _albedo = null;
        _normal = null;
        _spec = null;
        _height = null;
        AbandonMaterialTextureArrays();
        AbandonGpuTimerProfiler();
        _genesisPrograms.Clear();
        _genesisProgramLru.Clear();
        _program = null;
        _shadowProgram = null;
        _shadowTarget = null;
        _shadowTargetCascadeNear = null;
        _shadowTargetCascadeMid = null;
        _shadowTargetsNearRes = 0;
        _shadowTargetsMidRes = 0;
        _shadowTargetsFarRes = 0;
        _shadowTargetsWantCascades = false;
        _shadowTargetsDirty = false;
        _shaderCtx = null;
        _shaderToolchainPlan = null;
        _entityBoneUbo = 0;
        _entityPrevBoneUbo = 0;
        _entityNormalBoneUbo = 0;
        _entityBoneUpload = null;
        _entityPrevBoneUpload = null;
        _entityNormalBoneUpload = null;
        _genesisMaterialDrawRecordSsbo = 0;
        _genesisMaterialDrawRecordUpload = null;
        _genesisMaterialDrawRecordsUseSsbo = false;
        AbandonGenesisIndirectDrawCommands();
        AbandonImageHistogramResources();
        DestroyAtmosphereResources();
        DestroyGodRayResources();
        DestroyVolumeResources();
        DestroyVolumetricCloudResources();
        DestroyPreviewTaaResources();
        DestroyHdr2DResources(_gl);
        DestroyMoonBillboard();
        DestroyLineOverlay();
        _nativeOverlayRenderer = null;
        _nativeOverlayShaderErrorLogged = false;
        DestroySunDebugOverlay();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeEntityRebakeWorker();
        if (_shaderPrewarmProgressHooked)
        {
            PreviewShaderPrewarm.ProgressChanged -= OnShaderPrewarmProgress;
            _shaderPrewarmProgressHooked = false;
        }

        lock (_sync)
        {
            _gpuAlive = false;
        }
    }

    private void DisposeEntitySkinningUploadBuffers()
    {
        _entityBoneUpload?.Dispose();
        _entityBoneUpload = null;
        _entityPrevBoneUpload?.Dispose();
        _entityPrevBoneUpload = null;
        _entityNormalBoneUpload?.Dispose();
        _entityNormalBoneUpload = null;
        _entityBoneUbo = 0;
        _entityPrevBoneUbo = 0;
        _entityNormalBoneUbo = 0;
        _entitySkinningUsesSsbo = false;
        _entityBoneUboBlockBoundMain = false;
        _entityBoneUboBlockBoundShadow = false;
    }
}
