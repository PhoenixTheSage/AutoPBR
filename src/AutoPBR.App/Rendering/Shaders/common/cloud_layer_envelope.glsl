// Shared multi-deck cumulus + cirrus altitude stack. Keep formulas aligned with
// PreviewCloudLayerEnvelope / PreviewCloudLightAltitudeBounds on the CPU.

#ifndef GENESIS_CLOUD_LAYER_ENVELOPE_GLSL
#define GENESIS_CLOUD_LAYER_ENVELOPE_GLSL

#ifndef CLOUD_LAYER_ENVELOPE_MAX_DECKS
#define CLOUD_LAYER_ENVELOPE_MAX_DECKS 3
#endif

uniform int uCumulusLayerCount;
uniform float uInterDeckGap;
uniform float uHeightVariance;
uniform float uUpperThicknessScale;
uniform float uUpperCoverageScale;
uniform float uUpperDensityScale;
uniform vec3 uUpperWindOffset;
uniform float uCirrusGap;
uniform float uCirrusThickness;
uniform int uStyleBias;

struct VcCumulusDeck
{
    float baseAltitude;
    float topAltitude;
    float coverageScale;
    float densityScale;
    float heightVarianceOffset;
    int index;
};

struct VcCloudAltitudeStack
{
    int deckCount;
    float supportBase;
    float supportTop;
    float cirrusBase;
    float cirrusThickness;
    float cirrusTop;
    float heightVariance;
};

float vcStyleBiasTarget(int styleBias)
{
    // 0 = Auto, 1..4 map to family centers matching sparse 0.25-wide bins.
    if (styleBias <= 0)
    {
        return -1.0;
    }

    float family = float(clamp(styleBias - 1, 0, 3));
    return (family + 0.5) / 4.0;
}

float vcApplyStyleBias(float cloudType, int styleBias)
{
    float target = vcStyleBiasTarget(styleBias);
    if (target < 0.0)
    {
        return saturate1(cloudType);
    }

    return saturate1(mix(cloudType, target, 0.82));
}

vec4 vcApplyWeatherStyleBias(vec4 weather, int styleBias)
{
    weather.y = vcApplyStyleBias(weather.y, styleBias);
    weather.g = weather.y;
    return weather;
}

// Cheap low-frequency XZ variance in [-amplitude, +amplitude].
float vcCloudHeightVarianceOffset(vec2 xz, float volumeSize, float amplitude)
{
    if (amplitude <= 1e-4)
    {
        return 0.0;
    }

    float scale = max(volumeSize, 8.0) * 2.75;
    vec2 p = xz / scale;
    float n =
        sin(p.x * 1.7 + p.y * 0.9) * 0.55 +
        sin(p.x * 0.55 - p.y * 1.3 + 2.1) * 0.30 +
        sin((p.x + p.y) * 2.4 + 0.7) * 0.15;
    return n * amplitude;
}

VcCloudAltitudeStack vcBuildCloudAltitudeStack(
    float layerBaseAltitude,
    float volumeHeight,
    int cumulusLayerCount,
    float interDeckGap,
    float heightVariance,
    float upperThicknessScale,
    float cirrusGap,
    float cirrusThickness,
    float cirrusStrength)
{
    VcCloudAltitudeStack stack;
    float safeHeight = max(volumeHeight, 0.01);
    float variance = max(heightVariance, 0.0);
    float gap = max(interDeckGap, 0.0);
    float upperScale = max(upperThicknessScale, 0.05);
    int decks = clamp(cumulusLayerCount, 1, CLOUD_LAYER_ENVELOPE_MAX_DECKS);

    float deck0Base = max(layerBaseAltitude, 0.01);
    float deck0Top = deck0Base + safeHeight;
    float topDeckTop = deck0Top;

    if (decks >= 2)
    {
        float deck1Base = topDeckTop + gap;
        float deck1Top = deck1Base + safeHeight * upperScale;
        topDeckTop = deck1Top;
    }

    if (decks >= 3)
    {
        float deck2Base = topDeckTop + gap;
        float deck2Top = deck2Base + safeHeight * upperScale;
        topDeckTop = deck2Top;
    }

    stack.deckCount = decks;
    stack.heightVariance = variance;
    // Occupancy stays on the fixed deck band; variance only remaps the vertical
    // profile. Keep a small soft pad for light-cache filtering (match CPU).
    float softPad = min(variance * 0.2, safeHeight * 0.08);
    stack.supportBase = max(deck0Base - softPad, 0.001);
    stack.supportTop = topDeckTop + softPad;

    float safeCirrusGap = max(cirrusGap, 0.0);
    float safeCirrusThickness = max(cirrusThickness, 0.1);
    stack.cirrusBase = topDeckTop + safeCirrusGap;
    stack.cirrusThickness = safeCirrusThickness;
    stack.cirrusTop = stack.cirrusBase + safeCirrusThickness;
    if (cirrusStrength <= 0.0)
    {
        stack.cirrusBase = topDeckTop;
        stack.cirrusThickness = 0.0;
        stack.cirrusTop = topDeckTop;
    }

    return stack;
}

