namespace AutoPBR.App.Rendering.OpenGL;

internal readonly record struct GlGpuDrawReductionSnapshot(
    uint ExaminedCommands,
    uint WrittenCommands,
    uint FrustumCulledCommands,
    uint DistanceCulledCommands,
    uint EmptyCommands,
    uint VisibilityFlagCulledCommands,
    uint OverflowCommands,
    uint MaximumIndexCount,
    uint OcclusionCulledCommands = 0)
{
    /// <summary>
    /// Diagnostic dwords after the visible-command counter (examined…occlusion).
    /// Visible count stays at SSBO byte offset 0 for <c>MultiDrawElementsIndirectCount</c>.
    /// </summary>
    public const int DwordCount = 9;

    public uint AccountedCommands =>
        WrittenCommands +
        FrustumCulledCommands +
        DistanceCulledCommands +
        EmptyCommands +
        VisibilityFlagCulledCommands +
        OverflowCommands +
        OcclusionCulledCommands;

    public bool IsConsistent => ExaminedCommands == AccountedCommands;

    public static GlGpuDrawReductionSnapshot FromDwords(ReadOnlySpan<uint> dwords)
    {
        if (dwords.Length < DwordCount)
        {
            throw new ArgumentException($"GPU draw reduction snapshot requires {DwordCount} uints.", nameof(dwords));
        }

        return new GlGpuDrawReductionSnapshot(
            dwords[0],
            dwords[1],
            dwords[2],
            dwords[3],
            dwords[4],
            dwords[5],
            dwords[6],
            dwords[7],
            dwords[8]);
    }

    public string FormatDiagnostic() =>
        $"examined={ExaminedCommands}, written={WrittenCommands}, " +
        $"frustum={FrustumCulledCommands}, distance={DistanceCulledCommands}, " +
        $"empty={EmptyCommands}, flags={VisibilityFlagCulledCommands}, " +
        $"overflow={OverflowCommands}, maxIndices={MaximumIndexCount}, " +
        $"occlusion={OcclusionCulledCommands}";
}
