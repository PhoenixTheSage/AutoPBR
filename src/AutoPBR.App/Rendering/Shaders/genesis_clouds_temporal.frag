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
uniform sampler2D uHistoryCloudMoments;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;
uniform vec3 uCameraPos;
uniform vec3 uPrevCameraPos;
uniform vec2 uWindDelta;
uniform vec2 uCirrusWindDelta;
uniform vec2 uTexelSize;
uniform float uTemporalWeight;
uniform float uMomentSigma;
uniform float uMomentMinBand;
uniform float uHistoryConfidence;
uniform int uHasHistory;
uniform int uHasMoments;
uniform int uCloudDataDirect;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 FragCloudData;
layout(location = 2) out vec2 FragCloudMoments;

const float CLOUD_MOMENT_ALPHA_EPSILON = 1e-4;
const float CLOUD_MOMENT_MAX_LUMINANCE = 64.0;

vec2 cloudCurrentLuminanceMoments(vec4 cloud)
{
    if (cloud.a <= CLOUD_MOMENT_ALPHA_EPSILON)
    {
        return vec2(-1.0, 0.0);
    }

    vec3 unpremultiplied = clamp(
        cloud.rgb / max(cloud.a, CLOUD_MOMENT_ALPHA_EPSILON),
        vec3(0.0),
        vec3(CLOUD_MOMENT_MAX_LUMINANCE));
    float luminance = clamp(trLuminance(unpremultiplied), 0.0, CLOUD_MOMENT_MAX_LUMINANCE);
    return vec2(luminance, luminance * luminance);
}

bool cloudMomentsValid(vec2 moments)
{
    return moments.x >= 0.0 &&
        moments.x <= CLOUD_MOMENT_MAX_LUMINANCE &&
        moments.y >= 0.0 &&
        moments.y <= CLOUD_MOMENT_MAX_LUMINANCE * CLOUD_MOMENT_MAX_LUMINANCE + 1.0;
}

void cloudNeighborhood(
    out vec3 colorMin,
    out vec3 colorMax,
    out float alphaMin,
    out float alphaMax,
    out float unpremultipliedLuminanceMean)
{
    colorMin = vec3(1e6);
    colorMax = vec3(-1e6);
    alphaMin = 1.0;
    alphaMax = 0.0;
    float luminanceSum = 0.0;
    float luminanceCount = 0.0;
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
            if (tap.a > CLOUD_MOMENT_ALPHA_EPSILON)
            {
                vec3 unpremultiplied = clamp(
                    tap.rgb / tap.a,
                    vec3(0.0),
                    vec3(CLOUD_MOMENT_MAX_LUMINANCE));
                luminanceSum += trLuminance(unpremultiplied);
                luminanceCount += 1.0;
            }
        }
    }

    unpremultipliedLuminanceMean =
        luminanceCount > 0.0 ? luminanceSum / luminanceCount : 0.0;
}

vec3 cloudClipHistoryWithMoments(
    vec3 historyRgb,
    float historyAlpha,
    float neighborhoodMean,
    float luminanceBand,
    vec3 neighborhoodMin,
    vec3 neighborhoodMax)
{
    float historyLuminance = trLuminance(
        historyRgb / max(historyAlpha, CLOUD_MOMENT_ALPHA_EPSILON));
    float clippedLuminance = clamp(
        historyLuminance,
        max(neighborhoodMean - luminanceBand, 0.0),
        neighborhoodMean + luminanceBand);
    if (historyLuminance > 1e-5)
    {
        historyRgb *= clippedLuminance / historyLuminance;
    }

    // The moment band owns luminance; retain the current YCoCg neighborhood bounds for
    // chroma so a valid luminance history cannot drag a stale cloud hue across a boundary.
    vec3 historyYCoCg = trRgbToYCoCg(historyRgb);
    historyYCoCg.yz = clamp(historyYCoCg.yz, neighborhoodMin.yz, neighborhoodMax.yz);
    return clamp(trYCoCgToRgb(historyYCoCg), vec3(0.0), vec3(64.0));
}

