#version 330 core
// Full-res bilateral upsample of half-res atmospheric fill / god rays + temporal reprojection.
// RGB = in-scatter, A = transmittance (1 = no extinction). Sky and geometry use separate tap
// selection so bilateral depth mixing cannot bleed a dark frustum into the dome.

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

vec4 bilateralUpsample(vec2 uv, float centerDepth, bool centerIsSky)
{
    vec3 accumRgb = vec3(0.0);
    float accumT = 0.0;
    float wSum = 0.0;

    for (int oy = 0; oy <= 1; ++oy)
    {
        for (int ox = 0; ox <= 1; ++ox)
        {
            vec2 offset = vec2(float(ox) - 0.5, float(oy) - 0.5) * uHalfResTexelSize;
            vec2 tapUv = clamp(uv + offset, vec2(0.001), vec2(0.999));
            float tapDepth = texture(uSceneDepth, tapUv).r;
            bool tapIsSky = tapDepth >= SKY_DEPTH_EPS;
            // Never mix sky and geometry taps — that produced the dark rectangular frustum.
            if (tapIsSky != centerIsSky)
            {
                continue;
            }

            float depthW = centerIsSky ? 1.0 : exp(-abs(tapDepth - centerDepth) * 1400.0);
            vec4 tapRays = texture(uHalfResRays, tapUv);
            accumRgb += tapRays.rgb * depthW;
            accumT += tapRays.a * depthW;
            wSum += depthW;
        }
    }

    if (wSum <= 1e-4)
    {
        vec4 center = texture(uHalfResRays, uv);
        return center;
    }

    return vec4(accumRgb / wSum, accumT / wSum);
}

void main()
{
    float depth = texture(uSceneDepth, vUv).r;
    bool isSky = depth >= SKY_DEPTH_EPS;
    vec4 current = bilateralUpsample(vUv, depth, isSky);
    vec3 finalRays = current.rgb;
    float finalT = current.a;

    if (uHasHistory > 0)
    {
        vec2 prevUv = trReprojectUvFromDepth(vUv, isSky ? 0.9995 : depth, uInvViewProj, uPrevViewProj);
        if (trPrevUvOnScreen(prevUv))
        {
            vec4 history = texture(uHistory, prevUv);
            float histDepth = texture(uSceneDepth, prevUv).r;
            float depthValid = isSky
                ? step(SKY_DEPTH_EPS, histDepth)
                : trDepthDisocclusionWeight(depth, histDepth, 0.002, 0.02);
            float reactive = trLuminanceReactiveWeight(current.rgb, history.rgb);
            float blend = uTemporalWeight * depthValid * reactive;
            finalRays = mix(finalRays, history.rgb, blend);
            finalT = mix(finalT, history.a, blend);
        }
    }

    float luma = max(max(finalRays.r, finalRays.g), finalRays.b);
    if (luma <= 1e-6 && finalT >= 0.999)
    {
        discard;
    }

    FragColor = vec4(finalRays, finalT);
}
