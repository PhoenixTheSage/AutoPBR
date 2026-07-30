// Flat continuous-world cloud-layer intersection shared by cumulus and cirrus rendering.

#ifndef GENESIS_CLOUD_SHELL_GLSL
#define GENESIS_CLOUD_SHELL_GLSL

float vcsAltitude(vec3 worldPosition, float groundWorldY)
{
    return worldPosition.y - groundWorldY;
}

// First visible forward interval through a horizontal altitude slab. Horizontal rays are
// valid only while the camera is inside the slab. Every interval is distance bounded so a
// continuous flat world never depends on a fictitious planet horizon for termination.
vec2 vcsIntersectAltitudeSlab(
    vec3 ro,
    vec3 rd,
    float groundWorldY,
    float innerAltitude,
    float outerAltitude,
    float maxTraceDistance)
{
    if (outerAltitude <= innerAltitude || maxTraceDistance <= 0.0)
    {
        return vec2(0.0, -1.0);
    }

    float lowerWorldY = groundWorldY + innerAltitude;
    float upperWorldY = groundWorldY + outerAltitude;
    if (abs(rd.y) <= 1e-6)
    {
        return ro.y >= lowerWorldY && ro.y <= upperWorldY
            ? vec2(0.0, maxTraceDistance)
            : vec2(0.0, -1.0);
    }

    vec2 roots = (vec2(lowerWorldY, upperWorldY) - ro.y) / rd.y;
    float tEnter = max(min(roots.x, roots.y), 0.0);
    float tExit = min(max(roots.x, roots.y), maxTraceDistance);
    return tExit > tEnter ? vec2(tEnter, tExit) : vec2(0.0, -1.0);
}

// Softly removes layers that first become reachable near the finite trace boundary. This
// replaces the former planet-tangent mask without bending the deck or drawing a hard rim.
float vcsDistanceVisibility(
    float entryDistance,
    float maxTraceDistance,
    float fadeFraction)
{
    float safeFade = clamp(fadeFraction, 0.01, 0.95);
    float fadeStart = maxTraceDistance * (1.0 - safeFade);
    return 1.0 - smoothstep(
        fadeStart,
        maxTraceDistance,
        max(entryDistance, 0.0));
}

// Near-field step sizing span. Long near-horizontal rays must not inherit the complete
// altitude-plane exit for their finest sample lattice.
float vcsMarchSpanLimit(float volumeSize, float volumeHeight)
{
    return max(max(volumeSize * 4.0, volumeHeight * 8.0), 256.0);
}

// Primary step length. The interval itself selects the policy: short rays divide their
// complete interval, while every long/grazing ray uses the same bounded near-field span.
// There is deliberately no inside/outside camera classification here.
float vcsMarchStepLength(
    float tEnter,
    float tExit,
    int steps,
    float volumeSize,
    float volumeHeight)
{
    float safeSteps = float(max(steps, 1));
    float interval = max(tExit - tEnter, 0.0);
    float sizedSpan = min(interval, vcsMarchSpanLimit(volumeSize, volumeHeight));
    return max(sizedSpan / safeSteps, 0.01);
}

#endif // GENESIS_CLOUD_SHELL_GLSL
