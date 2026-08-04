// CQ4.5 sparse cloud page lookup, cascade blending, and conservative-distance traversal.

#ifndef GENESIS_SPARSE_CLOUD_TRAVERSAL_GLSL
#define GENESIS_SPARSE_CLOUD_TRAVERSAL_GLSL

uniform sampler3D uSparseCloudAtlas;
uniform usampler3D uSparseCloudPageL0;
uniform usampler3D uSparseCloudPageL1;
uniform usampler3D uSparseCloudPageL2;
uniform ivec3 uSparseCloudOriginL0;
uniform ivec3 uSparseCloudOriginL1;
uniform ivec3 uSparseCloudOriginL2;
uniform int uHasSparseCloudTraversal;

const ivec3 CQ45_PAGE_DIMENSIONS = ivec3(32, 16, 32);
const int CQ45_LOGICAL_BRICK_SIZE = 8;
const int CQ45_PHYSICAL_BRICK_SIZE = 10;
const int CQ45_PHYSICAL_BORDER = 1;
const int CQ45_ATLAS_BRICKS_PER_AXIS = 16;
const float CQ45_ATLAS_TEXEL_SIZE = 160.0;
const uint CQ45_REQUESTED_PAGE = 65535u;
const int CQ45_MAX_TRAVERSAL_ITERATIONS = 64;
const float CQ45_CASCADE_BLEND_FRACTION = 0.10;

struct Cq45LevelSample
{
    float density;
    float distanceWorld;
    float voxelWorldSize;
    float edgeWeight;
    float faceWeight;
    float resident;
    float level;
    uint pageValue;
};

struct Cq45ResolvedBase
{
    float density;
    float safeDistanceWorld;
    float voxelWorldSize;
    float selectedLevel;
    float shellWeight;
    float resident;
};

struct Cq45TraversalResult
{
    float t;
    float density;
    float safeDistanceWorld;
    float voxelWorldSize;
    float selectedLevel;
    float shellWeight;
    float found;
    int pageSteps;
    int distanceSteps;
    int fineSteps;
    int fallbackQueries;
};

float cq45VoxelWorldSize(int level)
{
    return level == 0 ? 2.0 : (level == 1 ? 8.0 : 32.0);
}

ivec3 cq45Origin(int level)
{
    return level == 0
        ? uSparseCloudOriginL0
        : (level == 1 ? uSparseCloudOriginL1 : uSparseCloudOriginL2);
}

uint cq45FetchPage(int level, ivec3 localPage)
{
    if (level == 0)
    {
        return texelFetch(uSparseCloudPageL0, localPage, 0).r;
    }
    if (level == 1)
    {
        return texelFetch(uSparseCloudPageL1, localPage, 0).r;
    }
    return texelFetch(uSparseCloudPageL2, localPage, 0).r;
}

float cq45ClipmapEdgeWeight(vec3 pagePosition)
{
    vec3 edgePages = min(
        pagePosition,
        vec3(CQ45_PAGE_DIMENSIONS) - pagePosition);
    float edgeDistance = min(edgePages.x, min(edgePages.y, edgePages.z));
    float blendPages =
        float(min(
            CQ45_PAGE_DIMENSIONS.x,
            min(CQ45_PAGE_DIMENSIONS.y, CQ45_PAGE_DIMENSIONS.z))) *
        CQ45_CASCADE_BLEND_FRACTION;
    return smoothstep(0.0, max(blendPages, 0.001), edgeDistance);
}

float cq45PageMapped(uint pageValue)
{
    return pageValue > 0u &&
            pageValue != CQ45_REQUESTED_PAGE &&
            pageValue <= 4095u
        ? 1.0
        : 0.0;
}

