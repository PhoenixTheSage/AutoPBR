#version 330 core
// Depth-aware upsample of the half-res cloud target onto the full-res frame.
// Cloud rays are clipped to opaque scene depth, so geometry pixels remain valid when the
// camera is inside cloud. Four-tap reconstruction rejects taps across scene-depth edges.

//!include "common/cloud_temporal.glsl"
//!include "common/cloud_present.glsl"
//!include "common/cloud_shell.glsl"
//!include "common/ray_reconstruct.glsl"
//!include "common/cloud_scene_depth.glsl"

in vec2 vUv;
uniform sampler2D uClouds;
uniform sampler2D uCloudData;
uniform sampler2D uSceneDepth;
uniform vec2 uCloudTexelSize;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform float uGroundWorldY;
uniform float uPlanetRadius;
uniform int uHasSceneDepth;
uniform int uCloudDataDirect;
uniform float uCloudExposure;
uniform int uHdrPresent;
uniform int uApplyCloudEncoding;
uniform int uCloudSourceFullResolution;
out vec4 FragColor;

const float CLOUD_HORIZON_FEATHER = 0.0025;

float sceneDepthWeight(float centerDepth, vec2 tapUv)
{
    if (uHasSceneDepth < 1)
    {
        return 1.0;
    }

    float tapDepth = texture(uSceneDepth, tapUv).r;
    bool centerSky = !csdHasOpaqueDepth(centerDepth, uHasSceneDepth);
    bool tapSky = !csdHasOpaqueDepth(tapDepth, uHasSceneDepth);
    if (centerSky != tapSky)
    {
        return 0.0;
    }

    return centerSky ? 1.0 : exp(-abs(centerDepth - tapDepth) * 420.0);
}

float cloudTapSceneVisibility(float sceneDistance, vec2 tapUv)
{
    vec4 cloudData = texture(uCloudData, tapUv);
    if (!ctMetadataValid(cloudData, uCloudDataDirect))
    {
        return 0.0;
    }

    return csdCloudInFrontOfScene(
        ctMetadataDistance(cloudData, uCloudDataDirect), sceneDistance);
}

float cloudPlanetReconstructionMask(vec3 rayDir)
{
    float planetRadius = max(uPlanetRadius, 1.0);
    vec3 planetCenter = vec3(0.0, uGroundWorldY - planetRadius, 0.0);
    float planetDistance = vcsPlanetOcclusionDistance(
        uCameraPos, rayDir, planetCenter, planetRadius);
    vec4 cloudData = texture(uCloudData, vUv);

    if (planetDistance < 1e8 &&
        (!ctMetadataValid(cloudData, uCloudDataDirect) ||
            ctMetadataDistance(cloudData, uCloudDataDirect) >= planetDistance -
            max(0.04, planetDistance * 0.002)))
    {
        float horizonVisibility = vcsPlanetHorizonVisibility(
            uCameraPos, rayDir, planetCenter, planetRadius, CLOUD_HORIZON_FEATHER);
        // Trace/history already contain the atmospheric horizon fade. Reconstruction only
        // rejects fully hidden far-side samples; applying the full visibility again creates
        // a quarter-opacity stripe at the tangent.
        return horizonVisibility > 1e-4 ? 1.0 : 0.0;
    }

    return 1.0;
}

void main()
{
    vec2 o = uCloudSourceFullResolution > 0
        ? vec2(0.0)
        : uCloudTexelSize * 0.5;
    vec2 uv0 = vUv + vec2(-o.x, -o.y);
    vec2 uv1 = vUv + vec2(o.x, -o.y);
    vec2 uv2 = vUv + vec2(-o.x, o.y);
    vec2 uv3 = vUv + vec2(o.x, o.y);
    vec4 c0 = texture(uClouds, uv0);
    vec4 c1 = texture(uClouds, uv1);
    vec4 c2 = texture(uClouds, uv2);
    vec4 c3 = texture(uClouds, uv3);
    float centerDepth = uHasSceneDepth > 0 ? texture(uSceneDepth, vUv).r : 1.0;
    vec3 rayDir = grWorldRayDir(vUv, uInvViewProj, uCameraPos);
    float sceneDistance = csdSceneRayDistanceFromDepth(
        centerDepth, vUv, uInvViewProj, uCameraPos, rayDir, uHasSceneDepth);
    float planetMask = cloudPlanetReconstructionMask(rayDir);
    if (planetMask <= 1e-3)
    {
        discard;
    }

    // Scene rejection is per cloud tap. A single nearest metadata fetch no longer rejects
    // the entire four-tap reconstruction at terrain and subject silhouettes.
    float w0 = sceneDepthWeight(centerDepth, uv0) * cloudTapSceneVisibility(sceneDistance, uv0);
    float w1 = sceneDepthWeight(centerDepth, uv1) * cloudTapSceneVisibility(sceneDistance, uv1);
    float w2 = sceneDepthWeight(centerDepth, uv2) * cloudTapSceneVisibility(sceneDistance, uv2);
    float w3 = sceneDepthWeight(centerDepth, uv3) * cloudTapSceneVisibility(sceneDistance, uv3);
    float wSum = w0 + w1 + w2 + w3;

    if (wSum <= 1e-5)
    {
        discard;
    }

    float coverage = (c0.a * w0 + c1.a * w1 + c2.a * w2 + c3.a * w3) / wSum;
    if (coverage <= 0.03)
    {
        discard;
    }

    // Cloud RGB is premultiplied integrated radiance, so filter it directly with opacity.
    vec3 rgb = (c0.rgb * w0 + c1.rgb * w1 + c2.rgb * w2 + c3.rgb * w3) / wSum;
    vec3 presentedRgb = cpEncodeCloudRadiance(
        rgb, coverage, uCloudExposure, uHdrPresent, uApplyCloudEncoding);
    FragColor = vec4(presentedRgb, coverage) * planetMask;
}
