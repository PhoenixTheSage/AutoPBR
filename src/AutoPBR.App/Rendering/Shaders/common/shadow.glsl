// Genesis preview shader - directional shadow sampling helpers.
// Compatible with desktop GLSL 330 core and GLSL ES 300 (after GlslSourceAdapter).
// Hardware shadow comparison via sampler2DShadow + PCF, slope-scaled bias, and a
// manual border check (ES 300 has no CLAMP_TO_BORDER).

#ifndef GENESIS_SHADOW_GLSL
#define GENESIS_SHADOW_GLSL

//!include "common.glsl"

// Project world position into shadow UV. Returns xyz = UV+depth, w = inside frustum (1 = lit path).
vec4 worldToShadowUv(vec3 worldPos, mat4 lightVP)
{
    vec4 clip = lightVP * vec4(worldPos, 1.0);
    if (clip.w <= 0.0)
    {
        return vec4(0.0, 0.0, 0.0, 0.0);
    }

    vec3 ndc = clip.xyz / clip.w;
    vec3 uv = ndc * 0.5 + 0.5;

    float inside = 1.0;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || uv.z < 0.0 || uv.z > 1.0)
    {
        inside = 0.0;
    }

    return vec4(uv, inside);
}

float shadowMapTexelDepth(vec2 shadowTexelSize)
{
    return max(max(shadowTexelSize.x, shadowTexelSize.y), 1.0 / 4096.0);
}

// minBias/maxBias are normalized-depth offsets. Soften grazing slope so flat receivers at low
// sun (terrain pad) do not peter-pan away from caster contact. Keep a light texel floor for acne.
float computeShadowBias(vec3 N, vec3 L, float minBias, float maxBias, vec2 shadowTexelSize)
{
    float ndl = clamp(dot(normalize(N), normalize(L)), 0.0, 1.0);
    float slope = 1.0 - ndl;
    float texel = shadowMapTexelDepth(shadowTexelSize);
    // Quadratic slope keeps mid-angles useful without full maxBias on near-grazing ground.
    float slopeBias = maxBias * slope * slope;
    float configured = max(minBias, slopeBias);
    return max(configured, texel * 0.5);
}

float sampleShadowPcf3x3(sampler2DShadow shadowTex, vec3 shadowUv, vec2 texelSize)
{
    vec2 t = texelSize;
    vec2 uv = shadowUv.xy;
    float z = shadowUv.z;
    float sum =
        texture(shadowTex, vec3(uv + vec2(-t.x, -t.y), z)) +
        texture(shadowTex, vec3(uv + vec2( 0.0, -t.y), z)) +
        texture(shadowTex, vec3(uv + vec2( t.x, -t.y), z)) +
        texture(shadowTex, vec3(uv + vec2(-t.x,  0.0), z)) +
        texture(shadowTex, vec3(uv + vec2( 0.0,  0.0), z)) +
        texture(shadowTex, vec3(uv + vec2( t.x,  0.0), z)) +
        texture(shadowTex, vec3(uv + vec2(-t.x,  t.y), z)) +
        texture(shadowTex, vec3(uv + vec2( 0.0,  t.y), z)) +
        texture(shadowTex, vec3(uv + vec2( t.x,  t.y), z));

    return sum * (1.0 / 9.0);
}

float sampleShadowBordered(sampler2DShadow shadowTex, vec3 shadowUv)
{
    if (shadowUv.x < 0.0 || shadowUv.x > 1.0 || shadowUv.y < 0.0 || shadowUv.y > 1.0)
    {
        return 1.0;
    }

    return texture(shadowTex, shadowUv);
}

