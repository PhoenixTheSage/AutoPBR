#version 330 core
// Separable depth-aware bilateral blur for screen-space AO.

//!include "common/screen_space_ao_common.glsl"

in vec2 vUv;

uniform sampler2D uAoSource;
uniform sampler2D uSceneDepth;
uniform vec2 uAoTexelSize;
uniform vec2 uBlurDirection;
uniform float uDepthSigma;
uniform int uHasSceneDepth;

out vec4 FragColor;

void main()
{
    float centerAo = texture(uAoSource, vUv).r;
    float outAo = centerAo;
    if (uHasSceneDepth > 0)
    {
        float centerDepth = texture(uSceneDepth, vUv).r;
        if (centerDepth < 1.0 - SSAO_SKY_DEPTH_EPS)
        {
            float wSum = 1.0;
            float aoSum = centerAo;
            // 5-tap separable kernel.
            float offsets[2];
            offsets[0] = 1.0;
            offsets[1] = 2.0;
            float weights[2];
            weights[0] = 0.25;
            weights[1] = 0.125;
            for (int i = 0; i < 2; i++)
            {
                vec2 offset = uBlurDirection * uAoTexelSize * offsets[i];
                vec2 uv0 = vUv + offset;
                vec2 uv1 = vUv - offset;
                float d0 = texture(uSceneDepth, uv0).r;
                float d1 = texture(uSceneDepth, uv1).r;
                float w0 = weights[i] * exp(-abs(d0 - centerDepth) * uDepthSigma);
                float w1 = weights[i] * exp(-abs(d1 - centerDepth) * uDepthSigma);
                aoSum += texture(uAoSource, uv0).r * w0;
                aoSum += texture(uAoSource, uv1).r * w1;
                wSum += w0 + w1;
            }

            outAo = aoSum / max(wSum, 1e-4);
        }
    }

    FragColor = vec4(outAo, outAo, outAo, 1.0);
}