float cq45FaceResidentFactor(
    int level,
    ivec3 localPage,
    vec3 brickLocalVoxel)
{
    // Fade this page's contribution over the outer voxel when the adjacent page is
    // unmapped/requested. Fully resident neighborhoods keep factor 1.
    float factor = 1.0;
    float fade = 1.25;
    vec3 local01 = brickLocalVoxel / float(CQ45_LOGICAL_BRICK_SIZE);

    if (local01.x < fade / float(CQ45_LOGICAL_BRICK_SIZE) && localPage.x > 0)
    {
        float mapped = cq45PageMapped(
            cq45FetchPage(level, localPage + ivec3(-1, 0, 0)));
        factor = min(
            factor,
            mix(smoothstep(0.0, fade, brickLocalVoxel.x), 1.0, mapped));
    }
    if (local01.x >
            1.0 - fade / float(CQ45_LOGICAL_BRICK_SIZE) &&
        localPage.x < CQ45_PAGE_DIMENSIONS.x - 1)
    {
        float mapped = cq45PageMapped(
            cq45FetchPage(level, localPage + ivec3(1, 0, 0)));
        factor = min(
            factor,
            mix(
                smoothstep(
                    0.0,
                    fade,
                    float(CQ45_LOGICAL_BRICK_SIZE) - brickLocalVoxel.x),
                1.0,
                mapped));
    }
    if (local01.y < fade / float(CQ45_LOGICAL_BRICK_SIZE) && localPage.y > 0)
    {
        float mapped = cq45PageMapped(
            cq45FetchPage(level, localPage + ivec3(0, -1, 0)));
        factor = min(
            factor,
            mix(smoothstep(0.0, fade, brickLocalVoxel.y), 1.0, mapped));
    }
    if (local01.y >
            1.0 - fade / float(CQ45_LOGICAL_BRICK_SIZE) &&
        localPage.y < CQ45_PAGE_DIMENSIONS.y - 1)
    {
        float mapped = cq45PageMapped(
            cq45FetchPage(level, localPage + ivec3(0, 1, 0)));
        factor = min(
            factor,
            mix(
                smoothstep(
                    0.0,
                    fade,
                    float(CQ45_LOGICAL_BRICK_SIZE) - brickLocalVoxel.y),
                1.0,
                mapped));
    }
    if (local01.z < fade / float(CQ45_LOGICAL_BRICK_SIZE) && localPage.z > 0)
    {
        float mapped = cq45PageMapped(
            cq45FetchPage(level, localPage + ivec3(0, 0, -1)));
        factor = min(
            factor,
            mix(smoothstep(0.0, fade, brickLocalVoxel.z), 1.0, mapped));
    }
    if (local01.z >
            1.0 - fade / float(CQ45_LOGICAL_BRICK_SIZE) &&
        localPage.z < CQ45_PAGE_DIMENSIONS.z - 1)
    {
        float mapped = cq45PageMapped(
            cq45FetchPage(level, localPage + ivec3(0, 0, 1)));
        factor = min(
            factor,
            mix(
                smoothstep(
                    0.0,
                    fade,
                    float(CQ45_LOGICAL_BRICK_SIZE) - brickLocalVoxel.z),
                1.0,
                mapped));
    }

    return factor;
}

Cq45LevelSample cq45SampleLevel(int level, vec3 worldPosition)
{
    Cq45LevelSample result;
    result.density = 0.0;
    result.distanceWorld = 0.0;
    result.voxelWorldSize = cq45VoxelWorldSize(level);
    result.edgeWeight = 0.0;
    result.faceWeight = 1.0;
    result.resident = 0.0;
    result.level = float(level);
    result.pageValue = 0u;

    float brickWorldSize =
        result.voxelWorldSize * float(CQ45_LOGICAL_BRICK_SIZE);
    vec3 logicalPagePosition = worldPosition / brickWorldSize;
    ivec3 logicalPage = ivec3(floor(logicalPagePosition));
    ivec3 localPage = logicalPage - cq45Origin(level);
    if (any(lessThan(localPage, ivec3(0))) ||
        any(greaterThanEqual(localPage, CQ45_PAGE_DIMENSIONS)))
    {
        return result;
    }

    uint pageValue = cq45FetchPage(level, localPage);
    result.pageValue = pageValue;
    if (pageValue == 0u || pageValue == CQ45_REQUESTED_PAGE ||
        pageValue > 4095u)
    {
        return result;
    }

    int physicalIndex = int(pageValue - 1u);
    ivec3 atlasBrick;
    atlasBrick.x = physicalIndex % CQ45_ATLAS_BRICKS_PER_AXIS;
    int quotient = physicalIndex / CQ45_ATLAS_BRICKS_PER_AXIS;
    atlasBrick.y = quotient % CQ45_ATLAS_BRICKS_PER_AXIS;
    atlasBrick.z = quotient / CQ45_ATLAS_BRICKS_PER_AXIS;

    vec3 logicalVoxel = worldPosition / result.voxelWorldSize;
    vec3 brickLocalVoxel =
        logicalVoxel -
        vec3(logicalPage * CQ45_LOGICAL_BRICK_SIZE);
    // Logical pages are [0, 8). Keep the GL half-texel compensated coordinate inside
    // this physical brick's 10 texels so LINEAR filtering cannot bleed into the next
    // packed atlas brick and draw dark cell lines on every brick face.
    brickLocalVoxel = clamp(brickLocalVoxel, vec3(0.0), vec3(7.999));
    vec3 atlasBrickMin =
        vec3(atlasBrick * CQ45_PHYSICAL_BRICK_SIZE) + vec3(0.5);
    vec3 atlasBrickMax =
        vec3(atlasBrick * CQ45_PHYSICAL_BRICK_SIZE) +
        vec3(float(CQ45_PHYSICAL_BRICK_SIZE) - 0.5);
    vec3 atlasTexel = clamp(
        vec3(atlasBrick * CQ45_PHYSICAL_BRICK_SIZE) +
            vec3(float(CQ45_PHYSICAL_BORDER) + 0.5) +
            brickLocalVoxel,
        atlasBrickMin,
        atlasBrickMax);
    vec2 densityDistance = textureLod(
        uSparseCloudAtlas,
        atlasTexel / CQ45_ATLAS_TEXEL_SIZE,
        0.0).rg;

    result.density = densityDistance.r;
    result.distanceWorld =
        densityDistance.g * 255.0 * result.voxelWorldSize;
    // Soften density toward coarser data near missing neighbors, but do not open
    // shellWeight — that would disable conservative empty-space skipping.
    result.edgeWeight = cq45ClipmapEdgeWeight(
        logicalPagePosition - vec3(cq45Origin(level)));
    result.faceWeight = level == 0
        ? cq45FaceResidentFactor(level, localPage, brickLocalVoxel)
        : 1.0;
    result.resident = 1.0;
    return result;
}

