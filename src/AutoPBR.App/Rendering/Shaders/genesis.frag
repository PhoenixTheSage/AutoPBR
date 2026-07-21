#version 330 core
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
#extension GL_ARB_shader_storage_buffer_object : require
#endif
#define GENESIS_FRAGMENT_STAGE 1
// AutoPBR Genesis preview shader - fragment stage.
// Algorithms inspired by LabPBR 1.3 spec (https://shaderlabs.org/wiki/LabPBR_Material_Standard)
// and Glimmer Shaders (MIT, https://github.com/jbritain/glimmer-shaders).
// Single-pass forward emulation of POM, parallax self-shadow, Cook-Torrance,
// LabPBR-aware metal/dielectric F0, subsurface scattering, environment IBL,
// emission, directional shadow map (Phase 2) and ACES Filmic tonemapping.

//!include "common/genesis_draw_record.glsl"
//!include "common/common.glsl"
//!include "common/material_labpbr.glsl"
//!include "common/brdf.glsl"
//!include "common/parallax.glsl"
//!include "common/sss.glsl"
//!include "common/ibl.glsl"
//!include "common/tonemap.glsl"
//!include "common/shadow.glsl"

in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUv;
in vec4 vWorldTangent;
in vec4 vLightClip;
in vec4 vCurrClip;
in vec4 vPrevClip;

uniform sampler2D uAlbedo;
uniform sampler2D uNormal;
uniform sampler2D uSpecular;
uniform sampler2D uHeight;
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
uniform sampler2DArray uAlbedoArray;
uniform sampler2DArray uNormalArray;
uniform sampler2DArray uSpecularArray;
#endif
uniform sampler2D uAtmoSkyViewLut;
uniform sampler2DShadow uShadowMap;
uniform sampler2DShadow uShadowMapNear;
uniform sampler2DShadow uShadowMapMid;

uniform vec3  uCameraPos;
uniform vec3  uLightDir;
uniform vec3  uLightColor;
uniform float uAmbient;
uniform float uNormalStrength;
uniform float uHeightStrength;
uniform float uSpecularStrength;
uniform float uRoughnessScale;
uniform float uExposure;
uniform int uHdrPresent;
uniform float uHdrPaperWhiteNits;
uniform float uParallaxAoStrength;
uniform int   uEnableParallax;
uniform int   uEnableParallaxAo;
uniform int   uEnableNormalMap;
uniform int   uEnableSpecularMap;
uniform int   uHasNormal;
uniform int   uHasSpecular;
uniform int   uHasHeight;
uniform int   uSceneKind;
uniform int   uIsGroundPass;
uniform float uTerrainPomFadeStart;
uniform float uTerrainPomFadeEnd;
uniform float uAlphaCutoff;
uniform int   uItemAlphaBlend;
// 0 = off, 1 = cutout, 2 = blend - batched block/item models and entity emulated rigs.
uniform int   uEntityAlphaMode;
uniform int   uPreviewDepthLayerDebug;
uniform vec3  uPreviewLayerDebugTint;

// Genesis-specific.
uniform int   uEnableSss;
uniform int   uEnableParallaxShadow;
uniform int   uEnableIbl;
uniform int   uEnableAtmosphericSky;
uniform float uSssStrength;
uniform float uIblStrength;
uniform float uEmissionStrength;
uniform vec3  uSkyTint;
uniform vec3  uGroundTint;
uniform float uAtmosphereSunIntensity;
uniform float uAerialFogStrength;
uniform float uTerrainFogStart;
uniform float uTerrainFogEnd;

// Genesis directional shadow map (Phase 2).
uniform mat4  uLightViewProj;
uniform mat4  uLightViewProjNear;
uniform mat4  uLightViewProjMid;
uniform int   uEnableShadowMap;
uniform int   uEnableShadowCascades;
uniform float uCascadeSplitDistance;
uniform float uCascadeMidSplitDistance;
uniform float uCascadeBlendWidth;
uniform float uShadowDistance;
uniform float uShadowFadeStart;
uniform float uShadowMinBias;
uniform float uShadowMaxBias;
uniform vec2  uShadowTexelSize;
uniform vec2  uShadowTexelSizeNear;
uniform vec2  uShadowTexelSizeMid;
uniform float uShadowSoftnessTexels;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 TaaSignal;

