using System.Numerics;
using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private void InitGenesisMaterialDrawRecordSsbo(GL gl)
    {
        DisposeGenesisMaterialDrawRecordBuffer();
        _genesisMaterialDrawRecordsUseSsbo = ShouldUseMaterialDrawRecordSsbo();
        if (!_genesisMaterialDrawRecordsUseSsbo)
        {
            EmitDiagnostic("[3D preview] Material/draw records: scalar uniform fallback.");
            return;
        }

        Array.Clear(_genesisMaterialDrawRecordScratch);
        var usePersistent = _glCapabilities?.CanUsePersistentUploadRing == true;
        var ssboOffsetAlignment = Math.Max(16, gl.GetInteger((GetPName)0x90DF));
        _genesisMaterialDrawRecordUpload = new GlPersistentMappedUploadBuffer(
            gl,
            (BufferTargetARB)0x90D2,
            GenesisMaterialDrawRecordSsboBindingPoint,
            _genesisMaterialDrawRecordScratch.Length,
            ssboOffsetAlignment,
            usePersistent);
        _genesisMaterialDrawRecordSsbo = _genesisMaterialDrawRecordUpload.Handle;
        _genesisMaterialDrawRecordUpload.Upload(_genesisMaterialDrawRecordScratch);
        EmitDiagnostic("[3D preview] Material/draw records: " +
                       (_genesisMaterialDrawRecordUpload.UsesPersistentMapping
                           ? "persistent mapped SSBO transport."
                           : "BufferSubData SSBO transport."));
    }

    private bool TryUploadGenesisMaterialDrawRecords(ref GlRenderFrame frame)
    {
        if (!_genesisMaterialDrawRecordsUseSsbo ||
            _genesisMaterialDrawRecordUpload is null ||
            _genesisMaterialDrawRecordSsbo == 0 ||
            frame.BlockModel is null ||
            frame.BlockSlots is not { Length: > 0 })
        {
            return false;
        }

        var slots = frame.BlockSlots;
        var batches = frame.BlockModel.DrawBatches;
        if (batches.Length <= 0)
        {
            return false;
        }

        if (batches.Length > GenesisMaterialDrawRecordMaxRecords)
        {
            if (!_loggedMaterialDrawRecordOverflow)
            {
                _loggedMaterialDrawRecordOverflow = true;
                EmitDiagnostic(
                    $"[3D preview] Material/draw record SSBO skipped: batch count {batches.Length} exceeds capacity {GenesisMaterialDrawRecordMaxRecords}; using uniform fallback.");
            }

            return false;
        }

        var byteCount = batches.Length * GenesisMaterialDrawRecordBytes;
        var records = MemoryMarshal.Cast<byte, float>(_genesisMaterialDrawRecordScratch.AsSpan(0, byteCount));
        records.Clear();

        for (var i = 0; i < batches.Length; i++)
        {
            var batch = batches[i];
            if ((uint)batch.MaterialIndex >= (uint)slots.Length)
            {
                continue;
            }

            var slot = slots[batch.MaterialIndex];
            PackGenesisMaterialDrawRecord(
                records.Slice(i * GenesisMaterialDrawRecordFloats, GenesisMaterialDrawRecordFloats),
                ref frame,
                batch,
                slot,
                batch.MaterialIndex);
        }

        _genesisMaterialDrawRecordUpload.Upload(_genesisMaterialDrawRecordScratch.AsSpan(0, byteCount));
        return true;
    }

    private static void PackGenesisMaterialDrawRecord(
        Span<float> record,
        ref GlRenderFrame frame,
        PreviewDrawBatch batch,
        PreviewMaterial slot,
        int materialIndex)
    {
        var hasNormal = slot.NormalRgba is { Length: > 0 };
        var hasSpecular = slot.SpecularRgba is { Length: > 0 };
        var hasHeight = slot.HeightRgba is { Length: > 0 };
        var batchUsesTranslucentOverlay =
            batch.LayerPolicy.Kind == PreviewDepthLayerKind.TranslucentOverlay;
        var batchAlphaMode = batchUsesTranslucentOverlay
            ? (int)PreviewEntityAlphaMode.Blend
            : frame.EntityAlphaModeUniform;
        var batchAllowsParallax = !frame.EntityEmulatedPreview || batch.EnableParallax;
        var batchParallax = frame.EnableParallaxEff && batchAllowsParallax && hasHeight;
        var textureAtlasScale = frame.EntityEmulatedPreview
            ? EntityTextureAtlasScale(slot)
            : Vector2.One;
        var heightTexSize = hasHeight
            ? new Vector2(Math.Max(1, slot.Width), Math.Max(1, slot.Height))
            : Vector2.One;

        // POM UV travel stays full-strength; entity atlas mapping uses params0.yz (TextureAtlasScale).
        record[0] = 1f;
        record[1] = textureAtlasScale.X;
        record[2] = textureAtlasScale.Y;
        record[3] = heightTexSize.X;
        record[4] = heightTexSize.Y;
        record[5] = materialIndex;
        record[8] = batchParallax ? 1f : 0f;
        record[9] = batchParallax && frame.EnableParallaxAoEff ? 1f : 0f;
        record[10] = batchParallax && frame.EnableParallaxShadowEff ? 1f : 0f;
        record[11] = frame.EnableTessellationDisplacementEff && batchAllowsParallax && hasHeight ? 1f : 0f;
        record[12] = hasNormal ? 1f : 0f;
        record[13] = hasSpecular ? 1f : 0f;
        record[14] = hasHeight ? 1f : 0f;
        record[15] = batchAlphaMode;
    }

    private void BindGenesisMaterialDrawRecordBuffer()
    {
        _genesisMaterialDrawRecordUpload?.BindBase();
    }

    private void MarkGenesisMaterialDrawRecordsSubmitted()
    {
        _genesisMaterialDrawRecordUpload?.MarkSubmitted();
    }

    private void DisposeGenesisMaterialDrawRecordBuffer()
    {
        _genesisMaterialDrawRecordUpload?.Dispose();
        _genesisMaterialDrawRecordUpload = null;
        _genesisMaterialDrawRecordSsbo = 0;
        _genesisMaterialDrawRecordsUseSsbo = false;
    }
}
