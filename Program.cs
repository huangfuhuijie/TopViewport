using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace TopViewport;

internal static class Program
{
    public static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TopViewport");
    public static readonly string SessionPath = Path.Combine(StateDirectory, "session.json");
    public static readonly string SnapshotPath = Path.Combine(StateDirectory, "display-backup.json");
    public static readonly string LogPath = Path.Combine(StateDirectory, "TopViewport.log");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--watchdog") return Recovery.RunWatchdog(args[1], args.Length > 2 ? args[2] : null);
            if (args.Length > 0 && args[0] == "--recover")
            {
                Native.ClipCursor(IntPtr.Zero);
                var saved=Recovery.Read(SessionPath);
                return saved==null || saved.Completed || Recovery.Restore(saved,SessionPath) ? 0 : 1;
            }
            if(args.Length>1 && args[0]=="--restore-snapshot")
            { Native.ClipCursor(IntPtr.Zero); DisplayManager.RestoreSnapshot(DisplayManager.LoadSnapshot(args[1])); return 0; }
            if (args.Length > 0 && args[0] == "--diagnose")
            {
                var snapshot = DisplayManager.CaptureSnapshot();
                string output = JsonSerializer.Serialize(new { Version = "0.1.3", Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.VersionString, Driver = DriverSetup.GetStatus(), Displays = DisplayManager.ListDisplays(), Snapshot = snapshot }, JsonOptions);
                File.WriteAllText(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "diagnostics.json"), output, Encoding.UTF8);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--install-driver")
            {
                var result = DriverSetup.InstallOrConfigure(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]));
                File.WriteAllText(args[4], JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
                return result.Success ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--remove-driver")
            {
                var result = DriverSetup.RemoveOwnDevice();
                File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
                return result.Success ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--selftest") return SelfTests.Run(args.Length > 1 ? args[1] : AppContext.BaseDirectory);
            if(args.Length>0 && (args[0]=="--live-test" || args[0]=="--power-test" || args[0]=="--tray-test"))
            {
                string folder=args.Length>1 ? args[1] : AppContext.BaseDirectory;
                bool simulatePower=args[0]=="--power-test";
                using var form=new MainForm();
                bool trayTest=args[0]=="--tray-test";
                form.Shown+=(_,_)=>form.BeginInvoke(new Action(()=> { if(trayTest) form.RunTrayTest(folder); else form.RunTimedTest(folder,simulatePower); }));
                Application.Run(form);
                return File.Exists(Path.Combine(folder,trayTest?"tray-test-success.json":simulatePower?"power-test-success.json":"live-test-success.json")) ? 0 : 1;
            }
            using var mutex = new Mutex(true, "Local\\TopViewport.Main", out bool created);
            if (!created) { MessageBox.Show("TopViewport 已经在运行。恢复快捷键：Ctrl + Alt + F12。", "TopViewport"); return 0; }
            Application.ThreadException += (_, e) => Log(e.Exception.ToString());
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject.ToString() ?? "Unhandled error");
            Recovery.RecoverSavedSession();
            using var mainForm=new MainForm();
            if(args.Length==5 && args[0]=="--resume")
                mainForm.Shown+=(_,_)=>mainForm.BeginInvoke(new Action(()=>mainForm.ResumeProjection(int.Parse(args[1]),int.Parse(args[2]),args[3],args[4])));
            Application.Run(mainForm);
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            if (args.Length == 0) MessageBox.Show(ex.Message, "TopViewport", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static void Log(string message)
    {
        try { Directory.CreateDirectory(StateDirectory); File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}", Encoding.UTF8); } catch { }
    }

    public static Process StartSelf(params string[] arguments)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "TopViewport.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动恢复进程。");
    }
}

