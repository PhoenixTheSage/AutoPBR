using AutoPBR.App.Rendering.Abstractions;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    // Keep clear of main-pass globals: shadow far/near/mid = 4/5/7, sky LUT = 6, material 2D = 0–3.
    private const int MainPassAlbedoArrayUnit = 8;
    private const int MainPassNormalArrayUnit = 9;
    private const int MainPassSpecularArrayUnit = 10;
    private const int MainPassHeightArrayUnit = 11;
    private const int ShadowPassAlbedoArrayUnit = 1;

    private bool ShouldUseMaterialTextureArrays() =>
        !_materialTextureArraysCompileDisabled &&
        ShouldUseMaterialDrawRecordSsbo() &&
        _glCapabilities?.CanUseMaterialTextureArrays == true;

    private void DisableMaterialTextureArraysCompile(string? reason)
    {
        if (_materialTextureArraysCompileDisabled)
        {
            return;
        }

        _materialTextureArraysCompileDisabled = true;
        var detail = string.IsNullOrWhiteSpace(reason) ? "compile failed" : TrimTessellationFailureReason(reason);
        EmitDiagnostic("[3D preview] Material texture-array path disabled for this session; using texture-unit fallback. " + detail);
    }

    private void ResetMaterialTextureArraysCompileState()
    {
        _materialTextureArraysCompileDisabled = false;
    }

    private bool TryEnsureMaterialTextureArrays(
        ref GlRenderFrame frame,
        bool materialDrawRecordsUploaded)
    {
        var slots = frame.BlockSlots;
        if (!GenesisMaterialTextureArrayEligibility.TryResolve(
                ShouldUseMaterialTextureArrays(),
                materialDrawRecordsUploaded,
                frame.BlockModel is not null,
                slots is { Length: > 0 },
                out _))
        {
            return false;
        }

        // Program selection may have compiled without array samplers even when eligibility
        // would otherwise pass (e.g. session compile fallback). Never claim arrays are live then.
        if (!_activeGenesisProgramKey.MaterialTextureArrays)
        {
            return false;
        }

        if (slots is not { Length: > 0 })
        {
            return false;
        }

        var maxLayers = Math.Max(1, _gl?.GetInteger(GetPName.MaxArrayTextureLayers) ?? 1);
        if (!GenesisMaterialTextureArrayPlan.TryCreate(slots, maxLayers, out var plan, out var reason))
        {
            LogMaterialTextureArrayFallbackOnce(reason);
            return false;
        }

        var resolvedPlan = plan;
        if (resolvedPlan is null)
        {
            return false;
        }

        if (_materialTextureArrayPlan is not null &&
            resolvedPlan.ContentEquals(_materialTextureArrayPlan) &&
            _materialAlbedoArray is not null &&
            _materialNormalArray is not null &&
            _materialSpecArray is not null &&
            _materialHeightArray is not null)
        {
            return true;
        }

        EnsureMaterialTextureArrayObjects(frame.Gl);
        var layerBytes = resolvedPlan.Width * resolvedPlan.Height * 4;
        var totalBytes = layerBytes * resolvedPlan.Layers;
        EnsureMaterialTextureArrayScratch(totalBytes);
        var scratch = _materialTextureArrayScratch;
        if (scratch is null)
        {
            return false;
        }

        frame.Gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        try
        {
            FillMaterialArrayScratch(slots, resolvedPlan, MaterialArrayMapKind.Albedo, scratch);
            _materialAlbedoArray!.UploadRgbaIfChanged(resolvedPlan.Width, resolvedPlan.Height, resolvedPlan.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
            FillMaterialArrayScratch(slots, resolvedPlan, MaterialArrayMapKind.Normal, scratch);
            _materialNormalArray!.UploadRgbaIfChanged(resolvedPlan.Width, resolvedPlan.Height, resolvedPlan.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
            FillMaterialArrayScratch(slots, resolvedPlan, MaterialArrayMapKind.Specular, scratch);
            _materialSpecArray!.UploadRgbaIfChanged(resolvedPlan.Width, resolvedPlan.Height, resolvedPlan.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
            FillMaterialArrayScratch(slots, resolvedPlan, MaterialArrayMapKind.Height, scratch);
            _materialHeightArray!.UploadRgbaIfChanged(resolvedPlan.Width, resolvedPlan.Height, resolvedPlan.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
        }
        finally
        {
            frame.Gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        }

        _materialTextureArrayPlan = resolvedPlan;
        if (!_loggedMaterialTextureArraysReady)
        {
            _loggedMaterialTextureArraysReady = true;
            EmitDiagnostic(
                $"[3D preview] Material texture arrays ready: layers={resolvedPlan.Layers}, size={resolvedPlan.Width}x{resolvedPlan.Height}, bindless={(_glCapabilities?.BindlessTextures == true ? "available" : "unavailable")}.");
        }

        return true;
    }

    private void BindMainPassMaterialTextureArrays(MainProgramUniformLocs u)
    {
        _materialAlbedoArray!.Bind(MainPassAlbedoArrayUnit);
        _materialNormalArray!.Bind(MainPassNormalArrayUnit);
        _materialSpecArray!.Bind(MainPassSpecularArrayUnit);
        _materialHeightArray!.Bind(MainPassHeightArrayUnit);
        SetIntLoc(u.AlbedoArray, MainPassAlbedoArrayUnit);
        SetIntLoc(u.NormalArray, MainPassNormalArrayUnit);
        SetIntLoc(u.SpecularArray, MainPassSpecularArrayUnit);
        SetIntLoc(u.HeightArray, MainPassHeightArrayUnit);
    }

    private void BindShadowPassMaterialTextureArray(ShadowProgramUniformLocs u)
    {
        _materialAlbedoArray!.Bind(ShadowPassAlbedoArrayUnit);
        SetIntOnProgramLoc(_shadowProgram!, u.AlbedoArray, ShadowPassAlbedoArrayUnit);
    }

    /// <summary>
    /// When the active program declares sampler2DArray uniforms, every unit must be complete
    /// even if the ground pass only samples 2D grass textures (uGenesisUseMaterialTextureArray=0).
    /// </summary>
    private void BindFallbackMaterialTextureArraysIfPresent(MainProgramUniformLocs u)
    {
        if (u is { AlbedoArray: < 0, NormalArray: < 0, SpecularArray: < 0, HeightArray: < 0 })
        {
            return;
        }

        EnsureFallbackMaterialTextureArrays(_gl!);
        if (u.AlbedoArray >= 0)
        {
            _fallbackMaterialAlbedoArray!.Bind(MainPassAlbedoArrayUnit);
            SetIntLoc(u.AlbedoArray, MainPassAlbedoArrayUnit);
        }

        if (u.NormalArray >= 0)
        {
            _fallbackMaterialNormalArray!.Bind(MainPassNormalArrayUnit);
            SetIntLoc(u.NormalArray, MainPassNormalArrayUnit);
        }

        if (u.SpecularArray >= 0)
        {
            _fallbackMaterialSpecArray!.Bind(MainPassSpecularArrayUnit);
            SetIntLoc(u.SpecularArray, MainPassSpecularArrayUnit);
        }

        if (u.HeightArray >= 0)
        {
            _fallbackMaterialHeightArray!.Bind(MainPassHeightArrayUnit);
            SetIntLoc(u.HeightArray, MainPassHeightArrayUnit);
        }
    }

    private void BindFallbackShadowMaterialTextureArrayIfPresent(ShadowProgramUniformLocs u)
    {
        if (u.AlbedoArray < 0)
        {
            return;
        }

        EnsureFallbackMaterialTextureArrays(_gl!);
        _fallbackMaterialAlbedoArray!.Bind(ShadowPassAlbedoArrayUnit);
        SetIntOnProgramLoc(_shadowProgram!, u.AlbedoArray, ShadowPassAlbedoArrayUnit);
    }

    private void EnsureFallbackMaterialTextureArrays(GL gl)
    {
        if (_fallbackMaterialAlbedoArray is not null)
        {
            return;
        }

        _fallbackMaterialAlbedoArray = new GlTexture2DArray(gl);
        _fallbackMaterialNormalArray = new GlTexture2DArray(gl);
        _fallbackMaterialSpecArray = new GlTexture2DArray(gl);
        _fallbackMaterialHeightArray = new GlTexture2DArray(gl);
        // 1x1x1 complete arrays so idle/ground draws never hit unbound sampler2DArray units.
        ReadOnlySpan<byte> albedo = [128, 128, 128, 255];
        ReadOnlySpan<byte> normal = [128, 128, 255, 255];
        ReadOnlySpan<byte> spec = [120, 60, 40, 255];
        ReadOnlySpan<byte> height = [128, 128, 128, 255];
        _fallbackMaterialAlbedoArray.UploadRgbaIfChanged(1, 1, 1, albedo, nearest: true);
        _fallbackMaterialNormalArray.UploadRgbaIfChanged(1, 1, 1, normal, nearest: true);
        _fallbackMaterialSpecArray.UploadRgbaIfChanged(1, 1, 1, spec, nearest: true);
        _fallbackMaterialHeightArray.UploadRgbaIfChanged(1, 1, 1, height, nearest: true);
    }

    private void EnsureMaterialTextureArrayObjects(GL gl)
    {
        _materialAlbedoArray ??= new GlTexture2DArray(gl);
        _materialNormalArray ??= new GlTexture2DArray(gl);
        _materialSpecArray ??= new GlTexture2DArray(gl);
        _materialHeightArray ??= new GlTexture2DArray(gl);
    }

    private void EnsureMaterialTextureArrayScratch(int bytes)
    {
        if (_materialTextureArrayScratch is null || _materialTextureArrayScratch.Length < bytes)
        {
            _materialTextureArrayScratch = new byte[bytes];
        }
    }

    private void FillMaterialArrayScratch(
        IReadOnlyList<PreviewMaterial> slots,
        GenesisMaterialTextureArrayPlan plan,
        MaterialArrayMapKind mapKind,
        byte[] scratch)
    {
        var layerBytes = plan.Width * plan.Height * 4;
        for (var layer = 0; layer < plan.Layers; layer++)
        {
            var dest = scratch.AsSpan(layer * layerBytes, layerBytes);
            var source = ResolveMaterialArraySource(slots[layer], mapKind);
            var sourceWidth = Math.Max(1, slots[layer].Width);
            var sourceHeight = Math.Max(1, slots[layer].Height);
            if (source is { Length: > 0 } src && src.Length >= sourceWidth * sourceHeight * 4)
            {
                ResampleMaterialArrayLayer(
                    src.Span,
                    sourceWidth,
                    sourceHeight,
                    dest,
                    plan.Width,
                    plan.Height,
                    slots[layer].GlUploadFlipRows);
            }
            else
            {
                FillNeutralLayer(dest, mapKind);
            }
        }
    }

    internal static void ResampleMaterialArrayLayer(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        bool flipRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationHeight);
        var sourceBytes = checked(sourceWidth * sourceHeight * 4);
        var destinationBytes = checked(destinationWidth * destinationHeight * 4);
        if (source.Length < sourceBytes || destination.Length < destinationBytes)
        {
            throw new ArgumentException("RGBA resample spans do not match their declared dimensions.");
        }

        for (var y = 0; y < destinationHeight; y++)
        {
            var sampledY = Math.Min(sourceHeight - 1, y * sourceHeight / destinationHeight);
            if (flipRows)
            {
                sampledY = sourceHeight - 1 - sampledY;
            }

            for (var x = 0; x < destinationWidth; x++)
            {
                var sampledX = Math.Min(sourceWidth - 1, x * sourceWidth / destinationWidth);
                var sourceOffset = (sampledY * sourceWidth + sampledX) * 4;
                var destinationOffset = (y * destinationWidth + x) * 4;
                source.Slice(sourceOffset, 4).CopyTo(destination.Slice(destinationOffset, 4));
            }
        }
    }

    private static ReadOnlyMemory<byte>? ResolveMaterialArraySource(PreviewMaterial material, MaterialArrayMapKind mapKind) =>
        mapKind switch
        {
            MaterialArrayMapKind.Albedo => material.AlbedoRgba,
            MaterialArrayMapKind.Normal => material.NormalRgba,
            MaterialArrayMapKind.Specular => material.SpecularRgba,
            MaterialArrayMapKind.Height => material.HeightRgba,
            _ => null,
        };

    private static void FillNeutralLayer(Span<byte> dest, MaterialArrayMapKind mapKind)
    {
        var r = (byte)255;
        var g = (byte)255;
        var b = (byte)255;
        var a = (byte)255;
        switch (mapKind)
        {
            case MaterialArrayMapKind.Albedo:
                r = 180;
                g = 180;
                b = 190;
                break;
            case MaterialArrayMapKind.Normal:
                r = 128;
                g = 128;
                b = 255;
                break;
            case MaterialArrayMapKind.Specular:
                r = 120;
                g = 60;
                b = 40;
                break;
            case MaterialArrayMapKind.Height:
                r = 128;
                g = 128;
                b = 128;
                break;
        }

        for (var i = 0; i + 3 < dest.Length; i += 4)
        {
            dest[i] = r;
            dest[i + 1] = g;
            dest[i + 2] = b;
            dest[i + 3] = a;
        }
    }

    private void LogMaterialTextureArrayFallbackOnce(string reason)
    {
        if (_loggedMaterialTextureArrayFallbackReason == reason)
        {
            return;
        }

        _loggedMaterialTextureArrayFallbackReason = reason;
        EmitDiagnostic("[3D preview] Material texture-array fallback: " + reason + ".");
    }

    private void DisposeMaterialTextureArrays()
    {
        _materialAlbedoArray?.Dispose();
        _materialAlbedoArray = null;
        _materialNormalArray?.Dispose();
        _materialNormalArray = null;
        _materialSpecArray?.Dispose();
        _materialSpecArray = null;
        _materialHeightArray?.Dispose();
        _materialHeightArray = null;
        _fallbackMaterialAlbedoArray?.Dispose();
        _fallbackMaterialAlbedoArray = null;
        _fallbackMaterialNormalArray?.Dispose();
        _fallbackMaterialNormalArray = null;
        _fallbackMaterialSpecArray?.Dispose();
        _fallbackMaterialSpecArray = null;
        _fallbackMaterialHeightArray?.Dispose();
        _fallbackMaterialHeightArray = null;
        _materialTextureArrayPlan = null;
        _materialTextureArrayScratch = null;
        _loggedMaterialTextureArraysReady = false;
        _loggedMaterialTextureArrayFallbackReason = null;
    }

    private void AbandonMaterialTextureArrays()
    {
        _materialAlbedoArray = null;
        _materialNormalArray = null;
        _materialSpecArray = null;
        _materialHeightArray = null;
        _fallbackMaterialAlbedoArray = null;
        _fallbackMaterialNormalArray = null;
        _fallbackMaterialSpecArray = null;
        _fallbackMaterialHeightArray = null;
        _materialTextureArrayPlan = null;
        _materialTextureArrayScratch = null;
        _loggedMaterialTextureArraysReady = false;
        _loggedMaterialTextureArrayFallbackReason = null;
    }

    private enum MaterialArrayMapKind
    {
        Albedo,
        Normal,
        Specular,
        Height,
    }
}
