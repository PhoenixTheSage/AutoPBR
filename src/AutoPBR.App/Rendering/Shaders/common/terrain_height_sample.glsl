// Shared height-column sampling for the terrain occluder atlas compute path.
// Mirrors PreviewTerrainBiomeSampler + PreviewTerrainHeightfield + PreviewTerrainAdvancedErosion
// height-only outputs (surface relative Y). Block kinds / biome ids are intentionally omitted.
//
// Advanced erosion (Phacelle noise + erosion filter):
// Copyright (c) 2025 Rune Skovbo Johansen — Mozilla Public License 2.0.
// See LICENSE-MPL-2.0.txt. Gradient noise derived from Inigo Quilez (MIT).

#ifndef TERRAIN_HEIGHT_SAMPLE_GLSL
#define TERRAIN_HEIGHT_SAMPLE_GLSL

const float thsTau = 6.28318530718;

const int thsFlatPadHalfExtent = 14;
const int thsTransitionBlocks = 4;
const int thsMaxRelief = 6;
const int thsDesertMaxRelief = 10;
const int thsMountainMaxRelief = 20;
const int thsBeachMaxRelief = 2;
const float thsBiomeBlendHalfWidth = 0.085;
const int thsFillDepth = 3;
const int thsSolidFloorRelativeY = -50;
const int thsClimateSeedSalt = int(0xC11A7E00u);

const float thsErosionScale = 0.18;
const float thsErosionStrengthBase = 0.20;
const float thsErosionGullyWeight = 0.55;
const float thsErosionDetail = 1.45;
const vec4 thsErosionRounding = vec4(0.1, 0.0, 0.1, 2.0);
const vec4 thsErosionOnset = vec4(1.25, 1.25, 2.8, 1.5);
const vec2 thsErosionAssumedSlope = vec2(0.7, 1.0);
const float thsErosionCellScale = 0.7;
const float thsErosionNormalization = 0.5;
const int thsErosionOctaves = 4;
const float thsErosionLacunarity = 2.0;
const float thsErosionGain = 0.5;

float thsFract(float v)
{
    return v - floor(v);
}

float thsClamp01(float t)
{
    return clamp(t, 0.0, 1.0);
}

float thsLerp(float a, float b, float t)
{
    return a + (b - a) * t;
}

float thsPowInv(float t, float power)
{
    return 1.0 - pow(1.0 - thsClamp01(t), power);
}

float thsEaseOut(float t)
{
    float v = 1.0 - thsClamp01(t);
    return 1.0 - v * v;
}

float thsSmoothStart(float t, float smoothing)
{
    if (t >= smoothing)
    {
        return t - 0.5 * smoothing;
    }

    if (smoothing <= 1e-8)
    {
        return t;
    }

    return 0.5 * t * t / smoothing;
}

vec2 thsSafeNormalize(vec2 n)
{
    float len = length(n);
    return len > 1e-10 ? n / len : n;
}

float thsSmoothstep(float edge0, float edge1, float x)
{
    if (edge1 <= edge0)
    {
        return x < edge0 ? 0.0 : 1.0;
    }

    float t = thsClamp01((x - edge0) / (edge1 - edge0));
    return t * t * (3.0 - 2.0 * t);
}

float thsSoftGateHigh(float value, float threshold, float blendHalfWidth)
{
    if (blendHalfWidth <= 0.0)
    {
        return value > threshold ? 1.0 : 0.0;
    }

    return thsSmoothstep(threshold - blendHalfWidth, threshold + blendHalfWidth, value);
}

float thsSoftGateLow(float value, float threshold, float blendHalfWidth)
{
    if (blendHalfWidth <= 0.0)
    {
        return value < threshold ? 1.0 : 0.0;
    }

    return 1.0 - thsSmoothstep(threshold - blendHalfWidth, threshold + blendHalfWidth, value);
}

