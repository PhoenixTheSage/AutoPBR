using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json.Nodes;

namespace AutoPBR.Preview;

/// <summary>
/// Low-level loader and keyframe sampler for bytecode-lifted animation IR JSON under
/// <c>Data/minecraft-native/animation/&lt;ver&gt;/</c>. Entity-specific entry points live on
/// <see cref="DefinitionAnimationPreviewSampling"/>.
/// </summary>
internal static class VanillaAnimationIrPreviewSampler
{
    private static readonly ConcurrentDictionary<string, JsonNode?> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetAnimationRoot(MinecraftNativeProfile? profile, string officialJvmName, out JsonObject root)
    {
        root = null!;
        foreach (var ver in NativeIrVersionLabels.ForProfile(profile))
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "minecraft-native",
                "animation",
                ver,
                $"{officialJvmName}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            var node = Cache.GetOrAdd(path, static p =>
            {
                try
                {
                    return JsonNode.Parse(File.ReadAllText(p));
                }
                catch
                {
                    return null;
                }
            });

            if (node is not JsonObject o)
            {
                continue;
            }

            root = o;
            return true;
        }

        return false;
    }

    private static bool TryGetDefinition(JsonObject animationRoot, string fieldName, out JsonObject definition)
    {
        definition = null!;
        if (animationRoot["definitions"] is not JsonArray defs)
        {
            return false;
        }

        foreach (var d in defs)
        {
            if (d is JsonObject o && string.Equals((string?)o["fieldName"], fieldName, StringComparison.Ordinal))
            {
                definition = o;
                return true;
            }
        }

        return false;
    }

    internal static bool TrySamplePositionLinear(JsonObject definition, string partName, float timeSeconds, out Vector3 v)
    {
        v = default;
        if (definition["channels"] is not JsonArray chans)
        {
            return false;
        }

        JsonArray? keyframes = null;
        foreach (var ch in chans)
        {
            if (ch is not JsonObject co)
            {
                continue;
            }

            if (!string.Equals((string?)co["partName"], partName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals((string?)co["target"], "POSITION", StringComparison.Ordinal))
            {
                continue;
            }

            if (co["keyframes"] is not JsonArray kf)
            {
                continue;
            }

            keyframes = kf;
            break;
        }

        if (keyframes is null || keyframes.Count == 0)
        {
            return false;
        }

        var span = ComputeLoopSeconds(definition, keyframes);
        return TrySampleVec3Animated(keyframes, timeSeconds, span, out v);
    }

    internal static bool TrySampleDegreesLinear(JsonObject definition, string partName, float timeSeconds, out Vector3 eulerDeg)
    {
        eulerDeg = default;
        if (definition["channels"] is not JsonArray chans)
        {
            return false;
        }

        JsonArray? keyframes = null;
        foreach (var ch in chans)
        {
            if (ch is not JsonObject co)
            {
                continue;
            }

            if (!string.Equals((string?)co["partName"], partName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals((string?)co["target"], "ROTATION", StringComparison.Ordinal))
            {
                continue;
            }

            if (co["keyframes"] is not JsonArray kf || kf.Count == 0)
            {
                continue;
            }

            keyframes = kf;
            break;
        }

        if (keyframes is null || keyframes.Count == 0)
        {
            return false;
        }

        var span = ComputeLoopSeconds(definition, keyframes);
        return TrySampleVec3Animated(keyframes, timeSeconds, span, out eulerDeg);
    }

    private static float ComputeLoopSeconds(JsonObject definition, JsonArray keyframes)
    {
        var maxT = 0f;
        foreach (var k in keyframes)
        {
            if (k is JsonObject ko && ko["timeSeconds"] is JsonValue tv)
            {
                maxT = Math.Max(maxT, (float)tv.GetValue<double>());
            }
        }

        if (definition.TryGetPropertyValue("lengthSeconds", out var lenNode) && lenNode is JsonValue lv)
        {
            maxT = Math.Max(maxT, (float)lv.GetValue<double>());
        }

        return maxT > 1e-4f ? maxT : 1f;
    }

    private static bool TrySampleVec3Animated(JsonArray keyframes, float timeSeconds, float span, out Vector3 v)
    {
        v = default;
        if (keyframes.Count == 0 || span <= 0f)
        {
            return false;
        }

        if (!TryReadKeyframe(keyframes[0]!, out var t0, out var v0))
        {
            return false;
        }

        var u = timeSeconds % span;
        if (u < 0f)
        {
            u += span;
        }

        if (keyframes.Count == 1 || u <= t0)
        {
            v = v0;
            return true;
        }

        for (var seg = 0; seg < keyframes.Count - 1; seg++)
        {
            if (!TryReadKeyframe(keyframes[seg]!, out var tA, out var vA) ||
                !TryReadKeyframe(keyframes[seg + 1]!, out var tB, out var vB))
            {
                return false;
            }

            if (u > tB && seg < keyframes.Count - 2)
            {
                continue;
            }

            var dt = tB - tA;
            if (dt <= 1e-6f)
            {
                v = vB;
                return true;
            }

            var w = Math.Clamp((u - tA) / dt, 0f, 1f);
            var mode = (string?)keyframes[seg]!.AsObject()["interpolation"] ?? "LINEAR";
            if (string.Equals(mode, "CATMULLROM", StringComparison.OrdinalIgnoreCase))
            {
                TryReadKeyframe(keyframes[Math.Max(0, seg - 1)]!, out _, out var vBefore);
                TryReadKeyframe(keyframes[Math.Min(keyframes.Count - 1, seg + 2)]!, out _, out var vAfter);
                var p0 = seg > 0 ? vBefore : vA;
                var p3 = seg + 2 < keyframes.Count ? vAfter : vB;
                v = new Vector3(
                    CatmullRom1D(p0.X, vA.X, vB.X, p3.X, w),
                    CatmullRom1D(p0.Y, vA.Y, vB.Y, p3.Y, w),
                    CatmullRom1D(p0.Z, vA.Z, vB.Z, p3.Z, w));
                return true;
            }

            if (!string.Equals(mode, "LINEAR", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "MIXED", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            v = Vector3.Lerp(vA, vB, w);
            return true;
        }

        if (TryReadKeyframe(keyframes[^1]!, out _, out v))
        {
            return true;
        }

        return false;
    }

    private static float CatmullRom1D(float p0, float p1, float p2, float p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static bool TryReadKeyframe(JsonNode? node, out float t, out Vector3 xyz)
    {
        t = 0f;
        xyz = default;
        if (node is not JsonObject o)
        {
            return false;
        }

        if (o["timeSeconds"] is not JsonValue tv)
        {
            return false;
        }

        t = (float)tv.GetValue<double>();
        if (o["x"] is not JsonValue xv || o["y"] is not JsonValue yv || o["z"] is not JsonValue zv)
        {
            return false;
        }

        xyz = new Vector3((float)xv.GetValue<double>(), (float)yv.GetValue<double>(), (float)zv.GetValue<double>());
        return true;
    }

    internal static bool TryGetDefinitionByClassField(
        string animationOfficialJvmName,
        string definitionField,
        out JsonObject definition)
    {
        definition = null!;
        return TryGetAnimationRoot(null, animationOfficialJvmName, out var root) &&
               TryGetDefinition(root, definitionField, out definition);
    }

    internal static bool TrySampleChannel(
        JsonObject channel,
        float timeSeconds,
        string target,
        out Vector3 value)
    {
        value = default;
        if (channel["keyframes"] is not JsonArray keyframes || keyframes.Count == 0)
        {
            return false;
        }

        var span = 1f;
        if (channel.TryGetPropertyValue("lengthSeconds", out var lenNode) && lenNode is JsonValue lv)
        {
            span = (float)lv.GetValue<double>();
        }

        return TrySampleVec3Animated(keyframes, timeSeconds, span, out value);
    }
}
