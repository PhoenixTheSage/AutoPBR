using System.Numerics;
using System.Runtime.CompilerServices;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Fixed six-plane storage for <see cref="GlRenderFrame"/> (ref-struct friendly).</summary>
[InlineArray(PreviewFrustumPlanes.PlaneCount)]
internal struct PreviewFrustumPlaneBuffer
{
    private Vector4 _element0;
}