float thsHash01(int x, int z, int seed)
{
    int h = seed;
    h = (h ^ x) * int(0x27D4EB2Du);
    h = (h ^ z) * int(0x165667B1u);
    h = h ^ (h >> 15);
    h = h * int(0x85EBCA6Bu);
    h = h ^ (h >> 13);
    return float(h & int(0x00FFFFFFu)) / 16777215.0;
}

float thsSampleValueNoise(float x, float z, int seed)
{
    int x0 = int(floor(x));
    int z0 = int(floor(z));
    float fx = x - float(x0);
    float fz = z - float(z0);
    float sx = fx * fx * (3.0 - 2.0 * fx);
    float sz = fz * fz * (3.0 - 2.0 * fz);

    float n00 = thsHash01(x0, z0, seed);
    float n10 = thsHash01(x0 + 1, z0, seed);
    float n01 = thsHash01(x0, z0 + 1, seed);
    float n11 = thsHash01(x0 + 1, z0 + 1, seed);

    float nx0 = n00 + (n10 - n00) * sx;
    float nx1 = n01 + (n11 - n01) * sx;
    return (nx0 + (nx1 - nx0) * sz) * 2.0 - 1.0;
}

float thsSampleFbm(int x, int z, int seed)
{
    float n =
        thsSampleValueNoise(float(x) * 0.045, float(z) * 0.045, seed) * 0.55 +
        thsSampleValueNoise(float(x) * 0.11, float(z) * 0.11, seed ^ int(0x9E3779B9u)) * 0.30 +
        thsSampleValueNoise(float(x) * 0.27, float(z) * 0.27, seed ^ int(0x85EBCA6Bu)) * 0.15;
    return clamp(n, -1.0, 1.0);
}

float thsNoise01(float x, float z, int seed)
{
    float n = thsSampleValueNoise(x, z, seed);
    return thsClamp01((n + 1.0) * 0.5);
}

vec2 thsHash2(vec2 xIn)
{
    vec2 k = vec2(0.3183099, 0.3678794);
    vec2 x = xIn * k + vec2(k.y, k.x);
    float scalar = thsFract(x.x * x.y * (x.x + x.y));
    vec2 v = vec2(16.0 * k.x * scalar, 16.0 * k.y * scalar);
    return vec2(-1.0) + 2.0 * vec2(thsFract(v.x), thsFract(v.y));
}

vec3 thsNoised(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = p - i;

    vec2 u = f * f * f * (f * (f * 6.0 - vec2(15.0)) + vec2(10.0));
    vec2 du = 30.0 * f * f * (f * (f - vec2(2.0)) + vec2(1.0));

    vec2 ga = thsHash2(i);
    vec2 gb = thsHash2(i + vec2(1.0, 0.0));
    vec2 gc = thsHash2(i + vec2(0.0, 1.0));
    vec2 gd = thsHash2(i + vec2(1.0, 1.0));

    float va = dot(ga, f);
    float vb = dot(gb, f - vec2(1.0, 0.0));
    float vc = dot(gc, f - vec2(0.0, 1.0));
    float vd = dot(gd, f - vec2(1.0, 1.0));

    float value = va + u.x * (vb - va) + u.y * (vc - va) + u.x * u.y * (va - vb - vc + vd);
    vec2 deriv =
        ga
        + u.x * (gb - ga)
        + u.y * (gc - ga)
        + u.x * u.y * (ga - gb - gc + gd)
        + du * (vec2(u.y, u.x) * (va - vb - vc + vd) + vec2(vb, vc) - vec2(va));

    return vec3(value, deriv);
}

vec3 thsFbmDeriv(vec2 p, float frequency, int octaves, float lacunarity, float gain)
{
    vec3 n = vec3(0.0);
    float freq = frequency;
    float amp = 1.0;
    for (int o = 0; o < 8; o++)
    {
        if (o >= octaves)
        {
            break;
        }

        vec3 s = thsNoised(p * freq);
        n += vec3(s.x * amp, s.y * amp * freq, s.z * amp * freq);
        amp *= gain;
        freq *= lacunarity;
    }

    return n;
}

