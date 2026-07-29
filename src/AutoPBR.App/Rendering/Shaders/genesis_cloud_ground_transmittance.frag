#version 330 core
// CQ3.5 publication pass. The output is a snapped light-plane 2D field representing
// full-column cloud transmittance at the ground-facing end of each sun ray.

//!include "common/cloud_light_cache.glsl"

in vec2 vUv;

uniform sampler2DArray uNearCache;
uniform sampler2DArray uFarCache;
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
uniform vec2 uOutputPlaneCenter;
uniform float uOutputWorldSpan;
uniform float uGroundWorldY;

layout(location = 0) out float GroundTransmittance;

void main()
{
    // The output grid is aligned to the committed far cascade. Reconstruct the
    // corresponding world-space point where that light ray intersects ground.
    vec2 lightPlane =
        (vUv - vec2(0.5)) * uOutputWorldSpan + uOutputPlaneCenter;
    vec3 planePoint =
        uBasisRight * lightPlane.x +
        uBasisUp * lightPlane.y;
    float forwardY = uBasisForward.y;
    if (abs(forwardY) < 1e-4)
    {
        GroundTransmittance = 1.0;
        return;
    }

    float lightDepth = (uGroundWorldY - planePoint.y) / forwardY;
    vec3 groundWorld = planePoint + uBasisForward * lightDepth;
    vec3 weights;
    vec3 cacheLighting = cqlResolveLighting(
        uNearCache,
        uFarCache,
        groundWorld,
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
    float opticalDepth = cacheLighting.x;
    if (cacheLighting.z <= 0.5 ||
        isnan(opticalDepth) ||
        isinf(opticalDepth))
    {
        GroundTransmittance = 1.0;
        return;
    }

    float transmittance = exp(-max(opticalDepth, 0.0));
    GroundTransmittance =
        isnan(transmittance) || isinf(transmittance)
            ? 1.0
            : clamp(transmittance, 0.0, 1.0);
}
