using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Analytical advanced terrain erosion filter (CPU).
/// Adds branching gullies/ridges without hydraulic simulation — evaluable per sample
/// for infinite / chunked worlds.
/// </summary>
/// <remarks>
/// Core Phacelle noise + erosion filter algorithm:
/// Copyright (c) 2025 Rune Skovbo Johansen — Mozilla Public License 2.0.
/// Shadertoy: https://www.shadertoy.com/view/wXcfWn
/// Blog: https://blog.runevision.com/2026/03/fast-and-gorgeous-erosion-filter.html
///
/// This file is a Covered Software adaptation under MPL-2.0. See
/// <c>LICENSE-MPL-2.0.txt</c> beside this source and
/// https://mozilla.org/MPL/2.0/
///
/// Gradient noise (<see cref="Noised"/>) derived from Inigo Quilez (MIT).
/// </remarks>
public static class PreviewTerrainAdvancedErosion
{
    private const float Tau = MathF.PI * 2f;

    /// <summary>Defaults tuned for AutoPBR mountain columns (block-scale relief).</summary>
    public static ErosionFilterParams MountainParams { get; } = new(
        Scale: 0.18f,
        Strength: 0.20f,
        GullyWeight: 0.55f,
        Detail: 1.45f,
        Rounding: new Vector4(0.1f, 0.0f, 0.1f, 2.0f),
        Onset: new Vector4(1.25f, 1.25f, 2.8f, 1.5f),
        AssumedSlope: new Vector2(0.7f, 1.0f),
        CellScale: 0.7f,
        Normalization: 0.5f,
        Octaves: 4,
        Lacunarity: 2.0f,
        Gain: 0.5f);

    public readonly record struct ErosionFilterParams(
        float Scale,
        float Strength,
        float GullyWeight,
        float Detail,
        Vector4 Rounding,
        Vector4 Onset,
        Vector2 AssumedSlope,
        float CellScale,
        float Normalization,
        int Octaves,
        float Lacunarity,
        float Gain);

    public readonly record struct ErosionFilterResult(
        Vector3 Delta,
        float Magnitude,
        float RidgeMap,
        float Debug);

    /// <summary>Inigo Quilez 2D hash → roughly [-1, 1].</summary>
    public static Vector2 Hash2(Vector2 xIn)
    {
        var k = new Vector2(0.3183099f, 0.3678794f);
        var x = xIn * k + new Vector2(k.Y, k.X);
        var scalar = Fract(x.X * x.Y * (x.X + x.Y));
        var v = new Vector2(16f * k.X * scalar, 16f * k.Y * scalar);
        return -Vector2.One + 2f * new Vector2(Fract(v.X), Fract(v.Y));
    }

    /// <summary>Gradient noise with analytical derivatives: (value, ∂/∂x, ∂/∂y).</summary>
    public static Vector3 Noised(Vector2 p)
    {
        var i = new Vector2(MathF.Floor(p.X), MathF.Floor(p.Y));
        var f = p - i;

        var u = f * f * f * (f * (f * 6f - new Vector2(15f)) + new Vector2(10f));
        var du = 30f * f * f * (f * (f - new Vector2(2f)) + Vector2.One);

        var ga = Hash2(i);
        var gb = Hash2(i + new Vector2(1f, 0f));
        var gc = Hash2(i + new Vector2(0f, 1f));
        var gd = Hash2(i + new Vector2(1f, 1f));

        var va = Vector2.Dot(ga, f);
        var vb = Vector2.Dot(gb, f - new Vector2(1f, 0f));
        var vc = Vector2.Dot(gc, f - new Vector2(0f, 1f));
        var vd = Vector2.Dot(gd, f - new Vector2(1f, 1f));

        var value = va + u.X * (vb - va) + u.Y * (vc - va) + u.X * u.Y * (va - vb - vc + vd);
        var deriv =
            ga
            + u.X * (gb - ga)
            + u.Y * (gc - ga)
            + u.X * u.Y * (ga - gb - gc + gd)
            + du * (new Vector2(u.Y, u.X) * (va - vb - vc + vd) + new Vector2(vb, vc) - new Vector2(va));

        return new Vector3(value, deriv.X, deriv.Y);
    }

