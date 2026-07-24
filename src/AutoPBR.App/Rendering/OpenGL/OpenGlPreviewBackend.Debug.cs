using System.Numerics;
using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private const int SunDebugLineCount = 12;
    private const int SunDebugVertexCount = SunDebugLineCount * 2;
    private const int SunDebugFloatCount = SunDebugVertexCount * PreviewGridLinesFactory.FloatsPerVertex;

    private uint _sunDebugVao;
    private GlPersistentMappedUploadBuffer? _sunDebugVertexUpload;

    private void EnsureSunDebugOverlay(GL gl)
    {
        if (_sunDebugVao != 0)
        {
            return;
        }

        _sunDebugVao = gl.GenVertexArray();
        _sunDebugVertexUpload = new GlPersistentMappedUploadBuffer(
            gl,
            BufferTargetARB.ArrayBuffer,
            SunDebugFloatCount * sizeof(float),
            16,
            _glCapabilities?.CanUsePersistentUploadRing == true);
        gl.BindVertexArray(_sunDebugVao);
        _sunDebugVertexUpload.BindBuffer();
        unsafe
        {
            var stride = PreviewGridLinesFactory.FloatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        }

        gl.BindVertexArray(0);
    }

    private void DestroySunDebugOverlay()
    {
        var gl = _gl;
        if (gl is null)
        {
            _sunDebugVertexUpload = null;
            _sunDebugVao = 0;
            return;
        }

        _sunDebugVertexUpload?.Dispose();
        _sunDebugVertexUpload = null;

        if (_sunDebugVao != 0)
        {
            gl.DeleteVertexArray(_sunDebugVao);
            _sunDebugVao = 0;
        }
    }

    private void DrawSunProjectionDebug(ref GlRenderFrame frame)
    {
        if (!frame.Settings.ShowSunProjectionDebug || _lineProgram is not { IsValid: true })
        {
            return;
        }

        var gl = frame.Gl;
        EnsureSunDebugOverlay(gl);
        if (_sunDebugVao == 0)
        {
            return;
        }

        var viewProj = frame.Proj * frame.View;
        var towardSun = NormalizeOrDefault(-frame.WorldLightDir, Vector3.UnitY);
        var celestialFarPlane = frame.FarPlane > 1f ? frame.FarPlane : PreviewSunScreenProjection.SunDistance;
        var sunDist = Math.Clamp(celestialFarPlane * 0.72f, 1f, PreviewSunScreenProjection.SunDistance);
        var sunCenter = frame.Eye + towardSun * sunDist;
        var sunRight = CelestialBillboardRight(towardSun);
        var sunUp = Vector3.Normalize(Vector3.Cross(towardSun, sunRight));
        var sunRadius = sunDist * (PreviewSunScreenProjection.SunRadius / PreviewSunScreenProjection.SunDistance) *
                        Math.Clamp(frame.Settings.AtmosphereSunDiscSize, 0.05f, 2f);

        MoonBillboardPlacement moonPlacement = default;
        var drawMoonMarker = TryComputeMoonBillboardPlacement(
            frame.Eye,
            frame.WorldLightDir,
            celestialFarPlane,
            frame.Settings.AtmosphereMoonDiscSize,
            out moonPlacement);

        Span<float> verts = stackalloc float[SunDebugFloatCount];
        var i = 0;
        if (IsInFrontOfCamera(sunCenter, viewProj))
        {
            AppendCelestialDebugDiscWorld(
                ref i,
                verts,
                sunCenter,
                sunRight,
                sunUp,
                sunRadius,
                1f, 0.15f, 0.15f,
                0.15f, 1f, 0.35f);
        }

        if (drawMoonMarker && IsInFrontOfCamera(moonPlacement.Center, viewProj))
        {
            AppendCelestialDebugDiscWorld(
                ref i,
                verts,
                moonPlacement.Center,
                moonPlacement.Right,
                moonPlacement.Up,
                moonPlacement.Radius,
                0.55f, 0.65f, 1f,
                0.35f, 0.55f, 1f);
        }

        if (i == 0)
        {
            return;
        }

        var priorDepth = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Viewport(frame.VpX, frame.VpY, (uint)frame.Vw, (uint)frame.Vh);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        gl.BindVertexArray(_sunDebugVao);
        _sunDebugVertexUpload!.Upload(MemoryMarshal.AsBytes(verts[..i]));
        _sunDebugVertexUpload.BindBuffer();
        ConfigureSunDebugVertexPointers(gl, _sunDebugVertexUpload.ActiveOffset);
        _lineProgram.Use();
        SetLineProgramMvp(gl, viewProj);
        SetLineProgramColorMul(1f, 1f, 1f, 1f);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(i / PreviewGridLinesFactory.FloatsPerVertex));
        _sunDebugVertexUpload.MarkSubmitted();
        gl.BindVertexArray(0);

        if (priorDepth)
        {
            gl.Enable(EnableCap.DepthTest);
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }
    }

    private static bool IsInFrontOfCamera(Vector3 worldPos, Matrix4x4 viewProj)
    {
        var clip = Vector4.Transform(new Vector4(worldPos, 1f), viewProj);
        return clip.W > 1e-5f;
    }

    private static unsafe void ConfigureSunDebugVertexPointers(GL gl, nint byteOffset)
    {
        var stride = PreviewGridLinesFactory.FloatsPerVertex * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)byteOffset);
        gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(byteOffset + 3 * sizeof(float)));
    }

    private static Vector3 NormalizeOrDefault(Vector3 v, Vector3 fallback)
    {
        var len2 = v.LengthSquared();
        return len2 > 1e-12f ? v / MathF.Sqrt(len2) : fallback;
    }

    private static Vector3 CelestialBillboardRight(Vector3 towardBody)
    {
        towardBody = NormalizeOrDefault(towardBody, Vector3.UnitY);
        var right = Vector3.Cross(Vector3.UnitY, towardBody);
        if (right.LengthSquared() < 1e-10f)
        {
            right = Vector3.Cross(Vector3.UnitZ, towardBody);
        }

        return NormalizeOrDefault(right, Vector3.UnitX);
    }

    private static void AppendCelestialDebugDiscWorld(
        ref int offset,
        Span<float> verts,
        Vector3 center,
        Vector3 right,
        Vector3 up,
        float radius,
        float crossR,
        float crossG,
        float crossB,
        float ringR,
        float ringG,
        float ringB)
    {
        right = NormalizeOrDefault(right, Vector3.UnitX);
        up = NormalizeOrDefault(up, Vector3.UnitY);
        radius = Math.Max(radius, 1e-4f);
        var arm = radius * 1.4f;
        var ring = radius * 1.15f;
        AddSunDebugLine(verts, ref offset, center - right * arm, center + right * arm, crossR, crossG, crossB, 1f);
        AddSunDebugLine(verts, ref offset, center - up * arm, center + up * arm, crossR, crossG, crossB, 1f);
        var bl = center - right * ring - up * ring;
        var br = center + right * ring - up * ring;
        var tr = center + right * ring + up * ring;
        var tl = center - right * ring + up * ring;
        AddSunDebugLine(verts, ref offset, bl, br, ringR, ringG, ringB, 1f);
        AddSunDebugLine(verts, ref offset, br, tr, ringR, ringG, ringB, 1f);
        AddSunDebugLine(verts, ref offset, tr, tl, ringR, ringG, ringB, 1f);
        AddSunDebugLine(verts, ref offset, tl, bl, ringR, ringG, ringB, 1f);
    }

    private static void AddSunDebugLine(Span<float> verts, ref int offset, Vector3 p0, Vector3 p1,
        float r, float g, float b, float alpha)
    {
        WriteSunDebugVertex(verts, ref offset, p0.X, p0.Y, p0.Z, r, g, b, alpha);
        WriteSunDebugVertex(verts, ref offset, p1.X, p1.Y, p1.Z, r, g, b, alpha);
    }

    private static void WriteSunDebugVertex(Span<float> verts, ref int offset, float x, float y, float z, float r, float g, float b, float a)
    {
        verts[offset++] = x;
        verts[offset++] = y;
        verts[offset++] = z;
        verts[offset++] = r;
        verts[offset++] = g;
        verts[offset++] = b;
        verts[offset++] = a;
    }
}
