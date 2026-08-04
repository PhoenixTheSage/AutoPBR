#version 330 core

//!include "common/cloud_present.glsl"

in vec2 vUv;
uniform sampler2D uRays;
uniform sampler2D uCloudMask;
uniform int uHasCloudMask;
uniform int uCloudPresent;
uniform float uCloudExposure;
uniform int uHdrPresent;
uniform float uHdrPaperWhiteNits;
uniform float uHdrPeakNits;
uniform int uApplyCloudEncoding;
// 1 = RGB in-scatter + A transmittance (scene*T + inscatter via Blend One, SrcAlpha).
// 0 = legacy additive shafts (SrcAlpha, One with luma in A).
uniform int uTransmittanceComposite;
out vec4 FragColor;

void main()
{
    vec4 rays = texture(uRays, vUv);
    if (uTransmittanceComposite > 0)
    {
        float luma = max(max(rays.r, rays.g), rays.b);
        if (luma <= 1e-5 && rays.a >= 0.999)
        {
            discard;
        }
    }
    else if (rays.a <= 1e-5)
    {
        discard;
    }

    if (uHasCloudMask > 0)
    {
        float cloudA = texture(uCloudMask, vUv).a;
        rays.rgb *= 1.0 - smoothstep(0.02, 0.14, cloudA);
        if (uTransmittanceComposite > 0)
        {
            // Soften extinction slightly under opaque cloud so fill does not double-darken.
            rays.a = mix(rays.a, 1.0, smoothstep(0.02, 0.14, cloudA) * 0.35);
        }

        if (dot(rays.rgb, vec3(0.333333)) <= 1e-5 &&
            (uTransmittanceComposite <= 0 || rays.a >= 0.999))
        {
            discard;
        }
    }

    if (uCloudPresent > 0)
    {
        rays.rgb = cpEncodeCloudRadiance(
            rays.rgb,
            rays.a,
            uCloudExposure,
            uHdrPresent,
            uApplyCloudEncoding,
            uHdrPaperWhiteNits,
            uHdrPeakNits);
    }
    else
    {
        // Volume / SS shafts: display-referred encode for transmittance/additive blend onto
        // the already-presented FB (HDR soft-knee under paper-white; SDR soft-knee only).
        rays.rgb = cpEncodeShaftRadiance(
            rays.rgb,
            uHdrPresent,
            uHdrPaperWhiteNits,
            uHdrPeakNits);
        if (uTransmittanceComposite <= 0)
        {
            rays.a = saturate1(max(max(rays.r, rays.g), rays.b));
        }
    }

    FragColor = rays;
}
