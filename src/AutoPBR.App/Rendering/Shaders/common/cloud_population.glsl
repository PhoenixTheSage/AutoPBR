// CA2 deterministic cloud-population contract shared by procedural density and
// sparse brick generation. All placement is a pure world-space function so
// neighboring bricks, clipmap levels, and shell fallback agree at their borders.

#ifndef GENESIS_CLOUD_POPULATION_GLSL
#define GENESIS_CLOUD_POPULATION_GLSL

const uint CP_PARENT_SALT = 0xA511E9B3u;
const uint CP_SATELLITE_SALT = 0x63D83595u;

float cpSaturate(float value)
{
    return clamp(value, 0.0, 1.0);
}

uint cpHashCell(ivec2 cell, uint salt)
{
    uint hash = 2166136261u;
    hash = (hash ^ uint(cell.x)) * 16777619u;
    hash = (hash ^ uint(cell.y)) * 16777619u;
    hash = (hash ^ salt) * 16777619u;
    hash ^= hash >> 16u;
    hash *= 0x7FEB352Du;
    hash ^= hash >> 15u;
    return hash;
}

float cpHash01(ivec2 cell, uint salt)
{
    return float(cpHashCell(cell, salt) & 0x00FFFFFFu) / 16777215.0;
}

float cpParentCellSpan(float volumeSize)
{
    return max(max(volumeSize, 8.0) * 1.10, 160.0);
}

float cpSatelliteCellSpan(float volumeSize)
{
    return cpParentCellSpan(volumeSize) * 0.38;
}

vec2 cpCellCenter(ivec2 cell, float cellSpan, uint salt)
{
    vec2 jitter = vec2(
        cpHash01(cell, salt ^ 0x68BC21EBu),
        cpHash01(cell, salt ^ 0x02E5BE93u)) - vec2(0.5);
    return (vec2(cell) + vec2(0.5) + jitter * 0.36) * cellSpan;
}

float cpCellScale(ivec2 cell, uint salt, int satellite)
{
    float randomScale = cpHash01(cell, salt ^ 0x967A889Bu);
    return satellite > 0
        ? mix(0.58, 0.92, randomScale)
        : mix(0.72, 1.16, randomScale);
}

float cpCellRotation(ivec2 cell, uint salt)
{
    return cpHash01(cell, salt ^ 0xC2B2AE35u) * 6.28318530718;
}

float cpCellAspect(ivec2 cell, uint salt, int satellite)
{
    float randomAspect = cpHash01(cell, salt ^ 0x27D4EB2Fu);
    return satellite > 0
        ? mix(0.68, 1.24, randomAspect)
        : mix(0.76, 1.32, randomAspect);
}

vec2 cpCellLean(ivec2 cell, uint salt, int satellite)
{
    float angle =
        cpHash01(cell, salt ^ 0x165667B1u) * 6.28318530718;
    float amount = satellite > 0
        ? mix(
            0.035,
            0.085,
            cpHash01(cell, salt ^ 0xD3A2646Cu))
        : mix(
            0.045,
            0.145,
            cpHash01(cell, salt ^ 0xFD7046C5u));
    return vec2(cos(angle), sin(angle)) * amount;
}

float cpParentProbability(float coverage, float cloudType)
{
    float moisture = smoothstep(0.14, 0.84, cpSaturate(coverage));
    float stratusFill = smoothstep(0.72, 0.96, cpSaturate(cloudType)) * 0.08;
    return clamp(mix(0.24, 0.82, moisture) + stratusFill, 0.0, 0.90);
}

float cpSatelliteProbability(
    float coverage,
    float cloudType,
    float convection)
{
    float moisture = smoothstep(0.20, 0.78, cpSaturate(coverage));
    float cumulusBias = mix(0.72, 0.36, cpSaturate(cloudType));
    float lift = smoothstep(0.40, 0.90, cpSaturate(convection)) * 0.12;
    return clamp(moisture * cumulusBias + lift, 0.0, 0.78);
}

float cpSoftUnion(float a, float b)
{
    a = cpSaturate(a);
    b = cpSaturate(b);
    return a + b - a * b;
}

float cpCellInfluence(
    vec2 worldPosition,
    ivec2 cell,
    float cellSpan,
    uint salt,
    int satellite,
    out float support)
{
    vec2 center = cpCellCenter(cell, cellSpan, salt);
    float scale = cpCellScale(cell, salt, satellite);
    vec2 delta = (worldPosition - center) / max(cellSpan * scale, 1e-4);
    float radius = length(delta * vec2(1.0, 1.10));
    support = 1.0 - smoothstep(0.45, 0.74, radius);
    return 1.0 - smoothstep(0.34, 0.58, radius);
}

float cpValueField(vec2 worldPosition, float cellSpan, uint salt)
{
    vec2 lattice = worldPosition / max(cellSpan, 1e-4);
    ivec2 cell = ivec2(floor(lattice));
    vec2 f = fract(lattice);
    f = f * f * (vec2(3.0) - vec2(2.0) * f);
    float v00 = cpHash01(cell, salt);
    float v10 = cpHash01(cell + ivec2(1, 0), salt);
    float v01 = cpHash01(cell + ivec2(0, 1), salt);
    float v11 = cpHash01(cell + ivec2(1, 1), salt);
    return mix(mix(v00, v10, f.x), mix(v01, v11, f.x), f.y);
}

// Cheap population mask for the procedural shell. It uses two four-corner value
// fields rather than evaluating every neighboring template cell in each ray step.
// Sparse generation uses the same spans, hashes, probabilities, and soft-union
// rule, then applies the more expensive jittered envelope placement offline.
float cpPopulationMask(
    vec2 worldPosition,
    float volumeSize,
    float coverage,
    float cloudType,
    float convection)
{
    float parentSpan = cpParentCellSpan(volumeSize);
    float parentProbability = cpParentProbability(coverage, cloudType);
    float parentField = cpValueField(
        worldPosition,
        parentSpan,
        CP_PARENT_SALT ^ 0xB5297A4Du);
    float parentThreshold = 1.0 - parentProbability;
    float parent = smoothstep(
        parentThreshold - 0.16,
        parentThreshold + 0.16,
        parentField);

    float satelliteSpan = cpSatelliteCellSpan(volumeSize);
    float satelliteProbability = cpSatelliteProbability(
        coverage,
        cloudType,
        convection);
    float satelliteField = cpValueField(
        worldPosition,
        satelliteSpan,
        CP_SATELLITE_SALT ^ 0x1B56C4E9u);
    float satelliteThreshold = 1.0 - satelliteProbability;
    float satellite = smoothstep(
        satelliteThreshold - 0.12,
        satelliteThreshold + 0.12,
        satelliteField);

    // Satellites are admitted only inside the broad transition of a parent system.
    // They can create smaller boundary cells without becoming a global foam field.
    float attachment = smoothstep(0.04, 0.58, parent);
    return cpSoftUnion(parent, satellite * attachment * 0.86);
}

#endif // GENESIS_CLOUD_POPULATION_GLSL
