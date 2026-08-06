using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Scene;
using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Stage-2 LOD≥3 section meshing. Production uses CPU
/// <see cref="PreviewTerrainLodMeshBaker.BakeLodSection"/> on the budgeted pump (veg keep-mask
/// included). Desktop compute fills a sample-step height board for live parity / future emit.
/// </summary>
internal sealed class GlTerrainGpuLodMeshBaker : IDisposable
{
    public const int BoardSide = 18;
    public const int JobsPerFrameBudget = 2;
    public const byte MinLodLevel = PreviewStageConstants.TerrainGpuLodMinLevel;

    private const uint ShaderStorageBarrierBit = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LodColumn
    {
        public int Height;
        public int Bottom;
        public int Biome;
        public int Surface;
        public int Pad0;
        public int Pad1;
        public int Pad2;
        public int Pad3;
    }

    private readonly GL _gl;
    private GlShaderProgram? _boardProgram;
    private uint _boardSsbo;
    private bool _disposed;
    private bool _healthy;

    public GlTerrainGpuLodMeshBaker(GL gl) => _gl = gl;

    public bool IsHealthy => _healthy && !_disposed;
    public string? LastError { get; private set; }

    public void ConfigurePrograms(GlShaderProgram? board)
    {
        if (_disposed)
        {
            return;
        }

        _boardProgram = board is { IsValid: true } ? board : null;
        if (_boardProgram is not null)
        {
            EnsureBuffers();
            if (_boardSsbo == 0)
            {
                LastError = "SSBO allocation failed";
                _healthy = true; // CPU TryBake still available
                return;
            }
        }
        else
        {
            DisposeBuffers();
        }

        // Stage-2 production path is CPU BakeLodSection; board is optional parity.
        _healthy = true;
        LastError = _boardProgram is null ? "lod board program missing (CPU TryBake ok)" : null;
    }

    public void Demote(string reason)
    {
        LastError = reason;
        _healthy = false;
    }

    /// <summary>
    /// Production Stage-2 bake: CPU combined LOD section (hull + keep-masked vegetation).
    /// </summary>
    public PreviewTerrainChunkMesh? TryBake(in TerrainGpuLodJob job)
    {
        if (_disposed || !_healthy)
        {
            return null;
        }

        if (job.Key.LodLevel < MinLodLevel)
        {
            LastError = $"LOD level {job.Key.LodLevel} below GPU Stage-2 minimum {MinLodLevel}";
            return null;
        }

        return PreviewTerrainLodMeshBaker.BakeLodSection(
            job.Key,
            job.WorldGen,
            job.GrassSettings,
            job.Vegetation);
    }

    /// <summary>
    /// Live parity: dispatch the LOD height board, verify a center cell against CPU sampling,
    /// then return a CPU BakeLodSection mesh (veg included) so smoke can exercise the GL path.
    /// </summary>
    public PreviewTerrainChunkMesh? TryBakeGpuBoardParity(in TerrainGpuLodJob job)
    {
        if (!IsHealthy || _boardProgram is null)
        {
            return null;
        }

        if (job.Key.LodLevel < MinLodLevel)
        {
            LastError = $"LOD level {job.Key.LodLevel} below GPU Stage-2 minimum {MinLodLevel}";
            return null;
        }

        var gen = PreviewTerrainWorldGenSettings.Resolve(job.WorldGen);
        var sampleStep = Math.Max(1, job.Key.SampleStepMeters);
        var originX = job.Key.OriginWorldX();
        var originZ = job.Key.OriginWorldZ();
        var boardOriginX = originX - sampleStep;
        var boardOriginZ = originZ - sampleStep;

        _boardProgram.Use();
        SetUniform2i(_boardProgram, "uBoardOrigin", boardOriginX, boardOriginZ);
        SetUniform1i(_boardProgram, "uBoardSide", BoardSide);
        SetUniform1i(_boardProgram, "uSampleStep", sampleStep);
        SetUniform1i(_boardProgram, "uSeed", gen.Seed);
        SetUniform1f(_boardProgram, "uBiomeSize", gen.BiomeSize);
        SetUniform1f(_boardProgram, "uAmplification", gen.Amplification);
        SetUniform1f(_boardProgram, "uErosionStrength", gen.ErosionStrength);
        SetUniform1f(_boardProgram, "uContinentalness", gen.Continentalness);
        SetUniform1i(_boardProgram, "uFlatPadHalfExtent", PreviewStageConstants.TerrainFlatPadHalfExtent);
        SetUniform1i(_boardProgram, "uTransitionBlocks", PreviewStageConstants.TerrainTransitionBlocks);
        SetUniform1i(_boardProgram, "uFillDepth", PreviewStageConstants.TerrainFillDepth);

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _boardSsbo);
        var groups = (uint)((BoardSide + 7) / 8);
        _gl.DispatchCompute(groups, groups, 1);
        _gl.MemoryBarrier(ShaderStorageBarrierBit);

        var columns = new LodColumn[BoardSide * BoardSide];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _boardSsbo);
        _gl.GetBufferSubData<LodColumn>(BufferTargetARB.ShaderStorageBuffer, 0, columns);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, 0);
        _gl.UseProgram(0);

        // Center interior cell (index 1,1) should match max height over the sampleStep window.
        var cpuMax = int.MinValue;
        for (var dz = 0; dz < sampleStep; dz++)
        {
            for (var dx = 0; dx < sampleStep; dx++)
            {
                cpuMax = Math.Max(
                    cpuMax,
                    PreviewTerrainHeightfield.SampleColumn(originX + dx, originZ + dz, gen));
            }
        }

        var gpuHeight = columns[1 * BoardSide + 1].Height;
        if (gpuHeight != cpuMax)
        {
            LastError = $"lod board height mismatch gpu={gpuHeight} cpu={cpuMax}";
            return null;
        }

        return PreviewTerrainLodMeshBaker.BakeLodSection(
            job.Key,
            job.WorldGen,
            job.GrassSettings,
            job.Vegetation);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _healthy = false;
        DisposeBuffers();
        _boardProgram = null;
    }

    private void EnsureBuffers()
    {
        DisposeBuffers();
        var bytes = (nuint)(BoardSide * BoardSide * Marshal.SizeOf<LodColumn>());
        _boardSsbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _boardSsbo);
        unsafe
        {
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, bytes, null, BufferUsageARB.DynamicDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void DisposeBuffers()
    {
        if (_boardSsbo != 0)
        {
            _gl.DeleteBuffer(_boardSsbo);
            _boardSsbo = 0;
        }
    }

    private void SetUniform1i(GlShaderProgram program, string name, int value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform1(loc, value);
        }
    }

    private void SetUniform1f(GlShaderProgram program, string name, float value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform1(loc, value);
        }
    }

    private void SetUniform2i(GlShaderProgram program, string name, int x, int y)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform2(loc, x, y);
        }
    }
}
