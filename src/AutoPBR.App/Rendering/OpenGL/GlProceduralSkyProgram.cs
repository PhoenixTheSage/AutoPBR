using System.Text;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Self-contained procedural sky (day/night, stars, horizon) with no LUT texture dependency.</summary>
internal sealed class GlProceduralSkyProgram : IDisposable
{
    private const string Vert330 = """
#version 330 core
layout(location = 0) in vec2 aPos;
out vec2 vUv;
void main()
{
    vUv = aPos * 0.5 + 0.5;
    gl_Position = vec4(aPos, 0.0, 1.0);
}
""";

    private const string Frag330 = """
#version 330 core
in vec2 vUv;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform vec3 uLightDir;
uniform float uSunIntensity;
uniform float uSkyExposure;
uniform float uRenderTime;
uniform float uTurbidity;
uniform float uHorizonFalloff;
uniform float uHorizonFogStrength;
uniform float uSunDiscStrength;
uniform float uSunDiscBrightness;
uniform float uSunCosDiscEdge;
uniform float uMoonCosDiscEdge;
uniform float uViewportAspect;
uniform float uSunDiscRadiusUv;
uniform float uSunElevation;
uniform float uGroundWorldY;
uniform int uHdrPresent;
out vec4 FragColor;

const float SKY_PI = 3.14159265358979323846;

vec3 worldRayDir(vec2 uv, mat4 invViewProj, vec3 cameraPos)
{
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, uv.y * 2.0 - 1.0);
    vec4 worldH = invViewProj * vec4(ndc, 1.0, 1.0);
    vec3 farPt = worldH.xyz / max(worldH.w, 1e-6);
    vec3 rd = farPt - cameraPos;
    float len2 = dot(rd, rd);
    if (len2 < 1e-12)
    {
        return vec3(0.0, 1.0, 0.0);
    }
    return rd * inversesqrt(len2);
}

float dayFactor(vec3 lightPropagationDir, float sunIntensity)
{
    vec3 towardLight = normalize(-lightPropagationDir);
    float sunElev = towardLight.y;
    float dayFromSun = smoothstep(-0.04, 0.22, sunElev);
    float dayFromIntensity = smoothstep(0.08, 2.0, sunIntensity);
    return clamp(dayFromSun * dayFromIntensity, 0.0, 1.0);
}

float hash31(vec3 p)
{
    p = fract(p * 0.3183099 + vec3(0.17, 0.31, 0.47));
    p += dot(p, p.yzx + vec3(33.33));
    return fract((p.x + p.y) * p.z);
}

float starTwinkle(float timeSec, float seed)
{
    float phase = seed * 6.2831853;
    float rate = mix(1.7, 5.5, fract(seed * 17.13));
    float scint =
        0.50 * sin(timeSec * rate + phase) +
        0.32 * sin(timeSec * rate * 1.73 + phase * 1.9) +
        0.18 * sin(timeSec * rate * 3.11 + phase * 2.7);
    float twinkle = 1.0 + 0.10 * scint;
    float sparkle = pow(max(sin(timeSec * rate * 0.41 + phase * 3.7), 0.0), 28.0);
    return twinkle + sparkle * mix(0.08, 0.22, seed);
}

vec3 stars(vec3 viewDir, float timeSec)
{
    if (viewDir.y <= 0.01)
    {
        return vec3(0.0);
    }

    vec3 p = normalize(viewDir) * 140.0;
    float grid = 6.5;
    vec3 cell = floor(p * grid);
    vec3 local = fract(p * grid) - 0.5;
    float r = length(local);
    float core = 1.0 - smoothstep(0.18, 0.52, r);
    float spike = exp(-r * r * 14.0);
    float shape = max(core, spike);

    float h = hash31(cell);
    float primary = 0.0;
    if (h > 0.9935)
    {
        float mag = (h - 0.9935) / 0.0065;
        float base = mix(0.75, 1.35, pow(clamp(mag, 0.0, 1.0), 0.65));
        primary = base * starTwinkle(timeSec, h) * shape;
    }

    float h2 = hash31(cell + vec3(17.0, 3.0, 11.0));
    float secondary = 0.0;
    if (h2 > 0.9985)
    {
        float mag2 = (h2 - 0.9985) / 0.0015;
        float base2 = mix(0.45, 0.95, pow(clamp(mag2, 0.0, 1.0), 0.65));
        secondary = base2 * starTwinkle(timeSec, h2) * shape;
    }

    float star = primary + secondary;
    vec3 tint = mix(vec3(0.82, 0.90, 1.0), vec3(1.0, 0.92, 0.82), fract(h * 7.91));
    return tint * star;
}

vec3 nightZenith(vec3 viewDir)
{
    float t = clamp(viewDir.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.01, 0.012, 0.02), vec3(0.02, 0.035, 0.07), t);
}

float rayleighPhase(float cosTheta)
{
    return (3.0 / (16.0 * SKY_PI)) * (1.0 + cosTheta * cosTheta);
}

float miePhase(float cosTheta)
{
    const float g = 0.76;
    float gg = g * g;
    float denom = pow(max(1.0 + gg - 2.0 * g * cosTheta, 1e-3), 1.5);
    return (1.0 - gg) / (4.0 * SKY_PI * denom);
}

float horizonAltitudeFade(float camY, float groundY)
{
    float alt = max(camY - groundY, 0.0);
    return 1.0 - smoothstep(8.0, 56.0, alt);
}

vec3 proceduralSky(vec3 viewDir, vec3 lightPropagationDir, float sunIntensity, float turbidity, float horizonFalloff,
    float horizonBandScale)
{
    float bandScale = clamp(horizonBandScale, 0.0, 1.0);
    float mu = clamp(viewDir.y, -1.0, 1.0);
    vec3 towardSun = normalize(-lightPropagationDir);
    float cosSun = dot(viewDir, towardSun);
    float sunElev = max(towardSun.y, 0.0);
    float illum = 0.8 + 0.2 * smoothstep(1.0, 12.0, max(sunIntensity, 0.0));
    vec3 zenithBlue = vec3(0.20, 0.55, 0.85);
    vec3 horizonBlue = vec3(0.58, 0.82, 0.97);
    float gradT = pow(1.0 - max(mu, 0.0), 2.4);
    vec3 sky = mix(zenithBlue, horizonBlue, gradT * mix(0.7, 1.0, bandScale));
    float bandExp = mix(9.0, 3.5, clamp(horizonFalloff, 0.0, 1.0));
    float horizonBand = pow(1.0 - max(mu, 0.0), bandExp);
    float turbidityT = clamp((turbidity - 1.0) / 9.0, 0.0, 1.0);
    vec3 hazeCol = mix(vec3(0.80, 0.90, 1.0), vec3(0.92, 0.88, 0.82), turbidityT);
    sky = mix(sky, hazeCol, horizonBand * mix(0.25, 0.55, turbidityT) * bandScale);
    float lowSun = 1.0 - smoothstep(0.04, 0.42, sunElev);
    float sunFacing = clamp(cosSun * 0.5 + 0.5, 0.0, 1.0);
    float sunBias = pow(sunFacing, 3.0);
    vec3 warmCol = vec3(1.0, 0.46, 0.18);
    sky = mix(sky, warmCol, horizonBand * lowSun * sunBias * 0.85 * bandScale);
    vec3 mieTint = mix(vec3(1.0, 0.95, 0.85), warmCol, lowSun);
    float mieAmt = miePhase(cosSun) * mix(0.05, 0.4, turbidityT);
    sky += mieTint * mieAmt * 0.4;
    sky = max(sky * illum, vec3(0.0));
    float dayAmt = dayFactor(lightPropagationDir, sunIntensity);
    sky = mix(nightZenith(viewDir), sky, dayAmt);
    return max(sky, vec3(0.0));
}

vec3 horizonGlow(vec3 viewDir, float dayAmt, float horizonBandScale)
{
    float band = exp(-abs(viewDir.y) * 9.0);
    vec3 sunTint = vec3(1.0, 0.93, 0.74);
    vec3 nightGlow = vec3(0.04, 0.05, 0.08);
    vec3 dayGlow = sunTint * 0.28 + vec3(0.42, 0.66, 0.86);
    return mix(nightGlow, dayGlow, dayAmt) * band * 0.42 * clamp(horizonBandScale, 0.0, 1.0);
}

vec3 belowHorizonFog(vec3 viewDir, float strength, float horizonBandScale)
{
    if (viewDir.y >= 0.0 || strength <= 0.0)
    {
        return vec3(0.0);
    }

    float depth = smoothstep(0.0, -0.55, viewDir.y);
    return vec3(0.06, 0.07, 0.09) * depth * strength * clamp(horizonBandScale, 0.0, 1.0);
}

vec3 seamFogDir(vec3 viewDir)
{
    vec3 d = normalize(viewDir);
    float y = clamp(mix(d.y, 0.12, 0.35), 0.02, 0.55);
    return normalize(vec3(d.x, y, d.z));
}

float aboveHorizonHazeWeight(vec3 viewDir, float strength, float horizonBandScale)
{
    if (strength <= 0.0)
    {
        return 0.0;
    }

    float elev = max(normalize(viewDir).y, 0.0);
    float band = exp(-elev * 7.5);
    return band * clamp(strength, 0.0, 1.0) * 0.72 * clamp(horizonBandScale, 0.0, 1.0);
}

vec3 sunDiscAureole(vec3 viewDir, vec3 lightPropagationDir, float cosDiscEdge, float bloomRadiusUv, float bloomStrength, float discBrightness, float turbidity)
{
    if (bloomStrength <= 0.0 && discBrightness <= 0.0)
    {
        return vec3(0.0);
    }

    vec3 towardSun = normalize(-lightPropagationDir);
    vec3 vd = normalize(viewDir);
    float cosAngle = clamp(dot(vd, towardSun), -1.0, 1.0);
    float thetaDisc = max(acos(clamp(cosDiscEdge, -1.0, 1.0)), 1e-3);
    float r = acos(cosAngle) / thetaDisc;
    float sunElev = max(towardSun.y, 0.0);
    float lowSun = 1.0 - smoothstep(0.04, 0.42, sunElev);
    float turbidityT = clamp((turbidity - 1.0) / 9.0, 0.0, 1.0);
    float pixelElev = asin(clamp(vd.y, -1.0, 1.0)) / thetaDisc;
    float discCut = smoothstep(-0.22, 0.1, pixelElev);
    float glowCut = smoothstep(-3.0, 0.5, pixelElev);
    float disc = 0.0;
    if (r < 1.0)
    {
        float limb = 1.0 - 0.6 * (1.0 - sqrt(max(1.0 - r * r, 0.0)));
        disc = limb * (1.0 - smoothstep(0.92, 1.0, r)) * discCut;
    }

    float spread = mix(2.5, 9.0, clamp(bloomRadiusUv * 36.0, 0.0, 1.0)) * (1.0 + turbidityT * 1.6);
    float circumsolar = exp(-pow(max(r - 1.0, 0.0) / (spread * 0.4), 1.5));
    float skirt = 1.0 / (1.0 + pow(r / spread, 2.0));
    vec3 discCol = mix(vec3(1.0, 0.90, 0.72), vec3(1.0, 0.46, 0.12), lowSun);
    vec3 glowCol = mix(vec3(1.0, 0.80, 0.52), vec3(0.92, 0.93, 1.0), turbidityT * 0.7);
    glowCol = mix(glowCol, vec3(1.0, 0.40, 0.10), lowSun * 0.85);
    vec3 glow = glowCol * (circumsolar * 1.85 + skirt * 0.4) * glowCut;
    float discBright = max(discBrightness, 0.0);
    float bloom = max(bloomStrength, 0.0);
    return (discCol * disc * 34.0 * discBright + glow) * bloom;
}

vec2 moonDiscUv(vec3 viewDir, vec3 towardMoon, float cosDiscEdge)
{
    vec3 vd = normalize(viewDir);
    float cosAngle = clamp(dot(vd, towardMoon), -1.0, 1.0);
    float sinTheta = sqrt(max(1.0 - cosAngle * cosAngle, 0.0));
    vec3 tangent = vd - towardMoon * cosAngle;
    float tLen2 = dot(tangent, tangent);
    if (tLen2 < 1e-10)
    {
        return vec2(0.5);
    }

    tangent *= inversesqrt(tLen2);
    vec3 moonUp = abs(towardMoon.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 moonRight = normalize(cross(moonUp, towardMoon));
    moonUp = cross(towardMoon, moonRight);
    float angularRadius = max(acos(clamp(cosDiscEdge, -1.0, 1.0)), 1e-4);
    vec2 discUv = vec2(dot(tangent, moonRight), dot(tangent, moonUp)) * (sinTheta / angularRadius);
    return discUv * 0.5 + 0.5;
}

vec3 moonDiscShading(vec3 viewDir, vec3 lightPropagationDir, float cosDiscEdge)
{
    vec3 towardMoon = normalize(lightPropagationDir);
    float cosAngle = dot(normalize(viewDir), towardMoon);
    float edge = clamp(cosDiscEdge, 0.94, 0.99998);
    float penumbra = (1.0 - edge) * 2.5;
    float outerCos = clamp(edge - penumbra, -1.0, 1.0);
    float disc = smoothstep(outerCos, edge, cosAngle);
    if (disc <= 1e-4)
    {
        return vec3(0.0);
    }

    vec2 mUv = moonDiscUv(viewDir, towardMoon, edge);
    vec3 samplePos = vec3(mUv * 8.0, 0.0);
    float n0 = hash31(samplePos);
    float n1 = hash31(samplePos * 2.13 + vec3(1.7, 4.1, 0.0));
    float n2 = hash31(samplePos * 4.37 + vec3(9.0, 2.3, 0.0));
    float mare = smoothstep(0.38, 0.62, n0 * 0.55 + n1 * 0.3 + n2 * 0.15);
    float crater = smoothstep(0.82, 0.94, n1) * smoothstep(0.15, 0.45, n2);
    vec3 highland = vec3(0.78, 0.80, 0.84);
    vec3 lowland = vec3(0.58, 0.60, 0.66);
    vec3 moonCol = mix(highland, lowland, mare * 0.9);
    moonCol = mix(moonCol, vec3(0.48, 0.50, 0.55), crater * 0.55);
    float radial = length(mUv - 0.5) * 2.0;
    moonCol *= 1.0 - smoothstep(0.55, 1.0, radial) * 0.35;
    moonCol *= 0.65 + 0.35 * disc;
    return moonCol * disc;
}

void main()
{
    vec3 viewDir = worldRayDir(vUv, uInvViewProj, uCameraPos);
    float dayAmt = dayFactor(uLightDir, uSunIntensity);
    float horizonBandScale = horizonAltitudeFade(uCameraPos.y, uGroundWorldY);
    vec3 sky = proceduralSky(viewDir, uLightDir, uSunIntensity, uTurbidity, uHorizonFalloff, horizonBandScale);
    vec3 nightSky = nightZenith(viewDir) + stars(viewDir, uRenderTime);
    sky = mix(nightSky, sky, dayAmt);
    sky += horizonGlow(viewDir, dayAmt, horizonBandScale);
    sky += belowHorizonFog(viewDir, uHorizonFogStrength, horizonBandScale);

    float aboveW = aboveHorizonHazeWeight(viewDir, uHorizonFogStrength, horizonBandScale);
    if (aboveW > 1e-4)
    {
        vec3 hazeCol = proceduralSky(seamFogDir(viewDir), uLightDir, uSunIntensity, uTurbidity, uHorizonFalloff,
            horizonBandScale);
        sky = mix(sky, hazeCol, aboveW);
    }

    float sunVis = smoothstep(0.0, 0.06, dayAmt) * (0.35 + 0.65 * dayAmt);
    if (sunVis > 0.001)
    {
        sky += sunDiscAureole(viewDir, uLightDir, uSunCosDiscEdge, uSunDiscRadiusUv, uSunDiscStrength, uSunDiscBrightness, uTurbidity) * sunVis;
    }

    sky *= uSkyExposure * 1.4;
    vec3 outRgb = max(sky, vec3(0.0));
    if (uHdrPresent <= 0)
    {
        float lum = dot(outRgb, vec3(0.2126, 0.7152, 0.0722));
        if (lum > 1e-5)
        {
            outRgb *= (lum / (1.0 + lum)) / lum;
        }

        outRgb = pow(clamp(outRgb, vec3(0.0), vec3(1.0)), vec3(1.0 / 2.2));
    }

    FragColor = vec4(outRgb, 1.0);
}
""";

    private readonly GL _gl;
    private uint _program;
    private bool _disposed;

    public GlProceduralSkyProgram(GL gl, bool useOpenGlEs, out string? error)
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