vec4 thsPhacelleNoise(vec2 p, vec2 normDir, float freq, float offsetCycles, float normalization)
{
    vec2 sideDir = vec2(-normDir.y, normDir.x) * (freq * thsTau);
    float offset = offsetCycles * thsTau;

    vec2 pInt = floor(p);
    vec2 pFrac = p - pInt;

    vec2 phaseDir = vec2(0.0);
    float weightSum = 0.0;

    for (int i = -1; i <= 2; i++)
    {
        for (int j = -1; j <= 2; j++)
        {
            vec2 gridOffset = vec2(float(i), float(j));
            vec2 gridPoint = pInt + gridOffset;
            vec2 randomOffset = thsHash2(gridPoint) * 0.5;
            vec2 v = pFrac - gridOffset - randomOffset;

            float sqrDist = dot(v, v);
            float weight = max(exp(-sqrDist * 2.0) - 0.01111, 0.0);
            weightSum += weight;

            float waveInput = dot(v, sideDir) + offset;
            phaseDir += vec2(cos(waveInput), sin(waveInput)) * weight;
        }
    }

    vec2 interpolated = phaseDir / max(weightSum, 1e-10);
    float magRaw = length(interpolated);
    float magnitude = max(1.0 - normalization, magRaw);
    vec2 normalized = interpolated / magnitude;
    return vec4(normalized.x, normalized.y, sideDir.x, sideDir.y);
}

void thsErosionFilter(
    vec2 p,
    vec3 baseHeightAndSlope,
    float fadeTargetIn,
    float strengthScale,
    out vec3 delta,
    out float magnitude)
{
    float strength = thsErosionStrengthBase * thsErosionScale * strengthScale;
    float fadeTarget = clamp(fadeTargetIn, -1.0, 1.0);

    vec3 inputHs = baseHeightAndSlope;
    vec3 hAndS = baseHeightAndSlope;

    float freq = 1.0 / (thsErosionScale * thsErosionCellScale);
    vec2 slope = vec2(hAndS.y, hAndS.z);
    float slopeLength = max(length(slope), 1e-10);
    magnitude = 0.0;
    float roundingMult = 1.0;

    float roundingForInput = thsLerp(
        thsErosionRounding.y,
        thsErosionRounding.x,
        thsClamp01(fadeTarget + 0.5)) * thsErosionRounding.z;
    float combiMask = thsEaseOut(thsSmoothStart(
        slopeLength * thsErosionOnset.x,
        roundingForInput * thsErosionOnset.x));

    float ridgeMapCombiMask = thsEaseOut(slopeLength * thsErosionOnset.z);
    float ridgeMapFadeTarget = fadeTarget;

    vec2 gullySlope = mix(
        slope,
        slope / slopeLength * thsErosionAssumedSlope.x,
        thsErosionAssumedSlope.y);

    for (int o = 0; o < 8; o++)
    {
        if (o >= thsErosionOctaves)
        {
            break;
        }

        vec4 phacelle = thsPhacelleNoise(
            p * freq,
            thsSafeNormalize(gullySlope),
            thsErosionCellScale,
            0.25,
            thsErosionNormalization);
        vec2 pZw = vec2(phacelle.z, phacelle.w) * -freq;
        float sloping = abs(phacelle.y);

        gullySlope += sign(phacelle.y) * pZw * strength * thsErosionGullyWeight;

        vec3 octaveHAndS = vec3(phacelle.x, phacelle.y * pZw.x, phacelle.y * pZw.y);
        vec3 faded = mix(
            vec3(fadeTarget, 0.0, 0.0),
            octaveHAndS * thsErosionGullyWeight,
            combiMask);
        hAndS += faded * strength;
        magnitude += strength;

        fadeTarget = faded.x;

        float roundingForOctave = thsLerp(
            thsErosionRounding.y,
            thsErosionRounding.x,
            thsClamp01(phacelle.x + 0.5)) * roundingMult;
        float newMask = thsEaseOut(thsSmoothStart(
            sloping * thsErosionOnset.y,
            roundingForOctave * thsErosionOnset.y));
        combiMask = thsPowInv(combiMask, thsErosionDetail) * newMask;

        ridgeMapFadeTarget = thsLerp(ridgeMapFadeTarget, octaveHAndS.x, ridgeMapCombiMask);
        float newRidgeMask = thsEaseOut(sloping * thsErosionOnset.w);
        ridgeMapCombiMask *= newRidgeMask;

        strength *= thsErosionGain;
        freq *= thsErosionLacunarity;
        roundingMult *= thsErosionRounding.w;
    }

    delta = hAndS - inputHs;
}

