using System.Text.Json;

namespace AutoPBR.Core.Models;

public sealed record SpecularRule(byte[] ColorRgb, byte[] SpecularRgba, double Percent)
{
    public byte ColorR => ColorRgb[0];
    public byte ColorG => ColorRgb[1];
    public byte ColorB => ColorRgb[2];

    public byte SpecR => SpecularRgba[0];
    public byte SpecG => SpecularRgba[1];
    public byte SpecB => SpecularRgba[2];
    public byte SpecA => SpecularRgba.Length > 3 ? SpecularRgba[3] : (byte)255;
}

public sealed class SpecularData
{
    public required IReadOnlyDictionary<string, IReadOnlyList<SpecularRule>> ByTextureName { get; init; }

    public static SpecularData LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static SpecularData Load(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected a JSON object at root.");

        var dict = new Dictionary<string, IReadOnlyList<SpecularRule>>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;

            var rules = new List<SpecularRule>();
            foreach (var ruleEl in prop.Value.EnumerateArray())
            {
                if (ruleEl.ValueKind != JsonValueKind.Object)
                    continue;

                var colorRgb = ReadByteArray(ruleEl, "color", minLen: 3, maxLen: 3);
                var specRgba = ReadByteArray(ruleEl, "specular", minLen: 3, maxLen: 4);
                if (specRgba.Length == 3)
                    specRgba = [specRgba[0], specRgba[1], specRgba[2], 255];

                var percent = ruleEl.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble()
                    : 0.0;

                rules.Add(new SpecularRule(colorRgb, specRgba, percent));
            }

            dict[prop.Name] = rules;
        }

        return new SpecularData { ByTextureName = dict };
    }

    private static byte[] ReadByteArray(JsonElement obj, string propName, int minLen, int maxLen)
    {
        if (!obj.TryGetProperty(propName, out var el) || el.ValueKind != JsonValueKind.Array)
            return new byte[minLen];

        var tmp = new List<byte>(maxLen);
        foreach (var n in el.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number)
                continue;
            tmp.Add((byte)Math.Clamp(n.GetInt32(), 0, 255));
            if (tmp.Count >= maxLen)
                break;
        }

        while (tmp.Count < minLen)
            tmp.Add(0);

        return tmp.ToArray();
    }
}

