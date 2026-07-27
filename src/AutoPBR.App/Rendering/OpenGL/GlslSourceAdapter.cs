using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Adapts desktop GLSL 3.30 sources for OpenGL ES 3.0 (e.g. Avalonia/ANGLE on Windows).</summary>
internal static class GlslSourceAdapter
{
    private const string DesktopVersion = "#version 330 core";

    public static string Adapt(string source, ShaderType type, bool useOpenGlEs)
    {
        // NVIDIA desktop GLSL and ANGLE both report unexpected EOF / end-of-shader syntax
        // errors when comments contain en/em dashes or other non-ASCII bytes.
        source = StripNonAscii(source);

        if (!useOpenGlEs)
        {
            return source;
        }

        if (!TryStripDesktopVersionHeader(source, out var body))
        {
            return source;
        }

        var prec = type == ShaderType.FragmentShader
            ? "precision highp float;\nprecision highp int;\n"
              + "precision highp sampler2D;\n"
              + "precision highp sampler2DArray;\n"
              + "precision highp sampler2DShadow;\n"
              + "precision highp sampler3D;\n"
            // Vertex skinning uses int bone indices + UBO scalars; default int precision on GLES can be too low for ANGLE.
            : "precision highp float;\nprecision highp int;\n";

        return "#version 300 es\n" + prec + "#define GENESIS_GLES 1\n" + body;
    }

    // GLSL lexers (NVIDIA desktop + ANGLE ES) can choke on non-ASCII bytes in comments
    // (e.g. en/em dashes), reporting a bogus "syntax error" / unexpected EOF at end-of-shader.
    // Replace every non-ASCII char with '-' so 1:1 length (and line/column) is preserved.
    internal static string StripNonAscii(string source)
    {
        var hasNonAscii = false;
        foreach (var c in source)
        {
            if (c > '\x7F')
            {
                hasNonAscii = true;
                break;
            }
        }

        if (!hasNonAscii)
        {
            return source;
        }

        var chars = source.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] > '\x7F')
            {
                chars[i] = '-';
            }
        }

        return new string(chars);
    }

    private static bool TryStripDesktopVersionHeader(string source, out string remainder)
    {
        var trimmed = source.TrimStart();
        if (!trimmed.StartsWith(DesktopVersion, StringComparison.Ordinal))
        {
            remainder = source;
            return false;
        }

        var after = trimmed[DesktopVersion.Length..].TrimStart('\r', '\n');
        remainder = after;
        return true;
    }
}