float thsSampleErodedMountainNormalized(float worldX, float worldZ, int seed, float erosionStrength)
{
    erosionStrength = clamp(erosionStrength, 0.0, 1.5);

    int oxBits = (seed * int(0x27D4EB2Du)) & 0xFFFF;
    int ozBits = (seed * int(0x85EBCA6Bu)) & 0xFFFF;
    float ox = float(oxBits) / 65535.0 * 40.0;
    float oz = float(ozBits) / 65535.0 * 40.0;
    vec2 p = vec2(worldX * 0.028 + ox, worldZ * 0.028 + oz);

    const float amp = 0.32;
    vec3 basis = thsFbmDeriv(p, 2.4, 3, 2.0, 0.18);
    basis *= amp;

    float fadeTarget = clamp(basis.x / (amp * 0.65), -1.0, 1.0);
    float strengthScale = max(erosionStrength, 1e-4);
    vec3 delta;
    float magnitude;
    thsErosionFilter(p, basis, fadeTarget, strengthScale, delta, magnitude);

    float carve = erosionStrength <= 1e-4 ? 0.0 : 0.65 * magnitude;
    float eroded = basis.x + delta.x * erosionStrength - carve;

    float ridge = thsNoised(p * 1.35 + vec2(17.1, -9.3)).x;
    ridge = 1.0 - abs(ridge);
    ridge = ridge * ridge * ridge;

    float n = eroded * 1.55 + ridge * 0.55 - 0.08;
    return clamp(n, -1.0, 1.0);
}

void thsComputeBiomeWeights(
    float temperature,
    float humidity,
    float continental,
    float blendHalfWidth,
    out float mountains,
    out float desert,
    out float beach,
    out float plains)
{
    temperature = clamp(temperature, 0.0, 1.0);
    humidity = clamp(humidity, 0.0, 1.0);
    continental = clamp(continental, 0.0, 1.0);
    blendHalfWidth = max(blendHalfWidth, 0.0);

    float mCont = thsSoftGateHigh(continental, 0.55, blendHalfWidth);
    float mTemp = thsSoftGateLow(temperature, 0.55, blendHalfWidth);
    float mAff = mCont * mTemp;

    float dTemp = thsSoftGateHigh(temperature, 0.62, blendHalfWidth);
    float dHum = thsSoftGateLow(humidity, 0.38, blendHalfWidth);
    float dAff = dTemp * dHum * (1.0 - mAff);

    float bAff = thsSoftGateLow(continental, 0.34, blendHalfWidth) * (1.0 - mAff) * (1.0 - dAff);
    float pAff = max(0.0, 1.0 - mAff - dAff - bAff);
    float sum = mAff + dAff + bAff + pAff;
    if (sum <= 1e-8)
    {
        mountains = 0.0;
        desert = 0.0;
        beach = 0.0;
        plains = 1.0;
        return;
    }

    mountains = mAff / sum;
    desert = dAff / sum;
    beach = bAff / sum;
    plains = pAff / sum;
}

float thsSamplePlainsHeightContinuous(int x, int z, int seed)
{
    return thsSampleFbm(x, z, seed) * float(thsMaxRelief);
}

float thsSampleDesertHeightContinuous(int x, int z, int seed)
{
    float n =
        thsSampleValueNoise(float(x) * 0.035, float(z) * 0.035, seed) * 0.65 +
        thsSampleValueNoise(float(x) * 0.09, float(z) * 0.09, seed ^ 0x11111111) * 0.35;
    return clamp(n, -1.0, 1.0) * float(thsDesertMaxRelief);
}

