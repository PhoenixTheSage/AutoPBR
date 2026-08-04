using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CA2.4 transactional loader. A complete, hash-valid v2 asymmetric-envelope set is always
/// preferred; any missing/corrupt v2 member falls back to the frozen, complete v1 set. Versions
/// are never mixed within one loaded <see cref="PreviewSparseCloudTemplateAssetSet"/>.
/// </summary>
internal static class PreviewSparseCloudTemplateAssetLoader
{
    public static bool TryLoad(
        out PreviewSparseCloudTemplateAssetSet assetSet,
        out string reason) =>
        TryLoad(
            PreviewCloudBakedAssetLoader.TryLoadBundledRaw,
            out assetSet,
            out reason);

    internal static bool TryLoad(
        PreviewCloudRawAssetReader assetReader,
        out PreviewSparseCloudTemplateAssetSet assetSet,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(assetReader);
        if (TryLoadVersion(
                assetReader,
                PreviewSparseCloudTemplateAssetContractV2.AssetVersion,
                PreviewSparseCloudTemplateAssetContractV2.GenerationAbi,
                PreviewSparseCloudTemplateAssetContractV2.Assets,
                out assetSet,
                out var v2Reason))
        {
            reason = v2Reason;
            return true;
        }

        if (TryLoadVersion(
                assetReader,
                PreviewSparseCloudTemplateAssetContract.AssetVersion,
                PreviewSparseCloudTemplateAssetContract.GenerationAbi,
                PreviewSparseCloudTemplateAssetContract.Assets,
                out assetSet,
                out var v1Reason))
        {
            reason = $"{v1Reason} (v2-{v2Reason})";
            return true;
        }

        assetSet = default;
        reason = $"v2-{v2Reason};v1-{v1Reason}";
        return false;
    }

    private static bool TryLoadVersion(
        PreviewCloudRawAssetReader assetReader,
        int assetVersion,
        string generationAbi,
        IReadOnlyList<PreviewSparseCloudTemplateAssetDescriptor> assets,
        out PreviewSparseCloudTemplateAssetSet assetSet,
        out string reason)
    {
        var payloads = new List<PreviewSparseCloudTemplateAssetPayload>(assets.Count);
        foreach (var descriptor in assets)
        {
            if (!assetReader(
                    descriptor.FileName,
                    out var data,
                    out var loadReason))
            {
                assetSet = default;
                reason = $"{descriptor.Symbol}-{loadReason}";
                return false;
            }

            if (!Validate(descriptor, data, out var validationReason))
            {
                assetSet = default;
                reason = $"{descriptor.Symbol}-{validationReason}";
                return false;
            }

            payloads.Add(new PreviewSparseCloudTemplateAssetPayload(
                descriptor,
                data));
        }

        assetSet = new PreviewSparseCloudTemplateAssetSet(
            assetVersion,
            generationAbi,
            payloads.AsReadOnly());
        reason =
            $"{generationAbi}/" +
            $"{payloads.Count}-templates/" +
            $"{assetSet.ByteLength}-bytes";
        return true;
    }

    internal static bool Validate(
        PreviewSparseCloudTemplateAssetDescriptor descriptor,
        ReadOnlySpan<byte> data,
        out string reason)
    {
        if (!PreviewSparseCloudTemplateAssetGenerator.ValidatePayloadForVersion(
                descriptor,
                data,
                out reason))
        {
            return false;
        }

        if (!PreviewSparseCloudTemplateAssetGenerator.HasExpectedHash(
                descriptor,
                data))
        {
            reason = "sha256-mismatch";
            return false;
        }

        reason = "valid";
        return true;
    }
}

internal readonly record struct PreviewSparseCloudTemplateAssetSet(
    int AssetVersion,
    string GenerationAbi,
    IReadOnlyList<PreviewSparseCloudTemplateAssetPayload> Templates)
{
    public long ByteLength =>
        Templates.Sum(template => template.Rg.LongLength);
}
