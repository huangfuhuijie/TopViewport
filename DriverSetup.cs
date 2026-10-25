using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TopViewport;

public sealed record DriverStatus(bool Installed, bool IsAdministrator, bool BundledFilesValid,
    string[] DeviceInstanceIds, string? OwnDeviceInstanceId, string Message);
public sealed record DriverOperationResult(bool Success, bool RebootRequired, string Message);

/// <summary>Installation is explicit: merely loading this type never changes the computer.</summary>
public static class DriverSetup
{
    public const string HardwareId = @"Root\MttVDD";
    private const string FriendlyName = "TopViewport Virtual Display";
    private const string RegistryPath = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
    private static readonly Guid DisplayClass = new("4D36E968-E325-11CE-BFC1-08002BE10318");
    private static readonly string StateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TopViewport", "driver-ownership.json");
    private static readonly Dictionary<string, string> ExpectedHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MttVDD.inf"] = "550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D",
        ["MttVDD.dll"] = "C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05",
        ["mttvdd.cat"] = "08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6"
    };

    public static DriverStatus GetStatus()
    {
        try
        {
            var devices = FindDevices();
            var own = ReadOwnership();
            bool valid = ValidateBundle(out var reason);
            return new DriverStatus(devices.Count > 0, IsAdmin(), valid, devices.Select(d => d.InstanceId).ToArray(),
                // INF installation may overwrite the friendly name. Identity is the
                // recorded exact instance plus its independently enumerated hardware ID.
                devices.Any(d => d.InstanceId.Equals(own, StringComparison.OrdinalIgnoreCase)) ? own : null,
                devices.Count > 0 ? (devices.All(d => d.Started && d.ProblemCode == 0) ? "已检测到正在运行的 Virtual Display Driver。" :
                    "虚拟显示设备已安装，但有设备未启动：" + string.Join("、", devices.Where(d => !d.Started || d.ProblemCode != 0).Select(d => d.InstanceId + " / 问题代码 " + d.ProblemCode))) : valid ? "虚拟显示驱动尚未安装。" : reason);
        }
        catch (Exception ex) { return new(false, IsAdmin(), false, Array.Empty<string>(), null, ex.Message); }
    }

    /// <summary>Caller launches an elevated copy via --install-driver. Requires a signed, pinned bundle.</summary>
    public static DriverOperationResult InstallOrConfigure(int width, int height, int refresh)
    {
        if (!IsAdmin()) return new(false, false, "需要管理员权限：请通过工具的“安装虚拟显示驱动”按钮继续。");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return new(false, false, "随附驱动仅支持 x64 Windows。请运行 x64 版本。");
        if (width < 640 || width > 7680 || height < 480 || height > 4320 || refresh < 24 || refresh > 500)
            return new(false, false, "无效的显示模式。宽度 640–7680、高度 480–4320、刷新率 24–500 Hz。");
        if (!ValidateBundle(out var validation)) return new(false, false, validation);

        IntPtr set = new(-1);
        DeviceInfoData createdInfo = default;
        string? createdId = null;
        string? configFile = null;
        byte[]? originalXml = null;
        string? writtenXmlHash = null;
        string? originalVddPath = null;
        object? originalVddValue = null;
        RegistryValueKind originalVddKind = RegistryValueKind.String;
        bool originalVddValueExisted = false;
        string? appliedVddPath = null;
        bool registryChanged = false;
        List<DeviceRecord> devices = new();
        try
        {
            // WinVerifyTrust performs OS trust validation; never import certificates or relax signing.
            VerifySignature(Path.Combine(AppContext.BaseDirectory, "driver", "MttVDD.dll"));
            VerifySignature(Path.Combine(AppContext.BaseDirectory, "driver", "mttvdd.cat"));
            devices = FindDevices();
            string configDir = ConfigurationDirectory();
            Directory.CreateDirectory(configDir);
            configFile = Path.Combine(configDir, "vdd_settings.xml");
            bool existed = File.Exists(configFile);
            if (existed) originalXml = File.ReadAllBytes(configFile);
            string? backup = ConfigureXml(configDir, width, height, refresh);
            if (!existed || backup is not null) writtenXmlHash = HashFile(configFile);
            using (var oldKey = Registry.LocalMachine.OpenSubKey(RegistryPath, false))
            {
                originalVddPath = oldKey?.GetValue("VDDPATH") as string;
                originalVddValueExisted = oldKey?.GetValueNames().Any(n => n.Equals("VDDPATH", StringComparison.OrdinalIgnoreCase)) == true;
                if (originalVddValueExisted)
                {
                    originalVddValue = oldKey!.GetValue("VDDPATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    originalVddKind = oldKey.GetValueKind("VDDPATH");
                }
            }
            using (var key = Registry.LocalMachine.CreateSubKey(RegistryPath, true))
                key.SetValue("VDDPATH", configDir, RegistryValueKind.String);
            appliedVddPath = configDir;
            registryChanged = !string.Equals(originalVddPath, configDir, StringComparison.OrdinalIgnoreCase);

            // The signed files are copied together; no byte in INF/DLL/CAT is modified.
            string packageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TopViewport", "signed-driver-25.7.23");
            Directory.CreateDirectory(packageDir);
            foreach (string file in ExpectedHashes.Keys)
                File.Copy(Path.Combine(AppContext.BaseDirectory, "driver", file), Path.Combine(packageDir, file), true);
            string inf = Path.GetFullPath(Path.Combine(packageDir, "MttVDD.inf"));

            if (devices.Count == 0)
            {
                Guid cls = DisplayClass;
                set = SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
                CheckHandle(set, "创建驱动设备信息集");
                createdInfo = NewInfo();
                Check(SetupDiCreateDeviceInfoW(set, "Display", ref cls, FriendlyName, IntPtr.Zero, 1, ref createdInfo), "创建设备信息");
                byte[] hw = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                Check(SetupDiSetDeviceRegistryPropertyW(set, ref createdInfo, 1, hw, (uint)hw.Length), "设置硬件 ID");
                byte[] name = Encoding.Unicode.GetBytes(FriendlyName + "\0");
                Check(SetupDiSetDeviceRegistryPropertyW(set, ref createdInfo, 12, name, (uint)name.Length), "设置设备名称");
                Check(SetupDiCallClassInstaller(0x19, set, ref createdInfo), "注册虚拟显示设备");
                createdId = InstanceId(set, ref createdInfo);
                WriteOwnership(createdId); // Record only the instance created by this app.
            }

            bool reboot = false;
            // Never force-downgrade another program's already installed driver package.
            // Existing devices need only the appended XML mode and a controlled restart.
            if (devices.Count == 0)
                Check(UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, inf, 1, out reboot), "安装已签名的虚拟显示驱动");
            bool restartPending = false;
            if (devices.Count > 0)
            {
                // Existing installations keep their other modes and all non-mode XML settings.
                // Re-enumeration is needed for the newly appended custom mode to appear.
                foreach (var device in devices)
                    if (!RestartDevice(device.InstanceId)) restartPending = true;
            }
            // A successful INF installation alone does not mean the UMDF device loaded.
            // Check the actual PnP state (e.g. a missing VC++ runtime can prevent startup).
            List<DeviceRecord> configured = new();
            for (int attempt = 0; attempt < 10; attempt++)
            {
                configured = FindDevices();
                if (configured.Count > 0 && configured.All(d => d.Started && d.ProblemCode == 0)) break;
                Thread.Sleep(300);
            }
            if (!reboot && (configured.Count == 0 || configured.Any(d => d.ProblemCode != 0 || !d.Started)))
                throw new InvalidOperationException("驱动包配置后，虚拟显示设备未成功启动。Windows 设备问题代码：" +
                    string.Join("、", configured.Select(d => d.ProblemCode)) + "。请在设备管理器查看该设备；可能需要重启或安装 Microsoft Visual C++ x64 Redistributable。" +
                    (backup is null ? "" : "原配置已备份：" + backup));
            // UpdateDriver may replace SPDRP_FRIENDLYNAME with the INF's display name.
            // Reapply our label only to the exact device recorded when we created it.
            string? recordedOwn = ReadOwnership();
            if (recordedOwn is not null && configured.Any(d => d.InstanceId.Equals(recordedOwn, StringComparison.OrdinalIgnoreCase)))
                SetRecordedDeviceFriendlyName(recordedOwn);
            string message = $"已配置 {width}×{height} @ {refresh} Hz。";
            if (backup is not null) message += "原配置备份：" + backup + "。";
            if (reboot || restartPending) message += "Windows 要求重启；请保存工作后重启再使用。";
            else message += "请回到工具刷新显示器列表并选择虚拟显示器。";
            return new(true, reboot || restartPending, message);
        }
        catch (Exception ex)
        {
            string cleanup = "";
            if (createdId is not null && set != new IntPtr(-1))
            {
                if (SetupDiCallClassInstaller(5, set, ref createdInfo)) DeleteOwnership();
                else cleanup = "已保留本工具创建设备的记录，可点击“移除本工具的虚拟设备”恢复。";
            }
            try
            {
                // Roll back only bytes still matching this operation's write. Never
                // overwrite a concurrent/manual edit made after the configuration step.
                if (configFile is not null && writtenXmlHash is not null && File.Exists(configFile) && HashFile(configFile) == writtenXmlHash)
                {
                    if (originalXml is null) File.Delete(configFile);
                    else File.WriteAllBytes(configFile, originalXml);
                    foreach (var device in devices) RestartDevice(device.InstanceId);
                }
                if (registryChanged)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, true);
                    if (key?.GetValue("VDDPATH") as string == appliedVddPath)
                    {
                        if (!originalVddValueExisted) key.DeleteValue("VDDPATH", false);
                        else if (originalVddValue is not null) key.SetValue("VDDPATH", originalVddValue, originalVddKind);
                    }
                }
                cleanup += "本次配置写入已尝试回滚，原配置备份保留。";
            }
            catch (Exception rollbackError) { cleanup += "配置回滚失败：" + rollbackError.Message + "；原配置备份保留。"; }
            return new(false, false, ex.Message + " " + cleanup);
        }
        finally { if (set != new IntPtr(-1) && set != IntPtr.Zero) SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Removes only the exact instance created and recorded by this app; leaves the driver store/configuration intact.</summary>
    public static DriverOperationResult RemoveOwnDevice()
    {
        if (!IsAdmin()) return new(false, false, "移除虚拟设备需要管理员权限。");
        string? own = ReadOwnership();
        if (string.IsNullOrWhiteSpace(own)) return new(false, false, "没有本工具创建设备的记录；不会移除其他软件的设备。");
        try
        {
            var match = FindDevices().SingleOrDefault(d => d.InstanceId.Equals(own, StringComparison.OrdinalIgnoreCase));
            if (match is null) return new(false, false, "记录对应的本工具设备不存在或身份不匹配；未移除任何设备。");
            // Migrate the first release's record safely if INF installation changed
            // its label. This never adopts a device absent from our ownership record.
            SetRecordedDeviceFriendlyName(own);
            Guid cls = DisplayClass;
            IntPtr set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, 0);
            CheckHandle(set, "枚举虚拟显示设备");
            try
            {
                var info = NewInfo();
                Check(SetupDiOpenDeviceInfoW(set, own, IntPtr.Zero, 0, ref info), "打开本工具的设备");
                Check(SetupDiCallClassInstaller(5, set, ref info), "移除本工具的虚拟显示设备");
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            DeleteOwnership();
            return new(true, false, "已移除本工具创建的虚拟显示设备。驱动包和配置保留，其他设备未改动。");
        }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }

    /// <summary>Only repairs the display label of our exact recorded device. No topology, XML, restart or driver installation.</summary>
    public static DriverOperationResult RepairOwnDeviceIdentity()
    {
        if (!IsAdmin()) return new(false, false, "修复虚拟设备标记需要管理员权限。");
        string? own = ReadOwnership();
        if (string.IsNullOrWhiteSpace(own)) return new(false, false, "没有有效的本工具设备记录；未修改任何设备。");
        try
        {
            var match = FindDevices().SingleOrDefault(d => d.InstanceId.Equals(own, StringComparison.OrdinalIgnoreCase));
            if (match is null) return new(false, false, "记录的设备不存在或硬件 ID 不匹配；未修改任何设备。");
            if (match.FriendlyName != FriendlyName) SetRecordedDeviceFriendlyName(own);
            return new(true, false, "已校验并修复本工具虚拟设备标记；显示布局和驱动配置未改变。");
        }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }

    private static void SetRecordedDeviceFriendlyName(string own)
    {
        if (!own.Equals(ReadOwnership(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("设备所有权记录已改变；未修改设备。");
        Guid cls = DisplayClass;
        IntPtr set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, 0);
        CheckHandle(set, "打开本工具设备信息集");
        try
        {
            var info = NewInfo();
            Check(SetupDiOpenDeviceInfoW(set, own, IntPtr.Zero, 0, ref info), "打开本工具记录的设备");
            string[] ids = GetProperty(set, ref info, 1).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (!ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("设备硬件 ID 与本工具记录不匹配；未修改设备。");
            byte[] name = Encoding.Unicode.GetBytes(FriendlyName + "\0");
            Check(SetupDiSetDeviceRegistryPropertyW(set, ref info, 12, name, (uint)name.Length), "修复本工具设备标记");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ValidateBundle(out string message)
    {
        try
        {
            foreach (var entry in ExpectedHashes)
            {
                string file = Path.Combine(AppContext.BaseDirectory, "driver", entry.Key);
                if (!File.Exists(file)) { message = "缺少随附驱动文件：" + file; return false; }
                using var stream = File.OpenRead(file);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Value, StringComparison.OrdinalIgnoreCase))
                { message = "驱动文件校验失败：" + entry.Key + "。请重新解压完整工具包。"; return false; }
            }
            message = "随附驱动 SHA-256 校验通过。";
            return true;
        }
        catch (Exception ex) { message = ex.Message; return false; }
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string ConfigurationDirectory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, false);
        string? existing = key?.GetValue("VDDPATH") as string;
        if (!string.IsNullOrWhiteSpace(existing) && !Path.IsPathFullyQualified(existing))
            throw new InvalidDataException("现有 VDDPATH 不是绝对路径；请先用原虚拟显示驱动管理器修正，未改写原配置。");
        return string.IsNullOrWhiteSpace(existing) ? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "VirtualDisplayDriver") : Path.GetFullPath(existing);
    }

    private static string? ConfigureXml(string directory, int width, int height, int refresh)
    {
        string file = Path.Combine(directory, "vdd_settings.xml");
        XDocument doc;
        if (File.Exists(file))
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (doc.Root?.Name.LocalName != "vdd_settings") throw new InvalidDataException("现有 vdd_settings.xml 根元素无效；配置保持原样。");
        }
        else doc = new XDocument(new XElement("vdd_settings", new XElement("monitors", new XElement("count", 1)),
            new XElement("gpu", new XElement("friendlyname", "default")), new XElement("resolutions"),
            new XElement("options", new XElement("CustomEdid", "false"), new XElement("PreventSpoof", "false"),
                new XElement("EdidCeaOverride", "false"), new XElement("HardwareCursor", "true"),
                new XElement("SDR10bit", "false"), new XElement("HDRPlus", "false"), new XElement("logging", "false"), new XElement("debuglogging", "false"))));
        XElement root = doc.Root!;
        XElement? resolutions = root.Element("resolutions");
        if (resolutions is null) { resolutions = new XElement("resolutions"); root.Add(resolutions); }
        bool present = resolutions.Elements("resolution").Any(r =>
            (int?)r.Element("width") == width && (int?)r.Element("height") == height &&
            (r.Elements("refresh_rate").Any(h => double.TryParse(h.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hz) && Math.Abs(hz - refresh) < .001) ||
             root.Element("global")?.Elements("g_refresh_rate").Any(h => int.TryParse(h.Value, out int hz) && hz == refresh) == true));
        if (present) return null;
        resolutions.Add(new XElement("resolution", new XElement("width", width), new XElement("height", height), new XElement("refresh_rate", refresh)));
        string? backup = null;
        if (File.Exists(file)) { backup = file + ".TopViewport-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".bak"; File.Copy(file, backup, false); }
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { doc.Save(temp); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return backup;
    }

    private sealed record DeviceRecord(string InstanceId, string FriendlyName, uint ProblemCode, bool Started);
    private static List<DeviceRecord> FindDevices()
    {
        var results = new List<DeviceRecord>();
        Guid cls = DisplayClass;
        IntPtr set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, 0);
        CheckHandle(set, "枚举显示设备");
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = NewInfo();
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw NativeError("枚举设备");
                }
                string[] hardware = GetProperty(set, ref info, 1).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (hardware.Any(h => h.Equals(HardwareId, StringComparison.OrdinalIgnoreCase)))
                {
                    uint result = CM_Get_DevNode_Status(out uint status, out uint problem, info.DeviceInstance, 0);
                    results.Add(new DeviceRecord(InstanceId(set, ref info), GetProperty(set, ref info, 12).TrimEnd('\0'),
                        result == 0 ? problem : uint.MaxValue, result == 0 && (status & 8) != 0));
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return results;
    }

    private static bool RestartDevice(string instanceId)
    {
        Guid cls = DisplayClass;
        IntPtr set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) return false;
        try
        {
            var info = NewInfo();
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref info)) return false;
            var change = new PropertyChangeParameters { Header = new ClassInstallHeader { Size = 8, InstallFunction = 0x12 }, StateChange = 3, Scope = 1, HardwareProfile = 0 };
            return SetupDiSetClassInstallParamsW(set, ref info, ref change, (uint)Marshal.SizeOf<PropertyChangeParameters>()) &&
                SetupDiCallClassInstaller(0x12, set, ref info);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static string GetProperty(IntPtr set, ref DeviceInfoData info, uint property)
    {
        byte[] buffer = new byte[16384];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, (uint)buffer.Length, out uint required)) return "";
        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length));
    }
    private static DeviceInfoData NewInfo() => new() { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
    private static string InstanceId(IntPtr set, ref DeviceInfoData info)
    {
        var text = new StringBuilder(2048);
        Check(SetupDiGetDeviceInstanceIdW(set, ref info, text, (uint)text.Capacity, out _), "读取设备实例 ID");
        return text.ToString();
    }
    private static string? ReadOwnership()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
            var root = doc.RootElement;
            if (root.GetProperty("product").GetString() != "TopViewport" ||
                !string.Equals(root.GetProperty("hardwareId").GetString(), HardwareId, StringComparison.OrdinalIgnoreCase)) return null;
            string? id = root.GetProperty("instanceId").GetString();
            return !string.IsNullOrWhiteSpace(id) && id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ? id : null;
        }
        catch { return null; }
    }
    private static void WriteOwnership(string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(new { product = "TopViewport", instanceId = id, hardwareId = HardwareId, createdUtc = DateTime.UtcNow }));
    }
    private static void DeleteOwnership() { if (File.Exists(StateFile)) File.Delete(StateFile); }
    private static void Check(bool ok, string operation) { if (!ok) throw NativeError(operation); }
    private static void CheckHandle(IntPtr h, string operation) { if (h == new IntPtr(-1) || h == IntPtr.Zero) throw NativeError(operation); }
    private static Win32Exception NativeError(string operation) => new(Marshal.GetLastWin32Error(), operation + "失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);

    private static void VerifySignature(string path)
    {
        var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = memory,
                StateAction = 1, ProviderFlags = 0x1010 };
            Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) throw new InvalidDataException($"Windows 未信任驱动签名：{Path.GetFileName(path)} (0x{result:X8})。未修改证书、安全启动或签名策略。");
        }
        finally { Marshal.DestroyStructure<TrustFile>(memory); Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData { public uint Size; public Guid ClassGuid; public uint DeviceInstance; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct ClassInstallHeader { public uint Size; public uint InstallFunction; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyChangeParameters { public ClassInstallHeader Header; public uint StateChange; public uint Scope; public uint HardwareProfile; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustData { public uint Size; public IntPtr PolicyCallback; public IntPtr SipClient; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings; }
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid cls, IntPtr parent);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid cls, string description, IntPtr parent, uint flags, ref DeviceInfoData info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfoData info, uint property, byte[] buffer, uint size);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfoData info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfoData info, StringBuilder text, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid cls, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfoData info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfoData info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr parent, uint flags, ref DeviceInfoData info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref DeviceInfoData info, ref PropertyChangeParameters parameters, uint size);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr window, string hardwareId, string inf, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint deviceInstance, uint flags);
}
