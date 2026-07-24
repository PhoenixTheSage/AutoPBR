// Heightfield column occupancy + Amanatides-Woo DDA for occlusion culling.
// Expects uniforms:
//   sampler2D uVoxelHeightAtlas; // RG: surfaceY, bottomY (relative block Y)
//   ivec2 uVoxelAtlasOrigin;     // world column of texel (0,0)
//   ivec2 uVoxelAtlasSize;
//   float uVoxelGroundPlaneY;
//   int uVoxelMaxSteps;

bool voxelColumnSolid(int worldX, int worldZ, int relativeLayerY)
{
    ivec2 tex = ivec2(worldX - uVoxelAtlasOrigin.x, worldZ - uVoxelAtlasOrigin.y);
    if (any(lessThan(tex, ivec2(0))) || any(greaterThanEqual(tex, uVoxelAtlasSize)))
    {
        return false;
    }

    vec2 column = texelFetch(uVoxelHeightAtlas, tex, 0).rg;
    float surface = column.r;
    float bottom = column.g;
    return float(relativeLayerY) >= bottom && float(relativeLayerY) <= surface;
}

bool voxelRayHitsSolidBefore(vec3 origin, vec3 direction, float maxDistance)
{
    float lenSq = dot(direction, direction);
    if (lenSq < 1e-12 || maxDistance <= 1e-5)
    {
        return false;
    }

    direction *= inversesqrt(lenSq);
    maxDistance = max(0.0, maxDistance - 1e-3);

    int x = int(floor(origin.x));
    int y = int(floor(origin.y));
    int z = int(floor(origin.z));

    int stepX = direction.x >= 0.0 ? 1 : -1;
    int stepY = direction.y >= 0.0 ? 1 : -1;
    int stepZ = direction.z >= 0.0 ? 1 : -1;

    float tDeltaX = direction.x != 0.0 ? abs(1.0 / direction.x) : 1e30;
    float tDeltaY = direction.y != 0.0 ? abs(1.0 / direction.y) : 1e30;
    float tDeltaZ = direction.z != 0.0 ? abs(1.0 / direction.z) : 1e30;

    float tMaxX = direction.x != 0.0
        ? ((direction.x >= 0.0 ? float(x + 1) : float(x)) - origin.x) / direction.x
        : 1e30;
    float tMaxY = direction.y != 0.0
        ? ((direction.y >= 0.0 ? float(y + 1) : float(y)) - origin.y) / direction.y
        : 1e30;
    float tMaxZ = direction.z != 0.0
        ? ((direction.z >= 0.0 ? float(z + 1) : float(z)) - origin.z) / direction.z
        : 1e30;

    float t = 0.0;
    // Cap work by ray length so distant batches cannot force a full 384-step march.
    int maxSteps = max(1, min(uVoxelMaxSteps, int(ceil(maxDistance)) + 2));
    for (int step = 0; step < maxSteps && t <= maxDistance; step++)
    {
        int relY = int(floor((float(y) + 0.5) - uVoxelGroundPlaneY));
        // Ignore the origin cell (t ~ 0) so a camera grazing solids does not occlude everything.
        if (t > 1e-4 && voxelColumnSolid(x, z, relY) && t < maxDistance)
        {
            return true;
        }

        if (tMaxX < tMaxY)
        {
            if (tMaxX < tMaxZ)
            {
                t = tMaxX;
                tMaxX += tDeltaX;
                x += stepX;
            }
            else
            {
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                z += stepZ;
            }
        }
        else if (tMaxY < tMaxZ)
        {
            t = tMaxY;
            tMaxY += tDeltaY;
            y += stepY;
        }
        else
        {
            t = tMaxZ;
            tMaxZ += tDeltaZ;
            z += stepZ;
        }
    }

    return false;
}

bool voxelSphereOccluded(vec3 camera, vec3 center, float radius)
{
    radius = max(0.0, radius * 0.85);
    vec3 toCenter = center - camera;
    float dist = length(toCenter);
    if (dist <= radius + 1e-3)
    {
        return false;
    }

    vec3 dir = toCenter / dist;
    vec3 nearPt = center - dir * radius;
    vec3 orthoA = abs(dir.y) < 0.9
        ? normalize(cross(dir, vec3(0.0, 1.0, 0.0)))
        : normalize(cross(dir, vec3(1.0, 0.0, 0.0)));
    vec3 orthoB = normalize(cross(dir, orthoA));

    vec3 samples[5];
    samples[0] = nearPt;
    samples[1] = nearPt + orthoA * (radius * 0.65);
    samples[2] = nearPt - orthoA * (radius * 0.65);
    samples[3] = nearPt + orthoB * (radius * 0.65);
    samples[4] = nearPt - orthoB * (radius * 0.65);

    for (int i = 0; i < 5; i++)
    {
        vec3 delta = samples[i] - camera;
        float sampleDist = length(delta);
        if (sampleDist < 1e-4)
        {
            return false;
        }

        if (!voxelRayHitsSolidBefore(camera, delta / sampleDist, sampleDist))
        {
            return false;
        }
    }

    return true;
}
