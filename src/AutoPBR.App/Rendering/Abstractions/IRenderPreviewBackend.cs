using System.Numerics;

using AutoPBR.Core.Models;
using AutoPBR.Preview;

using JetBrains.Annotations;

namespace AutoPBR.App.Rendering.Abstractions;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IRenderPreviewBackend : IDisposable
{
    string BackendName { get; }
    bool IsInitialized { get; }
    string? LastErrorMessage { get; }

    void Initialize(RenderPreviewInitializationOptions options);
    void Resize(int width, int height);
    void SetScene(IRenderPreviewScene scene);
    void SetMaterial(PreviewMaterial? material);
    void SetGroundMaterial(PreviewMaterial? material);

    /// <summary>
    /// Multi-slot LabPBR ground materials for BlockModel-style terrain (top/side/dirt/overlay).
    /// Empty or null falls back to <see cref="SetGroundMaterial"/>.
    /// </summary>
    void SetGroundMaterials(PreviewMaterial[]? slotMaterials, bool overlayIsCutout = true);

    /// <summary>CPU bake flags for streamed Full terrain chunks; invalidates resident meshes when changed.</summary>
    void SetTerrainGrassBakeSettings(PreviewTerrainGrassBakeSettings settings);
    void SetBlockModelPreview(PreviewModelSubject? subject, PreviewMaterial[]? slotMaterials);
    void SetRenderSettings(PreviewRenderSettings settings);
    void RenderFrame(TimeSpan elapsed);

    PreviewGpuInitProgress GpuInitProgress { get; }
    event Action<PreviewGpuInitProgress>? GpuInitProgressChanged;

    /// <summary>Clears on-disk and in-memory shader caches and reloads GPU programs on the preview GL thread.</summary>
    void InvalidateShaderCachesAndReload();

    /// <summary>Debug: world-space eye and look target for on-screen readout (OpenGL path).</summary>
    bool TryGetCameraDebugPose(out Vector3 eye, out Vector3 lookTarget);

    /// <summary>Debug fly: hold right mouse for FPS-style look; WASD/QE moves while right mouse is held.</summary>
    void SetDebugFlyInput(bool rightMouseHeld, bool keyW, bool keyA, bool keyS, bool keyD, bool keyQ, bool keyE,
        bool speedBoost, bool speedSlow);
}
