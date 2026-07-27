// Curved cloud-layer intersection shared by cumulus and cirrus rendering.

#ifndef GENESIS_CLOUD_SHELL_GLSL
#define GENESIS_CLOUD_SHELL_GLSL

bool vcsRaySphere(vec3 roFromCenter, vec3 rd, float radius, out vec2 roots)
{
    float b = dot(roFromCenter, rd);
    float c = dot(roFromCenter, roFromCenter) - radius * radius;
    float discriminant = b * b - c;
    if (discriminant < 0.0)
    {
        roots = vec2(0.0, -1.0);
        return false;
    }

    float root = sqrt(discriminant);
    roots = vec2(-b - root, -b + root);
    return true;
}

// First visible forward interval through the shell. Works below, inside, and above the layer.
vec2 vcsIntersectShell(vec3 ro, vec3 rd, vec3 center, float innerRadius, float outerRadius)
{
    vec3 oc = ro - center;
    vec2 outerRoots;
    if (outerRadius <= innerRadius || innerRadius <= 0.0 ||
        !vcsRaySphere(oc, rd, outerRadius, outerRoots) || outerRoots.y <= 0.0)
    {
        return vec2(0.0, -1.0);
    }

    float cameraRadius = length(oc);
    float tEnter;
    float tExit;
    vec2 innerRoots;
    bool hitsInner = vcsRaySphere(oc, rd, innerRadius, innerRoots);
    if (cameraRadius < innerRadius)
    {
        if (!hitsInner || innerRoots.y <= 0.0)
        {
            return vec2(0.0, -1.0);
        }

        tEnter = innerRoots.y;
        tExit = outerRoots.y;
    }
    else
    {
        tEnter = max(outerRoots.x, 0.0);
        tExit = outerRoots.y;
        if (hitsInner && innerRoots.x > tEnter)
        {
            tExit = min(tExit, innerRoots.x);
        }
    }

    return tExit > tEnter ? vec2(tEnter, tExit) : vec2(0.0, -1.0);
}

float vcsAltitude(vec3 worldPos, vec3 center, float planetRadius)
{
    return length(worldPos - center) - planetRadius;
}

// Distance to the first solid-planet intersection.  The cloud shell surrounds the
// planet, so a downward ray from below the layer must stop at the ground-facing sphere
// instead of continuing through the planet and entering the shell on the far side.
float vcsPlanetOcclusionDistance(vec3 ro, vec3 rd, vec3 center, float planetRadius)
{
    vec3 oc = ro - center;
    float cameraRadius = length(oc);
    if (cameraRadius < planetRadius - 1e-3)
    {
        return 0.0;
    }

    vec2 roots;
    if (!vcsRaySphere(oc, rd, planetRadius, roots))
    {
        return 1e9;
    }

    // At the surface, an inward ray has a zero near root and must be considered
    // immediately occluded; choosing the far root would expose far-side clouds.
    if (cameraRadius <= planetRadius + 1e-3 && dot(oc, rd) < 0.0)
    {
        return 0.0;
    }

    if (roots.x > 1e-3)
    {
        return roots.x;
    }

    return 1e9;
}

// Soft visibility at the geometric planet horizon. Most of the transition is biased behind
// the tangent: a cloud reaching the visible horizon stays nearly opaque, then fades over a
// few pixels on the far side. Centering the fade at 50% on the tangent creates a dark stripe
// when the same reconstructed cloud spans both sides of the horizon.
float vcsPlanetHorizonVisibility(vec3 ro, vec3 rd, vec3 center, float planetRadius, float feather)
{
    vec3 oc = ro - center;
    float cameraRadius = length(oc);
    if (cameraRadius <= planetRadius - 1e-3)
    {
        return 0.0;
    }

    vec3 localUp = oc / max(cameraRadius, 1e-4);
    float radiusRatio = clamp(planetRadius / max(cameraRadius, planetRadius), 0.0, 1.0);
    float horizonMu = -sqrt(max(1.0 - radiusRatio * radiusRatio, 0.0));
    float viewMu = dot(normalize(rd), localUp);
    float width = max(feather, 1e-5);
    return smoothstep(horizonMu - width * 2.0, horizonMu + width * 0.25, viewMu);
}

#endif // GENESIS_CLOUD_SHELL_GLSL
