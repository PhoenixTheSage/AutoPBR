using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Publishes cache optical depth as a snapped 2D Beer-Lambert transmittance field.
/// </summary>
internal sealed class GlCloudGroundTransmittancePublisher
{
    private readonly GL _gl;
    private readonly GlShaderProgram _program;
    private readonly uint _quadVertexArray;
    private readonly Uniforms _uniforms;

    public GlCloudGroundTransmittancePublisher(
        GL gl,
        GlShaderProgram program,
        uint quadVertexArray)
    {
        _gl = gl;
        _program = program;
        _quadVertexArray = quadVertexArray;
        _uniforms = Uniforms.Resolve(program);
    }

    public bool TryPublish(
        GlCloudLightFroxelCache cache,
        GlCloudGroundTransmittanceTarget target,
        float groundWorldY,
        out string diagnostic)
    {
        return TryPublish(
            cache,
            target,
            groundWorldY,
            System.Numerics.Vector3.Zero,
            1f,
            out diagnostic);
    }

    public bool TryPublish(
        GlCloudLightFroxelCache cache,
        GlCloudGroundTransmittanceTarget target,
        float groundWorldY,
        System.Numerics.Vector3 currentWindOffset,
        float windPeriod,
        out string diagnostic)
    {
        var hasFar = cache.Far.IsGenerated;
        var hasNear = target.Profile.CombineNearAndFar &&
            cache.Near.IsGenerated;
        if (!_program.IsValid ||
            _quadVertexArray == 0 ||
            !target.IsAllocated ||
            !hasFar)
        {
            diagnostic = "program-quad-target-or-far-cache-invalid";
            return false;
        }

        var priorBlend = _gl.IsEnabled(EnableCap.Blend);
        var priorDepth = _gl.IsEnabled(EnableCap.DepthTest);
        var priorCull = _gl.IsEnabled(EnableCap.CullFace);
        var priorScissor = _gl.IsEnabled(EnableCap.ScissorTest);
        var priorDepthMask = _gl.GetBoolean(GetPName.DepthWritemask);
        var priorColorMask = new bool[4];
        _gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);
        var priorReadFramebuffer = _gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDrawFramebuffer = _gl.GetInteger(GetPName.DrawFramebufferBinding);
        var priorProgram = _gl.GetInteger(GetPName.CurrentProgram);
        var priorVertexArray = _gl.GetInteger(GetPName.VertexArrayBinding);
        var priorActiveTexture = _gl.GetInteger(GetPName.ActiveTexture);
        var priorViewport = new int[4];
        _gl.GetInteger(GetPName.Viewport, priorViewport);
        _gl.ActiveTexture(TextureUnit.Texture0);
        var priorNearArray = _gl.GetInteger(GetPName.TextureBinding2DArray);
        _gl.ActiveTexture(TextureUnit.Texture1);
        var priorFarArray = _gl.GetInteger(GetPName.TextureBinding2DArray);

