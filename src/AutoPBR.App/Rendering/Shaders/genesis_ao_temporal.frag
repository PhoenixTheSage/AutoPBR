#version 330 core
// Temporal accumulation for screen-space AO history.

//!include "common/screen_space_ao_common.glsl"
//!include "common/ray_reconstruct.glsl"

in vec2 vUv;

uniform sampler2D uAoCurrent;
uniform sampler2D uAoHistory;
uniform sampler2D uSceneDepth;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;
uniform vec3 uCameraPos;
uniform float uTemporalWeight;
uniform int uHasHistory;
uniform int uHasSceneDepth;

out vec4 FragColor;

void main()
{
    float current = texture(uAoCurrent, vUv).r;
    float outAo = current;
    if (uHasHistory > 0 && uHasSceneDepth > 0)
    {
        float depth = texture(uSceneDepth, vUv).r;
        if (depth < 1.0 - SSAO_SKY_DEPTH_EPS)
        {
            vec3 worldPos = grWorldPosFromUvDepth(vUv, depth, uInvViewProj);
            vec4 prevClip = uPrevViewProj * vec4(worldPos, 1.0);
            vec2 prevUv = (prevClip.xy / max(prevClip.w, 1e-6)) * 0.5 + 0.5;
            if (prevUv.x > 0.0 && prevUv.x < 1.0 && prevUv.y > 0.0 && prevUv.y < 1.0 && prevClip.w > 0.0)
            {
                float history = texture(uAoHistory, prevUv).r;
                float w = clamp(uTemporalWeight, 0.0, 0.95);
                // Neighborhood clamp against current to limit ghosting.
                float nMin = current;
                float nMax = current;
                vec2 texel = 1.0 / vec2(textureSize(uAoCurrent, 0));
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        float s = texture(uAoCurrent, vUv + vec2(float(x), float(y)) * texel).r;
                        nMin = min(nMin, s);
                        nMax = max(nMax, s);
                    }
                }

                history = clamp(history, nMin, nMax);
                outAo = mix(current, history, w);
            }
        }
    }

    FragColor = vec4(outAo, outAo, outAo, 1.0);
}
