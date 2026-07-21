#version 330 core
// Cloud-specific half-resolution temporal reconstruction. History is reprojected through
// representative cloud distance, advected by wind, depth rejected, then neighborhood clipped.

//!include "common/common.glsl"
//!include "common/temporal_reproject.glsl"
//!include "common/cloud_temporal.glsl"
//!include "common/ray_reconstruct.glsl"

in vec2 vUv;
uniform sampler2D uCurrentClouds;
uniform sampler2D uCurrentCloudData;
uniform sampler2D uHistoryClouds;
uniform sampler2D uHistoryCloudData;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;
uniform vec3 uCameraPos;
uniform vec3 uPrevCameraPos;
uniform vec2 uWindDelta;
uniform vec2 uCirrusWindDelta;
uniform vec2 uTexelSize;
uniform float uTemporalWeight;
uniform int uHasHistory;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 FragCloudData;

void cloudNeighborhood(out vec3 colorMin, out vec3 colorMax, out float alphaMin, out float alphaMax)
{
    colorMin = vec3(1e6);
    colorMax = vec3(-1e6);
    alphaMin = 1.0;
    alphaMax = 0.0;
    for (int oy = -1; oy <= 1; ++oy)
    {
        for (int ox = -1; ox <= 1; ++ox)
        {
            vec2 tapUv = clamp(vUv + vec2(float(ox), float(oy)) * uTexelSize,
                vec2(0.001), vec2(0.999));
            vec4 tap = texture(uCurrentClouds, tapUv);
            vec3 ycocg = trRgbToYCoCg(tap.rgb);
            colorMin = min(colorMin, ycocg);
            colorMax = max(colorMax, ycocg);
            alphaMin = min(alphaMin, tap.a);
            alphaMax = max(alphaMax, tap.a);
        }
    }
}

void main()
{
    vec4 current = texture(uCurrentClouds, vUv);
    vec4 currentData = texture(uCurrentCloudData, vUv);
    FragColor = current;
    FragCloudData = currentData;

    if (uHasHistory < 1 || uTemporalWeight <= 0.0 || currentData.a < 0.5)
    {
        return;
    }

    float currentDistance = ctDecodeDistance(currentData.rg);
    vec3 rayDir = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    vec3 currentAnchor = uCameraPos + rayDir * currentDistance;
    bool isCirrus = currentData.b > 0.75;
    vec2 windDelta = isCirrus ? uCirrusWindDelta : uWindDelta;
    vec3 previousAnchor = currentAnchor + vec3(windDelta.x, 0.0, windDelta.y);
    vec2 previousUv = trReprojectUvFromWorld(previousAnchor, uPrevViewProj);
    if (!trPrevUvOnScreen(previousUv))
    {
        return;
    }

    vec4 history = texture(uHistoryClouds, previousUv);
    vec4 historyData = texture(uHistoryCloudData, previousUv);
    if (historyData.a < 0.5)
    {
        return;
    }

    float expectedPreviousDistance = length(previousAnchor - uPrevCameraPos);
    float historyDistance = ctDecodeDistance(historyData.rg);
    float distanceError = abs(historyDistance - expectedPreviousDistance);
    float depthNear = max(1.5, expectedPreviousDistance * 0.008);
    float depthFar = max(6.0, expectedPreviousDistance * 0.035);
    float depthWeight = 1.0 - smoothstep(depthNear, depthFar, distanceError);

    float kindWeight = 1.0 - smoothstep(0.20, 0.45, abs(currentData.b - historyData.b));
    vec2 velocity = vUv - previousUv;
    float motionWeight = trMotionRejectionWeight(velocity, 0.025, 0.24);
    float borderWeight = trHistoryBorderWeight(previousUv, 0.035);

    vec3 neighborhoodMin;
    vec3 neighborhoodMax;
    float alphaMin;
    float alphaMax;
    cloudNeighborhood(neighborhoodMin, neighborhoodMax, alphaMin, alphaMax);
    history.rgb = trClipHistoryToNeighborhoodYCoCg(
        history.rgb, current.rgb, neighborhoodMin, neighborhoodMax);
    history.a = clamp(history.a, max(alphaMin - 0.035, 0.0), min(alphaMax + 0.035, 1.0));

    float coverageAgreement = 1.0 - smoothstep(0.08, 0.42, abs(current.a - history.a));
    float luminanceAgreement = trLuminanceReactiveWeight(current.rgb, history.rgb);
    float reactiveWeight = mix(0.35, 1.0, min(coverageAgreement, luminanceAgreement));
    float historyWeight = clamp(uTemporalWeight * depthWeight * kindWeight * motionWeight *
        borderWeight * reactiveWeight, 0.0, 0.92);

    FragColor = mix(current, history, historyWeight);
}