vec3 genesisMaterialTextureCoord(vec2 uv)
{
    return vec3(uv, float(genesisMaterialTextureLayer(0)));
}

vec4 sampleGenesisAlbedo(vec2 uv)
{
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
    if (uGenesisUseMaterialTextureArray > 0)
    {
        return texture(uAlbedoArray, genesisMaterialTextureCoord(uv));
    }
#endif
    return texture(uAlbedo, uv);
}

vec4 sampleGenesisAlbedoGrad(vec2 uv, vec2 dx, vec2 dy)
{
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
    if (uGenesisUseMaterialTextureArray > 0)
    {
        return textureGrad(uAlbedoArray, genesisMaterialTextureCoord(uv), dx, dy);
    }
#endif
    return textureGrad(uAlbedo, uv, dx, dy);
}

vec4 sampleGenesisNormalGrad(vec2 uv, vec2 dx, vec2 dy)
{
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
    if (uGenesisUseMaterialTextureArray > 0)
    {
        return textureGrad(uNormalArray, genesisMaterialTextureCoord(uv), dx, dy);
    }
#endif
    return textureGrad(uNormal, uv, dx, dy);
}

vec4 sampleGenesisSpecularGrad(vec2 uv, vec2 dx, vec2 dy)
{
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
    if (uGenesisUseMaterialTextureArray > 0)
    {
        return textureGrad(uSpecularArray, genesisMaterialTextureCoord(uv), dx, dy);
    }
#endif
    return textureGrad(uSpecular, uv, dx, dy);
}

float sampleGenesisHeightGrad(vec2 uv, vec2 dx, vec2 dy)
{
#ifdef GENESIS_MATERIAL_TEXTURE_ARRAYS
    if (uGenesisUseMaterialTextureArray > 0)
    {
        return textureGrad(uHeightArray, genesisMaterialTextureCoord(uv), dx, dy).r;
    }
#endif
    return textureGrad(uHeight, uv, dx, dy).r;
}

vec3 sampleNormal(vec2 uv, vec2 dx, vec2 dy, vec3 Nw, vec3 Tw, vec3 Bw)
{
#if defined(GENESIS_ENABLE_NORMAL_MAP)
    if (uEnableNormalMap < 1 || genesisHasNormal(uHasNormal) < 1)
    {
        return normalize(Nw);
    }

    vec3 tn = sampleGenesisNormalGrad(uv, dx, dy).xyz * 2.0 - 1.0;
    tn.xy *= uNormalStrength;
    tn = normalize(tn);
    return normalize(mat3(Tw, Bw, Nw) * tn);
#else
    return normalize(Nw);
#endif
}

float metalPreviewBaseVisibility(float roughness)
{
    return mix(0.22, 0.38, saturate1(roughness));
}

float groundSpecularReceiverFade(vec3 worldPos, vec3 N, vec3 V)
{
    if (uIsGroundPass < 1)
    {
        return 1.0;
    }

    float dist = length(worldPos - uCameraPos);
    float distFade = 1.0 - smoothstep(18.0, 48.0, dist);
    float noV = max(dot(N, V), 0.0);
    float grazingFade = smoothstep(0.045, 0.22, noV);
    return clamp(mix(0.08, 1.0, distFade * grazingFade), 0.08, 1.0);
}

vec3 applyTerrainAerialFog(vec3 color)
{
    if (uAerialFogStrength <= 0.0 || uEnableAtmosphericSky <= 0)
    {
        return color;
    }

    float dist = length(vWorldPos - uCameraPos);
    float fogSpan = max(uTerrainFogEnd - uTerrainFogStart, 1.0);
    float fogT = saturate1((dist - uTerrainFogStart) / fogSpan);
    // Smoothstep so the LOD unload rim softens instead of a hard pop.
    fogT = fogT * fogT * (3.0 - 2.0 * fogT);
    float fogAmt = fogT * saturate1(uAerialFogStrength) * 0.92;
    vec3 fogCol = previewAerialFogRadiance(
        vWorldPos, uCameraPos, uLightDir, uLightColor, uAtmosphereSunIntensity,
        uSkyTint, uGroundTint, uEnableAtmosphericSky, uAtmoSkyViewLut);
    if (uHdrPresent > 0)
    {
        // Fog is authored for SDR post-ACES mixing. presentEncodeScRgb then multiplies those
        // midtones by paperWhite/80, which turns the night LOD band into a bright HDR slab.
        // Undo that scale while the scene light is moon-dim so night matches SDR.
        float lightLum = dot(uLightColor, vec3(0.2126, 0.7152, 0.0722));
        float nightAmt = 1.0 - smoothstep(0.08, 0.72, lightLum);
        float paperScale = max(uHdrPaperWhiteNits, 80.0) / 80.0;
        fogCol *= mix(1.0, 1.0 / max(paperScale, 1.0), nightAmt);
    }
    return mix(color, fogCol, fogAmt);
}

