using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CPU frustum / distance visibility for <see cref="PreviewDrawBatch"/> spheres.
/// Matches <see cref="GlGpuDrawCommandCompactor.WriteCullRecord(Span{float}, PreviewDrawBatch)"/> transform conventions.
/// </summary>
internal static class PreviewDrawBatchFrustumCull
{
    /// <summary>
    /// Transforms a preview-space batch sphere into world space using the same multiply and
    /// scale convention as the GPU cull record writer.
    /// </summary>
    public static void TransformBatchSphere(
        PreviewDrawBatch batch,
        Matrix4x4 modelMatrix,
        float boundsPadding,
        out Vector3 worldCenter,
        out float worldRadius)
    {
        var localCenter = batch.BoundsCenter;
        worldCenter = new Vector3(
            modelMatrix.M11 * localCenter.X + modelMatrix.M12 * localCenter.Y +
            modelMatrix.M13 * localCenter.Z + modelMatrix.M14,
            modelMatrix.M21 * localCenter.X + modelMatrix.M22 * localCenter.Y +
            modelMatrix.M23 * localCenter.Z + modelMatrix.M24,
            modelMatrix.M31 * localCenter.X + modelMatrix.M32 * localCenter.Y +
            modelMatrix.M33 * localCenter.Z + modelMatrix.M34);
        var modelScale = MathF.Max(
            new Vector3(modelMatrix.M11, modelMatrix.M12, modelMatrix.M13).Length(),
            MathF.Max(
                new Vector3(modelMatrix.M21, modelMatrix.M22, modelMatrix.M23).Length(),
                new Vector3(modelMatrix.M31, modelMatrix.M32, modelMatrix.M33).Length()));
        worldRadius = batch.HasBounds
            ? (MathF.Max(0f, batch.BoundsRadius) + MathF.Max(0f, boundsPadding)) * modelScale
            : -1f;
    }

    /// <summary>
    /// True when the batch should be drawn: unknown bounds stay visible; otherwise frustum + LOD.
    /// </summary>
    public static bool IsBatchVisible(
        PreviewDrawBatch batch,
        ReadOnlySpan<Vector4> frustumPlanes,
        Vector3 cameraPosition,
        Matrix4x4 modelMatrix,
        float boundsPadding = 0f)
    {
        if (!batch.HasBounds)
        {
            return true;
        }

        TransformBatchSphere(batch, modelMatrix, boundsPadding, out var center, out var radius);
        if (!PreviewFrustumPlanes.SphereIntersects(frustumPlanes, center, radius))
        {
            return false;
        }

        var lodMax = batch.LodMaxDistance;
        if (lodMax > 0f && float.IsFinite(lodMax))
        {
            var distanceToCamera = Vector3.Distance(center, cameraPosition);
            if (distanceToCamera - radius > lodMax)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when every batch has known bounds and all fail the visibility test.
    /// Unknown bounds prevent a full early-out.
    /// </summary>
    public static bool IsSubjectFullyCulled(
        IReadOnlyList<PreviewDrawBatch> batches,
        ReadOnlySpan<Vector4> frustumPlanes,
        Vector3 cameraPosition,
        Matrix4x4 modelMatrix,
        float boundsPadding = 0f)
    {
        if (batches.Count == 0)
        {
            return false;
        }

        foreach (var batch in batches)
        {
            if (!batch.HasBounds)
            {
                return false;
            }

            if (IsBatchVisible(batch, frustumPlanes, cameraPosition, modelMatrix, boundsPadding))
            {
                return false;
            }
        }

        return true;
    }
}
