namespace AutoPBR.App.Rendering.OpenGL;

internal static class GenesisMaterialTextureArrayEligibility
{
    public static bool TryResolve(
        bool capabilityEnabled,
        bool materialDrawRecordsUploaded,
        bool tessellationDisplacementActive,
        bool hasBlockModel,
        bool hasSlots,
        out string reason)
    {
        reason = string.Empty;
        if (!capabilityEnabled)
        {
            reason = "capability gate is off";
            return false;
        }

        if (!materialDrawRecordsUploaded)
        {
            reason = "material draw records are unavailable";
            return false;
        }

        if (!hasBlockModel)
        {
            reason = "no block/entity model";
            return false;
        }

        if (!hasSlots)
        {
            reason = "no material slots";
            return false;
        }

        return true;
    }
}
