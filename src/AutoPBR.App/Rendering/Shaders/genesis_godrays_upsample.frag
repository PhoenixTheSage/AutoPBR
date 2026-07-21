#version 330 core
// Full-res bilateral upsample of half-res god rays + temporal reprojection (P1).

//!include "common/common.glsl"
//!include "common/temporal_reproject.glsl"

in vec2 vUv;
uniform sampler2D uHalfResRays;
uniform sampler2D uSceneDepth;
uniform sampler2D uHistory;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;
uniform vec2 uHalfResTexelSize;
uniform float uTemporalWeight;
uniform int uHasHistory;

out vec4 FragColor;

const float SKY_DEPTH_EPS = 0.9992;

vec3 bilateralUpsample(vec2 uv, float centerDepth)
{
    vec3 accum = vec3(0.0);
    float wSum = 0.0;

    for (int oy = 0; oy <= 1; ++oy)
    {
        for (int ox = 0; ox <= 1; ++ox)
        {
            vec2 offset = vec2(float(ox) - 0.5, float(oy) - 0.5) * uHalfResTexelSize;
            vec2 tapUv = clamp(uv + offset, vec2(0.001), vec2(0.999));
            float tapDepth = texture(uSceneDepth, tapUv).r;
            float depthW = exp(-abs(tapDepth - centerDepth) * 1400.0);
            vec3 tapRays = texture(uHalfResRays, tapUv).rgb;
            accum += tapRays * depthW;
            wSum += depthW;
        }
    }

    return accum / max(wSum, 1e-4);
}

void main()
{
    float depth = texture(uSceneDepth, vUv).r;
    // Froxel integrate only marches receiver geometry; sky pixels must stay empty here.
    // Bilateral/temporal upsample otherwise bleeds shafts into the dome, and the cloud mask
    // then strips them unevenly (dark rectangular frustum over the cloud layer).
    if (depth >= SKY_DEPTH_EPS)
    {
        discard;
    }

    vec3 current = bilateralUpsample(vUv, depth);
    vec3 finalRays = current;

    if (uHasHistory > 0)
    {
        vec2 prevUv = trReprojectUvFromDepth(vUv, depth, uInvViewProj, uPrevViewProj);
        if (trPrevUvOnScreen(prevUv))
        {
            vec3 history = texture(uHistory, prevUv).rgb;
            float histDepth = texture(uSceneDepth, prevUv).r;
            float depthValid = trDepthDisocclusionWeight(depth, histDepth, 0.002, 0.02);
            float reactive = trLuminanceReactiveWeight(current, history);
            float blend = uTemporalWeight * depthValid * reactive;
            finalRays = mix(current, history, blend);
        }
    }

    float luma = max(max(finalRays.r, finalRays.g), finalRays.b);
    if (luma <= 1e-6)
    {
        discard;
    }

    FragColor = vec4(finalRays, luma);
}
