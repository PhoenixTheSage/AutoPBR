using System.Text;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Textured moon disc billboard opposite the sun light direction.</summary>
internal sealed class GlMoonBillboardProgram : IDisposable
{
    private const string Vert330 = """
#version 330 core
layout(location = 0) in vec2 aCorner;
uniform mat4 uViewProj;
uniform vec3 uMoonCenter;
uniform vec3 uMoonRight;
uniform vec3 uMoonUp;
uniform float uRadius;
out vec2 vTexCoord;
out vec3 vWorldPos;
void main()
{
    vTexCoord = aCorner * 0.5 + 0.5;
    vec3 worldPos = uMoonCenter + uMoonRight * (aCorner.x * uRadius) + uMoonUp * (aCorner.y * uRadius);
    vWorldPos = worldPos;
    gl_Position = uViewProj * vec4(worldPos, 1.0);
}
""";

    // Embedded fragment: keep GLES/ANGLE-safe (no return in main; single FragColor write).
    // HDR: same strength→radiance mapping as SDR (slider parity). Contrast expand only —
    // do not undo paper-white (that made HDR ~paperScale darker than SDR at the same setting).
    private const string Frag330 = """
#version 330 core
in vec2 vTexCoord;
in vec3 vWorldPos;
uniform sampler2D uMoonAlbedo;
uniform float uDiscStrength;
uniform vec3 uCameraPos;
uniform vec3 uMoonRight;
uniform vec3 uMoonUp;
uniform vec3 uMoonFacing;
uniform float uMoonCosDiscEdge;
uniform float uGlowStrength;
uniform float uTextureSharpness;
uniform int uHdrPresent;
out vec4 FragColor;

vec3 moonHdrPreserveContrast(vec3 rgb)
{
    // Expand maria/highland separation so ACES + paper-white present does not flatten
    // the disc; pivot near lunar mid-gray so mean brightness stays slider-matched to SDR.
    vec3 pivot = vec3(0.46);
    return max((rgb - pivot) * 1.55 + pivot, vec3(0.0));
}

vec3 moonSceneReferredRadiance(vec3 albedo, float strength, float discCut)
{
    vec3 rgb = albedo;
    if (uHdrPresent > 0)
    {
        rgb = moonHdrPreserveContrast(rgb);
    }

    return max(rgb * max(strength, 0.35) * discCut, vec3(0.0));
}

void main()
{
    vec2 centered = vTexCoord * 2.0 - 1.0;
    float d = length(centered);
    if (d > 1.22)
    {
        discard;
    }

    vec3 viewDir = normalize(vWorldPos - uCameraPos);
    float thetaDisc = max(acos(clamp(uMoonCosDiscEdge, -1.0, 1.0)), 1e-3);
    float pixelElev = asin(clamp(viewDir.y, -1.0, 1.0)) / thetaDisc;
    float discCut = smoothstep(-0.22, 0.1, pixelElev);
    float glowCut = discCut * discCut;
    float strength = max(uDiscStrength, 0.35);

    vec3 outRgb;
    float outAlpha;
    if (d > 1.0)
    {
        float outerCut = 1.0 - smoothstep(1.03, 1.20, d);
        float halo = exp(-pow((d - 1.0) * 7.0, 1.45)) * outerCut * glowCut * max(uGlowStrength, 0.0);
        vec3 haloCol = vec3(0.58, 0.68, 1.0) * halo * strength * 0.42;
        float haloAlpha = clamp(halo * strength * 0.16, 0.0, 0.18);
        if (haloAlpha < 0.004)
        {
            discard;
        }

        outRgb = haloCol;
        outAlpha = haloAlpha;
    }
    else
    {
        vec4 texel = texture(uMoonAlbedo, vTexCoord);
        vec2 texelStep = vec2(1.0 / 1024.0, 1.0 / 1024.0);
        vec3 blur = (
            texture(uMoonAlbedo, vTexCoord + vec2(texelStep.x, 0.0)).rgb +
            texture(uMoonAlbedo, vTexCoord - vec2(texelStep.x, 0.0)).rgb +
            texture(uMoonAlbedo, vTexCoord + vec2(0.0, texelStep.y)).rgb +
            texture(uMoonAlbedo, vTexCoord - vec2(0.0, texelStep.y)).rgb) * 0.25;
        float sharpen = clamp(uTextureSharpness, 0.0, 4.0);
        // SDR keeps hard 0..1 clamps; HDR leaves headroom for contrast expand.
        if (uHdrPresent > 0)
        {
            texel.rgb = (texel.rgb - vec3(0.5)) * 1.28 + vec3(0.5);
            texel.rgb = texel.rgb + (texel.rgb - blur) * sharpen * 0.28;
            texel.rgb = max(texel.rgb, vec3(0.0));
        }
        else
        {
            texel.rgb = clamp((texel.rgb - vec3(0.5)) * 1.28 + vec3(0.5), 0.0, 1.0);
            texel.rgb = clamp(texel.rgb + (texel.rgb - blur) * sharpen * 0.28, 0.0, 1.0);
        }

        float z = sqrt(max(1.0 - d * d, 0.0));
        vec3 sphereNormal = normalize(uMoonRight * centered.x + uMoonUp * centered.y - uMoonFacing * z);
        float fullMoonLight = clamp(dot(sphereNormal, -uMoonFacing), 0.0, 1.0);
        float surface = 0.42 + 0.58 * pow(fullMoonLight, 0.58);
        float grazing = pow(1.0 - max(fullMoonLight, 0.0), 2.2);
        float limb = 1.0 - smoothstep(0.62, 1.0, d) * 0.34;
        vec3 coolRim = vec3(0.64, 0.72, 0.95) * grazing * 0.08;

        vec3 albedo = texel.rgb * surface * limb + coolRim;
        albedo = mix(albedo, texel.rgb, 0.46);
        albedo = moonSceneReferredRadiance(albedo, strength, discCut);
        float alpha = texel.a * (1.0 - smoothstep(0.992, 1.0, d)) * discCut;
        if (alpha < 0.01)
        {
            discard;
        }

        outRgb = albedo;
        outAlpha = alpha;
    }

    FragColor = vec4(outRgb, outAlpha);
}
""";

