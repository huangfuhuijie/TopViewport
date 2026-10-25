using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TopViewport;

/// <summary>Pure fixtures: no enumeration, native display calls, files, or live layout changes.</summary>
internal static class DisplayIdentityTests
{
    public static void AddChecks(Action<string, Action> check)
    {
        check("Cached monitor identity follows a new GDI name despite old-name reuse", () =>
        {
            var previous = Info(@"\\.\DISPLAY1", "physical-A");
            var replacement = Info(@"\\.\DISPLAY1", "physical-B");
            var renamed = Info(@"\\.\DISPLAY31", "PHYSICAL-a");
            var found = DisplayManager.FindCurrentDisplay(previous, new[] { replacement, renamed });
            Require(found?.DeviceName == renamed.DeviceName, "Matched a recycled GDI slot instead of the original monitor device path.");
        });

        check("Disconnected physical monitor never falls back to an identical model", () =>
        {
            var previous = Info(@"\\.\DISPLAY1", "physical-A");
            var other = Info(@"\\.\DISPLAY1", "physical-B");
            Require(DisplayManager.FindCurrentDisplay(previous, new[] { other }) is null,
                "Disconnected physical monitor was substituted with the same model on another device path.");
            bool rejected = false;
            try { DisplayManager.ResolveSnapshotDisplayNames(Snapshot(), new[] { other }); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Recovery accepted a disconnected original physical display.");
        });

        check("Ambiguous device identity is rejected", () =>
        {
            var previous = Info(@"\\.\DISPLAY1", "physical-A");
            var duplicates = new[] { Info(@"\\.\DISPLAY31", "physical-A"), Info(@"\\.\DISPLAY32", "physical-A") };
            Require(DisplayManager.FindCurrentDisplay(previous, duplicates) is null,
                "An ambiguous monitor path selected an arbitrary display.");
        });

        check("Snapshot identity remapping preserves exact current modes and invalidates stale CCD", () =>
        {
            var original = Snapshot();
            var current = new[]
            {
                Info(@"\\.\DISPLAY31", "physical-A"),
                Info(@"\\.\DISPLAY32", "virtual-A", virtualDisplay: true, active: false, primary: false)
            };
            var resolved = DisplayManager.ResolveSnapshotDisplayNames(original, current);
            Require(resolved.Displays[0].DeviceName == @"\\.\DISPLAY31" && resolved.Displays[1].DeviceName == @"\\.\DISPLAY32",
                "Snapshot did not follow re-enumerated physical and virtual monitor names.");
            Require(resolved.Displays[0].CurrentMode == original.Displays[0].CurrentMode &&
                resolved.Displays[0].RegistryMode == original.Displays[0].RegistryMode,
                "Identity remapping changed the saved current/registry mode distinction.");
            Require(resolved.CreatedAt == original.CreatedAt && resolved.MonitorAreas[0].DeviceName == @"\\.\DISPLAY31" &&
                resolved.MonitorAreas[0].WorkArea == original.MonitorAreas[0].WorkArea,
                "Identity remapping changed saved timing, coordinates, or work areas.");
            Require(resolved.NativeTopology is null, "Stale native CCD indices survived a GDI name remap.");
            Require(original.Displays[0].DeviceName == @"\\.\DISPLAY1" && original.NativeTopology is not null,
                "Resolving a snapshot mutated the original recovery backup.");
            Require(DisplayManager.GetSnapshotDifferences(resolved, current).Count == 0,
                "Remapped fixture no longer passes exact recovery verification.");
            var wrong = current[0] with { Width = 1600, RefreshRate = 60 };
            var differences = DisplayManager.GetSnapshotDifferences(resolved, new[] { wrong, current[1] });
            Require(differences.Any(x => x.Contains("size=")) && differences.Any(x => x.Contains("refresh=")),
                "Stable identity remapping weakened size or refresh-rate postconditions.");
        });

        check("Detached virtual UI choice uses only a unique virtual driver match", () =>
        {
            var previous = Info(@"\\.\DISPLAY26", "", virtualDisplay: true, active: false, primary: false);
            var renamed = Info(@"\\.\DISPLAY32", "", virtualDisplay: true, active: false, primary: false);
            Require(DisplayManager.FindCurrentDisplay(previous, new[] { renamed })?.DeviceName == renamed.DeviceName,
                "A detached virtual monitor with no path could not rebind to its uniquely identified driver.");
            var ambiguous = renamed with { DeviceName = @"\\.\DISPLAY33" };
            Require(DisplayManager.FindCurrentDisplay(previous, new[] { renamed, ambiguous }) is null,
                "An ambiguous virtual driver match selected an arbitrary output.");
        });

        check("Legacy version-1 JSON without identity fields remains readable", () =>
        {
            var mode = new DisplayMode(1920, 1080, 165, 32);
            string json = JsonSerializer.Serialize(new
            {
                Version = 1,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Displays = new[] { new { DeviceName = @"\\.\DISPLAY1", IsActive = true, IsPrimary = true,
                    X = 0, Y = 0, CurrentMode = mode, RegistryMode = mode } },
                MonitorAreas = Array.Empty<SavedMonitorArea>(),
                NativeTopology = (DisplayConfigBackup?)null
            });
            var legacy = JsonSerializer.Deserialize<DisplaySnapshot>(json) ?? throw new InvalidOperationException("Legacy fixture was null.");
            Require(legacy.Version == 1 && legacy.Displays[0].DeviceId is null, "Optional identity fields broke the previous JSON schema.");
            var current = new[] { Info(@"\\.\DISPLAY1", "physical-A") };
            var resolved = DisplayManager.ResolveSnapshotDisplayNames(legacy, current);
            Require(DisplayManager.GetSnapshotDifferences(resolved, current).Count == 0,
                "A version-1 recovery file no longer preserves its original same-GDI behavior.");
        });
    }

    private static DisplaySnapshot Snapshot()
    {
        var mode = new DisplayMode(1920, 1080, 165, 32);
        return new DisplaySnapshot
        {
            CreatedAt = DateTimeOffset.UnixEpoch,
            Displays = new List<SavedDisplay>
            {
                new(@"\\.\DISPLAY1", true, true, 0, 0, mode, mode with { RefreshRate = 60 }, "physical-A", "GPU", "Panel"),
                new(@"\\.\DISPLAY26", false, false, 0, 0, null, null, "virtual-A", "Virtual Display Driver", "VDD")
            },
            MonitorAreas = new List<SavedMonitorArea>
            {
                new(@"\\.\DISPLAY1", new DisplayRectangle(0, 0, 1920, 1080), new DisplayRectangle(0, 0, 1920, 1040), true)
            },
            // A structural sentinel only: this array is never sent to a native API by these tests.
            NativeTopology = new DisplayConfigBackup(1, 0, new byte[72], Array.Empty<byte>())
        };
    }

    private static DisplayInfo Info(string name, string id, bool virtualDisplay = false, bool active = true, bool primary = true) =>
        new(name, virtualDisplay ? "Virtual Display Driver" : "GPU", virtualDisplay ? "VDD" : "Panel", id,
            active, primary, virtualDisplay, 0, 0, active ? 1920 : 0, active ? 1080 : 0, active ? 165 : 0, active ? 32 : 0, null);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
