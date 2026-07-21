#version 330 core
// Final present encode: copy scene color with SDR (passthrough of already-encoded)
// or HDR scRGB encode from linear scene-referred buffers.
// Y orientation for DXGI is corrected by a full-framebuffer flip after overlays.

//!include "common/present_encode.glsl"

in vec2 vUv;
uniform sampler2D uSceneColor;
uniform int uHdrPresent;
uniform int uSceneIsLinear;
uniform float uHdrPaperWhiteNits;
uniform float uHdrPeakNits;
out vec4 FragColor;

void main()
{
    vec4 scene = texture(uSceneColor, vUv);
    vec3 rgb = scene.rgb;
    if (uHdrPresent > 0)
    {
        vec3 linear = uSceneIsLinear > 0 ? max(rgb, vec3(0.0)) : srgbToLinear(rgb);
        rgb = presentEncodeScRgb(linear, uHdrPaperWhiteNits, uHdrPeakNits);
    }
    FragColor = vec4(rgb, scene.a);
}
