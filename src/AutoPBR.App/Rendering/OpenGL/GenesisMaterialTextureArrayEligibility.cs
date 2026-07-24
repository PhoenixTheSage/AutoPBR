namespace AutoPBR.App.Rendering.OpenGL;

internal static class GenesisMaterialTextureArrayEligibility
{
    public static bool TryResolve(
        bool capabilityEnabled,
        bool materialDrawRecordsUploaded,
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

        // Tessellation and texture arrays compose on desktop WGL (TES samples height arrays via
        // draw-record layer). Do not gate arrays on tessellation — that forced a fragile 2D-only
        // path when tessellation was on and an incomplete array path when it was off.

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
