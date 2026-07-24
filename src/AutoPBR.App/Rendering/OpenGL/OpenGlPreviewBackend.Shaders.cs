using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend
{
    private void SetMatrixOnProgram(GlShaderProgram program, string name, Matrix4x4 m)
    {
        var loc = program.GetUniformLocation(name);
        if (loc < 0)
        {
            return;
        }

        var mt = Matrix4x4.Transpose(m);
        _gl!.UniformMatrix4(loc, 1, false, in mt.M11);
    }

    private void SetMatrixOnProgram(GlProceduralSkyProgram program, string name, Matrix4x4 m)
    {
        var loc = program.GetUniformLocation(name);
        if (loc < 0)
        {
            return;
        }

        var mt = Matrix4x4.Transpose(m);
        _gl!.UniformMatrix4(loc, 1, false, in mt.M11);
    }

    private void SetVec3OnProgram(GlShaderProgram program, string name, Vector3 v)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl!.Uniform3(loc, v.X, v.Y, v.Z);
        }
    }

    private void SetVec3OnProgram(GlProceduralSkyProgram program, string name, Vector3 v)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl!.Uniform3(loc, v.X, v.Y, v.Z);
        }
    }

    private void SetFloatOnProgram(GlShaderProgram program, string name, float v)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl!.Uniform1(loc, v);
        }
    }

    private void SetFloatOnProgram(GlProceduralSkyProgram program, string name, float v)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl!.Uniform1(loc, v);
        }
    }

    private void SetIntOnProgram(GlShaderProgram program, string name, int v)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl!.Uniform1(loc, v);
        }
    }

    private void SetMatrixLoc(int loc, Matrix4x4 m)
    {
        if (loc < 0)
        {
            return;
        }

        var mt = Matrix4x4.Transpose(m);
        _gl!.UniformMatrix4(loc, 1, false, in mt.M11);
    }

    private void SetMatrixOnProgramLoc(GlShaderProgram program, int loc, Matrix4x4 m)
    {
        if (loc < 0)
        {
            return;
        }

        program.Use();
        var mt = Matrix4x4.Transpose(m);
        _gl!.UniformMatrix4(loc, 1, false, in mt.M11);
    }

    private void SetVec2Loc(int loc, Vector2 v)
    {
        if (loc >= 0)
        {
            _gl!.Uniform2(loc, v.X, v.Y);
        }
    }

    private void SetVec3Loc(int loc, Vector3 v)
    {
        if (loc >= 0)
        {
            _gl!.Uniform3(loc, v.X, v.Y, v.Z);
        }
    }

    private void SetVec4Loc(int loc, Vector4 v)
    {
        if (loc >= 0)
        {
            _gl!.Uniform4(loc, v.X, v.Y, v.Z, v.W);
        }
    }

    private void SetFloatLoc(int loc, float v)
    {
        if (loc >= 0)
        {
            _gl!.Uniform1(loc, v);
        }
    }

    private void SetIntLoc(int loc, int v)
    {
        if (loc >= 0)
        {
            _gl!.Uniform1(loc, v);
        }
    }

    private void SetFloatOnProgramLoc(GlShaderProgram program, int loc, float v)
    {
        if (loc >= 0)
        {
            program.Use();
            _gl!.Uniform1(loc, v);
        }
    }

    private void SetIntOnProgramLoc(GlShaderProgram program, int loc, int v)
    {
        if (loc >= 0)
        {
            program.Use();
            _gl!.Uniform1(loc, v);
        }
    }

    private void SetInt3OnProgramLoc(GlShaderProgram program, int loc, int x, int y, int z)
    {
        if (loc >= 0)
        {
            program.Use();
            _gl!.Uniform3(loc, x, y, z);
        }
    }

    private void SetVec2OnProgramLoc(GlShaderProgram program, int loc, Vector2 v)
    {
        if (loc >= 0)
        {
            program.Use();
            _gl!.Uniform2(loc, v.X, v.Y);
        }
    }

    private void SetVec3OnProgramLoc(GlShaderProgram program, int loc, Vector3 v)
    {
        if (loc >= 0)
        {
            program.Use();
            _gl!.Uniform3(loc, v.X, v.Y, v.Z);
        }
    }
}
