using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AutoPBR.Preview;

/// <summary>
/// Conservative per-batch bounds for a single-weight GPU-skinned preview mesh.
/// Bind-space AABBs are cached per bone, then only their corners are transformed each frame.
/// </summary>
public sealed class PreviewGpuSkinnedBounds
{
    private const int ParallelUpdateMinBatches = 32;

    private readonly BoneBounds[][] _batchBoneBounds;

    private PreviewGpuSkinnedBounds(BoneBounds[][] batchBoneBounds) =>
        _batchBoneBounds = batchBoneBounds;

    public int BatchCount => _batchBoneBounds.Length;

    public static PreviewGpuSkinnedBounds? TryBuild(
        IReadOnlyList<PreviewDrawBatch> batches,
        ReadOnlySpan<float> interleavedVertices,
        ReadOnlySpan<uint> indices,
        int vertexStrideFloats)
    {
        if (batches.Count == 0 ||
            vertexStrideFloats < MinecraftModelBaker.FloatsPerSkinnedVertex ||
            interleavedVertices.Length < vertexStrideFloats)
        {
            return null;
        }

        var result = new BoneBounds[batches.Count][];
        var anyBounds = false;
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            if (batch.FirstIndex < 0 || batch.IndexCount <= 0 || batch.FirstIndex >= indices.Length)
            {
                result[batchIndex] = [];
                continue;
            }

            var byBone = new Dictionary<int, BoundsAccumulator>();
            var end = Math.Min(indices.Length, batch.FirstIndex + batch.IndexCount);
            for (var i = batch.FirstIndex; i < end; i++)
            {
                var vertexIndex = indices[i];
                if (vertexIndex > int.MaxValue / vertexStrideFloats)
                {
                    continue;
                }

                var offset = (int)vertexIndex * vertexStrideFloats;
                if (offset < 0 || offset + 12 >= interleavedVertices.Length)
                {
                    continue;
                }

                var position = new Vector3(
                    interleavedVertices[offset],
                    interleavedVertices[offset + 1],
                    interleavedVertices[offset + 2]);
                if (!IsFinite(position))
                {
                    continue;
                }

                var boneIndex = EntityEmulatedGpuSkinningMath.DecodeSkinnedBoneIndexFromFloat(
                    interleavedVertices[offset + 12]);
                byBone.TryGetValue(boneIndex, out var accumulator);
                accumulator.Add(position);
                byBone[boneIndex] = accumulator;
            }

            result[batchIndex] = byBone
                .Where(pair => pair.Value.IsValid)
                .OrderBy(pair => pair.Key)
                .Select(pair => new BoneBounds(pair.Key, pair.Value.Min, pair.Value.Max))
                .ToArray();
            anyBounds |= result[batchIndex].Length > 0;
        }

        return anyBounds ? new PreviewGpuSkinnedBounds(result) : null;
    }

    public bool UpdateDrawBatchBounds(
        PreviewDrawBatch[] batches,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        int boneCount,
        float meshSpaceLiftY)
    {
        if (batches.Length < _batchBoneBounds.Length ||
            boneCount <= 0 ||
            boneMatrices.Length < boneCount)
        {
            ClearDrawBatchBounds(batches);
            return false;
        }

        boneCount = Math.Min(boneCount, boneMatrices.Length);
        if (_batchBoneBounds.Length >= ParallelUpdateMinBatches)
        {
            // Copy palette for workers — Span cannot be captured by Parallel.For lambdas.
            var palette = boneMatrices.Slice(0, boneCount).ToArray();
            var anyBoundsFlag = 0;
            var batchBoneBounds = _batchBoneBounds;
            var liftY = meshSpaceLiftY;
            Parallel.For(0, batchBoneBounds.Length, batchIndex =>
            {
                if (UpdateOneBatchBounds(batches, batchBoneBounds, palette, palette.Length, liftY, batchIndex))
                {
                    Interlocked.Exchange(ref anyBoundsFlag, 1);
                }
            });
            return anyBoundsFlag != 0;
        }

        var anyBounds = false;
        for (var batchIndex = 0; batchIndex < _batchBoneBounds.Length; batchIndex++)
        {
            anyBounds |= UpdateOneBatchBounds(
                batches,
                _batchBoneBounds,
                boneMatrices,
                boneCount,
                meshSpaceLiftY,
                batchIndex);
        }

        return anyBounds;
    }

    private static bool UpdateOneBatchBounds(
        PreviewDrawBatch[] batches,
        BoneBounds[][] batchBoneBounds,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        int boneCount,
        float meshSpaceLiftY,
        int batchIndex)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var boneBounds in batchBoneBounds[batchIndex])
        {
            var boneIndex = Math.Clamp(boneBounds.BoneIndex, 0, boneCount - 1);
            ExpandTransformedBounds(
                boneBounds,
                boneMatrices[boneIndex],
                meshSpaceLiftY,
                ref min,
                ref max);
        }

        if (!IsFinite(min) || !IsFinite(max))
        {
            batches[batchIndex] = batches[batchIndex] with
            {
                BoundsCenter = Vector3.Zero,
                BoundsRadius = -1f,
            };
            return false;
        }

        var center = (min + max) * 0.5f;
        batches[batchIndex] = batches[batchIndex] with
        {
            BoundsCenter = center,
            BoundsRadius = Vector3.Distance(center, max),
        };
        return true;
    }

    public static void ClearDrawBatchBounds(Span<PreviewDrawBatch> batches)
    {
        for (var i = 0; i < batches.Length; i++)
        {
            batches[i] = batches[i] with
            {
                BoundsCenter = Vector3.Zero,
                BoundsRadius = -1f,
            };
        }
    }

    private static void ExpandTransformedBounds(
        BoneBounds bounds,
        Matrix4x4 bone,
        float meshSpaceLiftY,
        ref Vector3 min,
        ref Vector3 max)
    {
        for (var corner = 0; corner < 8; corner++)
        {
            var bindPosition = new Vector3(
                (corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            var skinned = Vector3.Transform(bindPosition, bone);
            var preview = EntityEmulatedGpuSkinningMath.PreviewCuboidNormalizeTexelPosition(skinned);
            preview.Y += meshSpaceLiftY;
            min = Vector3.Min(min, preview);
            max = Vector3.Max(max, preview);
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct BoneBounds(int BoneIndex, Vector3 Min, Vector3 Max);

    private struct BoundsAccumulator
    {
        public Vector3 Min;
        public Vector3 Max;
        private bool _hasValue;

        public bool IsValid =>
            _hasValue && PreviewGpuSkinnedBounds.IsFinite(Min) && PreviewGpuSkinnedBounds.IsFinite(Max);

        public void Add(Vector3 position)
        {
            if (!_hasValue)
            {
                Min = position;
                Max = position;
                _hasValue = true;
                return;
            }

            Min = Vector3.Min(Min, position);
            Max = Vector3.Max(Max, position);
        }
    }
}
