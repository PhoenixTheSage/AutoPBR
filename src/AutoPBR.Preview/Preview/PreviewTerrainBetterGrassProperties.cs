namespace AutoPBR.Preview;

/// <summary>
/// Subset of OptiFine <c>assets/minecraft/optifine/bettergrass.properties</c> used by stage terrain.
/// </summary>
public sealed class PreviewTerrainBetterGrassProperties
{
    public const string ArchivePath = "assets/minecraft/optifine/bettergrass.properties";

    public bool GrassEnabled { get; init; } = true;

    public bool Multilayer { get; init; }

    /// <summary>Model texture path for BetterGrass top/side replacement (default <c>block/grass_block_top</c>).</summary>
    public string TextureGrass { get; init; } = "block/grass_block_top";

    /// <summary>Model texture path for multilayer base side (default <c>block/grass_block_side</c>).</summary>
    public string TextureGrassSide { get; init; } = "block/grass_block_side";

    public static PreviewTerrainBetterGrassProperties Default { get; } = new();

    public static PreviewTerrainBetterGrassProperties Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        var grass = true;
        var multilayer = false;
        var textureGrass = "block/grass_block_top";
        var textureGrassSide = "block/grass_block_side";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '!')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("grass", StringComparison.OrdinalIgnoreCase))
            {
                grass = ParseBool(value, defaultValue: true);
            }
            else if (key.Equals("grass.multilayer", StringComparison.OrdinalIgnoreCase))
            {
                multilayer = ParseBool(value, defaultValue: false);
            }
            else if (key.Equals("texture.grass", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(value))
            {
                textureGrass = value.Replace('\\', '/').Trim().TrimStart('/');
            }
            else if (key.Equals("texture.grass_side", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(value))
            {
                textureGrassSide = value.Replace('\\', '/').Trim().TrimStart('/');
            }
        }

        return new PreviewTerrainBetterGrassProperties
        {
            GrassEnabled = grass,
            Multilayer = multilayer,
            TextureGrass = textureGrass,
            TextureGrassSide = textureGrassSide,
        };
    }

    public static string ModelTextureToBlockZipPath(string modelTexturePath, string defaultNamespace = "minecraft")
    {
        var p = modelTexturePath.Replace('\\', '/').Trim().TrimStart('/');
        if (p.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            return p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? p : p + ".png";
        }

        if (p.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
        {
            var withNs = $"assets/{defaultNamespace}/{p}";
            return withNs.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? withNs : withNs + ".png";
        }

        // block/grass_block_top or minecraft:block/grass_block_top
        var ns = defaultNamespace;
        var rel = p;
        var colon = p.IndexOf(':');
        if (colon >= 0)
        {
            ns = p[..colon];
            rel = p[(colon + 1)..];
        }

        if (!rel.StartsWith("block/", StringComparison.OrdinalIgnoreCase) &&
            !rel.StartsWith("item/", StringComparison.OrdinalIgnoreCase) &&
            !rel.StartsWith("entity/", StringComparison.OrdinalIgnoreCase))
        {
            rel = "block/" + rel;
        }

        var zip = $"assets/{ns}/textures/{rel}";
        return zip.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? zip : zip + ".png";
    }

    private static bool ParseBool(string value, bool defaultValue)
    {
        if (bool.TryParse(value, out var b))
        {
            return b;
        }

        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }
}