        try
        {
            if (!target.TryBindWrite(out var bindDiagnostic))
            {
                diagnostic = bindDiagnostic;
                return false;
            }

            FlushErrors();
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.DepthMask(false);
            _gl.ColorMask(true, true, true, true);
            _program.Use();

            var far = cache.Far.GetSamplingTransform(
                currentWindOffset,
                windPeriod);
            var near = hasNear
                ? cache.Near.GetSamplingTransform(
                    currentWindOffset,
                    windPeriod)
                : default;
            var basis = far.Basis;
            SetVec3(_uniforms.BasisRight, basis.Right);
            SetVec3(_uniforms.BasisUp, basis.Up);
            SetVec3(_uniforms.BasisForward, basis.Forward);
            SetVec2(
                _uniforms.NearPlaneCenter,
                hasNear ? near.PlaneCenterX : 0f,
                hasNear ? near.PlaneCenterY : 0f);
            SetVec2(
                _uniforms.FarPlaneCenter,
                far.PlaneCenterX,
                far.PlaneCenterY);
            SetFloat(
                _uniforms.NearWorldSpan,
                hasNear ? near.Profile.WorldSpan : 1f);
            SetFloat(_uniforms.FarWorldSpan, far.Profile.WorldSpan);
            SetFloat(
                _uniforms.NearLightDepthMin,
                hasNear ? near.LightDepthMin : 0f);
            SetFloat(_uniforms.FarLightDepthMin, far.LightDepthMin);
            SetFloat(
                _uniforms.NearLightDepthSpan,
                hasNear ? near.LightDepthSpan : 1f);
            SetFloat(_uniforms.FarLightDepthSpan, far.LightDepthSpan);
            SetInt(
                _uniforms.NearDepth,
                hasNear ? near.Profile.Depth : 1);
            SetInt(_uniforms.FarDepth, far.Profile.Depth);
            SetFloat(
                _uniforms.NearOverlapFraction,
                cache.Profile.NearOverlapFraction);
            SetInt(_uniforms.HasNear, hasNear ? 1 : 0);
            SetInt(_uniforms.HasFar, 1);
            SetVec2(
                _uniforms.OutputPlaneCenter,
                far.PlaneCenterX,
                far.PlaneCenterY);
            SetFloat(_uniforms.OutputWorldSpan, far.Profile.WorldSpan);
            SetFloat(_uniforms.GroundWorldY, groundWorldY);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(
                TextureTarget.Texture2DArray,
                hasNear ? cache.Near.ArrayTextureHandle : 0);
            SetInt(_uniforms.NearCache, 0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(
                TextureTarget.Texture2DArray,
                cache.Far.ArrayTextureHandle);
            SetInt(_uniforms.FarCache, 1);

            _gl.BindVertexArray(_quadVertexArray);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            var error = _gl.GetError();
            if (error != GLEnum.NoError)
            {
                diagnostic = "draw-" + error;
                return false;
            }

            target.Commit(
                far,
                currentWindOffset,
                hasNear ? cache.Near.GenerationId : 0,
                cache.Far.GenerationId);
            diagnostic =
                $"published-cq3.5;generation={target.GenerationId};" +
                $"source={cache.Near.GenerationId}/{cache.Far.GenerationId};" +
                $"mode={(hasNear ? "near-far-overlap" : "far-native")};" +
                "windReprojection=committed";
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = $"{ex.GetType().Name}:{ex.Message}";
            return false;
        }
        finally
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(
                TextureTarget.Texture2DArray,
                (uint)Math.Max(0, priorNearArray));
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(
                TextureTarget.Texture2DArray,
                (uint)Math.Max(0, priorFarArray));
            _gl.ActiveTexture((TextureUnit)priorActiveTexture);
            _gl.BindVertexArray((uint)Math.Max(0, priorVertexArray));
            _gl.UseProgram((uint)Math.Max(0, priorProgram));
            _gl.BindFramebuffer(
                FramebufferTarget.ReadFramebuffer,
                (uint)Math.Max(0, priorReadFramebuffer));
            _gl.BindFramebuffer(
                FramebufferTarget.DrawFramebuffer,
                (uint)Math.Max(0, priorDrawFramebuffer));
            _gl.Viewport(
                priorViewport[0],
                priorViewport[1],
                (uint)Math.Max(1, priorViewport[2]),
                (uint)Math.Max(1, priorViewport[3]));
            _gl.DepthMask(priorDepthMask);
            _gl.ColorMask(
                priorColorMask[0],
                priorColorMask[1],
                priorColorMask[2],
                priorColorMask[3]);
            SetEnabled(EnableCap.Blend, priorBlend);
            SetEnabled(EnableCap.DepthTest, priorDepth);
            SetEnabled(EnableCap.CullFace, priorCull);
            SetEnabled(EnableCap.ScissorTest, priorScissor);
        }
    }

    private void SetEnabled(EnableCap capability, bool enabled)
    {
        if (enabled)
        {
            _gl.Enable(capability);
        }
        else
        {
            _gl.Disable(capability);
        }
    }

    private void SetInt(int location, int value)
    {
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetFloat(int location, float value)
    {
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetVec2(int location, float x, float y)
    {
        if (location >= 0)
        {
            _gl.Uniform2(location, x, y);
        }
    }

    private void SetVec3(int location, System.Numerics.Vector3 value)
    {
        if (location >= 0)
        {
            _gl.Uniform3(location, value.X, value.Y, value.Z);
        }
    }

    private void FlushErrors()
    {
        for (var index = 0;
             index < 16 && _gl.GetError() != GLEnum.NoError;
             index++)
        {
        }
    }

    private readonly record struct Uniforms(
        int NearCache,
        int FarCache,
        int BasisRight,
        int BasisUp,
        int BasisForward,
        int NearPlaneCenter,
        int FarPlaneCenter,
        int NearWorldSpan,
        int FarWorldSpan,
        int NearLightDepthMin,
        int FarLightDepthMin,
        int NearLightDepthSpan,
        int FarLightDepthSpan,
        int NearDepth,
        int FarDepth,
        int NearOverlapFraction,
        int HasNear,
        int HasFar,
        int OutputPlaneCenter,
        int OutputWorldSpan,
        int GroundWorldY)
    {
        public static Uniforms Resolve(GlShaderProgram program) =>
            new(
                program.GetUniformLocation("uNearCache"),
                program.GetUniformLocation("uFarCache"),
                program.GetUniformLocation("uBasisRight"),
                program.GetUniformLocation("uBasisUp"),
                program.GetUniformLocation("uBasisForward"),
                program.GetUniformLocation("uNearPlaneCenter"),
                program.GetUniformLocation("uFarPlaneCenter"),
                program.GetUniformLocation("uNearWorldSpan"),
                program.GetUniformLocation("uFarWorldSpan"),
                program.GetUniformLocation("uNearLightDepthMin"),
                program.GetUniformLocation("uFarLightDepthMin"),
                program.GetUniformLocation("uNearLightDepthSpan"),
                program.GetUniformLocation("uFarLightDepthSpan"),
                program.GetUniformLocation("uNearDepth"),
                program.GetUniformLocation("uFarDepth"),
                program.GetUniformLocation("uNearOverlapFraction"),
                program.GetUniformLocation("uHasNear"),
                program.GetUniformLocation("uHasFar"),
                program.GetUniformLocation("uOutputPlaneCenter"),
                program.GetUniformLocation("uOutputWorldSpan"),
                program.GetUniformLocation("uGroundWorldY"));
    }
}