float sampleShadowPcfSoft(sampler2DShadow shadowTex, vec3 shadowUv, vec2 texelSize, float softnessTexels)
{
    float radius = max(softnessTexels, 0.0);
    if (radius <= 1.0)
    {
        return sampleShadowPcf3x3(shadowTex, shadowUv, texelSize);
    }

    vec2 disk[16] = vec2[16](
        vec2(-0.942016, -0.399062), vec2( 0.945586, -0.768907),
        vec2(-0.094184, -0.929389), vec2( 0.344959,  0.293878),
        vec2(-0.915886,  0.457714), vec2(-0.815442, -0.879125),
        vec2(-0.382775,  0.276768), vec2( 0.974844,  0.756484),
        vec2( 0.443233, -0.975116), vec2( 0.537430, -0.473734),
        vec2(-0.264969, -0.418930), vec2( 0.791975,  0.190902),
        vec2(-0.241888,  0.997065), vec2(-0.814100,  0.914376),
        vec2( 0.199841,  0.786414), vec2( 0.143832, -0.141008)
    );

    float sum = sampleShadowBordered(shadowTex, shadowUv) * 2.0;
    float totalWeight = 2.0;
    for (int i = 0; i < 16; ++i)
    {
        vec2 off = disk[i] * texelSize * radius;
        sum += sampleShadowBordered(shadowTex, vec3(shadowUv.xy + off, shadowUv.z));
        totalWeight += 1.0;
    }

    return sum / totalWeight;
}

float shadowUvEdgeWeight(vec2 uv)
{
    // Soften the ortho border slightly; keep the band tight so large terrain maps do not
    // show a wide soft dark rectangle before full coverage.
    const float edge = 0.02;
    float wx = smoothstep(0.0, edge, uv.x) * smoothstep(0.0, edge, 1.0 - uv.x);
    float wy = smoothstep(0.0, edge, uv.y) * smoothstep(0.0, edge, 1.0 - uv.y);
    return clamp(wx * wy, 0.0, 1.0);
}

// Visibility in [0,1], or -1 when worldPos is outside this cascade's light frustum.
float sampleSceneShadowFromWorldOrOutside(vec3 worldPos, mat4 lightVp, sampler2DShadow shadowMap, vec2 texelSize,
    float minBias, float maxBias, vec3 N, vec3 L, float softnessTexels)
{
    vec4 shadowPack = worldToShadowUv(worldPos, lightVp);
    if (shadowPack.w < 0.5)
    {
        return -1.0;
    }

    vec3 sUv = shadowPack.xyz;
    float bias = computeShadowBias(N, L, minBias, maxBias, texelSize);
    sUv.z = clamp(sUv.z - bias, 0.0, 1.0);
    float vis = sampleShadowPcfSoft(shadowMap, sUv, texelSize, softnessTexels);
    return mix(1.0, vis, shadowUvEdgeWeight(sUv.xy));
}

float sampleSceneShadowFromWorld(vec3 worldPos, mat4 lightVp, sampler2DShadow shadowMap, vec2 texelSize,
    float minBias, float maxBias, vec3 N, vec3 L, float softnessTexels)
{
    float vis = sampleSceneShadowFromWorldOrOutside(
        worldPos, lightVp, shadowMap, texelSize, minBias, maxBias, N, L, softnessTexels);
    return vis < 0.0 ? 1.0 : vis;
}

float sampleSceneShadowFromClip(vec4 lightClip, sampler2DShadow shadowMap, vec2 texelSize,
    float minBias, float maxBias, vec3 N, vec3 L, float softnessTexels)
{
    if (lightClip.w <= 0.0)
    {
        return 1.0;
    }

    vec3 sUv = lightClip.xyz / lightClip.w;
    sUv = sUv * 0.5 + 0.5;
    if (sUv.x < 0.0 || sUv.x > 1.0 || sUv.y < 0.0 || sUv.y > 1.0 || sUv.z < 0.0 || sUv.z > 1.0)
    {
        return 1.0;
    }

    float bias = computeShadowBias(N, L, minBias, maxBias, texelSize);
    sUv.z = clamp(sUv.z - bias, 0.0, 1.0);
    float vis = sampleShadowPcfSoft(shadowMap, sUv, texelSize, softnessTexels);
    return mix(1.0, vis, shadowUvEdgeWeight(sUv.xy));
}