float thsSampleBeachHeightContinuous(int x, int z, int seed)
{
    float n = thsSampleFbm(x, z, seed ^ int(0xBEAC0001u));
    float maxR = float(thsBeachMaxRelief);
    return clamp((n * 0.55 - 0.15) * maxR, -maxR, maxR);
}

float thsSampleMountainHeightContinuous(int x, int z, int seed, float erosionStrength)
{
    float n = thsSampleErodedMountainNormalized(float(x), float(z), seed, erosionStrength);
    return n * float(thsMountainMaxRelief);
}

int thsApplyPadTransition(int relief, int chebyshev, int pad, int transitionBlocks)
{
    int blendEnd = pad + transitionBlocks;
    if (chebyshev < blendEnd && transitionBlocks > 0)
    {
        float t = float(chebyshev - pad) / float(transitionBlocks);
        t = thsClamp01(t);
        float s = t * t * (3.0 - 2.0 * t);
        return int(round(float(relief) * s));
    }

    return relief;
}

int thsSolidBottomY(int columnHeight, int fillDepth)
{
    return min(columnHeight - max(1, fillDepth) + 1, thsSolidFloorRelativeY);
}

/// Surface relative-Y matching PreviewTerrainBiomeSampler.SampleHeight.
int thsSampleColumnHeight(
    int x,
    int z,
    int seed,
    float biomeSize,
    float amplification,
    float erosionStrength,
    float continentalness,
    int flatPadHalfExtent,
    int transitionBlocks)
{
    int chebyshev = max(abs(x), abs(z));
    if (chebyshev <= flatPadHalfExtent)
    {
        return 0;
    }

    int climateSeed = seed ^ thsClimateSeedSalt;
    float freq = 1.0 / max(biomeSize, 1e-4);
    float temperature = thsNoise01(float(x) * 0.008 * freq, float(z) * 0.008 * freq, climateSeed);
    float humidity = thsNoise01(
        float(x) * 0.0095 * freq,
        float(z) * 0.0095 * freq,
        climateSeed ^ int(0xA5A5A5A5u));
    float continental = thsNoise01(
        float(x) * 0.0065 * freq,
        float(z) * 0.0065 * freq,
        climateSeed ^ 0x3C6EF372);
    continental = clamp((continental - 0.5) * continentalness + 0.5, 0.0, 1.0);

    float wMountains;
    float wDesert;
    float wBeach;
    float wPlains;
    thsComputeBiomeWeights(
        temperature,
        humidity,
        continental,
        thsBiomeBlendHalfWidth,
        wMountains,
        wDesert,
        wBeach,
        wPlains);

    const float eps = 1e-4;
    float h = 0.0;
    if (wPlains > eps)
    {
        h += wPlains * thsSamplePlainsHeightContinuous(x, z, seed);
    }

    if (wDesert > eps)
    {
        h += wDesert * thsSampleDesertHeightContinuous(x, z, seed);
    }

    if (wBeach > eps)
    {
        h += wBeach * thsSampleBeachHeightContinuous(x, z, seed);
    }

    if (wMountains > eps)
    {
        h += wMountains * thsSampleMountainHeightContinuous(x, z, seed, erosionStrength);
    }

    h *= amplification;
    float maxAbs = ceil(float(thsMountainMaxRelief) * amplification);
    h = clamp(h, -maxAbs, maxAbs);
    int relief = int(round(h));
    return thsApplyPadTransition(relief, chebyshev, flatPadHalfExtent, transitionBlocks);
}

vec2 thsSampleColumnSurfaceBottom(
    int x,
    int z,
    int seed,
    float biomeSize,
    float amplification,
    float erosionStrength,
    float continentalness,
    int flatPadHalfExtent,
    int transitionBlocks,
    int fillDepth)
{
    int surface = thsSampleColumnHeight(
        x,
        z,
        seed,
        biomeSize,
        amplification,
        erosionStrength,
        continentalness,
        flatPadHalfExtent,
        transitionBlocks);
    float bottom = float(thsSolidBottomY(surface, fillDepth));
    return vec2(float(surface), bottom);
}

