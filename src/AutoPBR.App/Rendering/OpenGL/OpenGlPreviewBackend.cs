using System.Numerics;

using AutoPBR.App.Lang;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Core.Models;

using Avalonia.Threading;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend : IRenderPreviewBackend
{
    private static readonly float DefaultOrbitBoomArmDistance = PreviewCamera.DefaultOrbitBoomArmDistance;

    private readonly object _sync = new();
    private GL? _gl;
    private bool _useOpenGlEs;
    private GlShaderProgram? _program;
    private bool _mainProgramUsesTessellation;
    private GlShaderProgram? _shadowProgram;
    private GlShadowMapTarget? _shadowTarget;
    private GlShadowMapTarget? _shadowTargetCascadeNear;
    private GlShadowMapTarget? _shadowTargetCascadeMid;
    private int _shadowTargetsNearRes;
    private int _shadowTargetsMidRes;
    private int _shadowTargetsFarRes;
    private bool _shadowTargetsWantCascades;
    /// <summary>Set from UI-thread settings updates; rebuilt on the GL thread with a current context.</summary>
    private bool _shadowTargetsDirty;
    private double _lastPreviewFingerprintLogMs;
    private int _previewPixelWidth = 1;
    private int _previewPixelHeight = 1;
    private GlTexture2D? _albedo;
    private GlTexture2D? _normal;
    private GlTexture2D? _spec;
    private GlTexture2D? _height;
    private GlTexture2DArray? _materialAlbedoArray;
    private GlTexture2DArray? _materialNormalArray;
    private GlTexture2DArray? _materialSpecArray;
    private GlTexture2DArray? _materialHeightArray;
    private GlTexture2DArray? _fallbackMaterialAlbedoArray;
    private GlTexture2DArray? _fallbackMaterialNormalArray;
    private GlTexture2DArray? _fallbackMaterialSpecArray;
    private GlTexture2DArray? _fallbackMaterialHeightArray;
    private GenesisMaterialTextureArrayPlan? _materialTextureArrayPlan;
    private byte[]? _materialTextureArrayScratch;
    private byte[]? _rgbaUploadScratch;
    private GlMeshBuffer? _mesh;
    private GlMeshBuffer? _groundMesh;
    private readonly Dictionary<TerrainChunkKey, TerrainGpuChunk> _terrainGpuChunks = new();
    private TerrainChunkStreamer? _terrainStreamer;
    /// <summary>
    /// When true, <see cref="NeedsContinuousRendering"/> keeps requesting frames so chunk uploads can catch up.
    /// Starts true so selecting a subject before the first idle catch-up does not freeze an empty terrain.
    /// Reads/writes must happen under <see cref="_sync"/>.
    /// </summary>
    private bool _terrainStreamingNeedsFrames = true;
    private float _terrainEnvFloorY = PreviewStageConstants.GroundPlaneWorldY;
    private float _terrainEnvCeilingY = PreviewStageConstants.GroundPlaneWorldY + 0.05f;
    private GlTexture2D? _grassGroundAlbedo;
    private GlTexture2D? _grassGroundNormal;
    private GlTexture2D? _grassGroundSpec;
    private GlTexture2D? _grassGroundHeight;
    private GlTexture2D? _neutralNormal;
    private GlTexture2D? _neutralSpec;
    private GlTexture2D? _neutralHeight;
    private bool _grassGroundReady;
    private PreviewMaterial? _grassGroundMaterial;
    private PreviewMaterial[]? _grassGroundSlotMaterials;
    private bool _grassGroundOverlayCutout = true;
    private bool[]? _grassGroundSlotCutout;
    private bool _grassGroundMaterialDirty = true;
    private bool _grassGroundHasNormal;
    private bool _grassGroundHasSpecular;
    private bool _grassGroundHasHeight;
    private GroundGpuSlot[] _grassGroundSlots = [];
    private PreviewTerrainGrassBakeSettings _terrainGrassBakeSettings = PreviewTerrainGrassBakeSettings.BuiltIn;
    private bool _terrainGrassBakeSettingsDirty;
    private PreviewTerrainVegetationBakePlan? _terrainVegetationBakePlan;
    private bool _terrainVegetationBakePlanDirty;
    private PreviewTerrainWorldGenSettings _terrainWorldGenSettings = PreviewTerrainWorldGenSettings.Default;
    private bool _terrainWorldGenSettingsDirty;
    private GlLineShaderProgram? _lineProgram;
    private uint _gridVao;
    private uint _gridVbo;
    private int _gridVertexCount;
    private uint _axesVao;
    private uint _axesVbo;
    private GlNativeOverlayRenderer? _nativeOverlayRenderer;
    private PreviewNativeWglOverlayBitmap? _nativeOverlayDebug;
    private PreviewNativeWglOverlayBitmap? _nativeOverlayFps;
    private PreviewNativeWglOverlayBitmap? _nativeOverlayCpu;
    private int _nativeOverlayMarginPixels = 8;
    private bool _nativeOverlayShaderErrorLogged;
    /// <summary>Bump when overlay shader source changes so hot sessions rebuild the program.</summary>
    private const int NativeOverlayShaderRev = 2;
    private int _nativeOverlayShaderRevLoaded;
    private GlMoonBillboardProgram? _moonProgram;
    private GlTexture2D? _moonAlbedo;
    private uint _moonVao;
    private uint _moonVbo;
    private GlShaderProgram? _atmoTransProgram;
    private GlShaderProgram? _atmoSkyViewProgram;
    private GlShaderProgram? _atmoSkyProgram;
    private GlProceduralSkyProgram? _proceduralSkyProgram;
    private uint _atmoQuadVao;
    private uint _atmoQuadVbo;
    private uint _atmoTransmittanceTex;
    private uint _atmoSkyViewTex;
    private uint _atmoTransmittanceFbo;
    private uint _atmoSkyViewFbo;
    private Vector3 _lastAtmoSunDir;
    private float _lastAtmoTurbidity = -1f;
    private float _lastAtmoSunIntensity = -1f;
    private float _lastAtmoHorizonFalloff = -1f;
    private bool _atmoLutsValid;
    private PreviewRenderSettingsSnapshot _settings = PreviewRenderSettingsSnapshot.From(new PreviewRenderSettings());
    private IRenderPreviewScene? _scene;
    private PreviewMaterial? _material;
    private PreviewModelSubject? _blockModelSubject;
    private PreviewMaterial[]? _blockModelSlots;
    private bool _materialDirty = true;
    private bool _meshDirty = true;
    private PreviewMaterialContentKey.Value _lastMaterialContentKey;
    private double _rotationAccum;
    private double _renderTimeAccum;
    private bool _prevPauseEntityIdleAnimation;
    private float _frozenEntityIdleAnimClock;

    /// <summary>Wall-clock (see <see cref="GlRender"/> <c>renderTime</c>) minimum spacing between emulated-entity CPU mesh rebakes.</summary>
    private const double MinEmulatedEntityRebakeIntervalSeconds = 1.0 / 30.0;

    private double _lastEmulatedEntityRebakeRenderTime = double.NegativeInfinity;
    private string? _emulatedRebakeSubjectKey;
    private string? _emulatedGpuSkinPrepFailedKey;
    /// <summary>Last emulated subject that received a successful animation-off IR bind-pose CPU rebake.</summary>
    private string? _entityBindPoseCommittedKey;
    private ulong _lastParityCatalogIncomingPackFingerprint;

    private readonly Matrix4x4[] _entityBoneScratch = new Matrix4x4[EntityGpuSkinningLimits.MaxBones];
    private readonly Matrix4x4[] _entityPrevBoneScratch = new Matrix4x4[EntityGpuSkinningLimits.MaxBones];
    /// <summary>std140: 64 mat4 (row-major source floats interpreted as column-major in GLSL) + 16 B tail scalars.</summary>
    private const int EntitySkinningUboMatrixBytes = EntityGpuSkinningLimits.MaxBones * 64;

    private const int EntitySkinningUboTotalBytes = EntitySkinningUboMatrixBytes;

    private readonly byte[] _entitySkinningUboScratch = new byte[EntitySkinningUboTotalBytes];
    private readonly byte[] _entityPrevSkinningUboScratch = new byte[EntitySkinningUboTotalBytes];
    private readonly byte[] _entityNormalSkinningUboScratch = new byte[EntitySkinningUboMatrixBytes];
    private GlPersistentMappedUploadBuffer? _entityBoneUpload;
    private GlPersistentMappedUploadBuffer? _entityPrevBoneUpload;
    private GlPersistentMappedUploadBuffer? _entityNormalBoneUpload;
    private GlPersistentMappedUploadBuffer? _genesisMaterialDrawRecordUpload;
    private GlIndirectDrawCommandBuffer? _genesisIndirectDrawCommands;
    private GlShaderProgram? _gpuDrawCommandCompactionProgram;
    private GlGpuDrawCommandCompactor? _gpuDrawCommandCompactor;
    private GlShaderProgram? _terrainShadowCullProgram;
    private GlTerrainShadowCuller? _terrainShadowCuller;
    private bool _terrainShadowCullCompileDisabled;
    private bool _loggedTerrainShadowGpuCull;
    private bool _frameSubjectGpuUploadsReady;
    private bool _frameSubjectUseMaterialDrawRecords;
    private bool _frameSubjectUseIndirectDrawCommands;
    private bool _frameSubjectUseMaterialTextureArrays;
    private bool _terrainShadowWorldAabbValid;
    private Vector3 _terrainShadowWorldAabbMin;
    private Vector3 _terrainShadowWorldAabbMax;
    private GlGpuTimerProfiler? _gpuTimerProfiler;
    private GlCpuTimerProfiler? _cpuTimerProfiler;
    private readonly GlGpuTimingWindow _gpuTimingWindow = new();
    private readonly GlGpuTimingHudLinger _gpuTimingHudLinger = new();
    private readonly GlGpuTimingHudLinger _cpuTimingHudLinger = new();
    private uint _entityBoneUbo;
    private uint _entityPrevBoneUbo;
    private uint _entityNormalBoneUbo;
    private uint _genesisMaterialDrawRecordSsbo;
    private int _entityPrevBoneSnapshotCount;
    private bool _entityPrevBoneSnapshotValid;

    private const uint EntitySkinningUboBindingPoint = 2;
    private const uint EntityPrevSkinningUboBindingPoint = 3;
    private const uint EntityNormalSkinningUboBindingPoint = 4;
    private const uint EntitySkinningSsboBindingPoint = 5;
    private const uint EntityPrevSkinningSsboBindingPoint = 6;
    private const uint EntityNormalSkinningSsboBindingPoint = 7;
    private const uint GenesisMaterialDrawRecordSsboBindingPoint = 8;
    private const int GenesisMaterialDrawRecordFloats = 16;
    private const int GenesisMaterialDrawRecordBytes = GenesisMaterialDrawRecordFloats * sizeof(float);
    private const int GenesisMaterialDrawRecordMaxRecords = 4096;
    private readonly byte[] _genesisMaterialDrawRecordScratch =
        new byte[GenesisMaterialDrawRecordMaxRecords * GenesisMaterialDrawRecordBytes];

    private readonly record struct EntitySkinningUniformLocs(
        int PreviewSpaceVerts,
        int BindMesh,
        int GpuSkinning,
        int BoneCount,
        int MeshLiftY,
        int PrevBonePaletteValid)
    {
        public bool IsComplete =>
            PreviewSpaceVerts >= 0 && BindMesh >= 0 && GpuSkinning >= 0 && BoneCount >= 0 && MeshLiftY >= 0;
    }

    private EntitySkinningUniformLocs _mainEntityUniformLocs;
    private EntitySkinningUniformLocs _shadowEntityUniformLocs;

    private bool _loggedEntityShaderInit;
    private bool _entityBoneUboBlockBoundMain;
    private bool _entityBoneUboBlockBoundShadow;

    private bool _disposed;
    private string? _lastError;
    private bool _gpuAlive;
    private PreviewGlCapabilities? _glCapabilities;
    private GlShaderToolchainPlan? _shaderToolchainPlan;
    private bool _entitySkinningUsesSsbo;
    private bool _entitySkinningSsboCompileDisabled;
    private bool _genesisMaterialDrawRecordsUseSsbo;
    private bool _materialDrawRecordSsboCompileDisabled;
    private bool _materialTextureArraysCompileDisabled;
    private bool _drawRecordBaseInstanceCompileDisabled;
    private bool _loggedMaterialDrawRecordOverflow;
    private bool _loggedMaterialTextureArraysReady;
    private string? _loggedMaterialTextureArrayFallbackReason;
    private bool _loggedIndirectDrawCommandBuffer;
    private bool _loggedMultiDrawIndirectGroups;
    private bool _loggedGpuCompactedDrawSubmission;
    private bool _loggedGpuTimerProfilerActive;
    private bool _loggedGpuTimerProfilerFallback;
    private bool _gpuDrawCommandCompactionCompileDisabled;
    private bool _genesisTessellationCompileDisabled;
    private bool _genesisTessellationFailureLogged;
    private int _appliedWglSwapInterval = int.MinValue;
    private int _appliedWglDisplayRefreshHz = int.MinValue;
    private Action<string>? _diagnosticLog;
    private Action? _requestPreviewFrame;
    private bool _loggedMeshReady;
    private bool _loggedZeroIndex;
    private int _lastMeshUploadStride = PreviewMesh.FloatsPerVertex;
    private bool _shadowCasterBoundsValid;
    private Vector3 _shadowCasterBoundsMin;
    private Vector3 _shadowCasterBoundsMax;
    private int _settingsRevision;
    private int _lastMainPassAppliedSettingsRevision = -1;
    private string? _entityBindPosePrepDiagKey;
    private string? _latestGpuTimingHudText;
    private string? _latestCpuTimingHudText;
    private double _lastGpuTimingDiagnosticSeconds = double.NegativeInfinity;

    /// <summary>Orbit camera is re-synced from the scene only when this changes (block vs item), so texture swaps keep user framing.</summary>
    private string? _orbitSyncedKey;

    private Vector3 _orbitBaseTarget;
    private float _orbitYaw;
    private float _orbitPitch;
    private float _orbitDistance = 4f;
    private Vector3 _orbitPan;
    private bool _flyEngaged;
    private Vector3 _flyPosition;
    private float _flyYaw;
    private float _flyPitch;
    private bool _debugFlyRmbHeld;
    private bool _flyKeyW;
    private bool _flyKeyA;
    private bool _flyKeyS;
    private bool _flyKeyD;
    private bool _flyKeyQ;
    private bool _flyKeyE;
    private bool _flySpeedBoost;
    private bool _flySpeedSlow;
    private float _flySpeedSessionMultiplier = 1f;
    private Vector3 _flyMoveVelocity;
    private float _camFlyLookRadPerPx = 0.006f;
    private float _flyMoveSpeed = 1f;
    private bool _flySmoothAcceleration = true;
    private bool _invertLookY;
    private float _orbitYawDefault;
    private float _orbitPitchDefault;
    private float _orbitDistanceDefault;
    private Vector3 _orbitPanDefault;
    private float _camOrbitRadPerPx = 0.006f;
    private float _camPanPerPx = 0.0022f;
    private float _camZoomPerWheelStep = 0.12f;
    /// <summary>Pivot-to-eye distance used when seeding orbit and when applying boom from settings (wheel zoom changes <see cref="_orbitDistance"/> temporarily).</summary>
    private float _orbitBoomArmDistance = DefaultOrbitBoomArmDistance;
    private bool _userCameraDragging;
    private bool _shaderPrewarmProgressHooked;

    public string BackendName => "OpenGL";
    public bool IsInitialized => _gpuAlive && _program?.IsValid == true;

    public string? LatestGpuTimingHudText
    {
        get
        {
            lock (_sync)
            {
                return _latestGpuTimingHudText;
            }
        }
    }

    public string? LatestCpuTimingHudText
    {
        get
        {
            lock (_sync)
            {
                return _latestCpuTimingHudText;
            }
        }
    }
    public string? LastErrorMessage => _lastError;

    public bool NeedsContinuousRendering
    {
        get
        {
            lock (_sync)
            {
                return !_gpuAlive ||
                       _gpuBootstrap is not null ||
                       _settings.AutoRotate ||
                       _settings.AnimateTimeOfDay ||
                       ShouldContinuouslyAccumulatePreviewTaa(_settings) ||
                       (_settings.EnableEntityAnimation && _blockModelSubject is { EnableRenderTimeAnimation: true }) ||
                       _settings is { EnableVolumetricClouds: true, CloudFreezeWind: false } ||
                       // Keep pumping while ground is shown and either idle (no subject) or terrain still catching up.
                       (_settings.ShowGroundMesh && _scene is { } &&
                        (!_settings.DrawPreviewSubject || _terrainStreamingNeedsFrames)) ||
                       (_debugFlyRmbHeld && _flyEngaged) ||
                       _userCameraDragging;
            }
        }
    }

    public void Initialize(RenderPreviewInitializationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
        {
            _ = options.MsaaSamples;
        }

        if (!_shaderPrewarmProgressHooked)
        {
            PreviewShaderPrewarm.ProgressChanged += OnShaderPrewarmProgress;
            _shaderPrewarmProgressHooked = true;
        }

        PreviewShaderPrewarm.EnsureStarted();
    }

    private void OnShaderPrewarmProgress()
    {
        lock (_sync)
        {
            if (_gl is null)
            {
                return;
            }

            RaiseGpuInitProgress(
                PreviewShaderPrewarm.IsComplete
                    ? (_gpuBootstrap is not null ? _gpuBootstrap.Phase : PreviewGpuInitPhases.Preparing)
                    : PreviewGpuInitPhases.PreparingShaderSources,
                _settings);
        }
    }

    /// <summary>Shader/renderer diagnostics (same sink as main log UI). Invoked from the GL thread.</summary>
    public void SetDiagnosticLog(Action<string>? log)
    {
        lock (_sync)
        {
            _diagnosticLog = log;
        }
    }

    private void EmitDiagnostic(string message)
    {
        Action<string>? log;
        lock (_sync)
        {
            log = _diagnosticLog;
        }

        log?.Invoke(message);
    }

    /// <summary>Public diagnostic sink for preview-host controls (e.g. OpenGL frame-queue recovery).</summary>
    public void EmitPreviewDiagnostic(string message) => EmitDiagnostic(message);

    public void SetRequestPreviewFrame(Action? requestFrame)
    {
        lock (_sync)
        {
            _requestPreviewFrame = requestFrame;
        }
    }

    private void RequestPreviewFrame()
    {
        Action? request;
        lock (_sync)
        {
            request = _requestPreviewFrame;
        }

        if (request is null)
        {
            return;
        }

        // Always defer — sync UI-thread calls during OnOpenGlRender recreate Avalonia #17865.
        Dispatcher.UIThread.Post(request, DispatcherPriority.Background);
    }

    public void Resize(int width, int height)
    {
        lock (_sync)
        {
            _previewPixelWidth = Math.Max(1, width);
            _previewPixelHeight = Math.Max(1, height);
        }
    }

    public void SetScene(IRenderPreviewScene scene)
    {
        lock (_sync)
        {
            var prev = _scene;
            var prevKind = prev?.SceneKind;
            var nextKind = scene.SceneKind;
            var orbitKey = ResolveOrbitSyncKey(scene, _blockModelSubject);
            var reseedOrbit = _orbitSyncedKey is null || !string.Equals(_orbitSyncedKey, orbitKey, StringComparison.Ordinal);
            if (reseedOrbit)
            {
                SyncOrbitFromSceneLocked(scene);
                _orbitSyncedKey = orbitKey;
            }

            var kindChanged = prevKind != nextKind;
            var meshContentChanged = kindChanged || SceneMeshContentChanged(prev, scene);
            _scene = scene;
            if (kindChanged || reseedOrbit)
            {
                InvalidatePreviewTaaHistory();
            }

            if (kindChanged)
            {
                _meshDirty = true;
            }
            else if (nextKind != PreviewSceneKind.ItemPlane && meshContentChanged)
            {
                // Block/entity scenes carry authoritative mesh refs on the scene object.
                _meshDirty = true;
            }
        }
    }

    private static bool SceneMeshContentChanged(IRenderPreviewScene? prev, IRenderPreviewScene next)
    {
        if (prev is null)
        {
            return true;
        }

        if (prev.Meshes.Count != next.Meshes.Count)
        {
            return true;
        }

        for (var i = 0; i < next.Meshes.Count; i++)
        {
            var a = prev.Meshes[i];
            var b = next.Meshes[i];
            if (!ReferenceEquals(a.InterleavedVertices, b.InterleavedVertices) ||
                !ReferenceEquals(a.Indices, b.Indices) ||
                !string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveOrbitSyncKey(IRenderPreviewScene scene, PreviewModelSubject? blockModel)
    {
        if (scene.SceneKind == PreviewSceneKind.ItemPlane)
        {
            return "item_plane";
        }

        if (blockModel?.EmulatedRebake?.AssetArchivePath is { Length: > 0 } entityPath)
        {
            return "entity:" + entityPath.Replace('\\', '/').TrimStart('/');
        }

        return scene.SceneKind == PreviewSceneKind.BlockModel ? "block_model" : "block_cube";
    }

    public void SetMaterial(PreviewMaterial? material)
    {
        lock (_sync)
        {
            var nextKey = PreviewMaterialContentKey.Compute(material);
            if (PreviewMaterialContentKey.Equals(_lastMaterialContentKey, nextKey))
            {
                return;
            }

            _lastMaterialContentKey = nextKey;
            _material = material;
            _materialDirty = true;
            InvalidatePreviewTaaHistory();
            if (_scene?.SceneKind == PreviewSceneKind.ItemPlane)
            {
                _meshDirty = true;
            }
        }
    }

    public void SetGroundMaterial(PreviewMaterial? material)
    {
        lock (_sync)
        {
            _grassGroundMaterial = material;
            _grassGroundSlotMaterials = material is null ? null : [material];
            _grassGroundSlotCutout = null;
            _grassGroundOverlayCutout = true;
            _grassGroundMaterialDirty = true;
            InvalidatePreviewTaaHistory();
        }
    }

    public void SetGroundMaterials(
        PreviewMaterial[]? slotMaterials,
        bool overlayIsCutout = true,
        bool[]? cutoutBySlot = null)
    {
        lock (_sync)
        {
            if (slotMaterials is null || slotMaterials.Length == 0)
            {
                _grassGroundMaterial = null;
                _grassGroundSlotMaterials = null;
                _grassGroundSlotCutout = null;
            }
            else
            {
                _grassGroundSlotMaterials = slotMaterials;
                _grassGroundMaterial = slotMaterials[0];
                _grassGroundSlotCutout = cutoutBySlot;
            }

            _grassGroundOverlayCutout = overlayIsCutout;
            _grassGroundMaterialDirty = true;
            InvalidatePreviewTaaHistory();
        }
    }

    public void SetTerrainGrassBakeSettings(PreviewTerrainGrassBakeSettings settings)
    {
        lock (_sync)
        {
            if (settings.Equals(_terrainGrassBakeSettings))
            {
                return;
            }

            _terrainGrassBakeSettings = settings;
            _terrainGrassBakeSettingsDirty = true;
            InvalidatePreviewTaaHistory();
        }
    }

    public void SetTerrainVegetationBakePlan(PreviewTerrainVegetationBakePlan? plan)
    {
        lock (_sync)
        {
            var next = plan is { HasAny: true } ? plan : PreviewTerrainVegetationBakePlan.Empty;
            var prevId = _terrainVegetationBakePlan?.Identity ?? PreviewTerrainVegetationBakePlan.Empty.Identity;
            if (string.Equals(prevId, next.Identity, StringComparison.Ordinal))
            {
                _terrainVegetationBakePlan = next;
                return;
            }

            _terrainVegetationBakePlan = next;
            _terrainVegetationBakePlanDirty = true;
            InvalidatePreviewTaaHistory();
        }
    }

    private sealed class GroundGpuSlot
    {
        public GlTexture2D? Albedo { get; set; }
        public GlTexture2D? Normal { get; set; }
        public GlTexture2D? Spec { get; set; }
        public GlTexture2D? Height { get; set; }
        public bool HasNormal { get; set; }
        public bool HasSpecular { get; set; }
        public bool HasHeight { get; set; }
        public int Width { get; init; } = 1;
        public int TexHeight { get; init; } = 1;
        public bool Cutout { get; init; }

        public void DisposeTextures()
        {
            Albedo?.Dispose();
            Albedo = null;
            Normal?.Dispose();
            Normal = null;
            Spec?.Dispose();
            Spec = null;
            Height?.Dispose();
            Height = null;
        }
    }

    private static bool CanReuseGpuSkinnedEntitySubject(PreviewModelSubject subject) =>
        subject is
        {
            GpuEntityBoneSkinning: true,
            EmulatedRebake.GpuPreparedBoneCount: > 0,
            EmulatedRebake.GpuBindPoseInterleavedVertices: not null
        };

    private static void ClearEntityGpuBindCache(EntityEmulatedPreviewRebakeContext? rebake)
    {
        if (rebake is null)
        {
            return;
        }

        rebake.GpuPreparedBoneCount = null;
        rebake.GpuBindPoseInverseLocalToParent = null;
        rebake.GpuBindPoseBonePalette = null;
        rebake.GpuBindPoseInterleavedVertices = null;
        rebake.GpuBoundCpuMeshFingerprint = null;
        rebake.GpuBoneDispatchRoute = null;
    }

    private static bool IsGpuSkinnedEntitySubject(PreviewModelSubject? subject) =>
        subject is
        {
            GpuEntityBoneSkinning: true,
            VertexStrideFloats: EntityEmulatedPreviewMeshLayout.SkinnedFloatsPerVertex,
            EmulatedRebake: not null
        };

    private static bool SameEntityPreviewAsset(PreviewModelSubject? a, PreviewModelSubject? b)
    {
        var pa = a?.EmulatedRebake?.AssetArchivePath;
        var pb = b?.EmulatedRebake?.AssetArchivePath;
        return pa is not null && pb is not null &&
               string.Equals(
                   pa.Replace('\\', '/').TrimStart('/'),
                   pb.Replace('\\', '/').TrimStart('/'),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameEntityPreviewPose(PreviewModelSubject? a, PreviewModelSubject? b) =>
        string.Equals(
            a?.EmulatedRebake?.PreviewPoseId,
            b?.EmulatedRebake?.PreviewPoseId,
            StringComparison.Ordinal);

    private static bool SameEntityPreviewSize(PreviewModelSubject? a, PreviewModelSubject? b) =>
        string.Equals(
            a?.EmulatedRebake?.PreviewSizeId,
            b?.EmulatedRebake?.PreviewSizeId,
            StringComparison.Ordinal);

    private static bool SameEntityPreviewContextType(PreviewModelSubject? a, PreviewModelSubject? b) =>
        string.Equals(
            a?.EmulatedRebake?.PreviewContextTypeId,
            b?.EmulatedRebake?.PreviewContextTypeId,
            StringComparison.Ordinal);

    private void InvalidateParityCatalogBindCommitState()
    {
        _entityBindPoseCommittedKey = null;
        _parityCatalogCpuBindDiagKey = null;
        _parityCatalogCpuBindFailDiagKey = null;
        _entityMeshUploadDeferredDiagKey = null;
        _lastParityCatalogIncomingPackFingerprint = 0;
        _meshDirty = true;
    }

    private void InvalidateEntityAssetBindState(EntityEmulatedPreviewRebakeContext rebake)
    {
        rebake.GpuPreparedBoneCount = null;
        rebake.GpuBindPoseInverseLocalToParent = null;
        rebake.GpuBindPoseBonePalette = null;
        rebake.GpuBindPoseInterleavedVertices = null;
        rebake.GpuBoundCpuMeshFingerprint = null;
        rebake.GpuBoneDispatchRoute = null;
        rebake.ElementPartIds = null;
        _emulatedGpuSkinPrepFailedKey = null;
        _emulatedRebakeSubjectKey = null;
        _entityBindPoseCommittedKey = null;
        _entityBindPosePrepDiagKey = null;
        _entityBonePaletteLastUploadedCount = 0;
        _parityPlacementDiagKey = null;
        _entityCpuRebakeDiagKey = null;
        _parityCatalogCpuBindDiagKey = null;
        _parityCatalogCpuBindFailDiagKey = null;
        _entityMeshUploadDeferredDiagKey = null;
        _lastParityCatalogIncomingPackFingerprint = 0;
        ResetEntityGpuRuntimeDiagState();
    }

    private static bool BlockModelGeometryChanged(PreviewModelSubject? prev, PreviewModelSubject? next)
    {
        if (prev is null && next is null)
        {
            return false;
        }

        if (ReferenceEquals(prev, next))
        {
            return false;
        }

        if (prev is null || next is null)
        {
            return true;
        }

        return !ReferenceEquals(prev.InterleavedVertices, next.InterleavedVertices) ||
               !ReferenceEquals(prev.Indices, next.Indices) ||
               prev.VertexStrideFloats != next.VertexStrideFloats ||
               prev.GpuEntityBoneSkinning != next.GpuEntityBoneSkinning ||
               !SameEntityPreviewAsset(prev, next);
    }

    private static bool BlockModelPreviewSubjectChanged(PreviewModelSubject? prev, PreviewModelSubject? next)
    {
        if (prev is null && next is null)
        {
            return false;
        }

        if (ReferenceEquals(prev, next))
        {
            return false;
        }

        if (prev is null || next is null)
        {
            return true;
        }

        if (!SameEntityPreviewAsset(prev, next))
        {
            return true;
        }

        if (!SameEntityPreviewPose(prev, next) ||
            !SameEntityPreviewSize(prev, next) ||
            !SameEntityPreviewContextType(prev, next))
        {
            return true;
        }

        return prev.EmulatedRebake is null && BlockModelGeometryChanged(prev, next);
    }

    private void RecordMeshUpload(ReadOnlySpan<float> interleavedVertices, int strideFloats)
    {
        _lastMeshUploadStride = strideFloats;
        if (TryComputeVertexBounds(interleavedVertices, strideFloats, out var min, out var max))
        {
            _shadowCasterBoundsMin = min;
            _shadowCasterBoundsMax = max;
            _shadowCasterBoundsValid = true;
        }
        else
        {
            _shadowCasterBoundsValid = false;
        }
    }

    private void UploadPreviewMesh(float[] interleavedVertices, uint[] indices, int strideFloats = PreviewMesh.FloatsPerVertex)
    {
        _mesh!.Upload(interleavedVertices, indices, strideFloats);
        RecordMeshUpload(interleavedVertices, strideFloats);
    }

    /// <summary>Multi-material Java block model preview. Pass <c>null</c> to clear it.</summary>
    public void SetBlockModelPreview(PreviewModelSubject? subject, PreviewMaterial[]? slotMaterials)
    {
        lock (_sync)
        {
            var prev = _blockModelSubject;
            var prevPath = prev?.EmulatedRebake?.AssetArchivePath;
            var nextPath = subject?.EmulatedRebake?.AssetArchivePath;
            var previewPoseChanged = SameEntityPreviewAsset(prev, subject) && !SameEntityPreviewPose(prev, subject);
            var previewSizeChanged = SameEntityPreviewAsset(prev, subject) && !SameEntityPreviewSize(prev, subject);
            var previewContextTypeChanged = SameEntityPreviewAsset(prev, subject) && !SameEntityPreviewContextType(prev, subject);
            if ((!string.Equals(prevPath, nextPath, StringComparison.OrdinalIgnoreCase) || previewPoseChanged || previewSizeChanged || previewContextTypeChanged) &&
                subject?.EmulatedRebake is { } rebake)
            {
                InvalidateEntityAssetBindState(rebake);
            }

            // UI re-pushes pack-converter CPU mesh every settings tick; keep the GPU bind VBO subject instead,
            // unless the CPU mesh fingerprint changed (Core geometry fix) — then drop stale GPU bind data.
            // Parity-catalog Explore defaults to animation-off CPU meshes — never substitute a stale GPU subject.
            if (subject is not null &&
                prev is not null &&
                !PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(subject.EmulatedRebake?.AssetArchivePath) &&
                IsGpuSkinnedEntitySubject(prev) &&
                CanReuseGpuSkinnedEntitySubject(prev) &&
                SameEntityPreviewAsset(prev, subject) &&
                !IsGpuSkinnedEntitySubject(subject))
            {
                var incomingFp = subject.EmulatedRebake?.PackConverterCpuMeshFingerprint ?? 0;
                var boundFp = prev.EmulatedRebake?.GpuBoundCpuMeshFingerprint;
                if (incomingFp != 0 && boundFp != incomingFp)
                {
                    ClearEntityGpuBindCache(prev.EmulatedRebake);
                    _entityBindPoseCommittedKey = null;
                    _emulatedGpuSkinPrepFailedKey = null;
                    _emulatedRebakeSubjectKey = null;
                    _meshDirty = true;
                }
                else
                {
                    subject = prev;
                }
            }

            // Parity-catalog Explore: UI re-pushes pack-converter CPU mesh every settings tick; keep the
            // OpenGL-committed TryRebakeMesh subject so fins stay attached and we avoid per-frame rebakes.
            if (subject is not null &&
                prev is not null &&
                PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(subject.EmulatedRebake?.AssetArchivePath) &&
                SameEntityPreviewAsset(prev, subject))
            {
                var incomingFp = subject.EmulatedRebake?.PackConverterCpuMeshFingerprint ?? 0;
                var prevFp = prev.EmulatedRebake?.PackConverterCpuMeshFingerprint ?? 0;
                var committedMeshStride = prev.VertexStrideFloats > 0
                    ? prev.VertexStrideFloats
                    : PreviewMesh.FloatsPerVertex;
                var committedMeshFp = prev.InterleavedVertices.Length > 0
                    ? PreviewMeshGeometryFingerprint.ComputeCpuPreviewMesh(
                        prev.InterleavedVertices,
                        committedMeshStride)
                    : 0UL;
                if (previewPoseChanged ||
                    (_entityBindPoseCommittedKey is not null &&
                     !PreviewRenderPassSetup.ParityCatalogCpuBindCommitKeyMatchesCurrentRevision(_entityBindPoseCommittedKey)) ||
                    (incomingFp != 0 && prevFp != 0 && incomingFp != prevFp) ||
                    (_lastParityCatalogIncomingPackFingerprint != 0 &&
                     incomingFp != 0 &&
                     incomingFp != _lastParityCatalogIncomingPackFingerprint) ||
                    (incomingFp != 0 &&
                     committedMeshFp != 0 &&
                     incomingFp != committedMeshFp))
                {
                    InvalidateParityCatalogBindCommitState();
                }

                if (incomingFp != 0)
                {
                    _lastParityCatalogIncomingPackFingerprint = incomingFp;
                }

                if (_entityBindPoseCommittedKey is not null &&
                    subject.EmulatedRebake is { } incomingRebake &&
                    PreviewRenderPassSetup.ParityCatalogCpuBindCommitKeyMatchesCurrentRevision(_entityBindPoseCommittedKey) &&
                    prev is
                    {
                        GpuEntityBoneSkinning: false,
                        EntityPreviewPlacementApplied: true,
                        InterleavedVertices.Length: > 0,
                        Indices.Length: > 0,
                        EmulatedRebake: not null
                    } &&
                    string.Equals(
                        _entityBindPoseCommittedKey,
                        PreviewRenderPassSetup.BuildParityCatalogCpuBindCommitKey(incomingRebake),
                        StringComparison.Ordinal))
                {
                    subject = new PreviewModelSubject
                    {
                        InterleavedVertices = prev.InterleavedVertices,
                        Indices = prev.Indices,
                        DrawBatches = prev.DrawBatches,
                        Materials = subject.Materials,
                        PrimaryMaterialIndex = subject.PrimaryMaterialIndex,
                        Sprite2DFoliageTarget = subject.Sprite2DFoliageTarget,
                        EnableRenderTimeAnimation = subject.EnableRenderTimeAnimation,
                        AnimationPreset = subject.AnimationPreset,
                        EmulatedRebake = prev.EmulatedRebake,
                        GpuEntityBoneSkinning = false,
                        VertexStrideFloats = 0,
                        EntityGpuMeshSpaceLiftY = 0f,
                        EntityGpuVerticesInPreviewSpace = true,
                        EntityPreviewAnchorOffset = prev.EntityPreviewAnchorOffset,
                        EntityPreviewPlacementApplied = true,
                        MeshProvenance = prev.MeshProvenance ?? subject.MeshProvenance
                    };
                }
            }

            _blockModelSubject = subject;
            _blockModelSlots = slotMaterials;
            _prevPauseEntityIdleAnimation = false;
            var subjectChanged = BlockModelPreviewSubjectChanged(prev, subject);
            if (subjectChanged)
            {
                InvalidatePreviewTaaHistory();
            }

            if (subject is not null && slotMaterials is { Length: > 0 })
            {
                var pi = Math.Clamp(subject.PrimaryMaterialIndex, 0, slotMaterials.Length - 1);
                var slotMat = slotMaterials[pi];
                var slotKey = PreviewMaterialContentKey.Compute(slotMat);
                if (!PreviewMaterialContentKey.Equals(_lastMaterialContentKey, slotKey))
                {
                    _lastMaterialContentKey = slotKey;
                    _material = slotMat;
                    _materialDirty = true;
                }
            }
            else if (subjectChanged)
            {
                _materialDirty = true;
            }

            if (BlockModelGeometryChanged(prev, subject))
            {
                var hasCommittedParityCpu = PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(nextPath) &&
                                            _entityBindPoseCommittedKey is not null &&
                                            subject is
                                            {
                                                GpuEntityBoneSkinning: false,
                                                EntityPreviewPlacementApplied: true,
                                                EntityGpuVerticesInPreviewSpace: true
                                            };
                if (!hasCommittedParityCpu)
                {
                    var incomingFp = subject?.EmulatedRebake?.PackConverterCpuMeshFingerprint ?? 0;
                    var prevFp = prev?.EmulatedRebake?.PackConverterCpuMeshFingerprint ?? 0;
                    var sameParityGeometry = PreviewRenderPassSetup.IsParityCatalogEmulatedAsset(nextPath) &&
                                             incomingFp != 0 &&
                                             incomingFp == prevFp;
                    if (!sameParityGeometry || previewPoseChanged)
                    {
                        _meshDirty = true;
                    }
                }
            }

            if (_scene is not null)
            {
                var orbitKey = ResolveOrbitSyncKey(_scene, subject);
                if (_orbitSyncedKey is null || !string.Equals(_orbitSyncedKey, orbitKey, StringComparison.Ordinal))
                {
                    SyncOrbitFromSceneLocked(_scene);
                    _orbitSyncedKey = orbitKey;
                }
            }
        }
    }

    private static bool IsSolidBlockGeometry(PreviewSceneKind k) =>
        k is PreviewSceneKind.BlockCube or PreviewSceneKind.BlockModel;

    private static bool IsEntityEmulatedPreview(PreviewModelSubject? blockModel) =>
        string.Equals(blockModel?.AnimationPreset, "entity_emulated", StringComparison.Ordinal);

    /// <summary>
    /// Entity rigs baked from clean-room Java cuboids can disagree with AutoPBR-generated tangent-space maps;
    /// disable solid back-face culling for those previews so occasional winding mismatches do not punch holes.
    /// Extruded sprite voxels are watertight cuboids and benefit from back-face culling.
    /// </summary>
    private static bool ShouldCullSolidBackFaces(
        PreviewSceneKind sceneKind,
        PreviewModelSubject? blockModel,
        in PreviewRenderSettingsSnapshot settings) =>
        (IsSolidBlockGeometry(sceneKind) && !IsEntityEmulatedPreview(blockModel)) ||
        (sceneKind == PreviewSceneKind.ItemPlane && settings.SpriteThickness > 1e-6f);

    public void SetRenderSettings(PreviewRenderSettings settings)
    {
        lock (_sync)
        {
            var prev = _settings;
            _settings = PreviewRenderSettingsSnapshot.From(settings);
            _settingsRevision++;
            if (_settings.DrawPreviewSubject != prev.DrawPreviewSubject ||
                _settings.EnableEntityAnimation != prev.EnableEntityAnimation ||
                _settings.ForceEntityCpuSkinning != prev.ForceEntityCpuSkinning ||
                _settings.PauseEntityIdleAnimation != prev.PauseEntityIdleAnimation ||
                _settings.SpritePlaneCount != prev.SpritePlaneCount ||
                Math.Abs(_settings.SpriteThickness - prev.SpriteThickness) > 1e-6f ||
                Math.Abs(_settings.EntityAnimationSpeed - prev.EntityAnimationSpeed) > 1e-6f ||
                Math.Abs(_settings.EntityAnimationAmplitude - prev.EntityAnimationAmplitude) > 1e-6f)
            {
                _meshDirty = true;
            }

            if (_settings.EnableEntityAnimation != prev.EnableEntityAnimation ||
                _settings.PauseEntityIdleAnimation != prev.PauseEntityIdleAnimation)
            {
                _lastEmulatedEntityRebakeRenderTime = double.NegativeInfinity;
            }

            if (!_settings.EnableEntityAnimation && prev.EnableEntityAnimation)
            {
                _entityBindPoseCommittedKey = null;
            }

            if (!_settings.ForceEntityCpuSkinning && prev.ForceEntityCpuSkinning)
            {
                _entityBindPoseCommittedKey = null;
            }

            var shadowTierChanged =
                _settings.EnableShadows != prev.EnableShadows ||
                _settings.EnableShadowCascades != prev.EnableShadowCascades ||
                _settings.ShadowMapResolution != prev.ShadowMapResolution;
            if (shadowTierChanged)
            {
                // SetRenderSettings runs on the UI thread; GenTexture requires a current GL context.
                _shadowTargetsDirty = true;
            }

            var nextWorldGen = new PreviewTerrainWorldGenSettings(
                _settings.TerrainWorldSeed,
                _settings.TerrainBiomeSize,
                _settings.TerrainAmplification,
                _settings.TerrainErosionStrength,
                _settings.TerrainContinentalness).Clamped();
            if (!nextWorldGen.Equals(_terrainWorldGenSettings))
            {
                _terrainWorldGenSettings = nextWorldGen;
                _terrainWorldGenSettingsDirty = true;
                InvalidatePreviewTaaHistory();
            }
        }
    }

    public void RenderFrame(TimeSpan elapsed)
    {
        lock (_sync)
        {
            _renderTimeAccum += elapsed.TotalSeconds;
            if (_settings.AutoRotate && !_userCameraDragging)
            {
                _rotationAccum += elapsed.TotalSeconds * 0.65;
            }

            if (_debugFlyRmbHeld && _flyEngaged)
            {
                TickFlyMovementLocked(elapsed);
            }
            else if (!_debugFlyRmbHeld)
            {
                _flyMoveVelocity = Vector3.Zero;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetCameraDebugPose(out Vector3 eye, out Vector3 lookTarget)
    {
        lock (_sync)
        {
            if (!_gpuAlive)
            {
                eye = default;
                lookTarget = default;
                return false;
            }

            if (_debugFlyRmbHeld && _flyEngaged)
            {
                ComposeFlyEye(_flyPosition, _flyYaw, _flyPitch, out eye, out lookTarget);
            }
            else
            {
                ComposeOrbitEye(_orbitBaseTarget, _orbitPan, _orbitYaw, _orbitPitch, _orbitDistance,
                    out eye, out lookTarget);
            }
            return true;
        }
    }

    /// <inheritdoc />
    public void SetDebugFlyInput(bool rightMouseHeld, bool keyW, bool keyA, bool keyS, bool keyD, bool keyQ, bool keyE,
        bool speedBoost, bool speedSlow)
    {
        lock (_sync)
        {
            if (rightMouseHeld && !_debugFlyRmbHeld)
            {
                BeginFlyFromCurrentPoseLocked();
            }
            else if (!rightMouseHeld && _debugFlyRmbHeld && _flyEngaged)
            {
                CommitFlyToOrbitLocked();
            }

            _debugFlyRmbHeld = rightMouseHeld;
            _flyKeyW = keyW;
            _flyKeyA = keyA;
            _flyKeyS = keyS;
            _flyKeyD = keyD;
            _flyKeyQ = keyQ;
            _flyKeyE = keyE;
            _flySpeedBoost = speedBoost;
            _flySpeedSlow = speedSlow;
        }
    }

    /// <summary>Orbit (rad/pixel), pan (world scale per pixel, scaled by distance in <see cref="ApplyCameraPanPixels"/>), zoom (step strength per wheel notch).</summary>
    public void SetCameraSensitivities(float orbitRadPerPx, float panPerPixel, float zoomPerWheelStep,
        float flyLookRadPerPx, bool invertLookY, float flyMoveSpeed, bool flySmoothAcceleration)
    {
        lock (_sync)
        {
            _camOrbitRadPerPx = Math.Clamp(orbitRadPerPx, 0.0008f, 0.04f);
            _camPanPerPx = Math.Clamp(panPerPixel, 0.0003f, 0.02f);
            _camZoomPerWheelStep = Math.Clamp(zoomPerWheelStep, 0.02f, 0.5f);
            _camFlyLookRadPerPx = Math.Clamp(flyLookRadPerPx, 0.0008f, 0.04f);
            _invertLookY = invertLookY;
            _flyMoveSpeed = Math.Clamp(flyMoveSpeed, 0.25f, 4f);
            _flySmoothAcceleration = flySmoothAcceleration;
        }
    }

    /// <summary>World-space orbit boom length (pivot to eye). Updates current distance and reset baseline.</summary>
    public void SetOrbitBoomArmDistance(float boomWorldUnits)
    {
        lock (_sync)
        {
            _orbitBoomArmDistance = Math.Clamp(boomWorldUnits, 1.05f, 120f);
            _orbitDistance = _orbitBoomArmDistance;
            _orbitDistanceDefault = _orbitBoomArmDistance;
        }
    }

    public void SetUserCameraDragging(bool dragging)
    {
        lock (_sync)
        {
            _userCameraDragging = dragging;
        }
    }

    public void ResetPreviewCameraToDefaults()
    {
        lock (_sync)
        {
            _orbitYaw = _orbitYawDefault;
            _orbitPitch = _orbitPitchDefault;
            _orbitDistance = _orbitDistanceDefault;
            _orbitPan = _orbitPanDefault;
            _flyEngaged = false;
            _flyMoveVelocity = Vector3.Zero;
            _flySpeedSessionMultiplier = 1f;
        }
    }

    public void FocusOrbitOnSubject()
    {
        lock (_sync)
        {
            _flyEngaged = false;
            _flyMoveVelocity = Vector3.Zero;
            if (!TryGetSubjectBoundsLocked(out var min, out var max))
            {
                return;
            }

            var center = (min + max) * 0.5f;
            _orbitPan = center - _orbitBaseTarget;
            var extents = max - min;
            var radius = MathF.Max(extents.X, MathF.Max(extents.Y, extents.Z)) * 0.5f;
            _orbitDistance = Math.Clamp(MathF.Max(radius * 2.8f, 1.05f), 1.05f, 120f);
        }
    }

    public void ApplyFlyLookPixels(float dx, float dy)
    {
        lock (_sync)
        {
            if (!_flyEngaged)
            {
                BeginFlyFromCurrentPoseLocked();
            }

            ApplyLookDeltaLocked(ref _flyYaw, ref _flyPitch, dx, dy, _camFlyLookRadPerPx);
        }
    }

    public void ApplyCameraOrbitPixels(float dx, float dy)
    {
        lock (_sync)
        {
            ApplyLookDeltaLocked(ref _orbitYaw, ref _orbitPitch, dx, dy, _camOrbitRadPerPx);
        }
    }

    public void ApplyCameraPanPixels(float dx, float dy)
    {
        lock (_sync)
        {
            ComposeOrbitEye(_orbitBaseTarget, _orbitPan, _orbitYaw, _orbitPitch, _orbitDistance,
                out var eye, out var look);
            var forward = look - eye;
            var fl = forward.Length();
            if (fl < 1e-5f)
            {
                return;
            }

            forward /= fl;
            var worldUp = Vector3.UnitY;
            var right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
            if (right.LengthSquared() < 1e-8f)
            {
                right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
            }

            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            var scale = _camPanPerPx * MathF.Max(0.35f, _orbitDistance * 0.22f);
            _orbitPan += right * (-dx * scale) + up * (dy * scale);
        }
    }

    /// <param name="wheelNotches">Typically ±1 per click (after normalizing platform delta).</param>
    public void ApplyCameraZoom(float wheelNotches)
    {
        lock (_sync)
        {
            if (_debugFlyRmbHeld && _flyEngaged)
            {
                var f = MathF.Exp(wheelNotches * _camZoomPerWheelStep * 0.35f);
                _flySpeedSessionMultiplier = Math.Clamp(_flySpeedSessionMultiplier * f, 0.15f, 8f);
                return;
            }

            var zoomF = MathF.Exp(-wheelNotches * _camZoomPerWheelStep * 0.18f);
            // Stay outside the unit cube (~0.5 extent) with margin so near-plane clipping does not shear faces.
            _orbitDistance = Math.Clamp(_orbitDistance * zoomF, 1.05f, 120f);
        }
    }

    private void SyncOrbitFromSceneLocked(IRenderPreviewScene scene)
    {
        var cam = scene.Camera;
        _orbitBaseTarget = cam.Target;
        var toEye = cam.Position - cam.Target;
        var dist = toEye.Length();
        if (dist < 1e-4f)
        {
            _orbitYaw = MathF.PI * 0.22f;
            _orbitPitch = 0.32f;
        }
        else
        {
            var dir = toEye / dist;
            _orbitPitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
            _orbitYaw = MathF.Atan2(dir.X, dir.Z);
        }

        _orbitDistance = Math.Clamp(_orbitBoomArmDistance, 1.05f, 120f);
        _orbitPan = Vector3.Zero;
        _flyEngaged = false;
        _orbitYawDefault = _orbitYaw;
        _orbitPitchDefault = _orbitPitch;
        _orbitDistanceDefault = _orbitDistance;
        _orbitPanDefault = _orbitPan;
    }

    private void BeginFlyFromCurrentPoseLocked()
    {
        ComposeOrbitEye(_orbitBaseTarget, _orbitPan, _orbitYaw, _orbitPitch, _orbitDistance,
            out var eye, out var lookTarget);
        _flyPosition = eye;
        var viewDir = lookTarget - eye;
        if (viewDir.LengthSquared() > 1e-8f)
        {
            YawPitchFromForward(Vector3.Normalize(viewDir), out _flyYaw, out _flyPitch);
        }
        else
        {
            _flyYaw = _orbitYaw;
            _flyPitch = _orbitPitch;
        }

        _flySpeedSessionMultiplier = 1f;
        _flyMoveVelocity = Vector3.Zero;
        _flyEngaged = true;
    }

    private void CommitFlyToOrbitLocked()
    {
        var forward = ForwardFromYawPitch(_flyYaw, _flyPitch);
        var pivot = _flyPosition + forward * _orbitDistance;
        _orbitPan = pivot - _orbitBaseTarget;
        YawPitchFromForward(-forward, out _orbitYaw, out _orbitPitch);
        _flyEngaged = false;
        _flyMoveVelocity = Vector3.Zero;
        _flySpeedSessionMultiplier = 1f;
    }

    private void ApplyLookDeltaLocked(ref float yaw, ref float pitch, float dx, float dy, float radPerPx)
    {
        yaw -= dx * radPerPx;
        var pitchDelta = dy * radPerPx;
        if (_invertLookY)
        {
            pitchDelta = -pitchDelta;
        }

        pitch = Math.Clamp(pitch - pitchDelta, -1.55f, 1.55f);
    }

    private void TickFlyMovementLocked(TimeSpan elapsed)
    {
        var dt = (float)elapsed.TotalSeconds;
        if (dt <= 0f || !float.IsFinite(dt))
        {
            return;
        }

        var forward = ForwardFromYawPitch(_flyYaw, _flyPitch);
        var worldUp = Vector3.UnitY;
        var rightRaw = Vector3.Cross(forward, worldUp);
        if (rightRaw.LengthSquared() < 1e-8f)
        {
            rightRaw = Vector3.Cross(forward, Vector3.UnitZ);
        }
        var right = rightRaw.LengthSquared() > 1e-12f
            ? Vector3.Normalize(rightRaw)
            : Vector3.UnitX;

        var moveIntent = Vector3.Zero;
        if (_flyKeyW)
        {
            moveIntent += forward;
        }

        if (_flyKeyS)
        {
            moveIntent -= forward;
        }

        if (_flyKeyD)
        {
            moveIntent += right;
        }

        if (_flyKeyA)
        {
            moveIntent -= right;
        }

        if (_flyKeyE)
        {
            moveIntent += worldUp;
        }

        if (_flyKeyQ)
        {
            moveIntent -= worldUp;
        }

        var speedScale = _flySpeedBoost ? 2.5f : _flySpeedSlow ? 0.35f : 1f;
        var maxSpeed = 5f * _flyMoveSpeed * _flySpeedSessionMultiplier * speedScale *
                       MathF.Max(0.35f, _orbitDistance * 0.15f);

        var previousPosition = _flyPosition;
        if (_flySmoothAcceleration)
        {
            var targetVelocity = moveIntent.LengthSquared() > 1e-8f
                ? Vector3.Normalize(moveIntent) * maxSpeed
                : Vector3.Zero;
            var blend = targetVelocity.LengthSquared() > 1e-8f ? 1f - MathF.Exp(-9f * dt) : 1f - MathF.Exp(-14f * dt);
            _flyMoveVelocity = Vector3.Lerp(_flyMoveVelocity, targetVelocity, blend);
            if (_flyMoveVelocity.LengthSquared() > 1e-10f)
            {
                _flyPosition += _flyMoveVelocity * dt;
            }
        }
        else if (moveIntent.LengthSquared() > 1e-8f)
        {
            _flyPosition += Vector3.Normalize(moveIntent) * maxSpeed * dt;
        }

        if (!IsFinite(_flyPosition) || !IsFinite(_flyMoveVelocity))
        {
            _flyPosition = previousPosition;
            _flyMoveVelocity = Vector3.Zero;
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private bool TryGetSubjectBoundsLocked(out Vector3 min, out Vector3 max)
    {
        ReadOnlySpan<float> verts;
        int stride;
        if (_blockModelSubject?.InterleavedVertices is { Length: > 0 } subjectVerts)
        {
            verts = subjectVerts;
            stride = _blockModelSubject.VertexStrideFloats > 0
                ? _blockModelSubject.VertexStrideFloats
                : PreviewMesh.FloatsPerVertex;
        }
        else if (_scene?.Meshes is { Count: > 0 } meshes &&
                 meshes[0].InterleavedVertices is { Length: > 0 } sceneVerts)
        {
            verts = sceneVerts;
            stride = PreviewMesh.FloatsPerVertex;
        }
        else
        {
            min = default;
            max = default;
            return false;
        }

        return TryComputeVertexBounds(verts, stride, out min, out max);
    }

    private static bool TryComputeVertexBounds(ReadOnlySpan<float> interleavedVertices, int vertexStrideFloats,
        out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity);
        if (vertexStrideFloats < 3 || interleavedVertices.Length < vertexStrideFloats)
        {
            return false;
        }

        for (var i = 0; i + 2 < interleavedVertices.Length; i += vertexStrideFloats)
        {
            var x = interleavedVertices[i];
            var y = interleavedVertices[i + 1];
            var z = interleavedVertices[i + 2];
            min = Vector3.Min(min, new Vector3(x, y, z));
            max = Vector3.Max(max, new Vector3(x, y, z));
        }

        return float.IsFinite(min.X);
    }

    private static Vector3 ForwardFromYawPitch(float yaw, float pitch)
    {
        var cp = MathF.Cos(pitch);
        var sp = MathF.Sin(pitch);
        var sy = MathF.Sin(yaw);
        var cy = MathF.Cos(yaw);
        return new Vector3(cp * sy, sp, cp * cy);
    }

    private static void YawPitchFromForward(Vector3 forward, out float yaw, out float pitch)
    {
        forward = Vector3.Normalize(forward);
        pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        yaw = MathF.Atan2(forward.X, forward.Z);
    }

    /// <summary>
    /// Places the eye on a sphere of radius <paramref name="distance"/> around pivot <paramref name="baseTarget"/> +
    /// <paramref name="pan"/> using yaw/pitch (orbit drag updates those angles).
    /// </summary>
    private static void ComposeOrbitEye(Vector3 baseTarget, Vector3 pan, float yaw, float pitch,
        float distance, out Vector3 eye, out Vector3 lookTarget)
    {
        lookTarget = baseTarget + pan;
        var offset = ForwardFromYawPitch(yaw, pitch) * distance;
        eye = lookTarget + offset;
    }

    private static void ComposeFlyEye(Vector3 position, float yaw, float pitch, out Vector3 eye, out Vector3 lookTarget)
    {
        eye = position;
        lookTarget = position + ForwardFromYawPitch(yaw, pitch);
    }

    private bool TryGetCachedShadowCasterBounds(out Vector3 min, out Vector3 max)
    {
        if (_shadowCasterBoundsValid)
        {
            min = _shadowCasterBoundsMin;
            max = _shadowCasterBoundsMax;
            return true;
        }

        min = default;
        max = default;
        return false;
    }
}
