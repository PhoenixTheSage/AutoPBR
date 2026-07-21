#version 330 core
// Shadow-aware screen-space god rays (epipolar-style occlusion via shadow maps).

//!include "common/common.glsl"
//!include "common/godray_integration.glsl"
#ifdef GENESIS_GODRAY_SPARSE_MARCH
//!include "common/godray_march_sparse.glsl"
#endif

in vec2 vUv;uniform sampler2D uSceneDepth;
uniform sampler2DShadow uShadowMap;
uniform sampler2DShadow uShadowMapNear;
uniform sampler2DShadow uShadowMapMid;
uniform mat4 uInvViewProj;
uniform mat4 uLightViewProj;
uniform mat4 uLightViewProjNear;
uniform mat4 uLightViewProjMid;
uniform vec3 uCameraPos;
uniform vec2 uSunUv;
uniform float uSunDiscRadius;
uniform float uSunConeRadius;
uniform float uStrength;
uniform float uLayerHeight;
uniform float uVolumeHeight;
uniform float uCloudDensity;
uniform float uVolumeSize;
uniform float uGroundWorldY;
uniform float uFogSlabHeight;
uniform float uHeightFogStrength;
uniform vec2 uShadowTexelSize;
uniform vec2 uShadowTexelSizeNear;
uniform vec2 uShadowTexelSizeMid;
uniform float uShadowMinBias;
uniform int uEnableShadowMap;
uniform int uEnableShadowCascades;
uniform float uCascadeSplitDistance;
uniform float uCascadeMidSplitDistance;
uniform float uCascadeBlendWidth;
uniform float uShadowDistance;
uniform float uShadowFadeStart;
uniform int uEnableCloudAttenuation;

out vec4 FragColor;

const int GR_SAMPLES = 48;
const float SKY_DEPTH_EPS = 0.9992;

void main()
{
    if (uStrength <= 0.0)
    {
        discard;
    }

    float receiverDepth = texture(uSceneDepth, vUv).r;
    if (receiverDepth >= SKY_DEPTH_EPS)
    {
        discard;
    }

    vec2 toSun = uSunUv - vUv;
    float distFromSun = length(toSun);
    if (distFromSun > uSunConeRadius)
    {
        discard;
    }

    float layerTop = uLayerHeight + uVolumeHeight;
    float shaft = 0.0;
    float visibility = 1.0;
    const float decay = 0.90;
    const float weight = 1.0 / float(GR_SAMPLES);

#ifdef GENESIS_GODRAY_SPARSE_MARCH
    bool grCoarseOccluded = false;
    bool grCoarseWasSky = false;
    float grCoarseBeamFalloff = 1.0;
#endif

    for (int i = 0; i < GR_SAMPLES; ++i)
    {
#ifdef GENESIS_GODRAY_SPARSE_MARCH
        if (grSparseMarchSkipOddStepShadow(i, grCoarseBeamFalloff, grCoarseOccluded, grCoarseWasSky))
        {
            visibility *= decay;
            continue;
        }
#endif

        float t;
#ifdef GENESIS_GODRAY_SPARSE_MARCH
        t = grSparseMarchT(i, GR_SAMPLES);
#else
        t = float(i) / max(float(GR_SAMPLES - 1), 1.0);
#endif
        vec2 marchUv = mix(vUv, uSunUv, t);
        if (marchUv.x < 0.002 || marchUv.x > 0.998 || marchUv.y < 0.002 || marchUv.y > 0.998)
        {
            break;
        }

        float beamDist = (1.0 - t) * distFromSun;
        float beamFalloff = 1.0 - smoothstep(uSunDiscRadius, uSunConeRadius, beamDist);
        if (beamFalloff <= 0.01)
        {
            visibility *= decay;
            continue;
        }

        float sampleDepth = texture(uSceneDepth, marchUv).r;
        float expectedDepth = mix(receiverDepth, SKY_DEPTH_EPS, t);
        if (sampleDepth < expectedDepth - 0.0006)
        {
            visibility *= 0.30;
            if (visibility < 0.04)
            {
                break;
            }
            continue;
        }

        if (sampleDepth >= SKY_DEPTH_EPS)
        {
            vec3 worldPos = grMarchWorldPos(marchUv, sampleDepth, uInvViewProj, uCameraPos, uLayerHeight, layerTop);
            float lightVis = grShadowGateCascaded(worldPos, uCameraPos,
                uLightViewProjNear, uLightViewProjMid, uLightViewProj,
                uShadowMapNear, uShadowMapMid, uShadowMap,
                uShadowTexelSizeNear, uShadowTexelSizeMid, uShadowTexelSize, uShadowMinBias, uEnableShadowMap,
                uEnableShadowCascades, uCascadeSplitDistance, uCascadeMidSplitDistance, uCascadeBlendWidth,
                uShadowDistance, uShadowFadeStart);
            float cloudAtten = grCloudAttenuation(worldPos, uGroundWorldY, uFogSlabHeight, uLayerHeight, layerTop,
                uCloudDensity, uVolumeSize, uHeightFogStrength, uEnableCloudAttenuation);
            shaft += visibility * weight * beamFalloff * lightVis * cloudAtten;
        }

#ifdef GENESIS_GODRAY_SPARSE_MARCH
        if (mod(float(i), 2.0) < 0.5)
        {
            grCoarseOccluded = sampleDepth < expectedDepth - 0.0006;
            grCoarseWasSky = sampleDepth >= SKY_DEPTH_EPS;
            grCoarseBeamFalloff = beamFalloff;
        }
#endif

        visibility *= decay;
    }

    float sunProximity = 1.0 - smoothstep(uSunDiscRadius, uSunConeRadius, distFromSun);
    vec3 warmScatter = vec3(1.0, 0.94, 0.82);
    vec3 rays = warmScatter * shaft * sunProximity * uStrength * 4.5;
    rays = softKnee(rays, 0.45);

    float alpha = saturate1(max(max(rays.r, rays.g), rays.b));
    if (alpha <= 1e-5)
    {
        discard;
    }

    FragColor = vec4(rays, alpha);
}
