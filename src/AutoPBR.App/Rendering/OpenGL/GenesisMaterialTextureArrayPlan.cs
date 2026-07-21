using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed record GenesisMaterialTextureArrayPlan(
    int Width,
    int Height,
    int Layers,
    PreviewMaterialContentKey.Value[] SlotKeys)
{
    public const int MaxLayers = 2048;

    public static bool TryCreate(
        IReadOnlyList<PreviewMaterial> slots,
        int maxTextureArrayLayers,
        out GenesisMaterialTextureArrayPlan? plan,
        out string reason)
    {
        plan = null;
        reason = string.Empty;
        if (slots.Count <= 0)
        {
            reason = "no material slots";
            return false;
        }

        var layerLimit = Math.Min(MaxLayers, Math.Max(1, maxTextureArrayLayers));
        if (slots.Count > layerLimit)
        {
            reason = $"slot count {slots.Count} exceeds texture-array layer limit {layerLimit}";
            return false;
        }

        var width = 1;
        var height = 1;
        foreach (var slot in slots)
        {
            width = Math.Max(width, Math.Max(1, slot.Width));
            height = Math.Max(height, Math.Max(1, slot.Height));
        }

        var keys = new PreviewMaterialContentKey.Value[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var slotWidth = Math.Max(1, slot.Width);
            var slotHeight = Math.Max(1, slot.Height);
            if (!HasValidAlbedo(slot, slotWidth, slotHeight))
            {
                reason = $"slot {i} has invalid albedo dimensions";
                return false;
            }

            if (!OptionalMapIsCompatible(slot.NormalRgba, slotWidth, slotHeight) ||
                !OptionalMapIsCompatible(slot.SpecularRgba, slotWidth, slotHeight) ||
                !OptionalMapIsCompatible(slot.HeightRgba, slotWidth, slotHeight))
            {
                reason = $"slot {i} has mixed map dimensions";
                return false;
            }

            keys[i] = PreviewMaterialContentKey.Compute(slot);
        }

        plan = new GenesisMaterialTextureArrayPlan(width, height, slots.Count, keys);
        return true;
    }

    public bool ContentEquals(GenesisMaterialTextureArrayPlan other)
    {
        if (Width != other.Width || Height != other.Height || Layers != other.Layers)
        {
            return false;
        }

        for (var i = 0; i < SlotKeys.Length; i++)
        {
            if (!PreviewMaterialContentKey.Equals(SlotKeys[i], other.SlotKeys[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidAlbedo(PreviewMaterial material, int width, int height) =>
        material.AlbedoRgba.Length >= width * height * 4;

    private static bool OptionalMapIsCompatible(ReadOnlyMemory<byte>? rgba, int width, int height) =>
        rgba is not { Length: > 0 } map || map.Length >= width * height * 4;
}
