using System;

namespace TopViewport;

internal static class DisplayPersistenceTests
{
    public static void AddChecks(Action<string, Action> check)
    {
        check("Projection selects a temporary CCD apply with no database-save or topology-lookup flags", () =>
        {
            // The documented temporary configuration is exactly APPLY | USE_SUPPLIED.
            // In particular, adding SAVE_TO_DATABASE would reintroduce the black
            // sign-in/empty physical desktop after reboot when no renderer exists.
            if (DisplayManager.TemporaryApplyFlags != 0xA0)
                throw new Exception("Projection apply is no longer a temporary supplied CCD configuration.");
        });
        check("Saved Windows layout can be inspected independently without changing it", () =>
        {
            string before = DisplayManager.GetPersistentLayoutSignature();
            if (string.IsNullOrWhiteSpace(before)) throw new Exception("Saved layout had no display paths.");
            DisplayManager.CaptureSnapshot();
            string after = DisplayManager.GetPersistentLayoutSignature();
            if (!string.Equals(before, after, StringComparison.Ordinal))
                throw new Exception("Read-only display enumeration changed the saved layout.");
        });
    }
}