bool vcTryGetCumulusDeck(
    float altitude,
    vec2 xz,
    float volumeSize,
    float layerBaseAltitude,
    float volumeHeight,
    float coverageScale,
    out VcCumulusDeck deck)
{
    float safeHeight = max(volumeHeight, 0.01);
    float variance = max(uHeightVariance, 0.0);
    float gap = max(uInterDeckGap, 0.0);
    float upperScale = max(uUpperThicknessScale, 0.05);
    int decks = clamp(uCumulusLayerCount, 1, CLOUD_LAYER_ENVELOPE_MAX_DECKS);
    // Variance remaps the vertical profile inside a fixed altitude band. Hard-shifting the
    // occupancy window by ±amplitude made grazing rays flicker in/out along XZ and read as
    // horizontal stair steps.
    float varianceOffset = vcCloudHeightVarianceOffset(xz, volumeSize, variance);

    float cursorBase = max(layerBaseAltitude, 0.01);
    for (int i = 0; i < CLOUD_LAYER_ENVELOPE_MAX_DECKS; ++i)
    {
        if (i >= decks)
        {
            break;
        }

        float thickness = i == 0 ? safeHeight : safeHeight * upperScale;
        float baseAlt = cursorBase;
        float topAlt = baseAlt + thickness;
        if (altitude >= baseAlt && altitude <= topAlt)
        {
            deck.baseAltitude = baseAlt;
            deck.topAltitude = topAlt;
            deck.heightVarianceOffset = varianceOffset;
            deck.index = i;
            deck.coverageScale = i == 0
                ? coverageScale
                : coverageScale * uUpperCoverageScale;
            deck.densityScale = i == 0 ? 1.0 : uUpperDensityScale;
            return true;
        }

        cursorBase += thickness + gap;
    }

    deck.baseAltitude = 0.0;
    deck.topAltitude = 0.0;
    deck.coverageScale = 0.0;
    deck.densityScale = 0.0;
    deck.heightVarianceOffset = 0.0;
    deck.index = -1;
    return false;
}

float vcDeckNormalizedHeight(float altitude, VcCumulusDeck deck)
{
    float layerH = max(deck.topAltitude - deck.baseAltitude, 0.001);
    float h = saturate1((altitude - deck.baseAltitude) / layerH);
    // Endpoint-preserving warp: raise/lower bulk mass inside the fixed altitude band.
    // A hard altitude shift (h - offset/H) created XZ-varying floors/ceilings that read
    // as horizontal stair slices on grazing rays.
    float amp = clamp(deck.heightVarianceOffset / layerH, -0.4, 0.4);
    return saturate1(h - amp * 4.0 * h * (1.0 - h));
}

bool vcTryGetCumulusDeckSupportBand(
    int deckIndex,
    float layerBaseAltitude,
    float volumeHeight,
    out float supportBase,
    out float supportTop,
    out float deckThickness)
{
    supportBase = 0.0;
    supportTop = 0.0;
    deckThickness = 0.0;
    int decks = clamp(uCumulusLayerCount, 1, CLOUD_LAYER_ENVELOPE_MAX_DECKS);
    if (deckIndex < 0 || deckIndex >= decks)
    {
        return false;
    }

    float safeHeight = max(volumeHeight, 0.01);
    float gap = max(uInterDeckGap, 0.0);
    float upperScale = max(uUpperThicknessScale, 0.05);
    float cursorBase = max(layerBaseAltitude, 0.01);
    for (int i = 0; i < CLOUD_LAYER_ENVELOPE_MAX_DECKS; ++i)
    {
        float thickness = i == 0 ? safeHeight : safeHeight * upperScale;
        if (i == deckIndex)
        {
            float guard = clamp(thickness * 0.015, 0.50, 1.50);
            supportBase = max(cursorBase - guard, 0.001);
            supportTop = cursorBase + thickness + guard;
            deckThickness = thickness;
            return true;
        }

        cursorBase += thickness + gap;
    }

    return false;
}

#endif