    /// <summary>fBm from <see cref="Noised"/> → (value, ∂/∂x, ∂/∂y).</summary>
    public static Vector3 Fbm(Vector2 p, float frequency, int octaves, float lacunarity, float gain)
    {
        var n = Vector3.Zero;
        var freq = frequency;
        var amp = 1f;
        for (var o = 0; o < octaves; o++)
        {
            var s = Noised(p * freq);
            n += new Vector3(s.X * amp, s.Y * amp * freq, s.Z * amp * freq);
            amp *= gain;
            freq *= lacunarity;
        }

        return n;
    }

    /// <summary>
    /// Rune Skovbo Johansen Phacelle noise.
    /// Returns (cos, sin, sideDir.X, sideDir.Y) with sideDir pre-multiplied by freq·τ.
    /// </summary>
    public static Vector4 PhacelleNoise(
        Vector2 p,
        Vector2 normDir,
        float freq,
        float offsetCycles,
        float normalization)
    {
        var sideDir = new Vector2(-normDir.Y, normDir.X) * (freq * Tau);
        var offset = offsetCycles * Tau;

        var pInt = new Vector2(MathF.Floor(p.X), MathF.Floor(p.Y));
        var pFrac = p - pInt;

        var phaseDir = Vector2.Zero;
        var weightSum = 0f;

        for (var i = -1; i <= 2; i++)
        {
            for (var j = -1; j <= 2; j++)
            {
                var gridOffset = new Vector2(i, j);
                var gridPoint = pInt + gridOffset;
                var randomOffset = Hash2(gridPoint) * 0.5f;
                var v = pFrac - gridOffset - randomOffset;

                var sqrDist = Vector2.Dot(v, v);
                var weight = MathF.Max(MathF.Exp(-sqrDist * 2f) - 0.01111f, 0f);
                weightSum += weight;

                var waveInput = Vector2.Dot(v, sideDir) + offset;
                phaseDir += new Vector2(MathF.Cos(waveInput), MathF.Sin(waveInput)) * weight;
            }
        }

        var interpolated = phaseDir / MathF.Max(weightSum, 1e-10f);
        var magRaw = interpolated.Length();
        var magnitude = MathF.Max(1f - normalization, magRaw);
        var normalized = interpolated / magnitude;
        return new Vector4(normalized.X, normalized.Y, sideDir.X, sideDir.Y);
    }

    /// <summary>
    /// Advanced erosion filter. <paramref name="baseHeightAndSlope"/> is (h, ∂h/∂x, ∂h/∂y).
    /// </summary>
    public static ErosionFilterResult ErosionFilter(
        Vector2 p,
        Vector3 baseHeightAndSlope,
        float fadeTargetIn,
        in ErosionFilterParams parameters)
    {
        var strength = parameters.Strength * parameters.Scale;
        var fadeTarget = Math.Clamp(fadeTargetIn, -1f, 1f);

        var input = baseHeightAndSlope;
        var hAndS = baseHeightAndSlope;

        var freq = 1f / (parameters.Scale * parameters.CellScale);
        var slope = new Vector2(hAndS.Y, hAndS.Z);
        var slopeLength = MathF.Max(slope.Length(), 1e-10f);
        var magnitude = 0f;
        var roundingMult = 1f;

        var roundingForInput = Lerp(
            parameters.Rounding.Y,
            parameters.Rounding.X,
            Clamp01(fadeTarget + 0.5f)) * parameters.Rounding.Z;
        var combiMask = EaseOut(SmoothStart(
            slopeLength * parameters.Onset.X,
            roundingForInput * parameters.Onset.X));

        var ridgeMapCombiMask = EaseOut(slopeLength * parameters.Onset.Z);
        var ridgeMapFadeTarget = fadeTarget;

        var gullySlope = Vector2.Lerp(
            slope,
            slope / slopeLength * parameters.AssumedSlope.X,
            parameters.AssumedSlope.Y);

        var octaves = Math.Clamp(parameters.Octaves, 1, 8);
        for (var o = 0; o < octaves; o++)
        {
            var phacelle = PhacelleNoise(
                p * freq,
                SafeNormalize(gullySlope),
                parameters.CellScale,
                offsetCycles: 0.25f,
                parameters.Normalization);
            var pZw = new Vector2(phacelle.Z, phacelle.W) * -freq;
            var sloping = MathF.Abs(phacelle.Y);

            gullySlope += MathF.Sign(phacelle.Y) * pZw * strength * parameters.GullyWeight;

            var octaveHAndS = new Vector3(phacelle.X, phacelle.Y * pZw.X, phacelle.Y * pZw.Y);
            var faded = Vector3.Lerp(
                new Vector3(fadeTarget, 0f, 0f),
                octaveHAndS * parameters.GullyWeight,
                combiMask);
            hAndS += faded * strength;
            magnitude += strength;

            fadeTarget = faded.X;

            var roundingForOctave = Lerp(
                parameters.Rounding.Y,
                parameters.Rounding.X,
                Clamp01(phacelle.X + 0.5f)) * roundingMult;
            var newMask = EaseOut(SmoothStart(
                sloping * parameters.Onset.Y,
                roundingForOctave * parameters.Onset.Y));
            combiMask = PowInv(combiMask, parameters.Detail) * newMask;

            ridgeMapFadeTarget = Lerp(ridgeMapFadeTarget, octaveHAndS.X, ridgeMapCombiMask);
            var newRidgeMask = EaseOut(sloping * parameters.Onset.W);
            ridgeMapCombiMask *= newRidgeMask;

            strength *= parameters.Gain;
            freq *= parameters.Lacunarity;
            roundingMult *= parameters.Rounding.W;
        }

        return new ErosionFilterResult(
            Delta: hAndS - input,
            Magnitude: magnitude,
            RidgeMap: ridgeMapFadeTarget * (1f - ridgeMapCombiMask),
            Debug: fadeTarget);
    }