Cq45ResolvedBase cq45ResolveBaseDensity(
    vec3 worldPosition,
    float shellBaseDensity)
{
    Cq45LevelSample l0 = cq45SampleLevel(0, worldPosition);
    Cq45LevelSample l1 = cq45SampleLevel(1, worldPosition);
    Cq45LevelSample l2 = cq45SampleLevel(2, worldPosition);

    Cq45ResolvedBase result;
    result.density = shellBaseDensity;
    result.safeDistanceWorld = 0.0;
    result.voxelWorldSize = 32.0;
    result.selectedLevel = -1.0;
    result.shellWeight = 1.0;
    result.resident = 0.0;

    if (l2.resident > 0.5)
    {
        float w = l2.edgeWeight * l2.faceWeight;
        result.density = mix(result.density, l2.density, w);
        result.shellWeight *= 1.0 - l2.edgeWeight;
        result.safeDistanceWorld =
            result.shellWeight <= 1e-3 ? l2.distanceWorld : 0.0;
        result.voxelWorldSize = l2.voxelWorldSize;
        result.selectedLevel = 2.0;
        result.resident = 1.0;
    }

    if (l1.resident > 0.5)
    {
        float w = l1.edgeWeight * l1.faceWeight;
        result.density = mix(result.density, l1.density, w);
        result.shellWeight *= 1.0 - l1.edgeWeight;
        result.safeDistanceWorld =
            result.shellWeight <= 1e-3
                ? (result.safeDistanceWorld > 0.0
                    ? min(result.safeDistanceWorld, l1.distanceWorld)
                    : l1.distanceWorld)
                : 0.0;
        result.voxelWorldSize = l1.voxelWorldSize;
        result.selectedLevel = 1.0;
        result.resident = 1.0;
    }

    if (l0.resident > 0.5)
    {
        float w = l0.edgeWeight * l0.faceWeight;
        result.density = mix(result.density, l0.density, w);
        result.shellWeight *= 1.0 - l0.edgeWeight;
        result.safeDistanceWorld =
            result.shellWeight <= 1e-3
                ? (result.safeDistanceWorld > 0.0
                    ? min(result.safeDistanceWorld, l0.distanceWorld)
                    : l0.distanceWorld)
                : 0.0;
        result.voxelWorldSize = l0.voxelWorldSize;
        result.selectedLevel = 0.0;
        result.resident = 1.0;
    }

    return result;
}

