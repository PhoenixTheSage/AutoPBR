using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.PreviewGpuAssets;

using Silk.NET.OpenGL;

using System.Numerics;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudBrickGenerationTests
{
    [Fact]
    public void GenerationRecord_MatchesStd430AbiAndIsDeterministic()
    {
        var key = new PreviewSparseCloudLogicalBrickKey(1, -17, 4, 29);
        var first =
            PreviewSparseCloudBrickGenerationContract.CreateRecord(key, 57);
        var second =
            PreviewSparseCloudBrickGenerationContract.CreateRecord(key, 57);

        Assert.Equal(
            PreviewSparseCloudVolumeContract.GenerationQueueRecordByteSize,
            Marshal.SizeOf<PreviewSparseCloudBrickGenerationRecord>());
        Assert.Equal(key, first.Key);
        Assert.Equal(57, first.PhysicalBrickIndex);
        Assert.Equal(first.StableSeed, second.StableSeed);
        Assert.Equal(
            PreviewSparseCloudBrickGenerationContract.StableHash(key),
            unchecked((uint)first.StableSeed));
    }

    [Fact]
    public void Controller_BackpressuresEnteringWithoutLosingPendingPages()
    {
        var controller = new PreviewSparseCloudClipmapController();
        var blocked = controller.Update(
            Vector3.Zero,
            Vector3.UnitZ,
            cloudVerticalCenterWorldY: 30f,
            ReadOnlySpan<Vector4>.Empty,
            frame: 0,
            maximumEntering: 0);

        Assert.Empty(blocked.Entering);
        Assert.Equal(0, blocked.RequestedCount);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.LogicalPageCount,
            blocked.PendingCount);

        var admitted = controller.Update(
            Vector3.Zero,
            Vector3.UnitZ,
            cloudVerticalCenterWorldY: 30f,
            ReadOnlySpan<Vector4>.Empty,
            frame: 1,
            maximumEntering: 2);
        Assert.Equal(2, admitted.Entering.Count);
        Assert.Equal(2, admitted.RequestedCount);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.LogicalPageCount - 2,
            admitted.PendingCount);
    }

    [Fact]
    public void ConservativeDistanceValidator_AcceptsEmptyAndMixedBricks()
    {
        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        var empty = new byte[size * size * size * 2];
        for (var index = 0; index < empty.Length; index += 2)
        {
            empty[index + 1] = 1;
        }

        Assert.True(
            PreviewSparseCloudBrickGenerationContract.ValidateConservativeBrick(
                empty,
                out var emptyReason),
            emptyReason);
        Assert.Equal("valid-empty", emptyReason);

        var mixed = empty.ToArray();
        var center = ((5 * size + 5) * size + 5) * 2;
        mixed[center] = 192;
        mixed[center + 1] = 0;
        Assert.True(
            PreviewSparseCloudBrickGenerationContract.ValidateConservativeBrick(
                mixed,
                out var mixedReason),
            mixedReason);
        Assert.Contains("valid-occupied", mixedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConservativeDistanceValidator_RejectsOverestimateAndOccupiedSkip()
    {
        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        var brick = new byte[size * size * size * 2];
        brick[0] = 255;
        brick[1] = 1;
        Assert.False(
            PreviewSparseCloudBrickGenerationContract.ValidateConservativeBrick(
                brick,
                out var occupiedReason));
        Assert.Contains(
            "occupied-distance-nonzero",
            occupiedReason,
            StringComparison.Ordinal);

        brick[1] = 0;
        brick[3] =
            PreviewSparseCloudBrickGenerationContract
                .MaximumConservativeDistance + 1;
        Assert.False(
            PreviewSparseCloudBrickGenerationContract.ValidateConservativeBrick(
                brick,
                out var overestimateReason));
        Assert.Contains(
            "distance-over-cq4.5-cap",
            overestimateReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GenerationFenceClassification_FailsClosed()
    {
        Assert.Equal(
            PreviewSparseCloudGenerationFenceState.Pending,
            GlSparseCloudBrickGenerator.ClassifyFenceResult(
                GLEnum.TimeoutExpired));
        Assert.Equal(
            PreviewSparseCloudGenerationFenceState.Complete,
            GlSparseCloudBrickGenerator.ClassifyFenceResult(
                GLEnum.ConditionSatisfied));
        Assert.Equal(
            PreviewSparseCloudGenerationFenceState.Failed,
            GlSparseCloudBrickGenerator.ClassifyFenceResult(
                (GLEnum)0x911D));
    }

    [Fact]
    public void TemplateSetAcceptance_AllowsBothV1AndV2ButRejectsMismatchedShapes()
    {
        var v1 = new PreviewSparseCloudTemplateAssetSet(
            PreviewSparseCloudTemplateAssetContract.AssetVersion,
            PreviewSparseCloudTemplateAssetContract.GenerationAbi,
            PreviewSparseCloudTemplateAssetContract.Assets
                .Select(descriptor => new PreviewSparseCloudTemplateAssetPayload(
                    descriptor,
                    new byte[descriptor.ByteLength]))
                .ToArray());
        Assert.True(
            GlSparseCloudBrickGenerator.IsTemplateSetAcceptable(v1, out var v1Diagnostic),
            v1Diagnostic);

        var v2 = new PreviewSparseCloudTemplateAssetSet(
            PreviewSparseCloudTemplateAssetContractV2.AssetVersion,
            PreviewSparseCloudTemplateAssetContractV2.GenerationAbi,
            PreviewSparseCloudTemplateAssetContractV2.Assets
                .Select(descriptor => new PreviewSparseCloudTemplateAssetPayload(
                    descriptor,
                    new byte[descriptor.ByteLength]))
                .ToArray());
        Assert.True(
            GlSparseCloudBrickGenerator.IsTemplateSetAcceptable(v2, out var v2Diagnostic),
            v2Diagnostic);

        var unknownVersion = v2 with { AssetVersion = 3 };
        Assert.False(
            GlSparseCloudBrickGenerator.IsTemplateSetAcceptable(unknownVersion, out var unknownDiagnostic));
        Assert.Equal("template-set-invalid", unknownDiagnostic);

        var shortCount = v1 with { Templates = v1.Templates.Take(11).ToArray() };
        Assert.False(
            GlSparseCloudBrickGenerator.IsTemplateSetAcceptable(shortCount, out var shortCountDiagnostic));
        Assert.Equal("template-set-invalid", shortCountDiagnostic);

        var truncatedPayload = new PreviewSparseCloudTemplateAssetPayload(
            PreviewSparseCloudTemplateAssetContract.Assets[^1],
            new byte[PreviewSparseCloudTemplateAssetContract.ByteLength / 2]);
        var wrongByteLength = v1 with
        {
            Templates = v1.Templates.Take(11).Append(truncatedPayload).ToArray(),
        };
        Assert.False(
            GlSparseCloudBrickGenerator.IsTemplateSetAcceptable(wrongByteLength, out var wrongByteLengthDiagnostic));
        Assert.Equal("template-set-invalid", wrongByteLengthDiagnostic);
    }
}
