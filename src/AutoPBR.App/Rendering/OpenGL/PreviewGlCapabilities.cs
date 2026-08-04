using System.Globalization;
using System.Runtime.InteropServices;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed record PreviewGlCapabilities(
    string VersionString,
    string Vendor,
    string Renderer,
    int Major,
    int Minor,
    bool IsOpenGlEs,
    bool BufferStorage,
    bool PersistentMappedBuffers,
    bool ShaderStorageBuffers,
    bool ComputeShaders,
    bool ImageLoadStore,
    bool ShaderAtomics,
    bool MultiDrawIndirect,
    bool IndirectParameters,
    bool ShaderDrawParameters,
    bool TimerQuery,
    bool TextureArrays,
    bool BindlessTextures,
    bool SpirV,
    bool SeparablePrograms,
    int MaxColorAttachments,
    int MaxDrawBuffers)
{
    public bool CanUsePersistentUploadRing => !IsOpenGlEs && PersistentMappedBuffers;

    public bool CanUseEntitySkinningSsbo => !IsOpenGlEs && ShaderStorageBuffers;

    public bool CanUseMaterialDrawRecordSsbo => !IsOpenGlEs && ShaderStorageBuffers;

    public bool CanUseComputeFroxelInject => !IsOpenGlEs && ComputeShaders && ImageLoadStore;

    public bool CanUseIndirectDrawCommands => !IsOpenGlEs && MultiDrawIndirect;

    public bool CanUseMultiDrawIndirectGroups =>
        CanUseIndirectDrawCommands && ShaderDrawParameters && ShaderStorageBuffers;

    public bool CanUseGpuCommandCompaction =>
        !IsOpenGlEs && ComputeShaders && ShaderStorageBuffers && ShaderAtomics && MultiDrawIndirect;

    public bool CanUseGpuBatchCulling => CanUseGpuCommandCompaction;

    public bool CanUseGpuCompactedDrawSubmission =>
        CanUseGpuBatchCulling && IndirectParameters && ShaderDrawParameters;

    public bool CanUseHierarchicalZOcclusion =>
        CanUseGpuCompactedDrawSubmission && ImageLoadStore;

    /// <summary>
    /// GPU terrain shadow cull writes MultiDrawIndirect commands + counters for
    /// <c>MultiDrawIndirectCount</c> (no index-list readback). Requires the same desktop stack as
    /// compacted subject draws.
    /// </summary>
    public bool CanUseGpuTerrainShadowCull => CanUseGpuCompactedDrawSubmission;

    public bool CanUseGpuReductionDiagnostics => CanUseGpuCommandCompaction;

    public bool CanUseImageHistogram =>
        !IsOpenGlEs && ComputeShaders && ImageLoadStore && ShaderStorageBuffers && ShaderAtomics;

    public bool CanUseMaterialTextureArrays =>
        !IsOpenGlEs && TextureArrays && ShaderStorageBuffers;

    public bool CanUseGpuTimerQueries => !IsOpenGlEs && TimerQuery;

    public bool CanUseSpirVShaderBinaries => !IsOpenGlEs && SpirV;

    public bool CanUseSeparableShaderPrograms => !IsOpenGlEs && SeparablePrograms;

    /// <summary>
    /// Desktop GL 3.3 guarantees color-renderable RGBA16F and RG32F textures. GLES/ANGLE keeps
    /// the packed RGBA8 cloud path so its MRT requirements and driver behavior remain unchanged.
    /// Allocation completeness is still verified at runtime before this path becomes active.
    /// </summary>
    public bool CanUseFloatingPointCloudTargets => !IsOpenGlEs && Major >= 3;

    /// <summary>
    /// CQ1.6 temporal moments add a third cloud color attachment. Keep this desktop-only and
    /// require both attachment and draw-buffer limits before attempting the RG16F profile.
    /// Framebuffer completeness remains the final runtime authority.
    /// </summary>
    public bool CanUseCloudTemporalMoments =>
        CanUseFloatingPointCloudTargets &&
        MaxColorAttachments >= 3 &&
        MaxDrawBuffers >= 3;

    public bool CanUseFragmentCloudLightingCache =>
        !IsOpenGlEs && Major >= 3 && TextureArrays;

    public bool CanUseComputeCloudLightingCache =>
        CanUseFragmentCloudLightingCache &&
        (Major > 4 || Major == 4 && Minor >= 3) &&
        ComputeShaders &&
        ImageLoadStore;

    /// <summary>
    /// CQ4 logical sparse cloud volumes require the desktop compute path, image load/store for
    /// atlas and page-table generation, and SSBOs for bounded request/residency queues. This is a
    /// hardware/API capability only; shader, asset, and resource readiness remain runtime gates.
    /// </summary>
    public bool CanUseSparseCloudVolumes =>
        !IsOpenGlEs &&
        ComputeShaders &&
        ImageLoadStore &&
        ShaderStorageBuffers;

    /// <summary>
    /// Screen-space AO needs a third scene-capture color attachment for view-space normals.
    /// </summary>
    public bool CanUseScreenSpaceAo =>
        MaxColorAttachments >= 3 &&
        MaxDrawBuffers >= 3;

    public string UploadTransportLabel => CanUsePersistentUploadRing ? "persistent-mapped UBO uploads" : "BufferSubData uploads";

    public string FormatDiagnostic()
    {
        var api = IsOpenGlEs ? "GLES" : "desktop GL";
        return "[3D preview] GL capabilities: " +
               $"{api} {Major}.{Minor}; " +
               $"persistentUpload={(CanUsePersistentUploadRing ? "on" : "off")}, " +
               $"ssbo={(ShaderStorageBuffers ? "yes" : "no")}, " +
               $"entitySsbo={(CanUseEntitySkinningSsbo ? "on" : "off")}, " +
               $"materialDrawSsbo={(CanUseMaterialDrawRecordSsbo ? "on" : "off")}, " +
               $"computeFroxels={(CanUseComputeFroxelInject ? "on" : "off")}, " +
               $"indirectDraws={(CanUseIndirectDrawCommands ? "on" : "off")}, " +
               $"multiDrawGroups={(CanUseMultiDrawIndirectGroups ? "on" : "off")}, " +
               $"gpuCommandCompaction={(CanUseGpuCommandCompaction ? "on" : "off")}, " +
               $"gpuBatchCulling={(CanUseGpuBatchCulling ? "on" : "off")}, " +
               $"gpuCompactedDraws={(CanUseGpuCompactedDrawSubmission ? "on" : "off")}, " +
               $"hiZOcclusion={(CanUseHierarchicalZOcclusion ? "on" : "off")}, " +
               $"voxelDdaOcclusion={(CanUseGpuCompactedDrawSubmission ? "on" : "off")}, " +
               $"gpuTerrainShadowCull={(CanUseGpuTerrainShadowCull ? "on" : "off")}, " +
               $"gpuReductions={(CanUseGpuReductionDiagnostics ? "on" : "off")}, " +
               $"imageHistogram={(CanUseImageHistogram ? "on" : "off")}, " +
               $"materialTextureArrays={(CanUseMaterialTextureArrays ? "on" : "off")}, " +
               $"cloudFpTargets={(CanUseFloatingPointCloudTargets ? "on" : "off")}, " +
               $"cloudMoments={(CanUseCloudTemporalMoments ? "on" : "off")}({MaxColorAttachments}/{MaxDrawBuffers}), " +
               $"cloudLightCacheFragment={(CanUseFragmentCloudLightingCache ? "on" : "off")}, " +
               $"cloudLightCacheCompute={(CanUseComputeCloudLightingCache ? "on" : "off")}, " +
               $"sparseCloudVolumes={(CanUseSparseCloudVolumes ? "on" : "off")}, " +
               $"screenSpaceAo={(CanUseScreenSpaceAo ? "on" : "off")}, " +
               $"gpuTimers={(CanUseGpuTimerQueries ? "on" : "off")}, " +
               $"compute={(ComputeShaders ? "yes" : "no")}, " +
               $"imageStore={(ImageLoadStore ? "yes" : "no")}, " +
               $"multiDrawIndirect={(MultiDrawIndirect ? "yes" : "no")}, " +
               $"drawParameters={(ShaderDrawParameters ? "yes" : "no")}, " +
               $"timerQuery={(TimerQuery ? "yes" : "no")}, " +
               $"spirv={(SpirV ? "yes" : "no")}, " +
               $"separablePrograms={(SeparablePrograms ? "yes" : "no")}.";
    }

    public string FormatContextSuffix()
    {
        var upload = CanUsePersistentUploadRing ? "persistent uploads" : "GLES-safe uploads";
        var entitySkinning = CanUseEntitySkinningSsbo ? "entity SSBO" : "entity UBO";
        var drawRecords = CanUseMaterialDrawRecordSsbo ? "draw SSBO" : "draw uniforms";
        var materialTextures = CanUseMaterialTextureArrays ? "material arrays" : "material samplers";
        var froxelInject = CanUseComputeFroxelInject ? "compute froxels" : "fragment froxels";
        var cloudTargets = CanUseFloatingPointCloudTargets
            ? CanUseCloudTemporalMoments ? "FP cloud targets + moments" : "FP cloud targets"
            : "RGBA8 clouds";
        var cloudDensity = CanUseSparseCloudVolumes
            ? "sparse-cloud capable"
            : "procedural clouds";
        var gpuTimers = CanUseGpuTimerQueries ? "GPU timers" : "no GPU timers";
        var drawCommands = CanUseMultiDrawIndirectGroups
            ? "multi-draw groups"
            : CanUseIndirectDrawCommands ? "indirect draws" : "direct draws";
        return $" · {upload} · {entitySkinning} · {drawRecords} · {materialTextures} · {froxelInject} · {cloudTargets} · {cloudDensity} · {drawCommands} · {gpuTimers}";
    }

    public static PreviewGlCapabilities FromGl(GL gl, bool useOpenGlEs, string versionString)
    {
        var vendor = ReadGlString(gl, StringName.Vendor);
        var renderer = ReadGlString(gl, StringName.Renderer);
        var extensions = ReadExtensionString(gl);
        var capabilities = FromStrings(versionString, vendor, renderer, extensions, useOpenGlEs);
        var maxColorAttachments = Math.Max(1, gl.GetInteger(GetPName.MaxColorAttachments));
        var maxDrawBuffers = Math.Max(1, gl.GetInteger(GetPName.MaxDrawBuffers));
        return capabilities with
        {
            MaxColorAttachments = maxColorAttachments,
            MaxDrawBuffers = maxDrawBuffers,
        };
    }

    internal static PreviewGlCapabilities FromStrings(
        string versionString,
        string vendor,
        string renderer,
        string extensions,
        bool? forceOpenGlEs = null,
        int? maxColorAttachments = null,
        int? maxDrawBuffers = null)
    {
        var isEs = forceOpenGlEs ?? versionString.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        var (major, minor) = ParseVersion(versionString);
        var extensionSet = BuildExtensionSet(extensions);

        bool HasExtension(string name) => extensionSet.Contains(name);
        bool VersionAtLeast(int reqMajor, int reqMinor) =>
            major > reqMajor || (major == reqMajor && minor >= reqMinor);

        var textureArrays = isEs
            ? VersionAtLeast(3, 0)
            : VersionAtLeast(3, 0) || HasExtension("GL_EXT_texture_array");
        var bufferStorage = !isEs && (VersionAtLeast(4, 4) || HasExtension("GL_ARB_buffer_storage"));
        var ssbo = !isEs && (VersionAtLeast(4, 3) || HasExtension("GL_ARB_shader_storage_buffer_object"));
        var compute = !isEs && (VersionAtLeast(4, 3) || HasExtension("GL_ARB_compute_shader"));
        var imageStore = !isEs && (VersionAtLeast(4, 2) || HasExtension("GL_ARB_shader_image_load_store"));
        var atomics = !isEs && (VersionAtLeast(4, 2) || HasExtension("GL_ARB_shader_atomic_counters"));
        var mdi = !isEs && (VersionAtLeast(4, 3) || HasExtension("GL_ARB_multi_draw_indirect"));
        var indirectParameters = !isEs &&
                                 (VersionAtLeast(4, 6) || HasExtension("GL_ARB_indirect_parameters"));
        var drawParameters = !isEs && (VersionAtLeast(4, 6) || HasExtension("GL_ARB_shader_draw_parameters"));
        var timerQuery = isEs
            ? HasExtension("GL_EXT_disjoint_timer_query")
            : VersionAtLeast(3, 3) || HasExtension("GL_ARB_timer_query");
        var bindless = !isEs && HasExtension("GL_ARB_bindless_texture");
        var spirv = !isEs && (VersionAtLeast(4, 6) || HasExtension("GL_ARB_gl_spirv"));
        var separable = !isEs && (VersionAtLeast(4, 1) || HasExtension("GL_ARB_separate_shader_objects"));
        var defaultColorAttachments = major >= 3 ? (isEs ? 4 : 8) : 1;
        var defaultDrawBuffers = major >= 3 ? (isEs ? 4 : 8) : 1;

        return new PreviewGlCapabilities(
            string.IsNullOrWhiteSpace(versionString) ? "(unknown)" : versionString,
            string.IsNullOrWhiteSpace(vendor) ? "unknown" : vendor,
            string.IsNullOrWhiteSpace(renderer) ? "unknown" : renderer,
            major,
            minor,
            isEs,
            bufferStorage,
            bufferStorage,
            ssbo,
            compute,
            imageStore,
            atomics,
            mdi,
            indirectParameters,
            drawParameters,
            timerQuery,
            textureArrays,
            bindless,
            spirv,
            separable,
            Math.Max(1, maxColorAttachments ?? defaultColorAttachments),
            Math.Max(1, maxDrawBuffers ?? defaultDrawBuffers));
    }

    private static (int Major, int Minor) ParseVersion(string versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return (0, 0);
        }

        var span = versionString.AsSpan().TrimStart();
        var digitStart = -1;
        for (var i = 0; i < span.Length; i++)
        {
            if (char.IsDigit(span[i]))
            {
                digitStart = i;
                break;
            }
        }

        if (digitStart < 0)
        {
            return (0, 0);
        }

        span = span[digitStart..];
        var dot = span.IndexOf('.');
        if (dot <= 0)
        {
            return (0, 0);
        }

        var minorEnd = dot + 1;
        while (minorEnd < span.Length && char.IsDigit(span[minorEnd]))
        {
            minorEnd++;
        }

        if (!int.TryParse(span[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(span[(dot + 1)..minorEnd], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return (0, 0);
        }

        return (major, minor);
    }

    private static HashSet<string> BuildExtensionSet(string extensions)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(extensions))
        {
            return set;
        }

        foreach (var extension in extensions.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(extension);
        }

        return set;
    }

    private static string ReadGlString(GL gl, StringName name)
    {
        unsafe
        {
            var ptr = gl.GetString(name);
            return ptr is null ? string.Empty : Marshal.PtrToStringUTF8((nint)ptr) ?? string.Empty;
        }
    }

    private static string ReadExtensionString(GL gl)
    {
        while (gl.GetError() != GLEnum.NoError)
        {
        }

        var extensions = ReadGlString(gl, StringName.Extensions);
        while (gl.GetError() != GLEnum.NoError)
        {
        }

        return extensions;
    }
}
