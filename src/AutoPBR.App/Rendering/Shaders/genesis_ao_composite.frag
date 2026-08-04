#version 330 core
// Present scene color multiplied by screen-space AO (HDR/SDR encode).

//!include "common/present_encode.glsl"
//!include "common/screen_space_ao_common.glsl"

in vec2 vUv;

uniform sampler2D uSceneColor;
uniform sampler2D uAo;
uniform sampler2D uViewNormal;
uniform int uHdrPresent;
uniform int uSceneIsLinear;
uniform float uHdrPaperWhiteNits;
uniform float uHdrPeakNits;
uniform float uAoStrength;
uniform int uHasAo;
uniform int uHasViewNormal;
uniform int uAoDebugView;

out vec4 FragColor;

void main()
{
    vec4 scene = texture(uSceneColor, vUv);
    vec3 rgb = scene.rgb;
    float ao = 1.0;
    if (uHasAo > 0)
    {
        ao = texture(uAo, vUv).r;
    }

    float applied = mix(1.0, ao, clamp(uAoStrength, 0.0, 1.0));
    vec3 shaded = rgb * applied;

    if (uAoDebugView == 1)
    {
        shaded = vec3(ao);
    }
    else if (uAoDebugView == 2)
    {
        shaded = vec3(applied);
    }
    else if (uHdrPresent > 0)
    {
        vec3 linear = uSceneIsLinear > 0 ? max(shaded, vec3(0.0)) : srgbToLinear(shaded);
        shaded = presentEncodeScRgb(linear, uHdrPaperWhiteNits, uHdrPeakNits);
    }

    FragColor = vec4(shaded, scene.a);
}
