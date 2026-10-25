using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TopViewport;

public sealed record DisplayMode(int Width, int Height, int RefreshRate, int BitsPerPixel, int Orientation = 0);
public sealed record DisplayRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record DisplayInfo(
    string DeviceName, string AdapterName, string MonitorName, string DeviceId,
    bool IsActive, bool IsPrimary, bool IsLikelyVirtual,
    int X, int Y, int Width, int Height, int RefreshRate, int BitsPerPixel,
    DisplayRectangle? WorkArea);

public sealed record SavedDisplay(
    string DeviceName, bool IsActive, bool IsPrimary,
    int X, int Y, DisplayMode? CurrentMode, DisplayMode? RegistryMode,
    string? DeviceId = null, string? AdapterName = null, string? MonitorName = null);

public sealed record SavedMonitorArea(string DeviceName, DisplayRectangle Bounds, DisplayRectangle WorkArea, bool IsPrimary);

// The binary payloads are native DISPLAYCONFIG_PATH_INFO and DISPLAYCONFIG_MODE_INFO arrays.
// They are passed back only to the Windows display API, never executed as code.
public sealed record DisplayConfigBackup(uint PathCount, uint ModeCount, byte[] Paths, byte[] Modes);

public sealed class DisplaySnapshot
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<SavedDisplay> Displays { get; set; } = new();
    public List<SavedMonitorArea> MonitorAreas { get; set; } = new();
    public DisplayConfigBackup? NativeTopology { get; set; }
}

public sealed record DisplayApplyResult(
    string VirtualDeviceName, string PhysicalDeviceName,
    DisplayMode VirtualMode, DisplayRectangle VirtualBounds, DisplayRectangle PhysicalBounds,
    int OtherDisplaysMoved);

public sealed record DisplayRestoreResult(bool UsedNativeTopology, IReadOnlyList<string> Warnings);

/// <summary>
/// Display enumeration and reversible topology changes. Enumeration and snapshot calls are read-only.
/// ApplyVirtualPrimary and RestoreSnapshot must only be called as a result of an explicit user action.
/// The caller must persist a snapshot BEFORE applying and provide an independent confirmation timer.
/// </summary>
public static class DisplayManager
{
    private const int EnumCurrentSettings = -1;
    private const int EnumRegistrySettings = -2;
    private const uint AttachedToDesktop = 0x1;
    private const uint PrimaryDevice = 0x4;
    private const uint MirroringDriver = 0x8;
    private const uint RemoteDevice = 0x04000000;
    private const uint DmPosition = 0x20;
    private const uint DmDisplayOrientation = 0x80;
    private const uint DmBitsPerPel = 0x40000;
    private const uint DmPelsWidth = 0x80000;
    private const uint DmPelsHeight = 0x100000;
    private const uint DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1;
    private const uint CdsTest = 0x2;
    private const uint CdsSetPrimary = 0x10;
    private const uint CdsNoReset = 0x10000000;
    private const uint QdcOnlyActivePaths = 0x2;
    private const uint SdcUseSuppliedDisplayConfig = 0x20;
    private const uint SdcValidate = 0x40;
    private const uint SdcApply = 0x80;
    private const uint SdcSaveToDatabase = 0x200;
    private const uint SpiSetWorkArea = 0x2f;
    private const uint SpifSendChange = 0x2;
    private const int NativePathBytes = 72;
    private const int NativeModeBytes = 64;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<DisplayInfo> ListDisplays(bool includeInactive = true)
    {
        var areas = EnumerateMonitorAreas().ToDictionary(x => x.DeviceName, StringComparer.OrdinalIgnoreCase);
        var targetDetails = GetActiveTargetDetails();
        var result = new List<DisplayInfo>();
        foreach (var device in EnumerateDisplayDevices())
        {
            bool active = (device.StateFlags & AttachedToDesktop) != 0;
            if (!includeInactive && !active) continue;
            var mode = ReadMode(device.DeviceName, active ? EnumCurrentSettings : EnumRegistrySettings);
            var monitor = NewDisplayDevice();
            string monitorName = "";
            string monitorId = "";
            if (EnumDisplayDevices(device.DeviceName, 0, ref monitor, 1))
            {
                monitorName = monitor.DeviceString ?? "";
                monitorId = monitor.DeviceID ?? "";
            }
            bool indirect = false;
            if (targetDetails.TryGetValue(device.DeviceName, out var details))
            {
                if (!string.IsNullOrWhiteSpace(details.FriendlyName)) monitorName = details.FriendlyName;
                if (!string.IsNullOrWhiteSpace(details.DevicePath)) monitorId = details.DevicePath;
                indirect = details.OutputTechnology is 16 or 17;
            }
            string evidence = $"{device.DeviceString} {device.DeviceID} {monitorName} {monitorId}";
            bool virtualHint = indirect || new[] { "virtual", "indirect", "spacedesk", "parsec", "duet", " vdd", "iddsample", "iddcx", "dummy" }
                .Any(x => evidence.Contains(x, StringComparison.OrdinalIgnoreCase));
            areas.TryGetValue(device.DeviceName, out var area);
            result.Add(new DisplayInfo(
                device.DeviceName, device.DeviceString ?? "", monitorName, monitorId,
                active, (device.StateFlags & PrimaryDevice) != 0, virtualHint,
                mode?.dmPositionX ?? 0, mode?.dmPositionY ?? 0,
                (int)(mode?.dmPelsWidth ?? 0), (int)(mode?.dmPelsHeight ?? 0),
                (int)(mode?.dmDisplayFrequency ?? 0), (int)(mode?.dmBitsPerPel ?? 0), area?.WorkArea));
        }
        return result;
    }