    /// <summary>
    /// Sample eroded mountain height in approximately [-1, 1] with optional seed offset.
    /// <paramref name="erosionStrength"/> scales the analytical gully carve (1 = default).
    /// </summary>
    public static float SampleErodedMountainNormalized(
        float worldX,
        float worldZ,
        int seed,
        float erosionStrength = 1f)
    {
        erosionStrength = Math.Clamp(
            erosionStrength,
            PreviewStageConstants.TerrainMinErosionStrength,
            PreviewStageConstants.TerrainMaxErosionStrength);

        // Domain offset from seed so different worlds don't share the same gully lattice.
        unchecked
        {
            var ox = ((seed * 0x27D4EB2D) & 0xFFFF) / 65535f * 40f;
            var oz = ((seed * unchecked((int)0x85EBCA6B)) & 0xFFFF) / 65535f * 40f;
            var p = new Vector2(worldX * 0.028f + ox, worldZ * 0.028f + oz);

            const float amp = 0.32f;
            var basis = Fbm(p, frequency: 2.4f, octaves: 3, lacunarity: 2f, gain: 0.18f);
            basis *= amp;

            var fadeTarget = Math.Clamp(basis.X / (amp * 0.65f), -1f, 1f);
            var parameters = MountainParams with
            {
                Strength = MountainParams.Strength * MathF.Max(erosionStrength, 1e-4f)
            };
            var filtered = ErosionFilter(p, basis, fadeTarget, parameters);

            // Shadertoy reference carving bias (−0.65·magnitude) for a mostly-cut look.
            var carve = erosionStrength <= 1e-4f ? 0f : 0.65f * filtered.Magnitude;
            var eroded = basis.X + filtered.Delta.X * erosionStrength - carve;

            // Mild cubic ridge keeps multi-block cliff potential after quantization.
            var ridge = Noised(p * 1.35f + new Vector2(17.1f, -9.3f)).X;
            ridge = 1f - MathF.Abs(ridge);
            ridge = ridge * ridge * ridge;

            var n = eroded * 1.55f + ridge * 0.55f - 0.08f;
            return Math.Clamp(n, -1f, 1f);
        }
    }

    private static float Fract(float v) => v - MathF.Floor(v);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Clamp01(float t) => Math.Clamp(t, 0f, 1f);

    private static float PowInv(float t, float power) => 1f - MathF.Pow(1f - Clamp01(t), power);

    private static float EaseOut(float t)
    {
        var v = 1f - Clamp01(t);
        return 1f - v * v;
    }

    private static float SmoothStart(float t, float smoothing)
    {
        if (t >= smoothing)
        {
            return t - 0.5f * smoothing;
        }

        if (smoothing <= 1e-8f)
        {
            return t;
        }

        return 0.5f * t * t / smoothing;
    }

    private static Vector2 SafeNormalize(Vector2 n)
    {
        var len = n.Length();
        return len > 1e-10f ? n / len : n;
    }
}