float shadowRangeFade(float dist, float fadeStart, float shadowDistance)
{
    float endDist = max(shadowDistance, fadeStart + 1e-3);
    return 1.0 - smoothstep(fadeStart, endDist, dist);
}

float shadowCascadeBlendT(float dist, float splitDist, float blendWidth)
{
    float halfBand = max(blendWidth, 0.0) * 0.5;
    if (halfBand > 1e-5)
    {
        return smoothstep(splitDist - halfBand, splitDist + halfBand, dist);
    }

    return dist > splitDist ? 1.0 : 0.0;
}

float sampleSceneShadowCascaded(vec3 worldPos, vec3 cameraPos, vec4 lightClipFar,
    mat4 lightVpNear, mat4 lightVpMid, mat4 lightVpFar,
    sampler2DShadow shadowNear, sampler2DShadow shadowMid, sampler2DShadow shadowFar,
    vec2 texelSizeNear, vec2 texelSizeMid, vec2 texelSizeFar,
    float minBias, float maxBias, vec3 N, vec3 L, float softnessTexels,
    int enableShadow, int enableCascades, float splitNear, float splitMid, float blendWidth,
    float shadowDistance, float shadowFadeStart)
{
    if (enableShadow < 1)
    {
        return 1.0;
    }

    float dist = length(worldPos - cameraPos);
    float rangeFade = shadowRangeFade(dist, shadowFadeStart, shadowDistance);
    if (rangeFade <= 1e-4)
    {
        return 1.0;
    }

    float outerSoftness = min(softnessTexels, 1.0);

    if (enableCascades < 1)
    {
        float singleVis = sampleSceneShadowFromClip(
            lightClipFar, shadowFar, texelSizeFar, minBias, maxBias, N, L, outerSoftness);
        return mix(1.0, singleVis, rangeFade);
    }

    // Prefer the highest-res cascade that covers this receiver. min() with far keeps tall
    // terrain casters that only fit the wide far ortho from leaving lit holes. Never treat
    // "outside preferred map" as lit — that painted a camera-centered wipe between near
    // coverage and the mid split whenever far was also misaligned.
    float nearMidT = shadowCascadeBlendT(dist, splitNear, blendWidth);
    float midFarT = shadowCascadeBlendT(dist, splitMid, blendWidth);

    float farSample = sampleSceneShadowFromWorldOrOutside(worldPos, lightVpFar, shadowFar, texelSizeFar,
        minBias, maxBias, N, L, outerSoftness);
    float farVis = farSample < 0.0 ? 1.0 : farSample;

    float nearSample = sampleSceneShadowFromWorldOrOutside(worldPos, lightVpNear, shadowNear, texelSizeNear,
        minBias, maxBias, N, L, softnessTexels);
    float midSample = sampleSceneShadowFromWorldOrOutside(worldPos, lightVpMid, shadowMid, texelSizeMid,
        minBias, maxBias, N, L, outerSoftness);

    float vis;
    if (nearMidT <= 0.0)
    {
        if (nearSample >= 0.0)
        {
            vis = min(nearSample, farVis);
        }
        else if (midSample >= 0.0)
        {
            vis = min(midSample, farVis);
        }
        else
        {
            vis = farVis;
        }
    }
    else if (nearMidT < 1.0)
    {
        float nearVis = nearSample >= 0.0 ? nearSample : (midSample >= 0.0 ? midSample : farVis);
        float midVis = midSample >= 0.0 ? midSample : farVis;
        vis = min(mix(nearVis, midVis, nearMidT), farVis);
    }
    else if (midFarT <= 0.0)
    {
        if (midSample >= 0.0)
        {
            vis = min(midSample, farVis);
        }
        else
        {
            vis = farVis;
        }
    }
    else if (midFarT < 1.0)
    {
        float midVis = midSample >= 0.0 ? midSample : farVis;
        vis = min(mix(midVis, farVis, midFarT), farVis);
    }
    else
    {
        vis = farVis;
    }

    return mix(1.0, vis, rangeFade);
}

#endif // GENESIS_SHADOW_GLSL
