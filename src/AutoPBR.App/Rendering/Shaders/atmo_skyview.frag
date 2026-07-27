#version 330 core
//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/sky_dome.glsl"

in vec2 vUv;
uniform sampler2D uTransmittanceLut;
uniform vec3 uSunDir;
uniform float uTurbidity;
uniform float uSunIntensity;
uniform float uHorizonFalloff;
out vec4 FragColor;

void main()
{
    vec3 viewDir = skyViewDirFromLutUv(vUv);

    // Transmittance LUT varies with elevation only. Sampling it with azimuth UV previously
    // pulled ClampToEdge left/right columns apart at the sky-view wrap, baking a meridian seam
    // into cloud ambient / IBL even though both edge view directions are identical.
    vec3 trans = srgbToLinear(texture(uTransmittanceLut, vec2(0.5, clamp(vUv.y, 0.0, 1.0))).rgb);
    vec3 col = skyDayRadiance(viewDir, uSunDir, uSunIntensity, uTurbidity, uHorizonFalloff, 1.0);
    col *= mix(vec3(1.0), trans + vec3(0.06), 0.35);

    float dayAmt = skyDayFactor(uSunDir, uSunIntensity);
    vec3 nightSky = skyNightZenith(viewDir);
    col = mix(nightSky, col, dayAmt);
    // Store untonemapped linear radiance (sRGB-encoded for 8-bit precision); the runtime
    // sky pass applies the single luminance tonemap. A knee here would double-compress.
    FragColor = vec4(linearToSrgb(clamp(col, vec3(0.0), vec3(1.0))), 1.0);
}
