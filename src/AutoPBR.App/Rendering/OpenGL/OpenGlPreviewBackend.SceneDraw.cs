using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend
{
    private void TryInitLineOverlay(GL gl, bool useOpenGlEs)
    {
        DestroyLineOverlay();
        _lineProgram = new GlLineShaderProgram(gl, useOpenGlEs, out var err);
        if (!_lineProgram.IsValid)
        {
            _lineProgram.Dispose();
            _lineProgram = null;
            if (!string.IsNullOrWhiteSpace(err))
            {
                EmitDiagnostic("[3D preview] Line overlay shader: " + err);
            }

            return;
        }

        // Thick XZ ribbons (triangles) so Core-profile GL still gets visible width.
        // Vertex color is white; runtime tint comes from uColorMul.
        var gridVerts = PreviewGridLinesFactory.BuildGrid(
            PreviewStageConstants.GridHalfExtent,
            PreviewStageConstants.GridStep,
            PreviewStageConstants.GridWorldY + PreviewStageConstants.GridYBias,
            1f, 1f, 1f, 1f,
            PreviewStageConstants.GridLineHalfWidth);
        _gridVertexCount = gridVerts.Length / PreviewGridLinesFactory.FloatsPerVertex;
        var axisVerts = PreviewGridLinesFactory.BuildAxes(0.82f, 1f, 0.28f, 0.22f, 0.22f, 0.95f, 0.28f, 0.32f, 0.58f, 1f);
        _gridVao = gl.GenVertexArray();
        _gridVbo = gl.GenBuffer();
        gl.BindVertexArray(_gridVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _gridVbo);
        gl.BufferData<float>(GLEnum.ArrayBuffer, gridVerts.AsSpan(), GLEnum.StaticDraw);
        unsafe
        {
            var stride = PreviewGridLinesFactory.FloatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        }

        gl.BindVertexArray(0);

        _axesVao = gl.GenVertexArray();
        _axesVbo = gl.GenBuffer();
        gl.BindVertexArray(_axesVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _axesVbo);
        gl.BufferData<float>(GLEnum.ArrayBuffer, axisVerts.AsSpan(), GLEnum.StaticDraw);
        unsafe
        {
            var stride = PreviewGridLinesFactory.FloatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        }

        gl.BindVertexArray(0);
        _lineUniformLocs = ResolveLineProgramUniformLocs(_lineProgram);
    }

    private void DestroyLineOverlay()
    {
        var gl = _gl;
        if (gl is null)
        {
            _lineProgram?.Dispose();
            _lineProgram = null;
            _gridVao = _gridVbo = _axesVao = _axesVbo = 0;
            _gridVertexCount = 0;
            return;
        }

        if (_gridVbo != 0)
        {
            gl.DeleteBuffer(_gridVbo);
            _gridVbo = 0;
        }

        if (_axesVbo != 0)
        {
            gl.DeleteBuffer(_axesVbo);
            _axesVbo = 0;
        }

        if (_gridVao != 0)
        {
            gl.DeleteVertexArray(_gridVao);
            _gridVao = 0;
        }

        if (_axesVao != 0)
        {
            gl.DeleteVertexArray(_axesVao);
            _axesVao = 0;
        }

        _gridVertexCount = 0;
        _lineProgram?.Dispose();
        _lineProgram = null;
    }

    private void DrawBackgroundGrid(GL gl, Matrix4x4 proj, Matrix4x4 view, in PreviewRenderSettingsSnapshot settings)
    {
        if (_lineProgram is null || !_lineProgram.IsValid || _gridVao == 0)
        {
            return;
        }

        var mvp = proj * view * Matrix4x4.Identity;
        _lineProgram.Use();
        SetLineProgramMvp(gl, mvp);
        SetLineProgramColorMul(settings.GridColorR, settings.GridColorG, settings.GridColorB, settings.GridColorA);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorCull = gl.IsEnabled(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(_gridVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_gridVertexCount);
        gl.BindVertexArray(0);
        if (priorCull)
        {
            gl.Enable(EnableCap.CullFace);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }
    }

    private void DrawCornerAxes(GL gl, int vpX, int vpY, int vw, int vh, Matrix4x4 proj, Matrix4x4 view)
    {
        if (_lineProgram is null || !_lineProgram.IsValid || _axesVao == 0)
        {
            return;
        }

        _ = proj;
        _ = view;
        var size = Math.Clamp(Math.Min(vw, vh) / 5, 72, 160);
        const int pad = 10;
        var vx = vpX + Math.Max(0, vw - pad - size);
        var vy = vpY + pad;
        gl.Viewport(vx, vy, (uint)size, (uint)size);
        var ortho = Matrix4x4.CreateOrthographic(2.4f, 2.4f, -4f, 4f);
        // Debug gizmo: orient world axes by the active camera view rotation only (no translation),
        // so orbit/pan/zoom behavior is visible in the inset.
        var viewRotationOnly = view;
        // PreviewGlMatrices stores view translation in the last column (M14/M24/M34 in Matrix4x4 row storage),
        // so zero those to keep the corner gizmo as rotation-only.
        viewRotationOnly.M14 = 0f;
        viewRotationOnly.M24 = 0f;
        viewRotationOnly.M34 = 0f;
        var model = viewRotationOnly;
        var mvp = ortho * Matrix4x4.Identity * model;
        gl.Disable(EnableCap.DepthTest);
        _lineProgram.Use();
        SetLineProgramMvp(gl, mvp);
        SetLineProgramColorMul(1f, 1f, 1f, 1f);
        gl.BindVertexArray(_axesVao);
        gl.DrawArrays(PrimitiveType.Lines, 0, 6);
        gl.BindVertexArray(0);
        gl.Disable(EnableCap.DepthTest);
        gl.Viewport(vpX, vpY, (uint)vw, (uint)vh);
    }

    private void SetLineProgramMvp(GL gl, Matrix4x4 mvp)
    {
        _ = gl;
        if (_lineProgram is null || !_lineProgram.IsValid)
        {
            return;
        }

        SetMatrixLoc(_lineUniformLocs.Mvp, mvp);
    }

    private void SetLineProgramColorMul(float r, float g, float b, float a)
    {
        if (_lineProgram is null || !_lineProgram.IsValid)
        {
            return;
        }

        SetVec4Loc(_lineUniformLocs.ColorMul, new Vector4(r, g, b, a));
    }
}