    public static IReadOnlyList<DisplayMode> ListModes(string deviceName)
    {
        EnsureDeviceName(deviceName);
        var result = new HashSet<DisplayMode>();
        for (int index = 0; index < 10000; index++)
        {
            var mode = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, index, ref mode, 0)) break;
            if (mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0) result.Add(ToMode(mode));
        }
        return result.OrderBy(x => x.Width).ThenBy(x => x.Height).ThenBy(x => x.RefreshRate).ThenBy(x => x.BitsPerPixel).ToArray();
    }

    /// <summary>
    /// Resolves a cached UI choice after monitor re-enumeration. A known monitor device path must
    /// still match exactly; a disconnected physical monitor is never replaced by another monitor.
    /// </summary>
    public static DisplayInfo? FindCurrentDisplay(DisplayInfo previous, IReadOnlyList<DisplayInfo>? current = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return FindCurrentDisplayCore(previous.DeviceName, previous.DeviceId, previous.AdapterName,
            previous.MonitorName, previous.IsLikelyVirtual, current ?? ListDisplays());
    }

    /// <summary>
    /// Makes a recovery copy with current GDI names, without changing the saved modes or coordinates.
    /// Optional identity fields preserve compatibility with existing version-1 recovery JSON.
    /// </summary>
    public static DisplaySnapshot ResolveSnapshotDisplayNames(DisplaySnapshot snapshot, IReadOnlyList<DisplayInfo>? current = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        current ??= ListDisplays();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<SavedDisplay>();
        bool remapped = false;
        foreach (var saved in snapshot.Displays)
        {
            var found = FindCurrentDisplayCore(saved.DeviceName, saved.DeviceId, saved.AdapterName, saved.MonitorName, null, current);
            if (found is null)
            {
                if (saved.IsActive)
                    throw new InvalidOperationException($"The original display {saved.DeviceName} ({saved.MonitorName ?? saved.DeviceId ?? "unknown monitor"}) is disconnected or cannot be identified uniquely; reconnect it before restoring.");
                resolved.Add(saved);
                continue;
            }
            names[saved.DeviceName] = found.DeviceName;
            remapped |= !string.Equals(saved.DeviceName, found.DeviceName, StringComparison.OrdinalIgnoreCase);
            resolved.Add(saved with { DeviceName = found.DeviceName });
        }
        // An old inactive adapter slot can acquire the name now used by an identified active monitor.
        // That obsolete slot must not contradict the active monitor's restored state.
        var activeNames = new HashSet<string>(resolved.Where(x => x.IsActive).Select(x => x.DeviceName), StringComparer.OrdinalIgnoreCase);
        resolved.RemoveAll(x => !x.IsActive && activeNames.Contains(x.DeviceName));
        return new DisplaySnapshot
        {
            Version = snapshot.Version,
            CreatedAt = snapshot.CreatedAt,
            Displays = resolved,
            MonitorAreas = snapshot.MonitorAreas.Select(x => names.TryGetValue(x.DeviceName, out var name)
                ? x with { DeviceName = name } : x).ToList(),
            NativeTopology = remapped ? null : snapshot.NativeTopology
        };
    }

    private static DisplayInfo? FindCurrentDisplayCore(string deviceName, string? deviceId, string? adapterName,
        string? monitorName, bool? isVirtual, IReadOnlyList<DisplayInfo> current)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var exact = current.Where(x => string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            return exact.Length == 1 ? exact[0] : null;
        }
        bool MatchesKnownFields(DisplayInfo candidate) =>
            (string.IsNullOrWhiteSpace(adapterName) || string.Equals(candidate.AdapterName, adapterName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(monitorName) || string.Equals(candidate.MonitorName, monitorName, StringComparison.OrdinalIgnoreCase)) &&
            (!isVirtual.HasValue || candidate.IsLikelyVirtual == isVirtual.Value);
        var sameName = current.Where(x => string.Equals(x.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) && MatchesKnownFields(x)).ToArray();
        if (sameName.Length == 1) return sameName[0];
        // Legacy snapshots have no identity at all: never guess a replacement physical display.
        // Virtual UI choices may lack a device path while detached; only a unique matching driver is safe.
        if (isVirtual == true && !string.IsNullOrWhiteSpace(adapterName))
        {
            var virtualMatches = current.Where(x => x.IsLikelyVirtual && MatchesKnownFields(x)).ToArray();
            if (virtualMatches.Length == 1) return virtualMatches[0];
        }
        return null;
    }

    public static DisplaySnapshot CaptureSnapshot()
    {
        var snapshot = new DisplaySnapshot();
        var identities = ListDisplays().ToDictionary(x => x.DeviceName, StringComparer.OrdinalIgnoreCase);
        foreach (var device in EnumerateDisplayDevices())
        {
            bool active = (device.StateFlags & AttachedToDesktop) != 0;
            var current = active ? ReadMode(device.DeviceName, EnumCurrentSettings) : null;
            var registry = ReadMode(device.DeviceName, EnumRegistrySettings);
            if (active && current is null)
                throw new InvalidOperationException($"Cannot read the current mode for {device.DeviceName}; no display settings have been changed.");
            identities.TryGetValue(device.DeviceName, out var identity);
            snapshot.Displays.Add(new SavedDisplay(device.DeviceName, active,
                (device.StateFlags & PrimaryDevice) != 0, current?.dmPositionX ?? registry?.dmPositionX ?? 0,
                current?.dmPositionY ?? registry?.dmPositionY ?? 0,
                current.HasValue ? ToMode(current.Value) : null, registry.HasValue ? ToMode(registry.Value) : null,
                identity?.DeviceId, identity?.AdapterName, identity?.MonitorName));
        }
        if (!snapshot.Displays.Any(x => x.IsActive && x.IsPrimary))
            throw new InvalidOperationException("Windows did not report an active primary display; no display settings have been changed.");
        snapshot.MonitorAreas = EnumerateMonitorAreas();
        snapshot.NativeTopology = CaptureNativeTopology();
        return snapshot;
    }

    public static void SaveSnapshot(DisplaySnapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // Replace only after the complete JSON is durable, so a failed write preserves the previous recovery file.
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, fullPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static DisplaySnapshot LoadSnapshot(string path)
    {
        using var stream = File.OpenRead(path);
        var snapshot = JsonSerializer.Deserialize<DisplaySnapshot>(stream, JsonOptions)
            ?? throw new InvalidDataException("The display recovery file is empty.");
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Extends the desktop: virtual display becomes primary at (0,0), physical display starts immediately
    /// below it. Its existing resolution, refresh rate and orientation are retained. Additional active
    /// displays are laid out to the right to avoid overlap. This requires an already installed virtual driver.
    /// </summary>
    public static DisplayApplyResult ApplyVirtualPrimary(
        string virtualDeviceName, string physicalDeviceName, int virtualWidth, int virtualHeight,
        int virtualRefreshRate = 0)
    {
        EnsureDeviceName(virtualDeviceName);
        EnsureDeviceName(physicalDeviceName);
        if (string.Equals(virtualDeviceName, physicalDeviceName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The virtual and physical display must be different.");
        if (virtualWidth < 320 || virtualHeight < 200 || virtualWidth > 32768 || virtualHeight > 32768)
            throw new ArgumentOutOfRangeException(nameof(virtualWidth), "The requested virtual dimensions are outside the supported range.");
        if (virtualRefreshRate < 0 || virtualRefreshRate > 1000)
            throw new ArgumentOutOfRangeException(nameof(virtualRefreshRate));

        var displays = ListDisplays();
        var virtualDisplay = displays.FirstOrDefault(x => string.Equals(x.DeviceName, virtualDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected virtual display is no longer connected.");
        var physicalDisplay = displays.FirstOrDefault(x => string.Equals(x.DeviceName, physicalDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected physical display is no longer connected.");
        if (!physicalDisplay.IsActive)
            throw new InvalidOperationException("The physical display must already be active before applying this layout.");
        var physicalMode = ReadMode(physicalDeviceName, EnumCurrentSettings)
            ?? throw new InvalidOperationException("Cannot read the physical display mode.");
        var virtualMode = FindRequestedMode(virtualDeviceName, virtualWidth, virtualHeight, virtualRefreshRate,
            physicalMode.dmDisplayFrequency);
        virtualMode.dmPositionX = 0;
        virtualMode.dmPositionY = 0;
        virtualMode.dmFields |= DmPosition;
        physicalMode.dmPositionX = 0;
        physicalMode.dmPositionY = virtualHeight;
        physicalMode.dmFields |= DmPosition;
        var staged = new List<(string Name, DEVMODE Mode, bool Primary)>
        {
            (virtualDeviceName, virtualMode, true),
            (physicalDeviceName, physicalMode, false)
        };
        int rightEdge = Math.Max(virtualWidth, (int)physicalMode.dmPelsWidth);
        foreach (var other in displays.Where(x => x.IsActive &&
                     !string.Equals(x.DeviceName, virtualDeviceName, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(x.DeviceName, physicalDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            var mode = ReadMode(other.DeviceName, EnumCurrentSettings)
                ?? throw new InvalidOperationException($"Cannot read the mode of {other.DeviceName}.");
            mode.dmPositionX = rightEdge;
            mode.dmPositionY = 0;
            mode.dmFields |= DmPosition;
            staged.Add((other.DeviceName, mode, false));
            rightEdge = checked(rightEdge + (int)mode.dmPelsWidth);
        }
        foreach (var item in staged)
        {
            // Some indirect-display drivers reject CDS_TEST while detached even for a mode they
            // enumerate. Its acceptance is therefore checked by the staged attach plus read-back
            // below, inside the snapshot/rollback transaction, rather than by this advisory test.
            if (!virtualDisplay.IsActive && string.Equals(item.Name, virtualDeviceName, StringComparison.OrdinalIgnoreCase))
                continue;
            var testMode = item.Mode;
            // Position belongs to the complete staged topology. Testing each new position against
            // the old topology separately can produce a false overlap/disconnection failure.
            testMode.dmFields &= ~DmPosition;
            CheckChangeResult(ChangeDisplaySettingsEx(item.Name, ref testMode, IntPtr.Zero, CdsTest, IntPtr.Zero),
                $"Testing {item.Name} ({testMode.dmPelsWidth}×{testMode.dmPelsHeight}@{testMode.dmDisplayFrequency})");
        }

        // Also roll back partial staging errors; the persisted caller snapshot protects against process failure.
        var recovery = CaptureSnapshot();
        try
        {
            foreach (var item in staged)
            {
                var mode = item.Mode;
                CheckChangeResult(ChangeDisplaySettingsEx(item.Name, ref mode, IntPtr.Zero,
                    CdsUpdateRegistry | CdsNoReset | (item.Primary ? CdsSetPrimary : 0), IntPtr.Zero), $"Staging {item.Name}");
            }
            CheckChangeResult(ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero), "Applying the display layout");

            // Drivers sometimes report success while choosing a different mode. Refuse that silently degraded state.
            var actual = ListDisplays(false);
            var v = actual.FirstOrDefault(x => string.Equals(x.DeviceName, virtualDeviceName, StringComparison.OrdinalIgnoreCase));
            var p = actual.FirstOrDefault(x => string.Equals(x.DeviceName, physicalDeviceName, StringComparison.OrdinalIgnoreCase));
            if (v is null || p is null || !v.IsPrimary || v.X != 0 || v.Y != 0 ||
                v.Width != virtualWidth || v.Height != virtualHeight ||
                (virtualRefreshRate > 0 && v.RefreshRate != virtualRefreshRate) ||
                p.X != 0 || p.Y != virtualHeight || p.Width != physicalDisplay.Width || p.Height != physicalDisplay.Height ||
                p.RefreshRate != physicalDisplay.RefreshRate)
                throw new InvalidOperationException("The display driver did not accept the requested exact layout.");
            return new DisplayApplyResult(virtualDeviceName, physicalDeviceName,
                new DisplayMode(v.Width, v.Height, v.RefreshRate, v.BitsPerPixel),
                new DisplayRectangle(v.X, v.Y, v.X + v.Width, v.Y + v.Height),
                new DisplayRectangle(p.X, p.Y, p.X + p.Width, p.Y + p.Height), staged.Count - 2);
        }
        catch (Exception applyError)
        {
            try { RestoreSnapshot(recovery); }
            catch (Exception restoreError)
            {
                throw new AggregateException("The layout failed and automatic recovery also failed. Use the saved recovery file.", applyError, restoreError);
            }
            throw new InvalidOperationException("The requested layout failed; the previous display layout has been restored. " + applyError.Message, applyError);
        }
    }

    public static DisplayRestoreResult RestoreSnapshot(DisplaySnapshot snapshot, bool restoreWorkAreas = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        var warnings = new List<string>();
        bool hadNativeTopology = snapshot.NativeTopology is not null;
        snapshot = ResolveSnapshotDisplayNames(snapshot);
        if (hadNativeTopology && snapshot.NativeTopology is null)
            warnings.Add("Monitor device identities were matched to new GDI display names; legacy recovery was used instead of stale native topology indices.");
        bool nativeRestored = false;
        var currentDeviceNames = new HashSet<string>(EnumerateDisplayDevices().Select(x => x.DeviceName), StringComparer.OrdinalIgnoreCase);
        var savedDeviceNames = new HashSet<string>(snapshot.Displays.Select(x => x.DeviceName), StringComparer.OrdinalIgnoreCase);
        // Installing/removing a display adapter can invalidate CCD source indices even when SetDisplayConfig
        // reports success. Legacy recovery identifies each device explicitly by its GDI display name.
        bool deviceInventoryMatches = currentDeviceNames.SetEquals(savedDeviceNames);
        if (snapshot.NativeTopology is not null && deviceInventoryMatches)
        {
            int nativeResult = RestoreNativeTopology(snapshot.NativeTopology);
            nativeRestored = nativeResult == 0;
            if (!nativeRestored)
                warnings.Add($"Exact display topology recovery returned Win32 error {nativeResult}; legacy display recovery was used.");
            else
            {
                var differences = GetSnapshotDifferences(snapshot, ListDisplays());
                if (differences.Count > 0)
                {
                    nativeRestored = false;
                    warnings.Add("Windows reported native topology recovery success but returned different display settings; legacy recovery was used: " + string.Join("; ", differences));
                }
            }
        }
        else if (snapshot.NativeTopology is not null)
            warnings.Add("The connected display-device inventory has changed; legacy recovery was used to avoid stale native topology indices.");
        if (!nativeRestored)
        {
            RestoreLegacyTopology(snapshot);
            VerifyRestoredTopology(snapshot);
        }
        if (restoreWorkAreas) RestoreWorkAreas(snapshot, warnings);
        return new DisplayRestoreResult(nativeRestored, warnings);
    }

    /// <summary>Explicit recovery path that bypasses saved CCD adapter/source indices entirely.</summary>
    public static DisplayRestoreResult RestoreSnapshotLegacy(DisplaySnapshot snapshot, bool restoreWorkAreas = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        snapshot = ResolveSnapshotDisplayNames(snapshot);
        RestoreLegacyTopology(snapshot);
        VerifyRestoredTopology(snapshot);
        var warnings = new List<string>();
        if (restoreWorkAreas) RestoreWorkAreas(snapshot, warnings);
        return new DisplayRestoreResult(false, warnings);
    }

    /// <summary>Pure comparison helper for recovery verification and diagnostics; changes no settings.</summary>
    public static IReadOnlyList<string> GetSnapshotDifferences(DisplaySnapshot snapshot, IReadOnlyList<DisplayInfo> actual)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(actual);
        ValidateSnapshot(snapshot);
        var differences = new List<string>();
        var byName = actual.ToDictionary(x => x.DeviceName, StringComparer.OrdinalIgnoreCase);
        foreach (var saved in snapshot.Displays)
        {
            if (!byName.TryGetValue(saved.DeviceName, out var found))
            {
                if (saved.IsActive) differences.Add(saved.DeviceName + " is missing");
                continue;
            }
            if (saved.IsActive != found.IsActive)
            {
                differences.Add($"{saved.DeviceName} active={found.IsActive}, expected {saved.IsActive}");
                continue;
            }
            if (!saved.IsActive) continue;
            var mode = saved.CurrentMode!;
            if (found.IsPrimary != saved.IsPrimary) differences.Add(saved.DeviceName + " has the wrong primary status");
            if (found.X != saved.X || found.Y != saved.Y)
                differences.Add($"{saved.DeviceName} position={found.X},{found.Y}, expected {saved.X},{saved.Y}");
            if (found.Width != mode.Width || found.Height != mode.Height)
                differences.Add($"{saved.DeviceName} size={found.Width}x{found.Height}, expected {mode.Width}x{mode.Height}");
            if (found.RefreshRate != mode.RefreshRate)
                differences.Add($"{saved.DeviceName} refresh={found.RefreshRate}, expected {mode.RefreshRate}");
            if (found.BitsPerPixel != mode.BitsPerPixel)
                differences.Add($"{saved.DeviceName} color depth={found.BitsPerPixel}, expected {mode.BitsPerPixel}");
        }
        var savedNames = new HashSet<string>(snapshot.Displays.Select(x => x.DeviceName), StringComparer.OrdinalIgnoreCase);
        foreach (var extra in actual.Where(x => x.IsActive && !savedNames.Contains(x.DeviceName)))
            differences.Add(extra.DeviceName + " is an unexpected active display and must be detached");
        return differences;
    }

    private static void VerifyRestoredTopology(DisplaySnapshot snapshot)
    {
        var differences = GetSnapshotDifferences(snapshot, ListDisplays());
        if (differences.Count > 0)
            throw new InvalidOperationException("Windows did not restore the requested display settings: " + string.Join("; ", differences));
    }

    private static void RestoreWorkAreas(DisplaySnapshot snapshot, List<string> warnings)
    {
        foreach (var area in snapshot.MonitorAreas)
        {
            var rect = new RECT { Left = area.WorkArea.Left, Top = area.WorkArea.Top,
                Right = area.WorkArea.Right, Bottom = area.WorkArea.Bottom };
            if (!SystemParametersInfo(SpiSetWorkArea, 0, ref rect, SpifSendChange))
                warnings.Add($"Windows could not restore the work area for {area.DeviceName}: {Marshal.GetLastWin32Error()}.");
        }
    }

    private static void RestoreLegacyTopology(DisplaySnapshot snapshot)
    {
        var devices = EnumerateDisplayDevices();
        var currentNames = new HashSet<string>(devices.Select(x => x.DeviceName), StringComparer.OrdinalIgnoreCase);
        foreach (var saved in snapshot.Displays.Where(x => x.IsActive))
            if (!currentNames.Contains(saved.DeviceName))
                throw new InvalidOperationException($"The original display {saved.DeviceName} is disconnected; full recovery requires reconnecting it.");
        // The primary display is staged first so Windows has an origin while detaching the virtual display.
        foreach (var saved in snapshot.Displays.Where(x => x.IsActive).OrderByDescending(x => x.IsPrimary))
        {
            var mode = BuildMode(saved.CurrentMode!, saved.X, saved.Y);
            CheckChangeResult(ChangeDisplaySettingsEx(saved.DeviceName, ref mode, IntPtr.Zero,
                CdsUpdateRegistry | CdsNoReset | (saved.IsPrimary ? CdsSetPrimary : 0), IntPtr.Zero), $"Restoring {saved.DeviceName}");
        }
        foreach (var device in devices)
        {
            bool shouldBeActive = snapshot.Displays.Any(x => x.IsActive && string.Equals(x.DeviceName, device.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (shouldBeActive || (device.StateFlags & AttachedToDesktop) == 0) continue;
            var mode = NewDevMode();
            mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;
            CheckChangeResult(ChangeDisplaySettingsEx(device.DeviceName, ref mode, IntPtr.Zero,
                CdsUpdateRegistry | CdsNoReset, IntPtr.Zero), $"Detaching {device.DeviceName}");
        }
        CheckChangeResult(ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero), "Restoring the display layout");
    }

    private static void ValidateSnapshot(DisplaySnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Displays is null || snapshot.MonitorAreas is null ||
            snapshot.Displays.Count > 128 || snapshot.MonitorAreas.Count > 128 ||
            snapshot.Displays.Count(x => x.IsActive && x.IsPrimary) != 1)
            throw new InvalidDataException("The display recovery file is invalid or has an unsupported version.");
        foreach (var saved in snapshot.Displays)
        {
            EnsureDeviceName(saved.DeviceName);
            if (saved.IsActive && (saved.CurrentMode is null || saved.CurrentMode.Width < 1 || saved.CurrentMode.Height < 1))
                throw new InvalidDataException("The recovery file has an invalid active display mode.");
        }
        if (snapshot.NativeTopology is { } native &&
            (native.PathCount is 0 or > 128 || native.ModeCount > 512 || native.Paths is null || native.Modes is null ||
             native.Paths.LongLength != (long)native.PathCount * NativePathBytes ||
             native.Modes.LongLength != (long)native.ModeCount * NativeModeBytes))
            throw new InvalidDataException("The recovery file contains malformed Windows topology data.");
    }

    private static void EnsureDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) || name.Length > 32)
            throw new ArgumentException("A Windows display device name such as \\\\.\\DISPLAY1 is required.", nameof(name));
    }

    private static DEVMODE FindRequestedMode(string deviceName, int width, int height, int frequency, uint preferredFrequency)
    {
        DEVMODE? best = null;
        long bestScore = long.MaxValue;
        for (int index = 0; index < 10000; index++)
        {
            var candidate = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, index, ref candidate, 0)) break;
            if (candidate.dmPelsWidth != width || candidate.dmPelsHeight != height || candidate.dmBitsPerPel < 24) continue;
            if (frequency != 0 && candidate.dmDisplayFrequency != frequency) continue;
            long score = Math.Abs((long)candidate.dmDisplayFrequency - (frequency != 0 ? frequency : preferredFrequency)) * 100
                + Math.Abs((long)candidate.dmBitsPerPel - 32);
            if (score < bestScore) { best = candidate; bestScore = score; }
        }
        if (best is null)
            throw new InvalidOperationException($"The virtual display driver does not advertise {width}×{height}" +
                (frequency > 0 ? $" at {frequency} Hz" : "") + ". Add this mode in the virtual driver configuration first.");
        var result = best.Value;
        result.dmDriverExtra = 0;
        result.dmFields = DmPosition | DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency | DmDisplayOrientation;
        return result;
    }

    private static DEVMODE BuildMode(DisplayMode mode, int x, int y)
    {
        var result = NewDevMode();
        result.dmFields = DmPosition | DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency | DmDisplayOrientation;
        result.dmPositionX = x; result.dmPositionY = y;
        result.dmPelsWidth = (uint)mode.Width; result.dmPelsHeight = (uint)mode.Height;
        result.dmBitsPerPel = (uint)mode.BitsPerPixel; result.dmDisplayFrequency = (uint)mode.RefreshRate;
        result.dmDisplayOrientation = (uint)mode.Orientation;
        return result;
    }

    private static DisplayMode ToMode(DEVMODE mode) => new((int)mode.dmPelsWidth, (int)mode.dmPelsHeight,
        (int)mode.dmDisplayFrequency, (int)mode.dmBitsPerPel, (int)mode.dmDisplayOrientation);

    private static DEVMODE? ReadMode(string deviceName, int which)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettingsEx(deviceName, which, ref mode, 0)) return null;
        // We intentionally retain no private driver bytes outside this fixed DEVMODE buffer.
        mode.dmDriverExtra = 0;
        return mode;
    }

    private static List<DISPLAY_DEVICE> EnumerateDisplayDevices()
    {
        var result = new List<DISPLAY_DEVICE>();
        for (uint index = 0; index < 128; index++)
        {
            var device = NewDisplayDevice();
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & (MirroringDriver | RemoteDevice)) != 0 || string.IsNullOrEmpty(device.DeviceName)) continue;
            result.Add(device);
        }
        return result;
    }

    private static List<SavedMonitorArea> EnumerateMonitorAreas()
    {
        var result = new List<SavedMonitorArea>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfo(monitor, ref info))
                result.Add(new SavedMonitorArea(info.szDevice, ToRectangle(info.rcMonitor), ToRectangle(info.rcWork), (info.dwFlags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate the current display work areas.");
        GC.KeepAlive(callback);
        return result;
    }

    private static DisplayRectangle ToRectangle(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static DisplayConfigBackup? CaptureNativeTopology()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int error = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount);
            if (error != 0) return null;
            if (pathCount is 0 or > 128 || modeCount > 512) return null;
            IntPtr paths = Marshal.AllocHGlobal(checked((int)pathCount * NativePathBytes));
            IntPtr modes = Marshal.AllocHGlobal(Math.Max(1, checked((int)modeCount * NativeModeBytes)));
            try
            {
                error = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (error == ErrorInsufficientBuffer) continue;
                if (error != 0) return null;
                var pathBytes = new byte[checked((int)pathCount * NativePathBytes)];
                var modeBytes = new byte[checked((int)modeCount * NativeModeBytes)];
                Marshal.Copy(paths, pathBytes, 0, pathBytes.Length);
                Marshal.Copy(modes, modeBytes, 0, modeBytes.Length);
                return new DisplayConfigBackup(pathCount, modeCount, pathBytes, modeBytes);
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        }
        return null;
    }

    private static int RestoreNativeTopology(DisplayConfigBackup backup)
    {
        IntPtr paths = Marshal.AllocHGlobal(backup.Paths.Length);
        IntPtr modes = Marshal.AllocHGlobal(Math.Max(1, backup.Modes.Length));
        try
        {
            Marshal.Copy(backup.Paths, 0, paths, backup.Paths.Length);
            Marshal.Copy(backup.Modes, 0, modes, backup.Modes.Length);
            int error = SetDisplayConfig(backup.PathCount, paths, backup.ModeCount, modes, SdcUseSuppliedDisplayConfig | SdcValidate);
            if (error != 0) return error;
            return SetDisplayConfig(backup.PathCount, paths, backup.ModeCount, modes,
                SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase);
        }
        finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
    }

    private sealed record TargetDetails(string FriendlyName, string DevicePath, uint OutputTechnology);

    private static Dictionary<string, TargetDetails> GetActiveTargetDetails()
    {
        var result = new Dictionary<string, TargetDetails>(StringComparer.OrdinalIgnoreCase);
        var backup = CaptureNativeTopology();
        if (backup is null) return result;
        var handle = GCHandle.Alloc(backup.Paths, GCHandleType.Pinned);
        try
        {
            IntPtr start = handle.AddrOfPinnedObject();
            for (int i = 0; i < backup.PathCount; i++)
            {
                var path = Marshal.PtrToStructure<DISPLAYCONFIG_PATH_INFO>(IntPtr.Add(start, i * NativePathBytes));
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 1, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id }, viewGdiDeviceName = ""
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 2, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId, id = path.targetInfo.id }, monitorFriendlyDeviceName = "", monitorDevicePath = ""
                };
                if (DisplayConfigGetDeviceInfo(ref source) == 0 && DisplayConfigGetDeviceInfo(ref target) == 0)
                    result[source.viewGdiDeviceName] = new TargetDetails(target.monitorFriendlyDeviceName, target.monitorDevicePath, target.outputTechnology);
            }
        }
        finally { handle.Free(); }
        return result;
    }

    private static void CheckChangeResult(int result, string action)
    {
        if (result == 0) return;
        string reason = result switch
        {
            1 => "The driver requires a restart; this mode cannot be applied safely now.",
            -1 => "The display driver rejected the change.",
            -2 => "The driver does not support the requested display mode.",
            -3 => "Windows could not update the display settings database.",
            -4 => "Windows rejected the display change flags.",
            -5 => "Windows rejected the display parameters.",
            -6 => "Windows cannot apply this configuration in the current display session.",
            _ => "Unknown display driver error."
        };
        throw new InvalidOperationException($"{action}: {reason} (DISP_CHANGE {result})");
    }

    private static DISPLAY_DEVICE NewDisplayDevice() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "" };
    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    // Display branch of DEVMODEW's anonymous union; correct size on both x86 and x64 is 220 bytes.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling, refreshRateNumerator, refreshRateDenominator, scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_INFO
    { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    { public uint type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE displayDevice, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, ref DEVMODE devMode, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX monitorInfo);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref RECT rect, uint flags);
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, IntPtr paths, ref uint modeCount, IntPtr modes, IntPtr currentTopologyId);
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, IntPtr paths, uint modeCount, IntPtr modes, uint flags);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}
