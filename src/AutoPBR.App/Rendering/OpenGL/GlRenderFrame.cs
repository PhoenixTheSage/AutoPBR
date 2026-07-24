using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Core.Models;
using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal ref struct GlRenderFrame
{
    public GL Gl;
    public int DefaultFbo;
    public int VpX;
    public int VpY;
    public int Vw;
    public int Vh;
    public int SceneCaptureW;
    public int SceneCaptureH;
    public float SceneCaptureScale;
    public PreviewRenderSettingsSnapshot Settings;
    public int SettingsRevision;
    public IRenderPreviewScene Scene;
    public PreviewMaterial? Material;
    public PreviewModelSubject? BlockModel;
    public PreviewMaterial[]? BlockSlots;
    public double Rotation;
    public double RenderTime;
    public Vector3 OrbitBaseTarget;
    public Vector3 OrbitPan;
    public bool FlyCamActive;
    public Vector3 FlyPosition;
    public float FlyYaw;
    public float FlyPitch;
    public float OrbitYaw;
    public float OrbitPitch;
    public float OrbitDistance;
    public bool MeshDirty;
    public bool MaterialDirty;
    public bool EntityEmulatedPreview;
    public EntityEmulatedPreviewRebakeContext? EntityRebakeCtx;
    public bool EntityEmulatedMaterialsOk;
    public float EntityEmulatedAnimClock;
    public bool EntityEmulatedPauseEdge;
    public bool UploadedLiveEntityAnim;
    public bool EntityBoneSnapshotValid;
    public int EntityBoneSnapshotCount;
    public bool EntityBonePaletteUploaded;
    public Vector3 WorldLightDir;
    public Matrix4x4 ShadowVp;
    public Matrix4x4 ShadowVpNear;
    public Matrix4x4 ShadowVpMid;
    public bool ShadowCascadesActive;
    public float CascadeSplitWorldDistance;
    public float CascadeMidSplitWorldDistance;
    public float CascadeBlendWorldWidth;
    public float ShadowDistance;
    public float ShadowFadeStart;
    /// <summary>
    /// Scales receiver shadow bias for large terrain-fitted far frustums (1 = subject-sized reference).
    /// </summary>
    public float ShadowBiasScale;
    public Matrix4x4 ModelMatrix;
    public int EntityAlphaModeUniform;
    public bool EntityBlendDraw;
    public bool EnableParallaxEff;
    public bool EnableParallaxAoEff;
    public bool EnableNormalMapEff;
    public bool EnableSpecularMapEff;
    public bool EnableParallaxShadowEff;
    public bool EnableTessellationDisplacementEff;
    public bool ShadowAvailable;
    public Vector3 Eye;
    public Vector3 LookTarget;
    public Matrix4x4 Proj;
    public Matrix4x4 UnjitteredProj;
    public Matrix4x4 View;
    /// <summary>Unjittered camera <c>proj * view</c> used for CPU/GPU frustum culling.</summary>
    public Matrix4x4 CameraViewProj;
    /// <summary>
    /// Rasterization <c>proj * view</c> matching the shaded pass (includes TAA jitter when active).
    /// Depth prepass + Hi-Z occlusion must use this so early-Z and occlusion tests agree with draws.
    /// </summary>
    public Matrix4x4 RasterViewProj;
    /// <summary>Six world-space frustum planes from <see cref="CameraViewProj"/> (left..far).</summary>
    public PreviewFrustumPlaneBuffer CameraFrustumPlanes;
    public bool CameraFrustumValid;
    public Vector2 PreviewTaaJitterNdc;
    public float NearPlane;
    public float FarPlane;
    public Vector3 LightDir;
    public bool GodRayCaptureActive;
    public bool HiZReady;
    public double LastVolumeInjectMs;
    public double LastVolumeIntegrateMs;
}
