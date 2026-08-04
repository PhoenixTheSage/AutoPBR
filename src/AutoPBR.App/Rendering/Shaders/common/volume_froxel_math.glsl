// Camera-aligned froxel grid math (inject path - no sampler2DArray helpers).

#ifndef GENESIS_VOLUME_FROXEL_MATH_GLSL
#define GENESIS_VOLUME_FROXEL_MATH_GLSL

const int VF_MAX_SLICES = 32;

float vfSliceNorm01(int sliceIndex, int sliceCount, float depthExp)
{
    float zLin = (float(sliceIndex) + 0.5) / float(max(sliceCount, 1));
    if (depthExp <= 1e-4)
    {
        return zLin;
    }

    float e = exp(depthExp);
    return (exp(depthExp * zLin) - 1.0) / max(e - 1.0, 1e-5);
}

float vfForward01ToSliceCoord(float forward01, int sliceCount, float depthExp)
{
    forward01 = clamp(forward01, 0.0, 1.0);
    if (depthExp <= 1e-4)
    {
        return forward01 * float(sliceCount);
    }

    float zLin = log(1.0 + forward01 * (exp(depthExp) - 1.0)) / max(depthExp, 1e-4);
    return zLin * float(sliceCount);
}

vec3 vfFroxelWorldPos(vec2 uv01, int sliceIndex, int sliceCount, vec3 cameraPos,
    vec3 camRight, vec3 camUp, vec3 camForward, vec3 halfExtent, float depthExp)
{
    float z = vfSliceNorm01(sliceIndex, sliceCount, depthExp);
    vec3 local = vec3(
        (uv01.x * 2.0 - 1.0) * halfExtent.x,
        (uv01.y * 2.0 - 1.0) * halfExtent.y,
        z * halfExtent.z * 2.0);
    return cameraPos + camRight * local.x + camUp * local.y + camForward * local.z;
}

vec3 vfWorldToFroxelUv(vec3 worldPos, vec3 cameraPos, vec3 camRight, vec3 camUp, vec3 camForward,
    vec3 halfExtent, int sliceCount, float depthExp)
{
    vec3 local = worldPos - cameraPos;
    vec3 uv;
    uv.x = dot(local, camRight) / max(halfExtent.x, 1e-3) * 0.5 + 0.5;
    uv.y = dot(local, camUp) / max(halfExtent.y, 1e-3) * 0.5 + 0.5;
    float forward01 = dot(local, camForward) / max(halfExtent.z * 2.0, 1e-3);
    uv.z = vfForward01ToSliceCoord(forward01, sliceCount, depthExp);
    return uv;
}

// Soft falloff near froxel XY edges (replaces hard continue that caused visible screen seams).
// Wider band so camera-pitch seams at the bottom third of the view fade instead of cutting.
float vfFroxelEdgeWeight(vec3 froxelUv)
{
    if (froxelUv.z < 0.0)
    {
        return 0.0;
    }

    float wx = smoothstep(0.0, 0.12, froxelUv.x) * smoothstep(1.0, 0.88, froxelUv.x);
    float wy = smoothstep(0.0, 0.12, froxelUv.y) * smoothstep(1.0, 0.88, froxelUv.y);
    return wx * wy;
}

// Soft fade as samples approach the froxel far plane (forward01 in 0..1).
float vfFroxelFarWeight(float forward01)
{
    return 1.0 - smoothstep(0.82, 0.985, clamp(forward01, 0.0, 1.0));
}

#endif // GENESIS_VOLUME_FROXEL_MATH_GLSL