void main()
{
    vec4 current = texture(uCurrentClouds, vUv);
    vec4 currentData = texture(uCurrentCloudData, vUv);
    FragColor = current;
    FragCloudData = currentData;
    vec2 currentMoments = cloudCurrentLuminanceMoments(current);
    if (!ctMetadataValid(currentData, uCloudDataDirect))
    {
        currentMoments = vec2(-1.0, 0.0);
    }
    FragCloudMoments = currentMoments;

    if (uHasHistory < 1 || uTemporalWeight <= 0.0 ||
        !ctMetadataValid(currentData, uCloudDataDirect))
    {
        return;
    }

    float currentDistance = ctMetadataDistance(currentData, uCloudDataDirect);
    float currentKind = ctMetadataKind(currentData, uCloudDataDirect);
    vec3 rayDir = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    vec3 currentAnchor = uCameraPos + rayDir * currentDistance;
    bool isCirrus = currentKind > 0.75;
    vec2 windDelta = isCirrus ? uCirrusWindDelta : uWindDelta;
    vec3 previousAnchor = currentAnchor + vec3(windDelta.x, 0.0, windDelta.y);
    vec2 previousUv = trReprojectUvFromWorld(previousAnchor, uPrevViewProj);
    if (!trPrevUvOnScreen(previousUv))
    {
        return;
    }

    vec4 history = texture(uHistoryClouds, previousUv);
    vec4 historyData = texture(uHistoryCloudData, previousUv);
    if (!ctMetadataValid(historyData, uCloudDataDirect))
    {
        return;
    }

    float expectedPreviousDistance = length(previousAnchor - uPrevCameraPos);
    float historyDistance = ctMetadataDistance(historyData, uCloudDataDirect);
    float historyKind = ctMetadataKind(historyData, uCloudDataDirect);
    float distanceError = abs(historyDistance - expectedPreviousDistance);
    float depthNear = max(1.5, expectedPreviousDistance * 0.008);
    float depthFar = max(6.0, expectedPreviousDistance * 0.035);
    float depthWeight = 1.0 - smoothstep(depthNear, depthFar, distanceError);

    float kindWeight = 1.0 - smoothstep(0.20, 0.45, abs(currentKind - historyKind));
    vec2 velocity = vUv - previousUv;
    float motionWeight = trMotionRejectionWeight(velocity, 0.025, 0.24);
    float borderWeight = trHistoryBorderWeight(previousUv, 0.035);

    vec3 neighborhoodMin;
    vec3 neighborhoodMax;
    float alphaMin;
    float alphaMax;
    float neighborhoodLuminanceMean;
    cloudNeighborhood(
        neighborhoodMin,
        neighborhoodMax,
        alphaMin,
        alphaMax,
        neighborhoodLuminanceMean);
    float clippedHistoryAlpha = clamp(
        history.a,
        max(alphaMin - 0.035, 0.0),
        min(alphaMax + 0.035, 1.0));
    if (history.a > CLOUD_MOMENT_ALPHA_EPSILON)
    {
        history.rgb *= clippedHistoryAlpha / history.a;
    }
    history.a = clippedHistoryAlpha;

    vec2 historyMoments = uHasMoments > 0
        ? texture(uHistoryCloudMoments, previousUv).rg
        : vec2(-1.0, 0.0);
    bool momentsValid = uHasMoments > 0 &&
        cloudMomentsValid(currentMoments) &&
        cloudMomentsValid(historyMoments);
    float momentVariance = 0.0;
    float luminanceBand = 0.0;
    vec2 clippedHistoryMoments = historyMoments;
    if (momentsValid)
    {
        momentVariance = max(
            historyMoments.y - historyMoments.x * historyMoments.x,
            0.0);
        luminanceBand = max(
            uMomentMinBand,
            max(uMomentSigma, 0.0) * sqrt(momentVariance));
        history.rgb = cloudClipHistoryWithMoments(
            history.rgb,
            history.a,
            neighborhoodLuminanceMean,
            luminanceBand,
            neighborhoodMin,
            neighborhoodMax);

        float clippedMomentMean = clamp(
            historyMoments.x,
            max(neighborhoodLuminanceMean - luminanceBand, 0.0),
            neighborhoodLuminanceMean + luminanceBand);
        clippedHistoryMoments = vec2(
            clippedMomentMean,
            clippedMomentMean * clippedMomentMean + momentVariance);
    }
    else
    {
        history.rgb = trClipHistoryToNeighborhoodYCoCg(
            history.rgb, current.rgb, neighborhoodMin, neighborhoodMax);
    }

    float coverageAgreement = 1.0 - smoothstep(0.08, 0.42, abs(current.a - history.a));
    float luminanceAgreement = trLuminanceReactiveWeight(current.rgb, history.rgb);
    float reactiveWeight = mix(0.35, 1.0, min(coverageAgreement, luminanceAgreement));
    float confidenceWeight = momentsValid ? clamp(uHistoryConfidence, 0.0, 1.0) : 1.0;
    float historyWeight = clamp(uTemporalWeight * depthWeight * kindWeight * motionWeight *
        borderWeight * reactiveWeight * confidenceWeight, 0.0, 0.92);

    FragColor = mix(current, history, historyWeight);
    if (momentsValid)
    {
        // Reuse the fully rejected color-history weight: depth, kind, motion, border,
        // coverage, reactive response, and confidence all gate moment persistence.
        FragCloudMoments = mix(currentMoments, clippedHistoryMoments, historyWeight);
    }
}
