// Genesis preview shader - Parallax Occlusion Mapping with binary refinement and self-shadow.
// Algorithmic pattern inspired by Glimmer Shaders (MIT, https://github.com/jbritain/glimmer-shaders)
// and the LabPBR community POM literature; expressed for a single-pass forward preview without
// access to a depth buffer.

#ifndef GENESIS_PARALLAX_GLSL
#define GENESIS_PARALLAX_GLSL

//!include "common.glsl"

#ifndef GENESIS_DRAW_RECORD_GLSL
float genesisParallaxUvScale(float fallbackValue)
{
    return fallbackValue;
}

vec2 genesisParallaxHeightTexSize(vec2 fallbackValue)
{
    return fallbackValue;
}
#endif

const int GEN_POM_TRACE_LAYERS_MAX = 128;
const int GEN_POM_REFINE_STEPS_MAX = 8;
const int GEN_POM_SHADOW_TAPS_MAX = 64;
const int GEN_POM_AO_TAPS = 8;
// Tangent-space view-Z floor used for grazing rays. Do not use this as a hard cutoff:
// close, low-angle ground views can legitimately sit below this value and should still
// show relief; the steer/UV caps below keep those rays bounded.
const float GEN_POM_MIN_VIEW_Z = 0.012;
// Soft cap on |tan(theta)| = |V.xy|/V.z before parallax scale (preserves direction; avoids bent rays).
const float GEN_POM_STEER_TAN_CAP = 6.0;
// Soft UV-limit knee: below this fraction of max shift, travel is unscaled; above it, excess compresses.
const float GEN_POM_UV_SOFT_START = 0.65;
// Keep in sync with PreviewStageConstants.ParallaxHeightStrengthMax / ParallaxMaxUvShiftMax.
const float GEN_POM_HEIGHT_STRENGTH_MAX = 4.0;
const float GEN_POM_MAX_UV_SHIFT_MAX = 4.0;

uniform int   uParallaxTraceLayers;
uniform int   uParallaxRefineSteps;
uniform int   uParallaxShadowSamples;
uniform float uParallaxShadowSoftness;
uniform float uParallaxMaxUvShift;
uniform float uParallaxUvScale;
uniform vec2 uParallaxHeightTexSize;
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
uniform sampler2DArray uHeightArray;
uniform int uGenesisUseMaterialTextureArray;
#endif

// Height for POM/shadow must stay sharp even on greedy-merged terrain quads (large UV derivatives).
float sampleHeight01Lod0(sampler2D heightTex, vec2 uv)
{
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
    if (uGenesisUseMaterialTextureArray > 0)
    {
        return 1.0 - textureLod(uHeightArray, vec3(uv, float(genesisMaterialTextureLayer(0))), 0.0).r;
    }
#endif
    return 1.0 - textureLod(heightTex, uv, 0.0).r;
}

// Matches UV displacement scale used for the view-ray march (also applied to parallax self-shadow).
float pomUvDisplacementScale(float strength)
{
    return clamp(strength, 0.0, GEN_POM_HEIGHT_STRENGTH_MAX) * 0.92;
}

float pomBaseUvScale(float strength)
{
    return pomUvDisplacementScale(strength) * clamp(genesisParallaxUvScale(uParallaxUvScale), 0.02, 1.0);
}

// Soft-limit full-depth UV travel. Unlike a hard clamp, higher strength still deepens slightly past the knee.
vec2 pomLimitUvTravel(vec2 totalOffset)
{
    float maxUvShift = clamp(uParallaxMaxUvShift, 0.05, GEN_POM_MAX_UV_SHIFT_MAX);
    float totalLen = length(totalOffset);
    if (totalLen <= GEN_EPS)
    {
        return totalOffset;
    }

    float softStart = maxUvShift * GEN_POM_UV_SOFT_START;
    if (totalLen <= softStart)
    {
        return totalOffset;
    }

    float excess = totalLen - softStart;
    float softTail = maxUvShift - softStart;
    float limitedLen = softStart + softTail * (excess / (excess + softTail));
    return totalOffset * (limitedLen / totalLen);
}

// Subject/atlas faces: keep the ray inside the starting unit tile (no fract wrap).
// Ground/terrain uses continuous UVs + GL_REPEAT instead - wrapping at tileBase breaks brick mortar seams.
vec2 pomConstrainSubjectOffset(vec2 localUv, vec2 totalOffset)
{
    vec2 room;
    room.x = totalOffset.x >= 0.0 ? localUv.x : (1.0 - localUv.x);
    room.y = totalOffset.y >= 0.0 ? localUv.y : (1.0 - localUv.y);
    room = max(room - vec2(1e-3), vec2(0.0));

    float scale = 1.0;
    if (abs(totalOffset.x) > GEN_EPS)
    {
        scale = min(scale, room.x / abs(totalOffset.x));
    }
    if (abs(totalOffset.y) > GEN_EPS)
    {
        scale = min(scale, room.y / abs(totalOffset.y));
    }
    return totalOffset * clamp(scale, 0.0, 1.0);
}

