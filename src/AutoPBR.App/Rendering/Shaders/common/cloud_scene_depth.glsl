// Shared scene-depth contract for cloud tracing and full-resolution reconstruction.

#ifndef GENESIS_CLOUD_SCENE_DEPTH_GLSL
#define GENESIS_CLOUD_SCENE_DEPTH_GLSL

const float CSD_NO_SCENE_HIT = 1e9;
const float CSD_CLEAR_DEPTH_EPS = 1e-7;

bool csdHasOpaqueDepth(float depth, int hasSceneDepth)
{
    // A sampled D24 clear is exactly 1.0. A broad "near one" threshold incorrectly
    // classifies distant terrain as sky when the camera's near plane is small.
    return hasSceneDepth > 0 && depth < 1.0 - CSD_CLEAR_DEPTH_EPS;
}

float csdSceneRayDistanceFromDepth(
    float depth,
    vec2 uv,
    mat4 invViewProj,
    vec3 cameraPos,
    vec3 rayDir,
    int hasSceneDepth)
{
    if (!csdHasOpaqueDepth(depth, hasSceneDepth))
    {
        return CSD_NO_SCENE_HIT;
    }

    vec3 scenePos = grWorldPosFromUvDepth(uv, depth, invViewProj);
    float distanceAlongRay = dot(scenePos - cameraPos, rayDir);
    // Reject only invalid reconstruction, not legitimate distant stage geometry. The
    // earlier fixed 240-unit cutoff disagreed with the dynamic camera projection.
    return distanceAlongRay > 1e-3 && distanceAlongRay < 1e8
        ? distanceAlongRay
        : CSD_NO_SCENE_HIT;
}

float csdSceneRayDistance(
    sampler2D sceneDepth,
    vec2 uv,
    mat4 invViewProj,
    vec3 cameraPos,
    vec3 rayDir,
    int hasSceneDepth)
{
    return csdSceneRayDistanceFromDepth(
        texture(sceneDepth, uv).r,
        uv,
        invViewProj,
        cameraPos,
        rayDir,
        hasSceneDepth);
}

float csdCloudDepthBias(float distanceToScene)
{
    return max(0.04, distanceToScene * 0.002);
}

float csdCloudInFrontOfScene(float distanceToCloud, float distanceToScene)
{
    if (distanceToScene >= CSD_NO_SCENE_HIT * 0.5)
    {
        return 1.0;
    }

    return distanceToCloud < distanceToScene - csdCloudDepthBias(distanceToScene)
        ? 1.0
        : 0.0;
}

#endif // GENESIS_CLOUD_SCENE_DEPTH_GLSL
