#version 330 core
// CQ3.1 fixed-world-position reference lookup; production cloud consumption starts in CQ3.3.

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

layout(location = 0) out vec4 FragColor;

void main()
{
    vec3 nearUnit = cqlWorldToUnit(
        uWorldPosition,
        uBasisRight,
        uBasisUp,
        uBasisForward,
        uNearPlaneCenter,
        uNearWorldSpan,
        uNearLightDepthMin,
        uNearLightDepthSpan);
    vec3 farUnit = cqlWorldToUnit(
        uWorldPosition,
        uBasisRight,
        uBasisUp,
        uBasisForward,
        uFarPlaneCenter,
        uFarWorldSpan,
        uFarLightDepthMin,
        uFarLightDepthSpan);
    vec3 weights = cqlCascadeWeights(
        nearUnit,
        farUnit,
        uNearOverlapFraction);
    vec2 nearValue = weights.x > 0.0
        ? cqlSampleCascadeExplicitDepth(uNearCache, nearUnit, uNearDepth)
        : vec2(0.0, 1.0);
    vec2 farValue = weights.y > 0.0
        ? cqlSampleCascadeExplicitDepth(uFarCache, farUnit, uFarDepth)
        : vec2(0.0, 1.0);
    vec2 cacheValue = nearValue * weights.x + farValue * weights.y;
    FragColor = vec4(cacheValue, weights.xy);
}