// Trace POM from sampled height map. Returns the displaced UV; writes the surface depth at that hit
// (0 at the surface, 1 fully embedded) so the self-shadow trace can start from the correct layer.
// Also writes effectiveStrength after UV-travel limiting so self-shadow / AO match displacement.
//   continuousUv : true for world-tiled ground (march across tiles); false for per-face subject UVs
//   strength     : height scale in 0..1 of layer thickness
vec2 traceParallaxPom(
    sampler2D heightTex,
    vec2 uv0,
    vec3 Vtan,
    float strength,
    bool continuousUv,
    out float hitDepth,
    out float effectiveStrength)
{
    hitDepth = 0.0;
    effectiveStrength = 0.0;
    if (strength <= 0.0)
    {
        return uv0;
    }

    // Front-facing hemisphere only (view exits +N side). Using abs(z) or bending xy/z breaks coherent motion.
    if (Vtan.z <= 0.0)
    {
        return uv0;
    }

    int layers = clamp(uParallaxTraceLayers, 8, GEN_POM_TRACE_LAYERS_MAX);
    float layerStep = 1.0 / float(layers);

    // Classic parallax ray on the tangent plane: delta_uv proportional to V.xy/V.z (unnormalized steer ok up to cap).
    vec2 steer = Vtan.xy / max(Vtan.z, GEN_POM_MIN_VIEW_Z);
    float tanMag = length(steer);
    if (tanMag > GEN_POM_STEER_TAN_CAP)
    {
        steer *= GEN_POM_STEER_TAN_CAP / max(tanMag, GEN_EPS);
    }

    float baseScale = pomBaseUvScale(strength);
    vec2 totalOffset = steer * baseScale;
    // Ray marches opposite view-steer in UV (toward the viewer on the height field).
    if (continuousUv)
    {
        totalOffset = pomLimitUvTravel(totalOffset);
    }
    else
    {
        vec2 local0 = fract(uv0);
        totalOffset = pomConstrainSubjectOffset(local0, totalOffset);
        totalOffset = pomLimitUvTravel(totalOffset);
    }

    float limitedLen = length(totalOffset);
    float baseLen = length(steer) * baseScale;
    effectiveStrength = strength * (limitedLen / max(baseLen, GEN_EPS));

    vec2 deltaUv = totalOffset * layerStep;
    vec2 curUv = uv0;
    float curLayer = 0.0;
    float curHeightSample = sampleHeight01Lod0(heightTex, curUv);

    // Linear march until ray depth crosses sampled height.
    int marchSteps = 0;
    float prevHeightSample = curHeightSample;
    for (int i = 0; i < GEN_POM_TRACE_LAYERS_MAX; ++i)
    {
        if (i >= layers)
        {
            break;
        }

        if (curLayer >= curHeightSample)
        {
            break;
        }

        prevHeightSample = curHeightSample;
        curUv -= deltaUv;
        curLayer += layerStep;
        marchSteps++;
        curHeightSample = sampleHeight01Lod0(heightTex, curUv);
    }

    if (marchSteps > 0)
    {
        vec2  prevUv = curUv + deltaUv;
        float prevLayer = curLayer - layerStep;
        float prevHeight = prevHeightSample;

        float afterDelta = curLayer - curHeightSample;
        float beforeDelta = prevLayer - prevHeight;
        float denom = max(afterDelta - beforeDelta, GEN_EPS);
        vec2 loUv = prevUv;
        float loLayer = prevLayer;
        vec2 hiUv = curUv;
        float hiLayer = curLayer;

        float secantT = clamp(-beforeDelta / denom, 0.0, 1.0);
        vec2 secantUv = mix(prevUv, curUv, secantT);
        float secantLayer = mix(prevLayer, curLayer, secantT);
        float secantHeight = sampleHeight01Lod0(heightTex, secantUv);
        if (secantLayer >= secantHeight)
        {
            hiUv = secantUv;
            hiLayer = secantLayer;
        }
        else
        {
            loUv = secantUv;
            loLayer = secantLayer;
        }

        int refineSteps = clamp(uParallaxRefineSteps, 0, GEN_POM_REFINE_STEPS_MAX);
        for (int i = 0; i < GEN_POM_REFINE_STEPS_MAX; ++i)
        {
            if (i >= refineSteps)
            {
                break;
            }

            vec2  midUv = 0.5 * (loUv + hiUv);
            float midLayer = 0.5 * (loLayer + hiLayer);
            float midH = sampleHeight01Lod0(heightTex, midUv);
            if (midLayer >= midH)
            {
                hiUv = midUv;
                hiLayer = midLayer;
            }
            else
            {
                loUv = midUv;
                loLayer = midLayer;
            }
        }

        curUv = hiUv;
        curLayer = hiLayer;
    }

    hitDepth = clamp(curLayer, 0.0, 1.0);
    return curUv;
}

