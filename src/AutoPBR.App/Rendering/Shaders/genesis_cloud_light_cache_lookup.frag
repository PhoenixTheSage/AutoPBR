#version 330 core
// CQ3.3 fixed-world-position cache selection/fallback reference lookup.

//!include "common/cloud_light_cache.glsl"

in vec2 vUv;

uniform sampler2DArray uNearCache;
uniform sampler2DArray uFarCache;
uniform vec3 uWorldPosition;
uniform vec3 uBasisRight;
uniform vec3 uBasisUp;
uniform vec3 uBasisForward;
uniform vec2 uNearPlaneCenter;
uniform vec2 uFarPlaneCenter;
uniform float uNearWorldSpan;
uniform float uFarWorldSpan;
uniform float uNearLightDepthMin;
uniform float uFarLightDepthMin;
uniform float uNearLightDepthSpan;
uniform float uFarLightDepthSpan;
uniform int uNearDepth;
uniform int uFarDepth;
uniform float uNearOverlapFraction;
uniform int uHasNear;
uniform int uHasFar;

layout(location = 0) out vec4 FragColor;

void main()
{
    vec3 weights;
    vec3 cacheLighting = cqlResolveLighting(
        uNearCache,
        uFarCache,
        uWorldPosition,
        uBasisRight,
        uBasisUp,
        uBasisForward,
        uNearPlaneCenter,
        uFarPlaneCenter,
        uNearWorldSpan,
        uFarWorldSpan,
        uNearLightDepthMin,
        uFarLightDepthMin,
        uNearLightDepthSpan,
        uFarLightDepthSpan,
        uNearDepth,
        uFarDepth,
        uNearOverlapFraction,
        uHasNear,
        uHasFar,
        weights);
    FragColor = vec4(cacheLighting.xy, weights.xy);
}
