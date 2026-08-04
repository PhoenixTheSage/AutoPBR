namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Immutable CQ4/CA2.4 v2 cloud-envelope ABI. Same dimensions, channel layout, and residency
/// contract as <see cref="PreviewSparseCloudTemplateAssetContract"/>, but with new deterministic
/// seeds and morphology that bakes visible macro asymmetry into the density itself. Never
/// reinterprets or overwrites frozen v1 bytes; v1 and v2 templates coexist side by side.
/// </summary>
public static class PreviewSparseCloudTemplateAssetContractV2
{
    public const int AssetVersion = 2;
    public const string GenerationAbi = "cq4-envelope-v2";
    public const int Width = PreviewSparseCloudTemplateAssetContract.Width;
    public const int Height = PreviewSparseCloudTemplateAssetContract.Height;
    public const int Depth = PreviewSparseCloudTemplateAssetContract.Depth;
    public const int ChannelCount = PreviewSparseCloudTemplateAssetContract.ChannelCount;
    public const int BytesPerChannel = PreviewSparseCloudTemplateAssetContract.BytesPerChannel;
    public const int VariantCountPerFamily =
        PreviewSparseCloudTemplateAssetContract.VariantCountPerFamily;
    public const int CumulusBaseLayer = PreviewSparseCloudTemplateAssetContract.CumulusBaseLayer;
    public const int MaximumEncodedDistance =
        PreviewSparseCloudTemplateAssetContract.MaximumEncodedDistance;

    public static IReadOnlyList<PreviewSparseCloudTemplateAssetDescriptor> Assets { get; } =
        Array.AsReadOnly(
        [
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusHumilis, 0, 51011, "b8875e9ae93fbea27c641d004fd85bf8a1238ec47b404cedef0c539aba3f7372"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusHumilis, 1, 51017, "da1ea8a12d2a8683da184cb6394801004c79fb2f0f1f3aa46838e37e3491edce"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusHumilis, 2, 51023, "bd9eb9541ae63277b320b95b543652c4d60dc5105e73e1661bbdcca6ccf01b06"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusMediocris, 0, 52013, "ed2ef54c3e1a2da691a73d532f85800c88c1fae0463a9240a45e5ae70996da46"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusMediocris, 1, 52019, "06f46f78f33fe1739d85a05323cad698e654950a0c87941cef03bb8bf5ed13b7"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusMediocris, 2, 52043, "43f37a8480c1945f614c61be18f12c4c2045b2f738eb4cfbfcedea17c34886b4"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusCongestus, 0, 53003, "8dbf438f8b544618fe0c5b8bcbcfb55ad137bef4d90c5c62aa0bcce210a61080"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusCongestus, 1, 53019, "333b80ea61767fe07a2cb50df3149a10c38cbb89cb715cecbba1ba5e95292b20"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusCongestus, 2, 53037, "37218bc4d8d8de6000c67c00a8eafc68a903eec17467ef50e2909821824fa443"),
            Descriptor(PreviewSparseCloudTemplateFamily.Stratus, 0, 54017, "8a9f851ae78d846ea26a7635939ac63059896e953163e707dfce043860376a98"),
            Descriptor(PreviewSparseCloudTemplateFamily.Stratus, 1, 54021, "51ef376fdab34ec6ef12bb0eddcd169107da7b71f1b670ec2e8b5204efa8bc52"),
            Descriptor(PreviewSparseCloudTemplateFamily.Stratus, 2, 54027, "109da7f7ca87a39da8c00b68df6e4b2b9b3199e0ad4eb3bd9a6d1116320b5d3c"),
        ]);

    public static int ByteLength => PreviewSparseCloudTemplateAssetContract.ByteLength;

    public static long TotalByteLength =>
        checked((long)ByteLength * Assets.Count);

    public static bool TryGet(
        PreviewSparseCloudTemplateFamily family,
        int variant,
        out PreviewSparseCloudTemplateAssetDescriptor descriptor)
    {
        descriptor = Assets.FirstOrDefault(
            asset => asset.Family == family && asset.Variant == variant)!;
        return descriptor is not null;
    }

    public static bool ValidatePayload(
        PreviewSparseCloudTemplateAssetDescriptor descriptor,
        ReadOnlySpan<byte> payload,
        out string reason)
    {
        if (!Assets.Contains(descriptor))
        {
            reason = "descriptor-not-cq4-envelope-v2";
            return false;
        }

        if (payload.Length != descriptor.ByteLength)
        {
            reason = $"length-{payload.Length}-expected-{descriptor.ByteLength}";
            return false;
        }

        reason = "valid";
        return true;
    }

    private static PreviewSparseCloudTemplateAssetDescriptor Descriptor(
        PreviewSparseCloudTemplateFamily family,
        int variant,
        int seed,
        string expectedSha256) =>
        new(
            family,
            variant,
            seed,
            $"cloud_envelope_{PreviewSparseCloudTemplateAssetContract.FamilySlug(family)}_" +
            $"{variant}_{Width}x{Height}x{Depth}_rg8_v{AssetVersion}.bin",
            expectedSha256,
            AssetVersion);
}