// Cheap parallax self-shadow: march toward the light from the hit point and accumulate the
// largest height-above-ray difference. Returns 1.0 (lit) .. 0.0 (fully shadowed); already gated
// to 1.0 if the light is below the surface in tangent space.
float traceParallaxShadow(
    sampler2D heightTex,
    vec2 uvHit,
    vec3 Ltan,
    float refDepth,
    float strength,
    vec2 dx,
    vec2 dy)
{
    if (Ltan.z <= 0.0 || strength <= 0.0)
    {
        return 1.0;
    }

    float lz = max(Ltan.z, GEN_EPS);
    int   taps = clamp(uParallaxShadowSamples, 4, GEN_POM_SHADOW_TAPS_MAX);
    float stepLen = refDepth / float(taps);
    if (stepLen <= 0.0)
    {
        return 1.0;
    }

    // Caller passes view-ray effectiveStrength so shadow UV travel matches limited POM displacement.
    float uvScale = pomBaseUvScale(strength);
    vec2 uvStep = (Ltan.xy / lz) * uvScale * stepLen;
    vec2 curUv = uvHit;
    float curLayer = refDepth;
    float maxOcclusion = 0.0;
    float sumOcclusion = 0.0;
    float occlusionWeight = 0.0;
    float softWidth = max(stepLen * mix(0.35, 5.0, clamp(uParallaxShadowSoftness, 0.0, 4.0) * 0.25), 0.0015);

    for (int i = 0; i < GEN_POM_SHADOW_TAPS_MAX; ++i)
    {
        if (i >= taps || curLayer <= 0.0)
        {
            break;
        }

        curUv += uvStep;
        curLayer -= stepLen;
        float sampleH = sampleHeight01Lod0(heightTex, curUv);
        if (curLayer < sampleH)
        {
            // Ray is above the surface here; no occluder.
            continue;
        }

        float delta = curLayer - sampleH;
        float occlusion = smoothstep(0.0, softWidth, delta);
        maxOcclusion = max(maxOcclusion, occlusion);
        sumOcclusion += occlusion;
        occlusionWeight += 1.0;
    }

    float avgOcclusion = occlusionWeight > 0.0 ? sumOcclusion / occlusionWeight : 0.0;
    float occlusion = mix(maxOcclusion, avgOcclusion, 0.28);
    return clamp(1.0 - occlusion, 0.0, 1.0);
}

// Contact AO from local height neighborhood around the POM hit point.
// This targets the "grounded" crevice darkening many packs pair with POM.
float traceParallaxAo(
    sampler2D heightTex,
    vec2 uvHit,
    float refDepth,
    float strength,
    float aoStrength,
    vec2 dx,
    vec2 dy)
{
    if (refDepth <= 0.0 || strength <= 0.0 || aoStrength <= 0.0)
    {
        return 1.0;
    }

    vec2 texelSize = vec2(1.0) / max(genesisParallaxHeightTexSize(uParallaxHeightTexSize), vec2(GEN_EPS));
    float radiusTexels = mix(0.75, 2.25, clamp(refDepth, 0.0, 1.0)) * clamp(strength, 0.0, GEN_POM_HEIGHT_STRENGTH_MAX);
    if (radiusTexels <= GEN_EPS)
    {
        return 1.0;
    }

    vec2 tapDirs[GEN_POM_AO_TAPS] = vec2[](
        vec2(1.0, 0.0), vec2(-1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, -1.0),
        vec2(0.7071, 0.7071), vec2(-0.7071, 0.7071), vec2(0.7071, -0.7071), vec2(-0.7071, -0.7071)
    );

    float occ = 0.0;
    for (int i = 0; i < GEN_POM_AO_TAPS; ++i)
    {
        float ring = (float(i) + 1.0) / float(GEN_POM_AO_TAPS);
        vec2 uv = uvHit + tapDirs[i] * texelSize * radiusTexels * ring;
        float sampleH = sampleHeight01Lod0(heightTex, uv);
        occ += max(0.0, sampleH - refDepth);
    }

    occ /= float(GEN_POM_AO_TAPS);
    // AO should ground local crevices, not replace normal/specular lighting response.
    return clamp(1.0 - occ * 1.35 * aoStrength, 0.78, 1.0);
}

#endif // GENESIS_PARALLAX_GLSL
