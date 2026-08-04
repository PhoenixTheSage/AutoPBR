namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Immutable CQ4 v1 cloud-envelope ABI. Each low-resolution RG8 volume stores envelope
/// density in R and conservative empty-space distance, in template voxels, in G.
/// </summary>
public static class PreviewSparseCloudTemplateAssetContract
{
    public const int AssetVersion = 1;
    public const string GenerationAbi = "cq4-envelope-v1";
    public const int Width = 32;
    public const int Height = 24;
    public const int Depth = 32;
    public const int ChannelCount = 2;
    public const int BytesPerChannel = 1;
    public const int VariantCountPerFamily = 3;
    public const int CumulusBaseLayer = 2;
    public const int MaximumEncodedDistance = 31;

    public static IReadOnlyList<PreviewSparseCloudTemplateAssetDescriptor> Assets { get; } =
        Array.AsReadOnly(
        [
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusHumilis, 0, 41011, "14e75eb90ced4724bba8b99c1b6793992a3bed1e29a04a6800fe80cca0150215"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusHumilis, 1, 41017, "7243c4aff5ccbc04841a8b2a1e188e079c21ca51332e6d81ae930c9905056ea2"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusHumilis, 2, 41023, "b339a7b8606db9bbe7337f8a4a39c9e20a105c1cdf6ef7b75753fafef1a28ac7"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusMediocris, 0, 42013, "49fd534dad6a022c86ca7b330ae96c545293246c6fb7c9f8192c7662baac11c8"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusMediocris, 1, 42019, "b1d28c3388bc58abcf82d82b6115fb5d17463911510dede04bb6a2ed1c39d158"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusMediocris, 2, 42043, "17eb08c96649fb00ed87432b41bccbe5bb034f7e812b05ff82eefc75b1e16cfa"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusCongestus, 0, 43003, "df86a07b694e6d6b0d59cf9c14446e9d6e1c0c0169b9ac05616a308f3c069423"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusCongestus, 1, 43019, "bb3a236194e2ec965b3cf2f6e2ec0043d348bbf9d7cbb738713819a284af87b6"),
            Descriptor(PreviewSparseCloudTemplateFamily.CumulusCongestus, 2, 43037, "e4f694832b20a62afb14f5fa919e647e2669d62640d1e44709b520e7a03788d2"),
            Descriptor(PreviewSparseCloudTemplateFamily.Stratus, 0, 44017, "e44e1b5e3c43cc9d31a22c405e3fd18885700bcf7e783500b713e0844ca2fb5a"),
            Descriptor(PreviewSparseCloudTemplateFamily.Stratus, 1, 44021, "a673246127dd1fff12f8e74548345ebc30086b8639646f536f2a57b94aee3f92"),
            Descriptor(PreviewSparseCloudTemplateFamily.Stratus, 2, 44027, "f9ae252468ea7e1187d105a5e2a63298116ae56e775c389b0a98848404198107"),
        ]);

    public static int ByteLength =>
        checked(Width * Height * Depth * ChannelCount * BytesPerChannel);

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
            reason = "descriptor-not-cq4-envelope-v1";
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

    public static string FamilySlug(PreviewSparseCloudTemplateFamily family) =>
        family switch
        {
            PreviewSparseCloudTemplateFamily.CumulusHumilis => "cumulus_humilis",
            PreviewSparseCloudTemplateFamily.CumulusMediocris => "cumulus_mediocris",
            PreviewSparseCloudTemplateFamily.CumulusCongestus => "cumulus_congestus",
            PreviewSparseCloudTemplateFamily.Stratus => "stratus",
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

    private static PreviewSparseCloudTemplateAssetDescriptor Descriptor(
        PreviewSparseCloudTemplateFamily family,
        int variant,
        int seed,
        string expectedSha256) =>
        new(
            family,
            variant,
            seed,
            $"cloud_envelope_{FamilySlug(family)}_{variant}_" +
            $"{Width}x{Height}x{Depth}_rg8_v{AssetVersion}.bin",
            expectedSha256);
}

public enum PreviewSparseCloudTemplateFamily
{
    CumulusHumilis = 0,
    CumulusMediocris = 1,
    CumulusCongestus = 2,
    Stratus = 3,
}

public sealed class PreviewSparseCloudTemplateAssetDescriptor
{
    public PreviewSparseCloudTemplateAssetDescriptor(
        PreviewSparseCloudTemplateFamily family,
        int variant,
        int seed,
        string fileName,
        string expectedSha256,
        int version = PreviewSparseCloudTemplateAssetContract.AssetVersion)
    {
        if (variant < 0 ||
            variant >= PreviewSparseCloudTemplateAssetContract.VariantCountPerFamily)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Family = family;
        Variant = variant;
        Seed = seed;
        FileName = fileName;
        ExpectedSha256 = expectedSha256;
        Version = version;
        Width = PreviewSparseCloudTemplateAssetContract.Width;
        Height = PreviewSparseCloudTemplateAssetContract.Height;
        Depth = PreviewSparseCloudTemplateAssetContract.Depth;
        ByteLength = PreviewSparseCloudTemplateAssetContract.ByteLength;
    }

    public PreviewSparseCloudTemplateFamily Family { get; }
    public int Variant { get; }
    public int Seed { get; }
    public string FileName { get; }
    public string ExpectedSha256 { get; }
    public int Version { get; }
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public int ByteLength { get; }
    public string Symbol =>
        $"{PreviewSparseCloudTemplateAssetContract.FamilySlug(Family)}-{Variant}-v{Version}";
    // Version-qualified so v1/v2 loader diagnostics for the same family/variant never collide.
    public string DimensionLabel => $"{Width}x{Height}x{Depth}";
}