    private readonly GL _gl;
    private uint _program;
    private bool _disposed;

    public GlMoonBillboardProgram(GL gl, bool useOpenGlEs, out string? error)
    {
        _gl = gl;
        error = null;
        _program = 0;
        var vSrc = GlslSourceAdapter.Adapt(Vert330, ShaderType.VertexShader, useOpenGlEs);
        var fSrc = GlslSourceAdapter.Adapt(Frag330, ShaderType.FragmentShader, useOpenGlEs);
        var vs = Compile(ShaderType.VertexShader, vSrc, ref error);
        if (vs == 0)
        {
            return;
        }

        var fs = Compile(ShaderType.FragmentShader, fSrc, ref error);
        if (fs == 0)
        {
            _gl.DeleteShader(vs);
            return;
        }

        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, vs);
        _gl.AttachShader(_program, fs);
        _gl.LinkProgram(_program);
        _gl.GetProgram(_program, GLEnum.LinkStatus, out var ok);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        if (ok == 0)
        {
            var linkLog = _gl.GetProgramInfoLog(_program);
            error = string.IsNullOrEmpty(error) ? linkLog : error + "\n" + linkLog;
            _gl.DeleteProgram(_program);
            _program = 0;
        }
    }

    public bool IsValid => _program != 0;

    public void Use() => _gl.UseProgram(_program);

    public int GetUniformLocation(string name) => _gl.GetUniformLocation(_program, name);

    private uint Compile(ShaderType type, string source, ref string? error)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, GLEnum.CompileStatus, out var ok);
        if (ok == 0)
        {
            var info = _gl.GetShaderInfoLog(s);
            var sb = new StringBuilder();
            sb.Append(type).Append(" compile failed: ").AppendLine(info);
            error = string.IsNullOrEmpty(error) ? sb.ToString() : error + "\n" + sb;
            _gl.DeleteShader(s);
            return 0;
        }

        return s;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_program != 0)
        {
            _gl.DeleteProgram(_program);
            _program = 0;
        }
    }
}