public sealed class SessionState
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public int ParentPid { get; set; }
    public long ParentStartTicks { get; set; }
    public string SnapshotPath { get; set; } = "";
    public DateTimeOffset Heartbeat { get; set; }
    public DateTimeOffset ConfirmationDeadline { get; set; }
    public bool Confirmed { get; set; }
    public bool Completed { get; set; }
    // QueryUnbiasedInterruptTime ticks are shared across processes and exclude sleep.
    public long HeartbeatAwakeTicks { get; set; }
    public long ConfirmationDeadlineAwakeTicks { get; set; }
    public bool Suspended { get; set; }
    public long ResumeGeneration { get; set; }
    public long ResumeGraceUntilAwakeTicks { get; set; }
}

internal static class Recovery
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    public static long AwakeNowTicks
    {
        get
        {
            if (!QueryUnbiasedInterruptTime(out ulong ticks))
                throw new InvalidOperationException("无法读取 Windows 唤醒计时器，未使用包含睡眠时间的超时计时器。");
            return checked((long)ticks);
        }
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public static void MarkSuspended(SessionState state)
    {
        state.Suspended = true;
        state.ResumeGraceUntilAwakeTicks = 0;
        state.Heartbeat = DateTimeOffset.UtcNow;
        state.HeartbeatAwakeTicks = AwakeNowTicks;
    }

    public static void MarkResumed(SessionState state, int graceSeconds = 30)
    {
        long now = AwakeNowTicks;
        state.Suspended = false;
        state.ResumeGeneration++;
        state.ResumeGraceUntilAwakeTicks = now + Math.Clamp(graceSeconds, 5, 120) * TicksPerSecond;
        state.Heartbeat = DateTimeOffset.UtcNow;
        state.HeartbeatAwakeTicks = now;
        if (!state.Confirmed)
        {
            state.ConfirmationDeadline = state.Heartbeat.AddSeconds(20);
            state.ConfirmationDeadlineAwakeTicks = now + 20 * TicksPerSecond;
        }
    }

    public static bool IsResumeGrace(SessionState state) => state.Suspended || AwakeNowTicks < state.ResumeGraceUntilAwakeTicks;
    public static bool IsConfirmationExpired(SessionState state) => !state.Confirmed && !IsResumeGrace(state) && ConfirmationSecondsRemaining(state) <= 0;
    public static double ConfirmationSecondsRemaining(SessionState state) => state.ConfirmationDeadlineAwakeTicks > 0
        ? Math.Max(0, (state.ConfirmationDeadlineAwakeTicks - AwakeNowTicks) / (double)TicksPerSecond)
        : Math.Max(0, (state.ConfirmationDeadline - DateTimeOffset.UtcNow).TotalSeconds);

    public static void BeginSession(SessionState state, string path)
    {
        using var recoveryMutex = new Mutex(false, "Local\\TopViewport.DisplayRecovery");
        bool held = false;
        try
        {
            try { held = recoveryMutex.WaitOne(TimeSpan.FromMilliseconds(500)); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new TimeoutException("上一显示恢复仍在进行，请稍后再启用。");
            WriteCore(state, path, true);
        }
        finally { if (held) recoveryMutex.ReleaseMutex(); }
    }

    public static void Write(SessionState state, string path) => WriteCore(state, path, false);

    private static void WriteCore(SessionState state, string path, bool beginSession)
    {
        using var mutex=new Mutex(false,"Local\\TopViewport.SessionState");
        bool held=false;
        try
        {
            try { held=mutex.WaitOne(TimeSpan.FromMilliseconds(500)); } catch(AbandonedMutexException) { held=true; }
            if(!held) throw new TimeoutException("恢复状态写入超时。");
            var previous=Read(path);
            if (previous is null && File.Exists(path))
                throw new IOException("恢复状态文件存在但暂时无法读取或已损坏；已保留原文件，未覆盖会话。");
            if (previous is not null && previous.SessionId != state.SessionId)
            {
                if (!beginSession)
                {
                    // An obsolete UI/watchdog must not overwrite a newer session.
                    state.Completed = true;
                    return;
                }
                if (!previous.Completed) throw new InvalidOperationException("上一显示会话尚未恢复，原始备份已保留。");
            }
            // A late UI heartbeat must never undo a completed watchdog/manual recovery.
            if(previous?.SessionId==state.SessionId && previous.Completed) state.Completed=true;
            if (!state.Completed)
            {
                long awake = AwakeNowTicks;
                state.HeartbeatAwakeTicks = awake;
                if (state.ConfirmationDeadlineAwakeTicks <= 0 || previous?.SessionId != state.SessionId || previous.ConfirmationDeadline != state.ConfirmationDeadline)
                    state.ConfirmationDeadlineAwakeTicks = awake + Math.Max(0, (state.ConfirmationDeadline - DateTimeOffset.UtcNow).Ticks);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { JsonSerializer.Serialize(stream,state,Program.JsonOptions); stream.Flush(true); }
                CommitStateFile(temporary,path);
            }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { if(held) mutex.ReleaseMutex(); }
    }
    public static SessionState? Read(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = OpenStateReadStream(path);
                return JsonSerializer.Deserialize<SessionState>(stream);
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 9)
            {
                // ReplaceFile can briefly deny a new open while switching names.
                // An already open reader keeps a complete old snapshot; a new
                // reader retries briefly instead of treating the gap as lost state.
                Thread.Sleep(2);
            }
            catch { return null; }
        }
    }
    internal static FileStream OpenStateReadStream(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

    private static void CommitStateFile(string temporary, string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // MoveFileEx(REPLACE_EXISTING) can reject an open destination
                // even with FILE_SHARE_DELETE on Windows. ReplaceFile preserves
                // an existing reader's old snapshot while publishing the new one.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && elapsed.ElapsedMilliseconds < 150)
            {
                // A legacy reader or antivirus can briefly omit FILE_SHARE_DELETE.
                // Retry the atomic replacement without dropping the state mutex.
                Thread.Sleep((int)Math.Min(10, Math.Max(1, 150 - elapsed.ElapsedMilliseconds)));
            }
        }
    }

    public static DisplayApplyResult ApplyCurrentSession(SessionState state, Func<DisplayApplyResult> apply) =>
        ApplyCurrentSessionAtPath(state, Program.SessionPath, apply);

    internal static DisplayApplyResult ApplyCurrentSessionAtPath(SessionState state, string path, Func<DisplayApplyResult> apply)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(apply);
        using var mutex = new Mutex(false, "Local\\TopViewport.DisplayRecovery");
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromMilliseconds(500)); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new TimeoutException("另一显示操作尚未完成，请稍后重试。");
            var current = Read(path);
            if (state.Completed || current is null || current.Completed || current.SessionId != state.SessionId)
                throw new InvalidOperationException("显示会话已结束或已被替换，未应用显示布局。");
            // Restore and BeginSession use this same mutex. A manual/watchdog
            // recovery cannot mark Completed between this check and the apply.
            return apply();
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }
    public static bool ParentAlive(SessionState state)
    {
        try { using var p = Process.GetProcessById(state.ParentPid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == state.ParentStartTicks; } catch { return false; }
    }
    public static int RunWatchdog(string sessionPath, string? expectedSessionId = null)
    {
        long readFailedSince = -1;
        SessionState? lastState = null;
        var policy = new WatchdogPolicy(expectedSessionId);
        while (true)
        {
            long awakeNow = AwakeNowTicks;
            var state = Read(sessionPath);
            if (state != null) { lastState = state; readFailedSince = -1; }
            else
            {
                if (readFailedSince < 0) readFailedSince = awakeNow;
                // A transient state-file read error must not delay recovery of a
                // known parent that has already died (including during suspend).
                if ((lastState is null || ParentAlive(lastState)) && awakeNow - readFailedSince < 10 * TicksPerSecond)
                { Thread.Sleep(250); continue; }
                state = lastState;
                if (state == null) return 1;
            }
            bool parentAlive = ParentAlive(state);
            WatchdogDecision decision = policy.Evaluate(state, parentAlive, awakeNow, DateTimeOffset.UtcNow);
            if (decision == WatchdogDecision.Exit) return 0;
            if (decision == WatchdogDecision.Recover)
            {
                Program.Log($"Watchdog restore: reason={policy.Reason}, parentAlive={parentAlive}, suspended={state.Suspended}, session={state.SessionId}");
                for(int attempt=0;attempt<20;attempt++)
                {
                    if(Restore(state,sessionPath)) return 0;
                    Thread.Sleep(1500);
                }
                return 1;
            }
            Thread.Sleep(250);
        }
    }
    public static bool Restore(SessionState state, string path)
    {
        using var mutex=new Mutex(false,"Local\\TopViewport.DisplayRecovery");
        bool held=false;
        try
        {
            try { held=mutex.WaitOne(TimeSpan.FromMilliseconds(500)); } catch(AbandonedMutexException) { held=true; }
            if(!held) throw new TimeoutException("另一恢复操作尚未完成，请稍后重试。");
            var already=Read(path);
            if (already is null && File.Exists(path))
                throw new IOException("恢复状态文件存在但暂时无法读取或已损坏；未恢复无法确认身份的会话。");
            if (already is not null && already.SessionId != state.SessionId)
            {
                // A delayed watchdog belongs to the old session, including its
                // cursor restriction. Do not touch the replacement session at all.
                state.Completed = true;
                Program.Log("Ignored obsolete display recovery for session " + state.SessionId);
                return true;
            }
            if(already?.SessionId==state.SessionId && already.Completed) { state.Completed=true; return true; }
            Native.ClipCursor(IntPtr.Zero);
            var result = DisplayManager.RestoreSnapshot(DisplayManager.LoadSnapshot(state.SnapshotPath));
            Program.Log("Restored display layout. " + string.Join("; ", result.Warnings));
            state.Completed = true;
            Write(state, path);
            return true;
        }
        catch (Exception ex) { Program.Log("Restore failed: " + ex); return false; }
        finally { if(held) mutex.ReleaseMutex(); }
    }
    public static void RecoverSavedSession()
    {
        var state = Read(Program.SessionPath);
        if (state == null || state.Completed || ParentAlive(state)) return;
        Restore(state, Program.SessionPath);
    }
}

