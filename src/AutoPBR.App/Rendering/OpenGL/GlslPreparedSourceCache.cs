using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using AutoPBR.App.Rendering.Abstractions;

using Avalonia.Platform;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>CPU cache for flattened + ES-adapted GLSL sources (safe off the GL thread).</summary>
internal static class GlslPreparedSourceCache
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    private static readonly (string File, ShaderType Type)[] PreviewShaderEntries =
    [
        ("genesis.vert", ShaderType.VertexShader),
        ("genesis.tcs", ShaderType.TessControlShader),
        ("genesis.tes", ShaderType.TessEvaluationShader),
        ("genesis.frag", ShaderType.FragmentShader),
        ("genesis_shadow.vert", ShaderType.VertexShader),
        ("genesis_shadow.frag", ShaderType.FragmentShader),
        ("atmo_sky.vert", ShaderType.VertexShader),
        ("atmo_sky.frag", ShaderType.FragmentShader),
        ("atmo_lut.vert", ShaderType.VertexShader),
        ("atmo_transmittance.frag", ShaderType.FragmentShader),
        ("atmo_skyview.frag", ShaderType.FragmentShader),
        ("genesis_godrays.vert", ShaderType.VertexShader),
        ("genesis_scene_present.frag", ShaderType.FragmentShader),
        ("genesis_godrays.frag", ShaderType.FragmentShader),
        ("genesis_godrays_shadow.frag", ShaderType.FragmentShader),
        ("genesis_godrays_upsample.frag", ShaderType.FragmentShader),
        ("genesis_godrays_composite.frag", ShaderType.FragmentShader),
        ("genesis_volume_inject.frag", ShaderType.FragmentShader),
        ("genesis_volume_integrate.frag", ShaderType.FragmentShader),
        ("genesis_volume_inject_lite.frag", ShaderType.FragmentShader),
        ("genesis_volume_integrate_lite.frag", ShaderType.FragmentShader),
        ("genesis_clouds.frag", ShaderType.FragmentShader),
        ("genesis_clouds_upsample.frag", ShaderType.FragmentShader),
        ("genesis_taa_resolve.frag", ShaderType.FragmentShader),
    ];

    private static readonly (string File, ShaderType Type, IReadOnlyDictionary<string, int> Defines)[] PrewarmVariantEntries =
    [
        ("genesis.frag", ShaderType.FragmentShader, GenesisShaderFeatureMaskBuilder.ToDefines(GenesisShaderFeatureMask.None)),
        ("genesis.frag", ShaderType.FragmentShader,
            GenesisShaderFeatureMaskBuilder.ToDefines(GenesisShaderFeatureMask.Shadow | GenesisShaderFeatureMask.Ibl)),
        ("genesis.frag", ShaderType.FragmentShader, GenesisShaderFeatureMaskBuilder.ToDefines(GenesisShaderFeatureMask.All)),
        ("genesis_godrays.frag", ShaderType.FragmentShader, new Dictionary<string, int> { ["GENESIS_GODRAY_SPARSE_MARCH"] = 1 }),
        ("genesis_godrays_shadow.frag", ShaderType.FragmentShader, new Dictionary<string, int> { ["GENESIS_GODRAY_SPARSE_MARCH"] = 1 }),
        ("genesis_volume_integrate.frag", ShaderType.FragmentShader, new Dictionary<string, int> { ["GENESIS_VOLUME_TEMPORAL"] = 1 }),
        ("genesis_clouds.frag", ShaderType.FragmentShader,
            new Dictionary<string, int> { ["GENESIS_CLOUD_QUALITY"] = PreviewVolumetricQuality.High }),
    ];

    public static string GetOrPrepare(string entryFile, ShaderType shaderType, bool useOpenGlEs) =>
        GetOrPrepare(entryFile, shaderType, useOpenGlEs, defines: null);

    public static string GetOrPrepare(
        string entryFile,
        ShaderType shaderType,
        bool useOpenGlEs,
        IReadOnlyDictionary<string, int>? defines)
    {
        var raw = GlslIncludeResolver.Resolve(entryFile, LoadShaderSource);
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        var defineKey = FormatDefinesKey(defines);
        var key = $"{(useOpenGlEs ? 'e' : 'd')}:{shaderType}:{entryFile}:{sourceHash}:{defineKey}";
        return Cache.GetOrAdd(key, _ =>
        {
            var prepared = GlslSourceAdapter.Adapt(raw, shaderType, useOpenGlEs);
            return PrependDefines(prepared, defines);
        });
    }

    private static string FormatDefinesKey(IReadOnlyDictionary<string, int>? defines)
    {
        if (defines is null || defines.Count == 0)
        {
            return "-";
        }

        var names = defines.Keys.OrderBy(static n => n, StringComparer.Ordinal).ToArray();
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            sb.Append(name).Append('=').Append(defines[name]).Append(';');
        }

        return sb.ToString();
    }

    private static string PrependDefines(string source, IReadOnlyDictionary<string, int>? defines)
    {
        if (defines is null || defines.Count == 0)
        {
            return source;
        }

        var defineBlock = new StringBuilder();
        foreach (var (name, value) in defines.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            defineBlock.Append("#define ").Append(name).Append(' ').Append(value).Append('\n');
        }

        var insertAt = FindDefineInsertionIndex(source);
        return source.Insert(insertAt, defineBlock.ToString());
    }

    /// <summary>
    /// GLSL requires <c>#version</c> before any other tokens (except whitespace/comments).
    /// Defines must be inserted after version, extensions, and ES precision qualifiers.
    /// </summary>
    private static int FindDefineInsertionIndex(string source)
    {
        var i = 0;
        var len = source.Length;
        while (i < len)
        {
            while (i < len && (source[i] == '\r' || source[i] == '\n' || char.IsWhiteSpace(source[i])))
            {
                i++;
            }

            if (i >= len)
            {
                break;
            }

            var lineStart = i;
            while (i < len && source[i] != '\n')
            {
                i++;
            }

            var lineEnd = i;
            if (i < len)
            {
                i++;
            }

            var trimmed = source.AsSpan(lineStart, lineEnd - lineStart).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("#version", StringComparison.Ordinal) ||
                trimmed.StartsWith("#extension", StringComparison.Ordinal) ||
                trimmed.StartsWith("precision ", StringComparison.Ordinal))
            {
                continue;
            }

            return lineStart;
        }

        return len;
    }

    public static string ComputePreparedSourceFingerprint(string entryFile, ShaderType shaderType, bool useOpenGlEs)
    {
        var prepared = GetOrPrepare(entryFile, shaderType, useOpenGlEs);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prepared)))[..12];
    }

    public static string GetShaderSourceOrigin(string entryFile)
    {
        var sourcePath = TryFindSourceShaderPath(entryFile);
        return sourcePath is null
            ? $"avares://AutoPBR.App/Rendering/Shaders/{entryFile}"
            : sourcePath;
    }

    public static int PrewarmWorkItemCount =>
        PreviewShaderEntries.Length * 2 + PrewarmVariantEntries.Length * 2;

    public static void Clear() => Cache.Clear();

    public static void PrewarmAllPreviewShadersParallel(Action? onItemComplete = null)
    {
        var work = new (string File, ShaderType Type, bool Es, IReadOnlyDictionary<string, int>? Defines)[PrewarmWorkItemCount];
        var i = 0;
        foreach (var (file, type) in PreviewShaderEntries)
        {
            work[i++] = (file, type, false, null);
            work[i++] = (file, type, true, null);
        }

        foreach (var (file, type, defines) in PrewarmVariantEntries)
        {
            work[i++] = (file, type, false, defines);
            work[i++] = (file, type, true, defines);
        }

        Parallel.ForEach(work, item =>
        {
            _ = GetOrPrepare(item.File, item.Type, item.Es, item.Defines);
            onItemComplete?.Invoke();
        });
    }

    public static string ComputeProgramKey(string vertexFile, string fragmentFile, bool useOpenGlEs, string cacheIdentity)
    {
        return ComputeProgramKey(
            useOpenGlEs,
            cacheIdentity,
            [(vertexFile, ShaderType.VertexShader), (fragmentFile, ShaderType.FragmentShader)]);
    }

    public static string ComputeProgramKey(
        bool useOpenGlEs,
        string cacheIdentity,
        IReadOnlyList<(string File, ShaderType Type)> stages,
        IReadOnlyDictionary<string, int>? defines = null)
    {
        // Hash adapted sources so include edits (e.g. common/sky_dome.glsl) invalidate disk program binaries.
        var payload = new StringBuilder(cacheIdentity);
        payload.Append('\0').Append(FormatDefinesKey(defines));
        foreach (var (file, type) in stages)
        {
            payload.Append('\0').Append(type).Append(':').Append(file).Append('\0');
            payload.Append(GetOrPrepare(file, type, useOpenGlEs, defines));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static string LoadShaderSource(string fileName)
    {
        var sourcePath = TryFindSourceShaderPath(fileName);
        if (sourcePath is not null)
        {
            return File.ReadAllText(sourcePath);
        }

        var uri = new Uri($"avares://AutoPBR.App/Rendering/Shaders/{fileName}");
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? TryFindSourceShaderPath(string fileName)
    {
        var relative = fileName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var path = Path.Combine(dir.FullName, "src", "AutoPBR.App", "Rendering", "Shaders", relative);
                if (File.Exists(path))
                {
                    return path;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
