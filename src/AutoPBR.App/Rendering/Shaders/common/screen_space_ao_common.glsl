// Shared helpers for SSAO / GTAO screen-space ambient occlusion.

#ifndef GENESIS_SCREEN_SPACE_AO_COMMON_GLSL
#define GENESIS_SCREEN_SPACE_AO_COMMON_GLSL

//!include "ray_reconstruct.glsl"

const float SSAO_SKY_DEPTH_EPS = 1e-7;
const float SSAO_PI = 3.14159265358979323846;

bool ssaoHasOpaqueDepth(float depth)
{
    return depth < 1.0 - SSAO_SKY_DEPTH_EPS;
}

bool ssaoHasGeometry(float depth, float geometryMask)
{
    return geometryMask > 0.5 && ssaoHasOpaqueDepth(depth);
}

vec3 ssaoUnpackViewNormal(vec4 encoded)
{
    return normalize(encoded.xyz * 2.0 - 1.0);
}

vec3 ssaoViewPosFromUvDepth(vec2 uv, float depth, mat4 invProj)
{
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, uv.y * 2.0 - 1.0);
    float z = depth * 2.0 - 1.0;
    vec4 viewH = invProj * vec4(ndc, z, 1.0);
    return viewH.xyz / max(viewH.w, 1e-6);
}

vec3 ssaoViewNormalFromDepth(sampler2D depthTex, vec2 uv, mat4 invProj, vec2 texelSize)
{
    float d = texture(depthTex, uv).r;
    float dX = texture(depthTex, uv + vec2(texelSize.x, 0.0)).r;
    float dY = texture(depthTex, uv + vec2(0.0, texelSize.y)).r;
    vec3 p = ssaoViewPosFromUvDepth(uv, d, invProj);
    vec3 pX = ssaoViewPosFromUvDepth(uv + vec2(texelSize.x, 0.0), dX, invProj);
    vec3 pY = ssaoViewPosFromUvDepth(uv + vec2(0.0, texelSize.y), dY, invProj);
    vec3 n = cross(pX - p, pY - p);
    float len2 = max(dot(n, n), 1e-12);
    // Face toward the camera (view-space camera looks down -Z).
    n *= inversesqrt(len2);
    if (n.z > 0.0)
    {
        n = -n;
    }

    return n;
}

vec2 ssaoProjectViewToUv(vec3 viewPos, mat4 proj)
{
    vec4 clip = proj * vec4(viewPos, 1.0);
    vec2 ndc = clip.xy / max(clip.w, 1e-6);
    return ndc * 0.5 + 0.5;
}

float ssaoInterleavedGradientNoise(vec2 pixel, float frame)
{
    vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    return fract(magic.z * fract(dot(pixel + frame, magic.xy)));
}

float ssaoSpatialNoise(vec2 pixel, float frame)
{
    return ssaoInterleavedGradientNoise(pixel, frame);
}

vec2 ssaoRotate2(vec2 v, float angle)
{
    float s = sin(angle);
    float c = cos(angle);
    return vec2(c * v.x - s * v.y, s * v.x + c * v.y);
}

// Jimenez multi-bounce AO approximation with a neutral albedo for forward compositing.
float ssaoGtaoMultiBounce(float ao, vec3 albedo)
{
    vec3 a = max(albedo, vec3(0.0));
    vec3 x = max(vec3(ao), vec3(0.0));
    vec3 g = max(vec3(1.0) - a, vec3(0.0));
    vec3 a2 = a * a;
    vec3 b = 2.0404 * a - 0.3324;
    vec3 c = -4.7951 * a2 + 0.6417 * a + 0.7953;
    vec3 d = 2.7552 * a2 - 0.6903 * a;
    vec3 multi = max(x, ((x * a + b) * x + c) * x + d);
    return clamp(dot(multi, vec3(0.3333333)), 0.0, 1.0);
}

#endif // GENESIS_SCREEN_SPACE_AO_COMMON_GLSL