internal enum WatchdogDecision { Continue, Exit, Recover }

/// <summary>Pure watchdog timing policy; injected clocks make sleep, clock jumps and session races testable.</summary>
internal sealed class WatchdogPolicy
{
    private const long Second = TimeSpan.TicksPerSecond;
    private string? sessionId;
    private bool initialized;
    private bool wasSuspended;
    private long previousAwake;
    private DateTimeOffset previousUtc;
    private DateTimeOffset previousHeartbeat;
    private long fallbackHeartbeatAwake;
    private long fallbackConfirmationDeadline;
    private long localResumeGraceUntil;
    private long suspendedObservedAwake;
    private long resumeGeneration;
    public string Reason { get; private set; } = "healthy";

    public WatchdogPolicy(string? expectedSessionId = null) { sessionId = expectedSessionId; }

    public WatchdogDecision Evaluate(SessionState state, bool parentAlive, long awakeNow, DateTimeOffset utcNow)
    {
        sessionId ??= state.SessionId;
        if (state.SessionId != sessionId) { Reason = "session-replaced"; return WatchdogDecision.Exit; }
        if (state.Completed) { Reason = "completed"; return WatchdogDecision.Exit; }
        // A crashed/exited parent still requires recovery even while a suspended
        // marker or a resume grace period remains in the last saved state.
        if (!parentAlive) { Reason = "parent-exited"; return WatchdogDecision.Recover; }

        bool wasAlreadySuspended = initialized && wasSuspended;
        if (!initialized)
        {
            fallbackHeartbeatAwake = awakeNow;
            fallbackConfirmationDeadline = awakeNow + Math.Max(0, (state.ConfirmationDeadline - utcNow).Ticks);
            resumeGeneration = state.ResumeGeneration;
        }
        else
        {
            long wallElapsed = (utcNow - previousUtc).Ticks;
            long awakeElapsed = Math.Max(0, awakeNow - previousAwake);
            // Covers a missed GUI suspend notification, hibernation, and wall-clock
            // corrections. Only the grace period is extended; timeout clocks never
            // use UTC or the sleep-inclusive GetTickCount64 counter.
            bool sleptOrClockChanged = Math.Abs((double)wallElapsed - awakeElapsed) > 3 * Second;
            bool watchdogWasPaused = awakeElapsed > 5 * Second;
            if (sleptOrClockChanged || watchdogWasPaused || (wasSuspended && !state.Suspended) || resumeGeneration != state.ResumeGeneration)
                localResumeGraceUntil = awakeNow + 30 * Second;
        }
        if (!initialized || previousHeartbeat != state.Heartbeat)
            fallbackHeartbeatAwake = awakeNow;
        previousHeartbeat = state.Heartbeat;
        previousAwake = awakeNow;
        previousUtc = utcNow;
        wasSuspended = state.Suspended;
        resumeGeneration = state.ResumeGeneration;
        initialized = true;

        long grace = Math.Max(localResumeGraceUntil, state.ResumeGraceUntilAwakeTicks);
        if (state.Suspended)
        {
            if (!wasAlreadySuspended) suspendedObservedAwake = awakeNow;
            long lastHeartbeat = state.HeartbeatAwakeTicks > 0 && state.HeartbeatAwakeTicks <= awakeNow
                ? state.HeartbeatAwakeTicks : fallbackHeartbeatAwake;
            // Sleep itself does not advance this clock. A UI that remains hung
            // after waking still needs protection even if it never handles Resume.
            if (awakeNow >= grace && awakeNow - Math.Max(suspendedObservedAwake, lastHeartbeat) > 60 * Second)
            { Reason = "suspended-parent-stalled"; return WatchdogDecision.Recover; }
            Reason = "suspended";
            return WatchdogDecision.Continue;
        }
        if (awakeNow < grace) { Reason = "resume-grace"; return WatchdogDecision.Continue; }
        long confirmationDeadline = state.ConfirmationDeadlineAwakeTicks > 0 ? state.ConfirmationDeadlineAwakeTicks : fallbackConfirmationDeadline;
        if (!state.Confirmed && awakeNow > confirmationDeadline)
        { Reason = "confirmation-expired"; return WatchdogDecision.Recover; }
        long heartbeat = state.HeartbeatAwakeTicks > 0 && state.HeartbeatAwakeTicks <= awakeNow
            ? state.HeartbeatAwakeTicks : fallbackHeartbeatAwake;
        if (state.Confirmed && awakeNow - heartbeat > 12 * Second)
        { Reason = "heartbeat-stalled"; return WatchdogDecision.Recover; }
        Reason = "healthy";
        return WatchdogDecision.Continue;
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public Rect(Rectangle r) { Left=r.Left; Top=r.Top; Right=r.Right; Bottom=r.Bottom; } }
    [DllImport("user32.dll")] public static extern bool ClipCursor(ref Rect rect);
    [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] public static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr insertAfter,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className,string? title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point point,uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size,Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    public static bool HasRecentInput(int milliseconds)
    { var info=new LastInput { Size=8 }; return GetLastInputInfo(ref info) && unchecked((uint)Environment.TickCount-info.Time)<milliseconds; }
}

public sealed class ProjectionForm : Form
{
    public ProjectionForm(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        Bounds = bounds; BackColor = Color.Black; ShowInTaskbar = false; TopMost = true;
        AutoScaleMode = AutoScaleMode.None; Text = "TopViewport Output";
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; } }
}
