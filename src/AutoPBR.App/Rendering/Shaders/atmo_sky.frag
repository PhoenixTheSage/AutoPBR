#version 330 core
//!include "common/common.glsl"
//!include "common/atmosphere.glsl"
//!include "common/sky_dome.glsl"
//!include "common/godray_integration.glsl"

in vec2 vUv;
uniform sampler2D uSkyViewLut;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform vec3 uLightDir;
uniform float uSunIntensity;
uniform float uHorizonFogStrength;
uniform float uSkyExposure;
uniform int uHdrPresent;
uniform float uSunDiscStrength;
uniform float uSunDiscBrightness;
uniform float uSunCosDiscEdge;
uniform float uMoonCosDiscEdge;
uniform float uRenderTime;
uniform float uViewportAspect;
uniform float uTurbidity;
uniform float uHorizonFalloff;
uniform float uSunDiscRadiusUv;
uniform float uGroundWorldY;
out vec4 FragColor;

// Matches atmo_skyview.frag (linear RGB) when the LUT bake is unavailable on GLES.
vec3 skyProceduralFromSun(vec3 viewDir, vec3 lightPropagationDir, float sunIntensity, float horizonBandScale)
{
    vec3 col = skyDayRadiance(viewDir, lightPropagationDir, sunIntensity, uTurbidity, uHorizonFalloff, horizonBandScale);
    float dayAmt = skyDayFactor(lightPropagationDir, sunIntensity);
    vec3 nightSky = skyNightZenith(viewDir);
    col = mix(nightSky, col, dayAmt);
    return max(col, vec3(0.0));
}

void main()
{
    vec3 viewDir = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    float dayAmt = skyDayFactor(uLightDir, uSunIntensity);
    float horizonBandScale = skyHorizonAltitudeFade(uCameraPos.y, uGroundWorldY);

    // Procedural radiance is continuous in view direction; the 2D sky-view LUT has an azimuth
    // seam on the -Z meridian that shows as a fixed world-space line when sampled per pixel.
    vec3 lutSky = skyProceduralFromSun(viewDir, uLightDir, uSunIntensity, horizonBandScale);

    float starAmt = 1.0 - smoothstep(0.22, 0.62, dayAmt);
    vec3 nightSky = skyNightZenith(viewDir) + skyStars(viewDir, uRenderTime) * starAmt;
    float sunElev = max(normalize(-uLightDir).y, 0.0);
    // Bounded tint: glow hue follows sun warmth but must not scale with raw sun
    // intensity, or the horizon glow whitewashes the blue sky at high intensities.
    float glowIllum = 0.7 + 0.3 * smoothstep(1.0, 12.0, uSunIntensity);
    vec3 sunTint = atmosphereSunWarmColor(glowIllum, sunElev);

    vec3 sky = mix(nightSky, lutSky, dayAmt);
    sky += skyHorizonGlow(viewDir, dayAmt, sunTint, horizonBandScale);
    sky += skyBelowHorizonFog(viewDir, uHorizonFogStrength, horizonBandScale);

    // Keep the half-set sun vivid (per-pixel horizon cut handles the geometry);
    // a hard dayAmt gate here made the disc pop out while still above the horizon.
    float sunVis = smoothstep(0.0, 0.06, dayAmt) * (0.35 + 0.65 * dayAmt);
    if (sunVis > 0.001)
    {
        sky += skySunDiscAureole(viewDir, uLightDir, uSunCosDiscEdge, uSunDiscRadiusUv,
            uSunDiscStrength, uSunDiscBrightness, uTurbidity) * sunVis;
    }

    sky *= uSkyExposure * 1.4;
    vec3 outRgb = max(sky, vec3(0.0));
    if (uHdrPresent <= 0)
    {
        outRgb = linearToSrgb(skyTonemapLum(outRgb));
    }
    FragColor = vec4(outRgb, 1.0);
}
