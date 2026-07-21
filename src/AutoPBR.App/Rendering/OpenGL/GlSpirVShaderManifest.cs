namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlSpirVShaderManifest
{
    private readonly HashSet<string> _assets;

    public GlSpirVShaderManifest(IEnumerable<string> assets)
    {
        _assets = new HashSet<string>(
            assets.Where(static asset => !string.IsNullOrWhiteSpace(asset))
                .Select(static asset => asset.Replace('\\', '/').Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _assets.Count;

    public bool Contains(string assetName) =>
        _assets.Contains(assetName.Replace('\\', '/').Trim());

    public static GlSpirVShaderManifest Empty { get; } = new([]);

    public static GlSpirVShaderManifest Bundled { get; } = new(
    [
        "spv/genesis_indirect_compact.comp.spv",
    ]);
}