// Block kinds mirror PreviewTerrainBlockKind.
const int thsBlockGrass = 0;
const int thsBlockDirt = 1;
const int thsBlockSand = 2;
const int thsBlockGravel = 3;
const int thsBlockStone = 4;

// Biomes mirror PreviewTerrainBiomeId.
const int thsBiomePlains = 0;
const int thsBiomeDesert = 1;
const int thsBiomeBeach = 2;
const int thsBiomeMountains = 3;

int thsDominantBiome(float mountains, float desert, float beach, float plains)
{
    int best = thsBiomePlains;
    float bestW = plains;
    if (beach >= bestW)
    {
        best = thsBiomeBeach;
        bestW = beach;
    }

    if (desert >= bestW)
    {
        best = thsBiomeDesert;
        bestW = desert;
    }

    if (mountains >= bestW)
    {
        best = thsBiomeMountains;
    }

    return best;
}

/// Column board sample: height, bottom, biome, surface, subsurface, deep (Stage-2).
void thsSampleColumnBoard(
    int x,
    int z,
    int seed,
    float biomeSize,
    float amplification,
    float erosionStrength,
    float continentalness,
    int flatPadHalfExtent,
    int transitionBlocks,
    int fillDepth,
    out int outHeight,
    out int outBottom,
    out int outBiome,
    out int outSurface,
    out int outSubsurface,
    out int outDeep)
{
    int chebyshev = max(abs(x), abs(z));
    if (chebyshev <= flatPadHalfExtent)
    {
        outHeight = 0;
        outBottom = thsSolidBottomY(0, fillDepth);
        outBiome = thsBiomePlains;
        outSurface = thsBlockGrass;
        outSubsurface = thsBlockDirt;
        outDeep = thsBlockStone;
        return;
    }

    int climateSeed = seed ^ thsClimateSeedSalt;
    float freq = 1.0 / max(biomeSize, 1e-4);
    float temperature = thsNoise01(float(x) * 0.008 * freq, float(z) * 0.008 * freq, climateSeed);
    float humidity = thsNoise01(
        float(x) * 0.0095 * freq,
        float(z) * 0.0095 * freq,
        climateSeed ^ int(0xA5A5A5A5u));
    float continental = thsNoise01(
        float(x) * 0.0065 * freq,
        float(z) * 0.0065 * freq,
        climateSeed ^ 0x3C6EF372);
    continental = clamp((continental - 0.5) * continentalness + 0.5, 0.0, 1.0);

    float wMountains;
    float wDesert;
    float wBeach;
    float wPlains;
    thsComputeBiomeWeights(
        temperature,
        humidity,
        continental,
        thsBiomeBlendHalfWidth,
        wMountains,
        wDesert,
        wBeach,
        wPlains);

    outHeight = thsSampleColumnHeight(
        x,
        z,
        seed,
        biomeSize,
        amplification,
        erosionStrength,
        continentalness,
        flatPadHalfExtent,
        transitionBlocks);
    outBottom = thsSolidBottomY(outHeight, fillDepth);
    outBiome = thsDominantBiome(wMountains, wDesert, wBeach, wPlains);

    if (outBiome == thsBiomeDesert || outBiome == thsBiomeBeach)
    {
        outSurface = thsBlockSand;
        outSubsurface = thsBlockSand;
        outDeep = thsBlockStone;
    }
    else if (outBiome == thsBiomeMountains)
    {
        bool rocky = (continental > 0.72) || (outHeight >= (thsMaxRelief + 4));
        outSurface = rocky ? thsBlockStone : thsBlockGrass;
        outSubsurface = rocky ? thsBlockGravel : thsBlockDirt;
        outDeep = thsBlockStone;
    }
    else
    {
        outSurface = thsBlockGrass;
        outSubsurface = thsBlockDirt;
        outDeep = thsBlockStone;
    }
}

#endif