float cq45DistanceToBrickBoundary(
    vec3 worldPosition,
    vec3 rayDirection,
    float brickWorldSize)
{
    vec3 logicalBrick = floor(worldPosition / brickWorldSize);
    vec3 nextBoundary =
        (logicalBrick +
         vec3(
             rayDirection.x >= 0.0 ? 1.0 : 0.0,
             rayDirection.y >= 0.0 ? 1.0 : 0.0,
             rayDirection.z >= 0.0 ? 1.0 : 0.0)) *
        brickWorldSize;
    vec3 distance = vec3(1e30);
    if (abs(rayDirection.x) > 1e-6)
    {
        distance.x =
            (nextBoundary.x - worldPosition.x) / rayDirection.x;
    }
    if (abs(rayDirection.y) > 1e-6)
    {
        distance.y =
            (nextBoundary.y - worldPosition.y) / rayDirection.y;
    }
    if (abs(rayDirection.z) > 1e-6)
    {
        distance.z =
            (nextBoundary.z - worldPosition.z) / rayDirection.z;
    }

    distance = max(distance, vec3(0.0));
    return min(distance.x, min(distance.y, distance.z));
}

Cq45TraversalResult cq45TraverseToCandidate(
    vec3 rayOrigin,
    vec3 rayDirection,
    float tStart,
    float tEnd,
    float fineStepWorld)
{
    Cq45TraversalResult result;
    result.t = tEnd;
    result.density = 0.0;
    result.safeDistanceWorld = 0.0;
    result.voxelWorldSize = 32.0;
    result.selectedLevel = -1.0;
    result.shellWeight = 1.0;
    result.found = 0.0;
    result.pageSteps = 0;
    result.distanceSteps = 0;
    result.fineSteps = 0;
    result.fallbackQueries = 0;

    float t = max(tStart, 0.0);
    for (int iteration = 0;
         iteration < CQ45_MAX_TRAVERSAL_ITERATIONS;
         ++iteration)
    {
        if (t >= tEnd)
        {
            break;
        }

        vec3 worldPosition = rayOrigin + rayDirection * t;
        Cq45ResolvedBase resolved =
            cq45ResolveBaseDensity(worldPosition, 0.0);
        if (resolved.shellWeight > 1e-3 || resolved.resident < 0.5)
        {
            result.t = t;
            result.density = resolved.density;
            result.safeDistanceWorld = 0.0;
            result.voxelWorldSize = resolved.voxelWorldSize;
            result.selectedLevel = resolved.selectedLevel;
            result.shellWeight = resolved.shellWeight;
            result.found = 1.0;
            result.fallbackQueries += 1;
            return result;
        }

        float voxelWorldSize = max(resolved.voxelWorldSize, 0.001);
        if (resolved.density > (0.5 / 255.0) ||
            resolved.safeDistanceWorld <= voxelWorldSize + 1e-4)
        {
            result.t = t;
            result.density = resolved.density;
            result.safeDistanceWorld = resolved.safeDistanceWorld;
            result.voxelWorldSize = voxelWorldSize;
            result.selectedLevel = resolved.selectedLevel;
            result.shellWeight = resolved.shellWeight;
            result.found = 1.0;
            result.fineSteps += 1;
            return result;
        }

        float brickWorldSize =
            voxelWorldSize * float(CQ45_LOGICAL_BRICK_SIZE);
        float boundaryDistance = cq45DistanceToBrickBoundary(
            worldPosition,
            rayDirection,
            brickWorldSize);
        float distanceStep = max(
            voxelWorldSize * 0.5,
            resolved.safeDistanceWorld * 0.8);
        bool boundaryCrossing =
            boundaryDistance <= distanceStep + 1e-4;
        float advance;
        if (boundaryCrossing)
        {
            result.pageSteps += 1;
            advance = boundaryDistance + voxelWorldSize * 1e-3;
        }
        else
        {
            result.distanceSteps += 1;
            advance = max(
                min(distanceStep, boundaryDistance),
                min(max(fineStepWorld, voxelWorldSize * 0.5), voxelWorldSize));
        }

        t += max(advance, voxelWorldSize * 1e-4);
    }

    // The bounded inner traversal must fail open. A long corner-to-corner clipmap ray can
    // cross more than 64 logical pages; returning "not found" here would make the caller
    // discard the unvisited tail. Yield the current position as a fine candidate instead,
    // allowing the outer cloud march to resume traversal on its next iteration.
    if (t < tEnd)
    {
        Cq45ResolvedBase continuation =
            cq45ResolveBaseDensity(rayOrigin + rayDirection * t, 0.0);
        result.t = t;
        result.density = continuation.density;
        result.safeDistanceWorld = continuation.safeDistanceWorld;
        result.voxelWorldSize = continuation.voxelWorldSize;
        result.selectedLevel = continuation.selectedLevel;
        result.shellWeight = continuation.shellWeight;
        result.found = 1.0;
        result.fineSteps += 1;
    }

    return result;
}

#endif // GENESIS_SPARSE_CLOUD_TRAVERSAL_GLSL
