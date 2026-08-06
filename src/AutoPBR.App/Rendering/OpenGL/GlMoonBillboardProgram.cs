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
    // Shared strength slider: SDR must approximate HDR midtone punch from presentEncodeScRgb
    // (ACES × paperWhite/80 ≈ 2.1–2.5× at default 200 nits). A 1.22× lift is invisible;
    // use ~2.1× with a soft shoulder so maria survive when the result exceeds display white.
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

// Matches default paper-white midtone scale (200/80) minus a little so HDR peaks can still
// read brighter than SDR white at the same slider value.
const float MOON_SDR_STRENGTH_GAIN = 2.15;

vec3 moonHdrPreserveContrast(vec3 rgb)
{
    // Expand maria/highland separation for ACES; preserve luminance so HDR does not
    // gain free brightness over SDR at the same strength.
    vec3 pivot = vec3(0.46);
    float lum0 = dot(rgb, vec3(0.2126, 0.7152, 0.0722));
    vec3 c = max((rgb - pivot) * 1.55 + pivot, vec3(0.0));
    float lum1 = max(dot(c, vec3(0.2126, 0.7152, 0.0722)), 1e-5);
    return c * (lum0 / lum1);
}

vec3 moonApplyStrength(vec3 rgb, float strength, float discCut)
{
    vec3 outRgb = rgb * max(strength, 0.35) * discCut;
    if (uHdrPresent <= 0)
    {
        outRgb *= MOON_SDR_STRENGTH_GAIN;
        // Soft shoulder above display white — keep maria when gain pushes past 1.0.
        float lum = dot(outRgb, vec3(0.2126, 0.7152, 0.0722));
        float shoulder = 1.0 / (1.0 + max(lum - 1.0, 0.0) * 0.85);
        outRgb *= shoulder;
    }

    return max(outRgb, vec3(0.0));
}

vec3 moonSceneReferredRadiance(vec3 albedo, float strength, float discCut)
{
    vec3 rgb = albedo;
    if (uHdrPresent > 0)
    {
        rgb = moonHdrPreserveContrast(rgb);
    }

    return moonApplyStrength(rgb, strength, discCut);
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
        vec3 haloCol = vec3(0.58, 0.68, 1.0) * halo * 0.42;
        haloCol = moonApplyStrength(haloCol, strength, 1.0);
        float haloAlpha = clamp(halo * strength * 0.16 * (uHdrPresent > 0 ? 1.0 : MOON_SDR_STRENGTH_GAIN), 0.0, 0.22);
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
