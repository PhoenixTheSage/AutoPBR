#version 330 core

//!include "common/cloud_present.glsl"

in vec2 vUv;
uniform sampler2D uRays;
uniform sampler2D uCloudMask;
uniform int uHasCloudMask;
uniform int uCloudPresent;
uniform float uCloudExposure;
uniform int uHdrPresent;
uniform int uApplyCloudEncoding;
out vec4 FragColor;

void main()
{
    vec4 rays = texture(uRays, vUv);
    if (rays.a <= 1e-5)
    {
        discard;
    }

    if (uHasCloudMask > 0)
    {
        float cloudA = texture(uCloudMask, vUv).a;
        rays.rgb *= 1.0 - smoothstep(0.02, 0.14, cloudA);
        if (dot(rays.rgb, vec3(0.333333)) <= 1e-5)
        {
            discard;
        }
    }

    if (uCloudPresent > 0)
    {
        rays.rgb = cpEncodeCloudRadiance(
            rays.rgb, rays.a, uCloudExposure, uHdrPresent, uApplyCloudEncoding);
    }

    FragColor = rays;
}
