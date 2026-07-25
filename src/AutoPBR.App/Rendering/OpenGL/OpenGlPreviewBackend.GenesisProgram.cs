
namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private readonly record struct GenesisProgramCacheKey(
        GenesisShaderFeatureMask Mask,
        bool Tessellation,
        bool EntitySkinningSsbo,
        bool MaterialDrawRecordSsbo,
        bool MaterialTextureArrays,
        bool DrawRecordBaseInstance);

    private const int MaxGenesisProgramCacheEntries = 32;

    private readonly Dictionary<GenesisProgramCacheKey, GlShaderProgram> _genesisPrograms = new();
    private readonly LinkedList<GenesisProgramCacheKey> _genesisProgramLru = new();
    private GenesisProgramCacheKey _activeGenesisProgramKey;

    private void DisableGenesisTessellationCompile(string? reason)
    {
        if (_genesisTessellationCompileDisabled)
        {
            return;
        }

        _genesisTessellationCompileDisabled = true;
        if (_genesisTessellationFailureLogged)
        {
            return;
        }

        _genesisTessellationFailureLogged = true;
        var detail = string.IsNullOrWhiteSpace(reason) ? "compile failed" : TrimTessellationFailureReason(reason);
        EmitDiagnostic("[3D preview] Genesis tessellation disabled for this session. " + detail);
    }

    private static string TrimTessellationFailureReason(string reason)
    {
        var oneLine = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 240 ? oneLine : oneLine[..240] + "...";
    }

    private void ResetGenesisTessellationCompileState()
    {
        _genesisTessellationCompileDisabled = false;
        _genesisTessellationFailureLogged = false;
    }

    private void ResetEntitySkinningSsboCompileState()
    {
        _entitySkinningSsboCompileDisabled = false;
    }

    private void ResetMaterialDrawRecordSsboCompileState()
    {
        _materialDrawRecordSsboCompileDisabled = false;
    }

    private void ResetDrawRecordBaseInstanceCompileState()
    {
        _drawRecordBaseInstanceCompileDisabled = false;
    }

    private void EnsureGenesisProgramForFrame(ref GlRenderFrame frame)
    {
        if (_shaderCtx is null)
        {
            return;
        }

        var mask = GenesisShaderFeatureMaskBuilder.Build(frame.Settings, frame.EntityEmulatedPreview);
        // Idle / environment-only frames have no subject to displace; keep the non-tessellation
        // program so the ground plane draws as ordinary triangles (same path as emulated entities).
        var wantsTessellation = !_useOpenGlEs &&
                                !_genesisTessellationCompileDisabled &&
                                frame.EnableTessellationDisplacementEff &&
                                frame.Settings.DrawPreviewSubject;
        // Idle has no material slots — keep a simple 2D-sampler Genesis program so the ground
        // pass (which only binds grass 2D textures) is not stuck with unbound tex-arrays/SSBOs.
        // Texture arrays compose with tessellation on desktop WGL (TES reads height via draw-record layer).
        var useMaterialDrawRecords =
            frame.Settings.DrawPreviewSubject && ShouldUseMaterialDrawRecordSsbo();
        var useMaterialTextureArrays =
            frame.Settings.DrawPreviewSubject &&
            ShouldUseMaterialTextureArrays();
        var cacheKey = new GenesisProgramCacheKey(
            mask,
            wantsTessellation,
            ShouldUseEntitySkinningSsbo(),
            useMaterialDrawRecords,
            useMaterialTextureArrays,
            useMaterialDrawRecords && ShouldUseDrawRecordBaseInstance());
        if (_program is { IsValid: true } && cacheKey == _activeGenesisProgramKey)
        {
            return;
        }

        if (!TryGetOrCreateGenesisProgramWithFallback(cacheKey, out cacheKey, out var program, out _))
        {
            return;
        }

        if (_program is not null && !_genesisPrograms.ContainsValue(_program))
        {
            _program.Dispose();
        }

        _program = program;
        _activeGenesisProgramKey = cacheKey;
        _mainProgramUsesTessellation = cacheKey.Tessellation;
        _mainEntityUniformLocs = ResolveEntitySkinningUniformLocs(_program);
        _mainUniformLocs = ResolveMainProgramUniformLocs(_program);
        _lastMainPassAppliedSettingsRevision = -1;
    }

    private bool TryGetOrCreateGenesisProgram(
        GenesisProgramCacheKey cacheKey,
        out GlShaderProgram program,
        out string? error)
    {
        error = null;
        if (cacheKey.Tessellation && _genesisTessellationCompileDisabled)
        {
            program = new GlShaderProgram(_gl!, 0);
            error = "Tessellation compile previously failed.";
            return false;
        }

        if (_genesisPrograms.TryGetValue(cacheKey, out var cached) && cached.IsValid)
        {
            TouchGenesisProgramLru(cacheKey);
            program = cached;
            return true;
        }

        var defines = BuildGenesisProgramDefines(
            cacheKey.Mask,
            cacheKey.EntitySkinningSsbo,
            cacheKey.MaterialDrawRecordSsbo,
            cacheKey.MaterialTextureArrays,
            cacheKey.DrawRecordBaseInstance);
        var debugSuffix = string.Concat(
            cacheKey.EntitySkinningSsbo ? "+entity-ssbo" : string.Empty,
            cacheKey.MaterialDrawRecordSsbo ? "+draw-ssbo" : string.Empty,
            cacheKey.MaterialTextureArrays ? "+tex-array" : string.Empty,
            cacheKey.DrawRecordBaseInstance ? "+draw-base-instance" : string.Empty);
        if (cacheKey.Tessellation)
        {
            program = CreatePreviewProgram(
                "genesis.vert",
                "genesis.tcs",
                "genesis.tes",
                "genesis.frag",
                out error,
                $"genesis+tess+{(byte)cacheKey.Mask:X2}{debugSuffix}",
                defines);
        }
        else
        {
            program = CreatePreviewProgram(
                "genesis.vert",
                "genesis.frag",
                out error,
                $"genesis+{(byte)cacheKey.Mask:X2}{debugSuffix}",
                defines);
        }

        if (!program.IsValid)
        {
            program.Dispose();
            program = new GlShaderProgram(_gl!, 0);
            return false;
        }

        if (_genesisPrograms.TryGetValue(cacheKey, out var old) && !ReferenceEquals(old, program))
        {
            old.Dispose();
        }

        _genesisPrograms[cacheKey] = program;
        TouchGenesisProgramLru(cacheKey);
        EvictGenesisProgramsIfNeeded();
        return true;
    }

    private void TouchGenesisProgramLru(GenesisProgramCacheKey key)
    {
        _genesisProgramLru.Remove(key);
        _genesisProgramLru.AddFirst(key);
    }

    private void EvictGenesisProgramsIfNeeded()
    {
        while (_genesisPrograms.Count > MaxGenesisProgramCacheEntries && _genesisProgramLru.Last is not null)
        {
            var evict = _genesisProgramLru.Last.Value;
            _genesisProgramLru.RemoveLast();
            if (_genesisPrograms.Remove(evict, out var disposed) && !ReferenceEquals(disposed, _program))
            {
                disposed.Dispose();
            }
        }
    }

    private void DestroyGenesisProgramCache()
    {
        foreach (var entry in _genesisPrograms.Values)
        {
            if (!ReferenceEquals(entry, _program))
            {
                entry.Dispose();
            }
        }

        _genesisPrograms.Clear();
        _genesisProgramLru.Clear();
        _activeGenesisProgramKey = default;
        ResetGenesisTessellationCompileState();
        ResetEntitySkinningSsboCompileState();
        ResetMaterialDrawRecordSsboCompileState();
        ResetMaterialTextureArraysCompileState();
        ResetDrawRecordBaseInstanceCompileState();
    }

    private void PrewarmCommonGenesisProgramsOnGpu()
    {
        if (_shaderCtx is null || _useOpenGlEs)
        {
            return;
        }

        var masks = new[]
        {
            GenesisShaderFeatureMask.None,
            GenesisShaderFeatureMask.Shadow | GenesisShaderFeatureMask.Ibl,
            GenesisShaderFeatureMask.All,
        };

        foreach (var mask in masks)
        {
            var cacheKey = new GenesisProgramCacheKey(
                mask,
                Tessellation: false,
                ShouldUseEntitySkinningSsbo(),
                ShouldUseMaterialDrawRecordSsbo(),
                ShouldUseMaterialTextureArrays(),
                DrawRecordBaseInstance: false);
            if (_genesisPrograms.ContainsKey(cacheKey))
            {
                continue;
            }

            _ = TryGetOrCreateGenesisProgram(cacheKey, out _, out _);
        }
    }

    private bool ShouldUseEntitySkinningSsbo() =>
        !_entitySkinningSsboCompileDisabled &&
        _glCapabilities?.CanUseEntitySkinningSsbo == true;

    private bool ShouldUseMaterialDrawRecordSsbo() =>
        !_materialDrawRecordSsboCompileDisabled &&
        _glCapabilities?.CanUseMaterialDrawRecordSsbo == true;

    private bool ShouldUseDrawRecordBaseInstance() =>
        !_drawRecordBaseInstanceCompileDisabled &&
        _glCapabilities?.CanUseMultiDrawIndirectGroups == true;

    private static IReadOnlyDictionary<string, int> BuildGenesisProgramDefines(
        GenesisShaderFeatureMask mask,
        bool entitySkinningSsbo,
        bool materialDrawRecordSsbo = false,
        bool materialTextureArrays = false,
        bool drawRecordBaseInstance = false)
    {
        var baseDefines = GenesisShaderFeatureMaskBuilder.ToDefines(mask);
        if (!entitySkinningSsbo && !materialDrawRecordSsbo && !materialTextureArrays)
        {
            return baseDefines;
        }

        var defines = new Dictionary<string, int>(baseDefines);
        if (entitySkinningSsbo)
        {
            defines["GENESIS_ENTITY_SKINNING_SSBO"] = 1;
        }

        if (materialDrawRecordSsbo)
        {
            defines["GENESIS_MATERIAL_DRAW_RECORD_SSBO"] = 1;
            if (drawRecordBaseInstance)
            {
                defines["GENESIS_DRAW_RECORD_BASE_INSTANCE"] = 1;
            }
        }

        if (materialTextureArrays && materialDrawRecordSsbo)
        {
            defines["GENESIS_MATERIAL_TEXTURE_ARRAYS"] = 1;
        }

        return defines;
    }

    private bool TryGetOrCreateGenesisProgramWithFallback(
        GenesisProgramCacheKey requestedKey,
        out GenesisProgramCacheKey resolvedKey,
        out GlShaderProgram program,
        out string? error)
    {
        resolvedKey = requestedKey;
        if (TryGetOrCreateGenesisProgram(resolvedKey, out program, out error))
        {
            return true;
        }

        if (resolvedKey.Tessellation)
        {
            DisableGenesisTessellationCompile(error);
            resolvedKey = resolvedKey with
            {
                Tessellation = false,
                MaterialTextureArrays = ShouldUseMaterialTextureArrays(),
                DrawRecordBaseInstance = resolvedKey.MaterialDrawRecordSsbo && ShouldUseDrawRecordBaseInstance()
            };
            if (TryGetOrCreateGenesisProgram(resolvedKey, out program, out error))
            {
                return true;
            }
        }

        if (resolvedKey.DrawRecordBaseInstance)
        {
            DisableDrawRecordBaseInstanceCompile(error);
            resolvedKey = resolvedKey with { DrawRecordBaseInstance = false };
            if (TryGetOrCreateGenesisProgram(resolvedKey, out program, out error))
            {
                return true;
            }
        }

        if (resolvedKey.MaterialTextureArrays)
        {
            DisableMaterialTextureArraysCompile(error);
            resolvedKey = resolvedKey with { MaterialTextureArrays = false };
            if (TryGetOrCreateGenesisProgram(resolvedKey, out program, out error))
            {
                return true;
            }
        }

        if (resolvedKey.MaterialDrawRecordSsbo)
        {
            DisableMaterialDrawRecordSsboCompile(error);
            resolvedKey = resolvedKey with
            {
                MaterialDrawRecordSsbo = false,
                MaterialTextureArrays = false,
                DrawRecordBaseInstance = false
            };
            if (TryGetOrCreateGenesisProgram(resolvedKey, out program, out error))
            {
                return true;
            }
        }

        if (resolvedKey.EntitySkinningSsbo)
        {
            DisableEntitySkinningSsboCompile(error);
            resolvedKey = resolvedKey with { EntitySkinningSsbo = false };
            if (TryGetOrCreateGenesisProgram(resolvedKey, out program, out error))
            {
                return true;
            }
        }

        return false;
    }

    private void DisableEntitySkinningSsboCompile(string? reason)
    {
        if (_entitySkinningSsboCompileDisabled)
        {
            return;
        }

        _entitySkinningSsboCompileDisabled = true;
        var detail = string.IsNullOrWhiteSpace(reason) ? "compile failed" : TrimTessellationFailureReason(reason);
        EmitDiagnostic("[3D preview] Entity skinning SSBO path disabled for this session; using UBO fallback. " + detail);
    }

    private void DisableMaterialDrawRecordSsboCompile(string? reason)
    {
        if (_materialDrawRecordSsboCompileDisabled)
        {
            return;
        }

        _materialDrawRecordSsboCompileDisabled = true;
        var detail = string.IsNullOrWhiteSpace(reason) ? "compile failed" : TrimTessellationFailureReason(reason);
        EmitDiagnostic("[3D preview] Material/draw record SSBO path disabled for this session; using uniform fallback. " + detail);
    }

    private void DisableDrawRecordBaseInstanceCompile(string? reason)
    {
        if (_drawRecordBaseInstanceCompileDisabled)
        {
            return;
        }

        _drawRecordBaseInstanceCompileDisabled = true;
        var detail = string.IsNullOrWhiteSpace(reason) ? "compile failed" : TrimTessellationFailureReason(reason);
        EmitDiagnostic("[3D preview] Multi-draw draw-record indexing disabled for this session; using per-batch draw-record uniforms. " + detail);
    }
}