void main()
{
    vec2 uvDx = dFdx(vUv);
    vec2 uvDy = dFdy(vUv);
    float groundPomFade = 1.0;
    if (uIsGroundPass > 0)
    {
        float fadeStart = max(uTerrainPomFadeStart, 0.0);
        float fadeEnd = max(uTerrainPomFadeEnd, fadeStart + 1e-3);
        float pomDist = length(vWorldPos.xz - uCameraPos.xz);
        groundPomFade = 1.0 - smoothstep(fadeStart, fadeEnd, pomDist);
    }
    float pomStrengthTrace = uHeightStrength * groundPomFade;
#if defined(GENESIS_ENABLE_POM)
    bool  pomActiveEarly = (genesisEnableParallax(uEnableParallax) > 0 && genesisHasHeight(uHasHeight) > 0 && pomStrengthTrace > 1e-5);
    vec4 albRaw = pomActiveEarly
        ? sampleGenesisAlbedoGrad(vUv, uvDx, uvDy)
        : sampleGenesisAlbedo(vUv);
#else
    bool  pomActiveEarly = false;
    vec4 albRaw = sampleGenesisAlbedo(vUv);
#endif
    if (uSceneKind == 1)
    {
        if (uItemAlphaBlend < 1 && albRaw.a < uAlphaCutoff)
        {
            discard;
        }
    }
    else if (genesisEntityAlphaMode(uEntityAlphaMode) == 1 && albRaw.a < uAlphaCutoff)
    {
        discard;
    }

    // Build TBN in world space.
    vec3 Nw = normalize(vWorldNormal);
    vec3 Tw = normalize(vWorldTangent.xyz);
    Tw = normalize(Tw - dot(Tw, Nw) * Nw);
    vec3 Bw = cross(Nw, Tw) * vWorldTangent.w;
    mat3 worldToTan = transpose(mat3(Tw, Bw, Nw));

    vec3 Vw = normalize(uCameraPos - vWorldPos);
    vec3 Vtan = normalize(worldToTan * Vw);

    // Parallax occlusion mapping in tangent space.
    vec2 uvDisp = vUv;
    vec2 uv = vUv;
    float pomDepth = 0.0;
#if defined(GENESIS_ENABLE_POM)
    bool  pomActive = pomActiveEarly;
    if (pomActive)
    {
        // Trace in tile-local space; height/albedo/normal/spec samples wrap so repeated ground tiles and cube-face edges stay seamless.
        uvDisp = traceParallaxPom(uHeight, vUv, Vtan, pomStrengthTrace, uvDx, uvDy, pomDepth);
        uv = uvDisp;
    }
#else
    bool  pomActive = false;
#endif

    // Re-sample albedo at displaced UV when POM is on.
    vec4 alb = pomActive ? sampleGenesisAlbedoGrad(uv, uvDx, uvDy) : albRaw;
    if (genesisEntityAlphaMode(uEntityAlphaMode) == 1 && alb.a < uAlphaCutoff)
    {
        discard;
    }

    vec3 albedoLinear = srgbToLinear(alb.rgb);
    if (uPreviewDepthLayerDebug != 0)
    {
        albedoLinear = uPreviewLayerDebugTint;
    }

    // Surface normal.
    vec3 N = sampleNormal(uv, uvDx, uvDy, Nw, Tw, Bw);
    vec3 V = Vw;
    vec3 L = normalize(-uLightDir); // uLightDir points where the light goes; flip for incoming direction.
    vec3 Ltan = normalize(worldToTan * L);

    // LabPBR _s decode (or neutral defaults when no spec map / spec map disabled).
    LabPbrMaterial mat;
#if defined(GENESIS_ENABLE_SPECULAR_MAP)
    if (genesisHasSpecular(uHasSpecular) > 0 && uEnableSpecularMap > 0)
    {
        vec4 sp = sampleGenesisSpecularGrad(uv, uvDx, uvDy);
        mat = decodeLabPbrSpec(sp, albedoLinear, uRoughnessScale, uSpecularStrength);
    }
    else
#endif
    {
        mat.smoothness = 0.0;
        mat.roughness = 0.9;
        mat.f0 = vec3(0.04);
        mat.metallic = 0.0;
        mat.sssAmount = 0.0;
        mat.porosity = 0.0;
        mat.emissionStrength = 0.0;
    }

    // Porosity cosmetic darkening (only visible when LabPBR _s.b <= 64).
    albedoLinear *= porosityAlbedoMultiplier(mat.porosity);

    // Parallax self-shadow trace toward the light (fragment-local; complements the directional shadow map).
    float pomShadow = 1.0;
    float pomAo = 1.0;
#if defined(GENESIS_ENABLE_POM)
    if (pomActive && genesisHasHeight(uHasHeight) > 0)
    {
#if defined(GENESIS_ENABLE_POM_SHADOW)
        if (genesisEnableParallaxShadow(uEnableParallaxShadow) > 0)
        {
            float tracedShadow = traceParallaxShadow(uHeight, uv, Ltan, pomDepth, pomStrengthTrace, uvDx, uvDy);
            pomShadow = mix(1.0, tracedShadow, groundPomFade);
        }
#endif
#if defined(GENESIS_ENABLE_POM_AO)
        if (genesisEnableParallaxAo(uEnableParallaxAo) > 0)
        {
            float tracedAo = traceParallaxAo(uHeight, uv, pomDepth, pomStrengthTrace, uParallaxAoStrength, uvDx, uvDy);
            pomAo = mix(1.0, tracedAo, groundPomFade);
        }
#endif
    }
#endif

    // Directional shadow map visibility (Phase 2 + cascade split).
    float shadowVis = 1.0;
#if defined(GENESIS_ENABLE_SHADOW)
    shadowVis = sampleSceneShadowCascaded(
        vWorldPos, uCameraPos, vLightClip,
        uLightViewProjNear, uLightViewProjMid, uLightViewProj,
        uShadowMapNear, uShadowMapMid, uShadowMap,
        uShadowTexelSizeNear, uShadowTexelSizeMid, uShadowTexelSize,
        uShadowMinBias, uShadowMaxBias, N, L, uShadowSoftnessTexels,
        uEnableShadowMap, uEnableShadowCascades,
        uCascadeSplitDistance, uCascadeMidSplitDistance, uCascadeBlendWidth,
        uShadowDistance, uShadowFadeStart);
#endif

    // Combined lighting visibility: parallax-local AND directional shadow gate.
    float lightVis = pomShadow * shadowVis;

    // Direct lighting: Cook-Torrance. Specular lobe is gated on the LabPBR _s map toggle so
    // "specular off" is diffuse-only and avoids view-dependent RGBA8 banding on flat faces.
    BrdfResult br = cookTorrance(N, V, L, albedoLinear, mat.f0, mat.roughness, mat.metallic);
    float groundSpecFade = groundSpecularReceiverFade(vWorldPos, N, V);
#if defined(GENESIS_ENABLE_SPECULAR_MAP)
    float specLobe = 1.0;
#else
    float specLobe = 0.0;
#endif
    br.specular *= groundSpecFade * specLobe;
    vec3 direct = (br.diffuse + br.specular) * uLightColor * lightVis;

    // Subsurface scattering contribution (gated; cheap front-wrap + back-translucency).
#if defined(GENESIS_ENABLE_SSS)
    if (mat.sssAmount > 0.0)
    {
        float sssScale = uSssStrength * lightVis;
        direct += sssWrappedDiffuse(N, L, albedoLinear, mat.sssAmount, uLightColor) * sssScale;
        direct += sssTransmission(V, L, albedoLinear, mat.sssAmount, uLightColor) * sssScale;
    }
#endif

    // Indirect lighting: environment IBL (LUT + sun when atmospheric sky is on).
    vec3 indirect = vec3(0.0);
    float metalBaseVis = mat.metallic * metalPreviewBaseVisibility(mat.roughness);
#if defined(GENESIS_ENABLE_IBL)
    PreviewEnvCtx iblCtx = buildPreviewEnvCtx(uLightDir, uLightColor, uAtmosphereSunIntensity, uSkyTint, uGroundTint);
    vec3 iblProbe = previewAmbientProbeIrradianceCtx(iblCtx, N, uEnableAtmosphericSky, uAtmoSkyViewLut);
    float dielectricDiffuseVisibility = 1.0 - mat.metallic;
    // The preview has no captured scene cubemap. Keep a small irradiance-backed base
    // for LabPBR metal IDs so valid G>=230 pixels do not crush to black off-highlight.
    vec3 metalPreviewIrradiance = max(iblProbe, vec3(uAmbient * 0.75));
    indirect += iblProbe * albedoLinear * dielectricDiffuseVisibility * uIblStrength;
    indirect += metalPreviewIrradiance * albedoLinear * metalBaseVis * uIblStrength;
    indirect += fakeIblSpecularWithProbe(iblCtx, iblProbe, N, V, mat.f0, mat.roughness, mat.metallic,
        uEnableAtmosphericSky, uAtmoSkyViewLut) * uIblStrength * groundSpecFade * specLobe;
#else
    // Constant ambient fallback so previews are not pitch-black with IBL off.
    float dielectricAmbientVisibility = 1.0 - mat.metallic;
    indirect = albedoLinear * uAmbient * (dielectricAmbientVisibility + metalBaseVis);
#endif

    // Parallax contact AO mostly darkens indirect/cavity light while keeping direct lobe readable.
    indirect *= pomAo;
    direct *= mix(1.0, pomAo, 0.22);

    // Emission (LabPBR _s.a additive).
    vec3 emission = albedoLinear * mat.emissionStrength * uEmissionStrength;

    vec3 hdr = (direct + indirect + emission) * uExposure;
    // Aerial fog colors are authored for post-ACES mixing (SDR). Apply fog there for both
    // paths; HDR inverts ACES so presentEncodeScRgb's tonemap restores the same midtones.
    vec3 foggedMapped = applyTerrainAerialFog(tonemapAcesNarkowicz(hdr));
    vec3 outRgb;
    if (uHdrPresent > 0)
    {
        outRgb = inverseTonemapAcesNarkowicz(foggedMapped);
    }
    else
    {
        outRgb = ditherSrgb8(linearToSrgb(foggedMapped), gl_FragCoord.xy);
    }

    float a = 1.0;
    if (uSceneKind == 1 && uItemAlphaBlend > 0)
    {
        a = alb.a;
    }
    else if (genesisEntityAlphaMode(uEntityAlphaMode) == 2)
    {
        a = alb.a;
    }

    float alphaSpan = max(fwidth(alb.a), 1.0 / 255.0);
    float alphaEdge = 1.0 - smoothstep(1.0, 4.0, abs(alb.a - uAlphaCutoff) / alphaSpan);
    float reactivity = 0.0;
    reactivity = max(reactivity, uIsGroundPass > 0 ? 0.08 : 0.0);
    reactivity = max(reactivity, uSceneKind == 1 ? 0.25 : 0.0);
    reactivity = max(reactivity, genesisEntityAlphaMode(uEntityAlphaMode) == 1 ? mix(0.20, 0.72, alphaEdge) : 0.0);
    reactivity = max(reactivity, genesisEntityAlphaMode(uEntityAlphaMode) == 2 ? max(0.65, 1.0 - alb.a) : 0.0);

    FragColor = vec4(outRgb, a);
    float motion = 0.0;
    if (vCurrClip.w > 1e-6 && vPrevClip.w > 1e-6)
    {
        vec2 currUv = (vCurrClip.xy / vCurrClip.w) * 0.5 + 0.5;
        vec2 prevUv = (vPrevClip.xy / vPrevClip.w) * 0.5 + 0.5;
        motion = clamp(length(currUv - prevUv) * 64.0, 0.0, 1.0);
    }

    TaaSignal = vec4(clamp(reactivity, 0.0, 1.0), 1.0, motion, 1.0);
}
