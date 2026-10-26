using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TopViewport;

public sealed class MainForm : Form
{
    private readonly ComboBox physical = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox virtualScreen = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown bottomPixels = new() { Minimum = 0, Maximum = 2048, Value = 60, Width = 90 };
    private readonly NumericUpDown hz = new() { Minimum = 24, Maximum = 240, Value = 60, Width = 90 };
    private readonly Label dimensions = new() { AutoSize = true, ForeColor = Color.FromArgb(32, 91, 166), Margin = new Padding(0, 12, 0, 12) };
    private readonly Label stateLabel = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(50, 65, 80) };
    private readonly Label driverLabel = new() { AutoSize = false, Height = 42, Dock = DockStyle.Fill };
    private readonly Button start = new() { Text = "启用上方显示区域", AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    private readonly Button stop = new() { Text = "恢复原显示设置", AutoSize = true, Enabled = false, Padding = new Padding(12, 6, 12, 6) };
    private readonly Button confirm = new() { Text = "画面正常，保留设置", AutoSize = true, Visible = false, Padding = new Padding(8, 5, 8, 5) };
    private readonly Button install = new() { Text = "安装 / 配置虚拟屏（需管理员）", AutoSize = true };
    private readonly Button refresh = new() { Text = "刷新显示器", AutoSize = true };
    private readonly Button remove = new() { Text = "移除本工具安装的虚拟屏", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer screenshotTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer screenshotPointerTimer = new() { Interval = 16 };
    private ScreenshotCursorOverlay? screenshotPointer;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem automaticWindowRecovery = new("自动找回跑屏窗口") { Checked=true,CheckOnClick=true };
    private readonly WindowPlacementGuard windowPlacementGuard = new(Environment.ProcessId);
    private bool initialWindowRecoveryPending;
    private bool screenClipPointerMode;
    private Rectangle screenClipSourceBounds;
    private Rectangle screenClipPhysicalBounds;
    private Rectangle screenClipPreviousRestriction;
    private int screenClipVisibleHeight;
    private ProjectionEngine? engine;
    private ProjectionForm? projection;
    private SessionState? session;
    private DisplayApplyResult? applied;
    private long startAt;
    private bool starting;
    private bool restoring;
    private bool hotkeyRegistered;
    private bool automatedTest;
    private DisplayInfo? selectedPhysical;
    private DisplayInfo? selectedVirtual;
    private DisplayInfo? activePhysical;
    private DisplayInfo? activeVirtual;
    private (int Width,int Height,int Refresh) activeMode;
    private long lastFrameCount;
    private long lastFrameAt;
    private readonly SemaphoreSlim lifecycle = new(1,1);
    private CancellationTokenSource? reconnectCancellation;
    private bool suspended;
    private bool reconnecting;
    private bool stopRequested;
    private bool exitRequested;
    private bool allowClose;
    private bool closing;
    private bool sessionEnding;
    private long previousTickAwake;
    private DateTimeOffset previousTickUtc;
    private long lastResumeNotification;
    private long suspendStartedAwake;
    private long reconnectAttempts;
    private bool changingTrayVisibility;
    private Rectangle normalControlPanelBounds;

    public MainForm()
    {
        Text = "TopViewport 0.1.5 · 屏幕可用区域";
        Icon = AppIcons.Get(AppIconState.Idle);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 248, 251);
        ClientSize = new Size(740, 690); MinimumSize = new Size(700, 660);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24), ColumnCount=1, RowCount=12 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach (int height in new[] { 40, 64, 45, 45, 56, 42, 44, 50, 50, 50, 45 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var title = new Label { Text="把完整桌面放在屏幕正常区域", Font=new Font(Font.FontFamily,17,FontStyle.Bold), AutoSize=true, ForeColor=Color.FromArgb(24,43,65) };
        layout.Controls.Add(title,0,0);
        layout.Controls.Add(new Label { Text="桌面、任务栏和应用会显示在实体屏幕上方的正常区域。\nWindows 会看到两个屏幕；跑到投射屏后方的普通窗口会自动移回。", Dock=DockStyle.Fill },0,1);
        layout.Controls.Add(Field("实体屏幕（投射到这里）",physical),0,2);
        layout.Controls.Add(Field("虚拟主屏（桌面运行在这里）",virtualScreen),0,3);
        var settings = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, Padding=new Padding(0,8,0,0) };
        settings.Controls.Add(new Label { Text="底部避让", AutoSize=true, Padding=new Padding(0,5,0,0) }); settings.Controls.Add(bottomPixels);
        settings.Controls.Add(new Label { Text="像素    虚拟屏刷新率", AutoSize=true, Padding=new Padding(0,5,0,0) }); settings.Controls.Add(hz);
        settings.Controls.Add(new Label { Text="Hz", AutoSize=true, Padding=new Padding(0,5,0,0) });
        layout.Controls.Add(settings,0,4); layout.Controls.Add(dimensions,0,5);
        var driverButtons = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false };
        driverButtons.Controls.Add(install); driverButtons.Controls.Add(refresh); layout.Controls.Add(driverButtons,0,6);
        layout.Controls.Add(driverLabel,0,7);
        var buttons = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false };
        buttons.Controls.Add(start); buttons.Controls.Add(stop); layout.Controls.Add(buttons,0,8);
        layout.Controls.Add(confirm,0,9);
        layout.Controls.Add(new Label { Text="紧急恢复：Ctrl + Alt + F12　｜　关闭程序也会恢复原布局", Font=new Font(Font.FontFamily,9,FontStyle.Bold), ForeColor=Color.FromArgb(25,79,126), Dock=DockStyle.Fill },0,10);
        var bottom = new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3 };
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent,100)); bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,40)); bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,35));
        bottom.Controls.Add(stateLabel,0,0);
        bottom.Controls.Add(new Label { Text="游戏建议先用窗口 / 无边框模式并选择虚拟主屏。独占全屏、HDR 和特定游戏兼容性需验证。", Dock=DockStyle.Fill,Font=new Font(Font.FontFamily,8.5f),ForeColor=Color.DimGray },0,1);
        bottom.Controls.Add(remove,0,2); layout.Controls.Add(bottom,0,11); Controls.Add(layout);
        var menu = new ContextMenuStrip(); menu.Items.Add("显示控制面板",null,(_,_)=>ShowControlPanel());
        menu.Items.Add("找回不可见窗口",null,(_,_)=>
        {
            int count=RecoverHiddenWindows(immediate:true,includeMinimized:true);
            stateLabel.Text=session is { Completed:false }
                ? (count>0 ? $"已请求找回 {count} 个跑屏窗口。" : "当前未发现需要找回的普通窗口。")
                : "请先启用上方显示区域；单屏布局下不需要找回投射屏窗口。";
        });
        menu.Items.Add(automaticWindowRecovery);
        menu.Items.Add("重新连接显示区域",null,async (_,_)=>await ReconnectProjectionAsync("手动重新连接"));
        menu.Items.Add("恢复原显示设置",null,async (_,_)=>await StopProjectionAsync()); menu.Items.Add("恢复并退出",null,(_,_)=>Close());
        tray = new NotifyIcon { Icon=AppIcons.Get(AppIconState.Idle),Text="TopViewport · 已就绪",Visible=true,ContextMenuStrip=menu };
        tray.MouseClick += (_,e)=> { if(e.Button==MouseButtons.Left) ShowControlPanel(); };
        tray.DoubleClick += (_,_)=>ShowControlPanel();
        automaticWindowRecovery.CheckedChanged += (_,_)=> { windowPlacementGuard.Reset(); initialWindowRecoveryPending=true; };
        start.Click += async (_,_)=>await StartProjectionAsync(); stop.Click += async (_,_)=>await StopProjectionAsync(); confirm.Click += (_,_)=>Confirm();
        install.Click += async (_,_)=>await ConfigureDriver(); remove.Click += async (_,_)=>await RemoveDriver(); refresh.Click += (_,_)=>RefreshDisplays();
        physical.SelectedIndexChanged += (_,_)=> { if (physical.SelectedItem is DisplayChoice c) { selectedPhysical=c.Info; if (c.Info.RefreshRate>=24 && c.Info.RefreshRate<=240) hz.Value=c.Info.RefreshRate; } UpdateDimensions(); };
        virtualScreen.SelectedIndexChanged += (_,_)=> { if(virtualScreen.SelectedItem is DisplayChoice c) selectedVirtual=c.Info; };
        bottomPixels.ValueChanged += (_,_)=>UpdateDimensions(); hz.ValueChanged += (_,_)=>UpdateDimensions();
        timer.Tick += (_,_)=>TickSession(); timer.Start();
        screenshotTimer.Tick += (_,_)=>TickScreenshotCompatibility(); screenshotTimer.Start();
        screenshotPointerTimer.Tick += (_,_)=>
        {
            if(screenClipPointerMode && Native.GetCursorPos(out var position)) screenshotPointer?.MoveTo(position);
        };
        Shown += (_,_)=>
        {
            hotkeyRegistered=Native.RegisterHotKey(Handle,5512,0x0002|0x0001|0x4000,0x7B);
            RefreshDisplays();
            if(!hotkeyRegistered) { start.Enabled=false; stateLabel.Text="恢复快捷键 Ctrl+Alt+F12 被其他程序占用。请先释放该快捷键，再重启本工具。"; }
        };
        FormClosing += async (_,e)=>
        {
            if(allowClose) return;
            e.Cancel=true;
            if(closing) return;
            closing=true; exitRequested=true;
            await StopProjectionAsync();
            if(session is { Completed:false })
            {
                closing=false; exitRequested=false;
                ReportProblem("显示布局尚未恢复，请点击恢复重试；原始备份已保留。");
                return;
            }
            allowClose=true;
            Close();
        };
        FormClosed += (_,_)=> { Native.UnregisterHotKey(Handle,5512); tray.Visible=false; tray.Dispose(); timer.Dispose(); screenshotTimer.Dispose(); screenshotPointerTimer.Dispose(); screenshotPointer?.Dispose(); };
        // Hide the existing HWND, rather than recreating it by changing ShowInTaskbar.
        // Hiding during a minimize can leave WinForms' normal placement at -32000;
        // keep our own valid normal bounds and reset the hidden window to Normal.
        Resize += (_,_)=>
        {
            if(changingTrayVisibility) return;
            if(WindowState==FormWindowState.Minimized) HideControlPanelToTray();
            else RememberControlPanelBounds();
        };
        LocationChanged += (_,_)=>RememberControlPanelBounds();
        normalControlPanelBounds=Bounds;
    }

    private void ShowControlPanel()
    {
        if(IsDisposed || closing) return;
        changingTrayVisibility=true;
        try
        {
            StartPosition=FormStartPosition.Manual;
            WindowState=FormWindowState.Normal;
            Bounds=FitControlPanelBounds(GetControlPanelWorkingArea());
            Show();
            WindowState=FormWindowState.Normal;
            // Show/DPI/layout notifications can adjust bounds. Recheck against
            // the current display topology, never the cached minimized position.
            Bounds=FitControlPanelBounds(GetControlPanelWorkingArea());
            normalControlPanelBounds=Bounds;
            BringToFront();
            Activate();
            Native.SetForegroundWindow(Handle);
        }
        finally { changingTrayVisibility=false; }
    }

    private void HideControlPanelToTray()
    {
        if(changingTrayVisibility) return;
        changingTrayVisibility=true;
        try
        {
            Hide();
            WindowState=FormWindowState.Normal;
            if(IsValidNormalBounds(normalControlPanelBounds)) Bounds=normalControlPanelBounds;
        }
        finally { changingTrayVisibility=false; }
    }

    private void RememberControlPanelBounds()
    {
        if(!changingTrayVisibility && Visible && WindowState==FormWindowState.Normal && IsValidNormalBounds(Bounds))
            normalControlPanelBounds=Bounds;
    }

    private static bool IsValidNormalBounds(Rectangle bounds)
        => bounds.Width>0 && bounds.Height>0 && bounds.Left>-30000 && bounds.Top>-30000;

    private Rectangle FitControlPanelBounds(Rectangle area)
    {
        var previous=IsValidNormalBounds(normalControlPanelBounds) ? normalControlPanelBounds : new Rectangle(30,30,Width,Height);
        int width=Math.Max(MinimumSize.Width,Math.Min(previous.Width,area.Width));
        int height=Math.Max(MinimumSize.Height,Math.Min(previous.Height,area.Height));
        int x=Math.Clamp(previous.Left,area.Left,Math.Max(area.Left,area.Right-width));
        int y=Math.Clamp(previous.Top,area.Top,Math.Max(area.Top,area.Bottom-height));
        return new Rectangle(x,y,width,height);
    }

    private Rectangle GetControlPanelWorkingArea()
    {
        try
        {
            // Read native monitor work areas every time: WinForms Screen caches
            // may still describe the physical monitor after a primary change.
            var displays=DisplayManager.ListDisplays(false);
            DisplayInfo? target=null;
            if(session is { Completed:false })
            {
                if(activeVirtual!=null) target=DisplayManager.FindCurrentDisplay(activeVirtual,displays);
                target ??= displays.FirstOrDefault(d=>d.IsPrimary && d.IsLikelyVirtual);
                // During wake/reconnect the virtual device may briefly vanish.
                // Retain its source coordinates rather than restoring the panel
                // behind the still-visible physical projection window.
                if(target==null && applied!=null)
                    return new Rectangle(applied.VirtualBounds.Left,applied.VirtualBounds.Top,applied.VirtualBounds.Width,applied.VirtualBounds.Height);
            }
            target ??= displays.FirstOrDefault(d=>d.IsPrimary) ?? displays.FirstOrDefault();
            if(target!=null)
            {
                var bounds=new Rectangle(target.X,target.Y,target.Width,target.Height);
                if(target.WorkArea!=null)
                {
                    var work=target.WorkArea;
                    var area=Rectangle.Intersect(bounds,Rectangle.FromLTRB(work.Left,work.Top,work.Right,work.Bottom));
                    if(area.Width>0 && area.Height>0) return area;
                }
                if(bounds.Width>0 && bounds.Height>0) return bounds;
            }
        }
        catch(Exception ex) { Program.Log("Reading control-panel work area: "+ex.Message); }
        if(applied!=null && session is { Completed:false })
            return new Rectangle(applied.VirtualBounds.Left,applied.VirtualBounds.Top,applied.VirtualBounds.Width,applied.VirtualBounds.Height);
        return Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0,0,1920,1080);
    }

    public async void ResumeProjection(int reservedBottom,int refreshRate,string physicalName,string virtualName)
    {
        for(int i=0;i<physical.Items.Count;i++)
            if(((DisplayChoice)physical.Items[i]).Info.DeviceName==physicalName) physical.SelectedIndex=i;
        for(int i=0;i<virtualScreen.Items.Count;i++)
            if(((DisplayChoice)virtualScreen.Items[i]).Info.DeviceName==virtualName) virtualScreen.SelectedIndex=i;
        bottomPixels.Value=Math.Clamp(reservedBottom,(int)bottomPixels.Minimum,(int)bottomPixels.Maximum);
        hz.Value=Math.Clamp(refreshRate,(int)hz.Minimum,(int)hz.Maximum);
        await StartProjectionAsync();
        if(session==null || session.Completed || engine==null) return;
        var resumeTimer=new System.Windows.Forms.Timer { Interval=100 };
        var deadline=DateTimeOffset.UtcNow.AddSeconds(5);
        resumeTimer.Tick+=(_,_)=>
        {
            if(session==null || session.Completed || engine==null || DateTimeOffset.UtcNow>deadline)
            { resumeTimer.Stop(); resumeTimer.Dispose(); return; }
            if(engine.FramesPresented>0)
            {
                Confirm();
                resumeTimer.Stop(); resumeTimer.Dispose();
                WindowState=FormWindowState.Minimized;
            }
        };
        resumeTimer.Start();
    }

    private static Control Field(string name,Control control)
    {
        var row = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2,RowCount=1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,235)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        row.Controls.Add(new Label { Text=name,AutoSize=true,Padding=new Padding(0,7,0,0) },0,0); row.Controls.Add(control,1,0); return row;
    }
    private void RefreshDisplays(bool updateStatus=true)
    {
        if (session!=null && !session.Completed) return;
        try
        {
            var pending=Recovery.Read(Program.SessionPath);
            if(pending is { Completed:false })
            {
                session=pending; stop.Enabled=true; start.Enabled=false;
                stateLabel.Text="上次显示布局尚未恢复。请先点击“恢复原显示设置”；原始备份会保留。";
                return;
            }
            var displays=DisplayManager.ListDisplays();
            var oldPhysical=selectedPhysical;
            var oldVirtual=selectedVirtual;
            decimal previousHz=hz.Value;
            physical.Items.Clear(); virtualScreen.Items.Clear();
            foreach (var d in displays.Where(d=>d.IsActive && !d.IsLikelyVirtual)) physical.Items.Add(new DisplayChoice(d));
            foreach (var d in displays.Where(d=>d.IsLikelyVirtual)) virtualScreen.Items.Add(new DisplayChoice(d));
            string? physicalName=oldPhysical==null ? null : DisplayManager.FindCurrentDisplay(oldPhysical,displays)?.DeviceName;
            string? virtualName=oldVirtual==null ? null : DisplayManager.FindCurrentDisplay(oldVirtual,displays)?.DeviceName;
            if(physical.Items.Count>0) physical.SelectedIndex=Math.Max(0,physical.Items.Cast<DisplayChoice>().ToList().FindIndex(c=>c.Info.DeviceName==physicalName));
            if(virtualScreen.Items.Count>0) virtualScreen.SelectedIndex=Math.Max(0,virtualScreen.Items.Cast<DisplayChoice>().ToList().FindIndex(c=>c.Info.DeviceName==virtualName));
            if(oldPhysical!=null) hz.Value=previousHz;
            var status=DriverSetup.GetStatus(); driverLabel.Text=status.Message;
            start.Enabled=hotkeyRegistered && !starting && !restoring && !reconnecting && physical.Items.Count>0 && virtualScreen.Items.Count>0;
            install.Enabled=physical.Items.Count>0;
            if(updateStatus) stateLabel.Text=virtualScreen.Items.Count==0 ? "下一步：点击“安装 / 配置虚拟屏”，在 Windows 提示时确认。安装后点击刷新显示器。" : "已准备好。启用后有 20 秒确认时间；未确认会自动恢复。";
            UpdateDimensions();
        }
        catch(Exception ex) { stateLabel.Text="读取显示器失败："+ex.Message; Program.Log(ex.ToString()); }
    }
    private (int Width,int Height,int Refresh) RequestedMode()
    {
        var p=(physical.SelectedItem as DisplayChoice)?.Info ?? throw new InvalidOperationException("请选择实体显示器。");
        return (p.Width,Math.Max(480,p.Height-(int)bottomPixels.Value),(int)hz.Value);
    }
    private void UpdateDimensions()
    {
        if (physical.SelectedItem is not DisplayChoice c) { dimensions.Text="未找到活动实体显示器"; return; }
        bottomPixels.Maximum=Math.Max(0,c.Info.Height-480);
        var m=RequestedMode(); dimensions.Text=$"实体 {c.Info.Width} × {c.Info.Height} → 可用 {m.Width} × {m.Height}，底部保留 {c.Info.Height-m.Height} 像素（{100.0*m.Height/c.Info.Height:F2}% 可用）";
    }
    private async Task ConfigureDriver()
    {
        if(starting || session is { Completed:false }) return;
        if(physical.SelectedItem is not DisplayChoice) return;
        var m=RequestedMode();
        await RunElevated("--install-driver",m.Width.ToString(),m.Height.ToString(),m.Refresh.ToString());
        RefreshDisplays();
    }
    private async Task RemoveDriver()
    {
        await StopProjectionAsync();
        if(session is { Completed:false }) return;
        await RunElevated("--remove-driver"); RefreshDisplays();
    }
    private async Task RunElevated(params string[] args)
    {
        install.Enabled=remove.Enabled=start.Enabled=false;
        DisplaySnapshot? preInstallLayout=null;
        string resultPath=Path.Combine(Program.StateDirectory,"driver-result-"+Guid.NewGuid().ToString("N")+".json");
        Directory.CreateDirectory(Program.StateDirectory);
        try
        {
            if(args.Length>0 && args[0]=="--install-driver")
            {
                preInstallLayout=DisplayManager.CaptureSnapshot();
                DisplayManager.SaveSnapshot(preInstallLayout,Path.Combine(Program.StateDirectory,"before-driver-layout.json"));
            }
            var launch=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"TopViewport.exe")) { UseShellExecute=true, Verb="runas", WindowStyle=ProcessWindowStyle.Hidden };
            // All arguments are generated numbers/flags or a quoted local path, never interpreted by a shell.
            launch.Arguments=string.Join(" ",args.Select(a=>"\""+a.Replace("\"","")+"\""))+" \""+resultPath+"\"";
            stateLabel.Text="等待 Windows 管理员确认并配置驱动…";
            using var child=Process.Start(launch) ?? throw new InvalidOperationException("无法启动驱动安装。");
            await child.WaitForExitAsync();
            if(File.Exists(resultPath))
            {
                using var doc=JsonDocument.Parse(File.ReadAllText(resultPath));
                string message=doc.RootElement.GetProperty("Message").GetString() ?? "操作结束";
                if(preInstallLayout!=null && doc.RootElement.GetProperty("Success").GetBoolean())
                    DisplayManager.RestoreSnapshot(preInstallLayout);
                MessageBox.Show(this,message,"驱动配置",MessageBoxButtons.OK,doc.RootElement.GetProperty("Success").GetBoolean()?MessageBoxIcon.Information:MessageBoxIcon.Warning);
            }
            else MessageBox.Show(this,"未收到安装结果。可查看日志或重新执行。","驱动配置");
        }
        catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==1223) { stateLabel.Text="已取消管理员确认；显示设置未改变。"; }
        catch(Exception ex) { stateLabel.Text=ex.Message; Program.Log(ex.ToString()); MessageBox.Show(this,ex.Message,"驱动配置失败"); }
        finally { install.Enabled=remove.Enabled=true; try { if(File.Exists(resultPath)) File.Delete(resultPath); } catch { } }
    }
    private async Task StartProjectionAsync()
    {
        if(starting || restoring || reconnecting || session is { Completed:false } || exitRequested || sessionEnding) return;
        if(!hotkeyRegistered) { ReportProblem("恢复快捷键尚未注册，请释放 Ctrl+Alt+F12 后重新打开工具。"); return; }
        if(physical.SelectedItem is not DisplayChoice p || virtualScreen.SelectedItem is not DisplayChoice v) return;
        starting=true; stopRequested=false; SetControls(false); SetStatus(AppIconState.Recovering,"正在启用");
        await lifecycle.WaitAsync();
        try
        {
            if(sessionEnding || stopRequested) return;
            // GDI DISPLAY numbers can change after a driver restart or wake.
            var displays=await Task.Run(()=>DisplayManager.ListDisplays());
            if(sessionEnding || stopRequested) return;
            activePhysical=DisplayManager.FindCurrentDisplay(p.Info,displays) ?? throw new InvalidOperationException("实体屏幕尚未重新连接，请刷新显示器后重试。");
            activeVirtual=DisplayManager.FindCurrentDisplay(v.Info,displays) ?? throw new InvalidOperationException("虚拟屏幕尚未重新连接，请刷新显示器后重试。");
            activeMode=(activePhysical.Width,Math.Max(480,activePhysical.Height-(int)bottomPixels.Value),(int)hz.Value);
            var m=activeMode;
            var modes=await Task.Run(()=>DisplayManager.ListModes(activeVirtual.DeviceName));
            if(sessionEnding || stopRequested) return;
            if(!modes.Any(x=>x.Width==m.Width && x.Height==m.Height && Math.Abs(x.RefreshRate-m.Refresh)<=1))
                throw new InvalidOperationException($"虚拟屏尚未提供 {m.Width}×{m.Height} @ {m.Refresh}Hz。请先点击“安装 / 配置虚拟屏”，再刷新。");
            Directory.CreateDirectory(Program.StateDirectory);
            string snapshotPath=Path.Combine(Program.StateDirectory,"display-backup-"+Guid.NewGuid().ToString("N")+".json");
            await Task.Run(()=>DisplayManager.SaveSnapshot(DisplayManager.CaptureSnapshot(),snapshotPath));
            if(sessionEnding || stopRequested) return;
            using var current=Process.GetCurrentProcess();
            var candidate=new SessionState { ParentPid=Environment.ProcessId,ParentStartTicks=current.StartTime.ToUniversalTime().Ticks,SnapshotPath=snapshotPath,Heartbeat=DateTimeOffset.UtcNow,ConfirmationDeadline=DateTimeOffset.UtcNow.AddSeconds(45) };
            // Publish only after BeginSession succeeds: a failed begin must not adopt or restore another session.
            Recovery.BeginSession(candidate,Program.SessionPath);
            session=candidate;
            using var watchdog=Program.StartSelf("--watchdog",Program.SessionPath,session.SessionId);
            stop.Enabled=true;
            var applySession=session;
            applied=await Task.Run(()=>Recovery.ApplyCurrentSession(applySession,()=>DisplayManager.ApplyVirtualPrimary(activeVirtual.DeviceName,activePhysical.DeviceName,m.Width,m.Height,m.Refresh)));
            if(!stopRequested && !suspended && !sessionEnding) await StartRenderingAsync();
            if(stopRequested) return;
            if(suspended) { stateLabel.Text="电脑正在睡眠，唤醒后会自动重新连接。"; return; }
            Location=new Point(30,30);
            session.Heartbeat=DateTimeOffset.UtcNow; session.ConfirmationDeadline=DateTimeOffset.UtcNow.AddSeconds(20); Recovery.Write(session,Program.SessionPath);
            confirm.Visible=true; SetStatus(AppIconState.Active,"正在显示");
            stateLabel.Text="等待第一帧… 如果画面异常，按 Ctrl+Alt+F12 恢复。";
        }
        catch(Exception ex)
        {
            Program.Log(ex.ToString());
            await StopCoreAsync();
            ReportProblem("无法启用："+ex.Message);
        }
        finally
        {
            starting=false; lifecycle.Release();
            if(session==null) { SetControls(true); RefreshDisplays(false); }
        }
    }

    private async Task StartRenderingAsync()
    {
        if(sessionEnding || stopRequested) return;
        if(applied==null || activeVirtual==null) throw new InvalidOperationException("显示布局尚未准备好。");
        var output=new Rectangle(applied.PhysicalBounds.Left,applied.PhysicalBounds.Top,applied.PhysicalBounds.Width,applied.PhysicalBounds.Height);
        projection=new ProjectionForm(output); projection.Show();
        var capture=new ProjectionEngine(); engine=capture;
        IntPtr target=projection.Handle;
        string source=applied.VirtualDeviceName;
        var m=activeMode;
        await Task.Run(()=>capture.Start(target,source,output,new Rectangle(0,0,m.Width,m.Height),m.Height));
        if(stopRequested || suspended) { capture.RequestStop(); return; }
        var rect=new Native.Rect(new Rectangle(0,0,m.Width,m.Height));
        if(!Native.ClipCursor(ref rect)) throw new InvalidOperationException("无法把鼠标限定到虚拟主屏，已中止启用。");
        Native.SetCursorPos(Math.Min(350,m.Width/2),Math.Min(250,m.Height/2));
        startAt=lastFrameAt=Recovery.AwakeNowTicks; lastFrameCount=0;
        windowPlacementGuard.Reset();
        initialWindowRecoveryPending=true;
    }

    private async Task StopRenderingAsync()
    {
        screenClipPointerMode=false;
        screenshotPointerTimer.Stop(); screenshotPointer?.Hide();
        windowPlacementGuard.Reset();
        Native.ClipCursor(IntPtr.Zero);
        var capture=engine; engine=null;
        if(capture!=null)
        {
            capture.RequestStop();
            await Task.Run(()=>
            {
                try { capture.Stop(); capture.Dispose(); }
                catch(Exception ex) { Program.Log(ex.ToString()); }
                if(capture.IsRunning) Program.Log("GPU worker stop is pending; retired its output window so a new capture can be created.");
            });
        }
        try { projection?.Close(); projection?.Dispose(); } catch(Exception ex) { Program.Log(ex.ToString()); }
        projection=null;
    }

    private void Confirm()
    {
        if(session==null || engine==null || engine.FramesPresented==0 || suspended || reconnecting) return;
        session.Confirmed=true; session.Heartbeat=DateTimeOffset.UtcNow; Recovery.Write(session,Program.SessionPath);
        confirm.Visible=false; SetStatus(AppIconState.Active,"正在显示");
        stateLabel.Text="已启用。最小化控制面板后可正常使用桌面；游戏选择虚拟主屏。";
    }

    private void TickSession()
    {
        long now=Recovery.AwakeNowTicks;
        var utcNow=DateTimeOffset.UtcNow;
        bool clockGap=previousTickAwake!=0 && Math.Abs((utcNow-previousTickUtc).Ticks-(now-previousTickAwake))>5*TimeSpan.TicksPerSecond;
        previousTickAwake=now; previousTickUtc=utcNow;
        if(session==null || session.Completed || restoring || sessionEnding) return;
        if(!reconnecting && (clockGap || (suspended && now-suspendStartedAwake>5*TimeSpan.TicksPerSecond)))
        { _=ReconnectProjectionAsync("检测到睡眠、取消睡眠或遗漏的恢复通知"); return; }
        var disk=Recovery.Read(Program.SessionPath);
        if(disk!=null && (disk.Completed || disk.SessionId!=session.SessionId)) { _=StopProjectionAsync(); return; }
        session.Heartbeat=utcNow;
        try { Recovery.Write(session,Program.SessionPath); }
        catch(Exception ex) { Program.Log(ex.ToString()); _=FailAndStopAsync("无法更新恢复状态："+ex.Message); return; }
        if(session.Completed) { _=StopProjectionAsync(); return; }
        if(suspended || reconnecting || starting) return;
        if(Recovery.IsConfirmationExpired(session)) { _=StopProjectionAsync(); return; }
        if(applied!=null)
        {
            try
            {
                var screens=DisplayManager.ListDisplays(false);
                var source=screens.FirstOrDefault(d=>d.DeviceName==applied.VirtualDeviceName);
                var output=screens.FirstOrDefault(d=>d.DeviceName==applied.PhysicalDeviceName);
                if(source==null || output==null || !source.IsPrimary || source.X!=0 || source.Y!=0 ||
                   output.X!=applied.PhysicalBounds.Left || output.Y!=applied.PhysicalBounds.Top ||
                   output.Width!=applied.PhysicalBounds.Width || output.Height!=applied.PhysicalBounds.Height ||
                   source.Height>output.Y || source.Width<1 || source.Height<1)
                {
                    if(Recovery.IsResumeGrace(session)) { _=ReconnectProjectionAsync("唤醒后显示布局尚在恢复"); return; }
                    throw new InvalidOperationException("系统改变了显示布局，已恢复原布局。可直接重新启用。");
                }
                var sourceBounds=new Rectangle(source.X,source.Y,source.Width,source.Height);
                if(!screenClipPointerMode)
                {
                    if(!Native.GetClipCursor(out var currentClip)) throw new InvalidOperationException("无法检查鼠标区域。");
                    var current=new Rectangle(currentClip.Left,currentClip.Top,currentClip.Right-currentClip.Left,currentClip.Bottom-currentClip.Top);
                    if(!sourceBounds.Contains(current))
                    {
                        var restricted=Rectangle.Intersect(current,sourceBounds);
                        if(restricted.Width<=0 || restricted.Height<=0) restricted=sourceBounds;
                        var rect=new Native.Rect(restricted);
                        if(!Native.ClipCursor(ref rect)) throw new InvalidOperationException("鼠标区域锁定失败。");
                    }
                }
                if(!screenClipPointerMode && automaticWindowRecovery.Checked && engine?.FramesPresented>0)
                {
                    RecoverHiddenWindows(screens,immediate:initialWindowRecoveryPending,includeMinimized:initialWindowRecoveryPending);
                    initialWindowRecoveryPending=false;
                }
            }
            catch(Exception ex) { Program.Log(ex.ToString()); _=FailAndStopAsync(ex.Message); return; }
        }
        if(engine!=null)
        {
            long count=engine.FramesPresented;
            if(count!=lastFrameCount) { lastFrameCount=count; lastFrameAt=now; }
            if(count==0 && now-startAt>10*TimeSpan.TicksPerSecond)
            { _=FailAndStopAsync("未收到虚拟主屏画面，已恢复。"+engine.Status); return; }
            if(!engine.IsRunning && now-startAt>3*TimeSpan.TicksPerSecond)
            { _=FailAndStopAsync("投射已停止，可直接重新启用。"+engine.Status); return; }
            if(!screenClipPointerMode && !Recovery.IsResumeGrace(session) && (now-engine.LastLoopAwakeTicks>12*TimeSpan.TicksPerSecond ||
               (count>0 && now-lastFrameAt>15*TimeSpan.TicksPerSecond && Native.HasRecentInput(2000))))
            { _=FailAndStopAsync("捕获画面长时间没有响应，已恢复原布局，可重新启用。"+engine.Status); return; }
            stateLabel.Text=session.Confirmed ? $"正在显示完整虚拟桌面　|　{engine.Status}\n恢复快捷键：Ctrl+Alt+F12" : $"请确认画面正常：{(int)Recovery.ConfirmationSecondsRemaining(session)} 秒后自动恢复\n{engine.Status}";
        }
    }

    private async Task FailAndStopAsync(string message)
    {
        await StopProjectionAsync();
        ReportProblem(message);
    }

    private async Task StopProjectionAsync()
    {
        stopRequested=true;
        reconnectCancellation?.Cancel();
        engine?.RequestStop();
        Native.ClipCursor(IntPtr.Zero);
        if(restoring) { while(restoring) await Task.Delay(100); return; }
        restoring=true;
        SetStatus(AppIconState.Recovering,"正在恢复原布局");
        SetControls(false); stop.Enabled=false;
        await lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { restoring=false; lifecycle.Release(); }
        if(session==null) { SetControls(true); RefreshDisplays(); stateLabel.Text="已恢复原显示布局。可以直接重新启用。"; }
    }

    private async Task StopCoreAsync()
    {
        await StopRenderingAsync();
        if(session is { Completed:false })
        {
            var state=session;
            await Task.Run(()=>Recovery.Restore(state,Program.SessionPath));
        }
        applied=null; confirm.Visible=false;
        if(session is { Completed:false })
        {
            SetControls(false); stop.Enabled=true;
            ReportProblem("自动恢复未完成，原始备份已保留。请再次点击恢复，或运行“恢复显示设置.cmd”。");
        }
        else
        {
            session=null; activePhysical=null; activeVirtual=null; stop.Enabled=false;
            SetStatus(AppIconState.Idle,"已就绪");
        }
    }

    private async Task ReconnectProjectionAsync(string reason)
    {
        if(reconnecting || restoring || stopRequested || exitRequested || sessionEnding) return;
        if(session==null || session.Completed)
        {
            suspended=false; RefreshDisplays(); return;
        }
        reconnecting=true; suspended=false;
        var cancellation=new CancellationTokenSource();
        reconnectCancellation=cancellation;
        var token=cancellation.Token;
        SetStatus(AppIconState.Recovering,"正在重新连接");
        stateLabel.Text="正在等待显示器唤醒并重新连接… 恢复按钮和退出菜单仍可使用。";
        SetControls(false); stop.Enabled=true;
        Program.Log("Reconnecting projection: "+reason);
        await lifecycle.WaitAsync();
        try
        {
            token.ThrowIfCancellationRequested();
            if(session==null || session.Completed || stopRequested) return;
            Recovery.MarkResumed(session,45); Recovery.Write(session,Program.SessionPath);
            await StopRenderingAsync();
            await Task.Delay(1500,token);
            long deadline=Recovery.AwakeNowTicks+30*TimeSpan.TicksPerSecond;
            Exception? lastError=null;
            do
            {
                token.ThrowIfCancellationRequested();
                if(suspended || stopRequested || session==null || session.Completed) throw new OperationCanceledException();
                try
                {
                    var displays=await Task.Run(()=>DisplayManager.ListDisplays());
                    var output=activePhysical==null ? null : DisplayManager.FindCurrentDisplay(activePhysical,displays);
                    var source=activeVirtual==null ? null : DisplayManager.FindCurrentDisplay(activeVirtual,displays);
                    if(output==null || source==null) throw new InvalidOperationException("显示器尚未重新连接。");
                    activePhysical=output; activeVirtual=source;
                    var m=activeMode;
                    var applySession=session;
                    applied=await Task.Run(()=>Recovery.ApplyCurrentSession(applySession,()=>DisplayManager.ApplyVirtualPrimary(source.DeviceName,output.DeviceName,m.Width,m.Height,m.Refresh)));
                    token.ThrowIfCancellationRequested();
                    await StartRenderingAsync();
                    long firstFrameDeadline=Recovery.AwakeNowTicks+5*TimeSpan.TicksPerSecond;
                    while(engine?.FramesPresented==0 && Recovery.AwakeNowTicks<firstFrameDeadline)
                        await Task.Delay(100,token);
                    token.ThrowIfCancellationRequested();
                    if(engine==null || !engine.IsRunning || engine.FramesPresented==0) throw new InvalidOperationException("唤醒后的捕获尚未产生画面。");
                    if(suspended) throw new OperationCanceledException();
                    reconnectAttempts++;
                    Recovery.MarkResumed(session,30); Recovery.Write(session,Program.SessionPath);
                    confirm.Visible=!session.Confirmed;
                    SetStatus(AppIconState.Active,"正在显示");
                    stateLabel.Text="显示区域已自动重新连接。";
                    Program.Log("Projection reconnected successfully, virtual="+source.DeviceName+", physical="+output.DeviceName);
                    return;
                }
                catch(OperationCanceledException) { throw; }
                catch(Exception ex)
                {
                    lastError=ex; Program.Log("Resume retry: "+ex.Message);
                    await StopRenderingAsync();
                    await Task.Delay(1000,token);
                }
            } while(Recovery.AwakeNowTicks<deadline);
            throw new InvalidOperationException("唤醒后未能重新连接，已尝试恢复原布局。可点击启用重试。"+(lastError?.Message??""));
        }
        catch(OperationCanceledException) { }
        catch(Exception ex)
        {
            Program.Log(ex.ToString());
            await StopCoreAsync();
            ReportProblem(ex.Message);
        }
        finally
        {
            reconnecting=false;
            if(ReferenceEquals(reconnectCancellation,cancellation)) reconnectCancellation=null;
            cancellation.Dispose(); lifecycle.Release();
            if(session==null && !restoring) { SetControls(true); RefreshDisplays(false); }
        }
    }

    private void HandlePowerNotification(int eventType)
    {
        if(eventType==4) // PBT_APMSUSPEND: never wait for GPU/display operations here.
        {
            suspended=true; suspendStartedAwake=Recovery.AwakeNowTicks; reconnectCancellation?.Cancel(); engine?.RequestStop();
            Native.ClipCursor(IntPtr.Zero);
            if(session is { Completed:false })
            {
                Recovery.MarkSuspended(session); Recovery.Write(session,Program.SessionPath);
                SetStatus(AppIconState.Recovering,"睡眠后将自动重连");
                stateLabel.Text="电脑正在睡眠，唤醒后会自动重新连接。";
            }
        }
        else if(eventType==0x12 || eventType==7 || eventType==6)
        {
            long now=Recovery.AwakeNowTicks;
            if(!suspended && lastResumeNotification!=0 && now-lastResumeNotification<3*TimeSpan.TicksPerSecond) return;
            lastResumeNotification=now;
            suspended=false;
            // Queue until after WM_POWERBROADCAST has returned to Windows.
            BeginInvoke(new Action(async ()=>
            {
                if(reconnecting)
                {
                    while(reconnecting && !exitRequested) await Task.Delay(100);
                }
                if(!restoring && !stopRequested && !exitRequested) await ReconnectProjectionAsync("Windows 电源恢复通知");
                else if(session==null && !restoring) RefreshDisplays();
            }));
        }
    }

    private void SetStatus(AppIconState iconState,string message)
    {
        var icon=AppIcons.Get(iconState); tray.Icon=icon; Icon=icon;
        tray.Text="TopViewport · "+message;
    }

    private void ReportProblem(string message)
    {
        stateLabel.Text=message;
        SetStatus(AppIconState.Error,"需要查看控制面板");
        Program.Log(message);
        if(!automatedTest && !Visible)
            tray.ShowBalloonTip(5000,"TopViewport",message.Length>220 ? message[..220] : message,ToolTipIcon.Warning);
    }

    private void SetControls(bool enabled)
    {
        physical.Enabled=virtualScreen.Enabled=bottomPixels.Enabled=hz.Enabled=install.Enabled=refresh.Enabled=remove.Enabled=enabled;
        start.Enabled=enabled && hotkeyRegistered && !exitRequested && physical.Items.Count>0 && virtualScreen.Items.Count>0;
    }

    private int RecoverHiddenWindows(IReadOnlyList<DisplayInfo>? displays=null,bool immediate=false,bool includeMinimized=false)
    {
        // Integration tests move only their explicitly supplied test windows.
        if(automatedTest || applied==null || session is not { Completed:false } ||
           restoring || starting || reconnecting || suspended || engine==null || engine.FramesPresented<1) return 0;
        try
        {
            displays ??= DisplayManager.ListDisplays(false);
            var source=displays.FirstOrDefault(d=>d.DeviceName==applied.VirtualDeviceName);
            if(source==null || !source.IsPrimary) return 0;
            var target=source.WorkArea==null
                ? new Rectangle(source.X,source.Y,source.Width,source.Height)
                : Rectangle.FromLTRB(source.WorkArea.Left,source.WorkArea.Top,source.WorkArea.Right,source.WorkArea.Bottom);
            var blocked=new Rectangle(applied.PhysicalBounds.Left,applied.PhysicalBounds.Top,applied.PhysicalBounds.Width,applied.PhysicalBounds.Height);
            var accessible=displays.Where(d=>d.DeviceName!=applied.VirtualDeviceName && d.DeviceName!=applied.PhysicalDeviceName)
                .Select(d=>new Rectangle(d.X,d.Y,d.Width,d.Height)).ToArray();
            int moved=windowPlacementGuard.RecoverWindows(target,blocked,accessible,immediate,includeMinimized);
            if(moved>0) Program.Log($"Moved {moved} inaccessible application window(s) into the visible virtual desktop.");
            return moved;
        }
        catch(Exception ex) { Program.Log("Window placement recovery: "+ex.Message); return 0; }
    }

    private void TickScreenshotCompatibility()
    {
        if(automatedTest || applied==null || session is not { Completed:false } || starting ||
           restoring || reconnecting || suspended || engine==null || engine.FramesPresented<1) return;
        try
        {
            var output=new Rectangle(applied.PhysicalBounds.Left,applied.PhysicalBounds.Top,applied.PhysicalBounds.Width,applied.PhysicalBounds.Height);
            if(ScreenClipCompatibility.TryGetActiveOverlay(output,out _))
            {
                if(!screenClipPointerMode)
                {
                    var source=DisplayManager.ListDisplays(false).FirstOrDefault(d=>d.DeviceName==applied.VirtualDeviceName);
                    if(source==null || !source.IsPrimary) return;
                    EnterScreenClipPointerMode(new Rectangle(source.X,source.Y,source.Width,source.Height),output,activeMode.Height);
                    Program.Log("Windows screenshot overlay detected; pointer temporarily uses physical output coordinates.");
                }
            }
            else if(screenClipPointerMode && (Native.GetAsyncKeyState(1)&0x8000)==0 && (Native.GetAsyncKeyState(2)&0x8000)==0)
            {
                ExitScreenClipPointerMode();
                Program.Log("Windows screenshot overlay closed; pointer returned to virtual desktop coordinates.");
            }
        }
        catch(Exception ex)
        {
            Program.Log("Screenshot pointer recovery: "+ex);
            screenClipPointerMode=false;
            screenshotPointerTimer.Stop(); screenshotPointer?.Hide();
            Native.ClipCursor(IntPtr.Zero);
            _=FailAndStopAsync("截图鼠标区域未能正确恢复，已尝试恢复原显示布局。");
        }
    }

    private void EnterScreenClipPointerMode(Rectangle source,Rectangle output,int visibleHeight)
    {
        if(!Native.GetCursorPos(out var cursor) || !Native.GetClipCursor(out var original))
            throw new InvalidOperationException("无法读取截图前的鼠标区域。");
        screenClipSourceBounds=source;
        screenClipPhysicalBounds=output;
        screenClipVisibleHeight=Math.Clamp(visibleHeight,1,output.Height);
        var originalBounds=Rectangle.FromLTRB(original.Left,original.Top,original.Right,original.Bottom);
        screenClipPreviousRestriction=source.Contains(originalBounds) && originalBounds.Width>0 && originalBounds.Height>0
            ? originalBounds : source;
        screenClipPointerMode=true;
        var mapped=source.Contains(cursor) ? ScreenClipCompatibility.MapSourceToPhysical(cursor,source,output,screenClipVisibleHeight)
            : new Point(Math.Clamp(cursor.X,output.Left,output.Right-1),Math.Clamp(cursor.Y,output.Top,output.Top+screenClipVisibleHeight-1));
        Native.ClipCursor(IntPtr.Zero);
        if(!Native.SetCursorPos(mapped.X,mapped.Y)) throw new InvalidOperationException("无法切换截图鼠标坐标。");
        var physicalRestriction=new Native.Rect(new Rectangle(output.Left,output.Top,output.Width,screenClipVisibleHeight));
        if(!Native.ClipCursor(ref physicalRestriction)) throw new InvalidOperationException("无法设置截图选区鼠标范围。");
        screenshotPointer ??= new ScreenshotCursorOverlay();
        screenshotPointer.ShowAt(mapped);
        screenshotPointerTimer.Start();
    }

    private void ExitScreenClipPointerMode()
    {
        if(!screenClipPointerMode) return;
        screenshotPointerTimer.Stop(); screenshotPointer?.Hide();
        if(!Native.GetCursorPos(out var cursor)) throw new InvalidOperationException("无法读取截图结束的鼠标位置。");
        var source=screenClipSourceBounds;
        var mapped=source.Contains(cursor) ? cursor
            : ScreenClipCompatibility.MapPhysicalToSource(cursor,source,screenClipPhysicalBounds,screenClipVisibleHeight);
        Native.ClipCursor(IntPtr.Zero);
        if(!Native.SetCursorPos(mapped.X,mapped.Y)) throw new InvalidOperationException("无法恢复桌面鼠标坐标。");
        var restriction=new Native.Rect(screenClipPreviousRestriction);
        if(!Native.ClipCursor(ref restriction)) throw new InvalidOperationException("无法恢复桌面鼠标区域。");
        screenClipPointerMode=false;
        lastFrameAt=Recovery.AwakeNowTicks;
    }

    public async void RunTimedTest(string folder,bool powerCycle=false)
    {
        automatedTest=true;
        Directory.CreateDirectory(folder);
        string output=Path.Combine(folder,powerCycle ? "power-test-success.json" : "live-test-success.json");
        if(File.Exists(output)) File.Delete(output);
        var original=DisplayManager.ListDisplays(false).ToArray();
        await StartProjectionAsync();
        if(powerCycle)
        {
            try
            {
                await WaitForFramesAsync(); Confirm();
                WindowState=FormWindowState.Minimized;
                for(int cycle=0;cycle<2;cycle++)
                {
                    long before=reconnectAttempts;
                    SimulatePowerNotification(4);
                    if(Recovery.Read(Program.SessionPath)?.Suspended!=true) throw new InvalidOperationException("挂起状态未保存。");
                    await Task.Delay(1100);
                    SimulatePowerNotification(0x12);
                    long deadline=Recovery.AwakeNowTicks+50*TimeSpan.TicksPerSecond;
                    while(reconnectAttempts==before && Recovery.AwakeNowTicks<deadline && session is { Completed:false }) await Task.Delay(100);
                    if(reconnectAttempts!=before+1 || session is not { Confirmed:true,Completed:false } || engine?.FramesPresented<1)
                        throw new InvalidOperationException("模拟唤醒后未自动恢复投射。");
                    if(Visible) throw new InvalidOperationException("唤醒后托盘窗口状态改变。");
                }
                SimulatePowerNotification(4);
                await Task.Delay(150);
                SimulatePowerNotification(0x12);
                while(!reconnecting && session is { Completed:false }) await Task.Delay(50);
                // A tray restore/exit must stay responsive while wake recovery is in progress.
                await StopProjectionAsync();
                if(session!=null || !start.Enabled || !refresh.Enabled) throw new InvalidOperationException("恢复后按钮未重新启用。");
                await StartProjectionAsync(); await WaitForFramesAsync(); Confirm();
                if(session is not { Confirmed:true,Completed:false }) throw new InvalidOperationException("同进程重新启用失败。");
            }
            catch(Exception ex)
            {
                Program.Log(ex.ToString()); await StopProjectionAsync();
                File.WriteAllText(Path.Combine(folder,"power-test-failure.txt"),ex.ToString()); Close(); return;
            }
        }
        var liveTimer=new System.Windows.Forms.Timer { Interval=8000 };
        liveTimer.Tick+=async (_,_)=>
        {
            liveTimer.Stop();
            try
            {
                if(session==null || session.Completed || engine==null || applied==null || engine.FramesPresented<5)
                    throw new InvalidOperationException("整桌面投射未成功启动。");
                long frames=engine.FramesPresented;
                string status=engine.Status;
                var liveLayout=DisplayManager.ListDisplays(false).ToArray();
                var size=RequestedMode();
                var taskbar=Native.FindWindow("Shell_TrayWnd",null);
                bool taskbarOnVirtualPrimary=taskbar!=IntPtr.Zero && Native.MonitorFromWindow(taskbar,2)==Native.MonitorFromPoint(new Point(size.Width/2,size.Height/2),2);
                if(!taskbarOnVirtualPrimary) throw new InvalidOperationException("Windows 主任务栏尚未进入虚拟主屏，停止测试并恢复。");
                if(Native.GetWindowRect(taskbar,out var bar))
                {
                    var exposed=Rectangle.Intersect(new Rectangle(0,0,size.Width,size.Height),Rectangle.FromLTRB(bar.Left,bar.Top,bar.Right,bar.Bottom));
                    if(exposed.Width>0 && exposed.Height>0) Native.SetCursorPos(exposed.Left+exposed.Width/2,exposed.Top+exposed.Height/2);
                }
                await Task.Delay(1000); // Works for top/bottom/side auto-hidden taskbars as well.
                string screenshot=Path.Combine(folder,"private-live-output.bmp");
                var capture=engine;
                await Task.Run(()=>capture.SaveSnapshotBmp(screenshot));
                using(var pixels=new Bitmap(screenshot))
                {
                    for(int y=size.Height+2;y<pixels.Height;y+=12)
                    for(int x=8;x<pixels.Width;x+=64)
                    {
                        Color c=pixels.GetPixel(x,y);
                        if(c.R>2 || c.G>2 || c.B>2) throw new InvalidOperationException("实体输出的预留区域不是黑色。");
                    }
                }
                File.Delete(screenshot); // The desktop itself is never included in the deliverable.
                await StopProjectionAsync();
                var restored=DisplayManager.ListDisplays(false).ToArray();
                bool restoredCorrectly=original.All(a=>restored.Any(b=>a.DeviceName==b.DeviceName && a.IsPrimary==b.IsPrimary && a.X==b.X && a.Y==b.Y && a.Width==b.Width && a.Height==b.Height && a.RefreshRate==b.RefreshRate)) && original.Length==restored.Length;
                if(!restoredCorrectly) throw new InvalidOperationException("测试后布局未精确恢复，请使用恢复按钮。");
                File.WriteAllText(output,JsonSerializer.Serialize(new { Success=true,SimulatedPowerNotifications=powerCycle,PowerCycles=powerCycle ? 2 : 0,RestoreDuringReconnect=powerCycle,ReenableInSameProcess=powerCycle,Frames=frames,Status=status,VisibleWidth=size.Width,VisibleHeight=size.Height,BottomBlackVerified=true,TaskbarOnVirtualPrimary=taskbarOnVirtualPrimary,LayoutRestored=true,LiveDisplays=liveLayout,RestoredDisplays=restored },Program.JsonOptions));
            }
            catch(Exception ex) { Program.Log(ex.ToString()); await StopProjectionAsync(); File.WriteAllText(Path.Combine(folder,powerCycle ? "power-test-failure.txt" : "live-test-failure.txt"),ex.ToString()); }
            finally { liveTimer.Dispose(); Close(); }
        };
        liveTimer.Start();
    }
    private async Task WaitForFramesAsync()
    {
        long deadline=Recovery.AwakeNowTicks+8*TimeSpan.TicksPerSecond;
        while(engine?.FramesPresented==0 && Recovery.AwakeNowTicks<deadline) await Task.Delay(100);
        if(engine==null || !engine.IsRunning || engine.FramesPresented<1) throw new InvalidOperationException("投射未产生画面。");
    }
    public async void RunWindowTest(string folder)
    {
        automatedTest=true;
        Directory.CreateDirectory(folder);
        string success=Path.Combine(folder,"window-test-success.json");
        string failure=Path.Combine(folder,"window-test-failure.txt");
        if(File.Exists(success)) File.Delete(success);
        if(File.Exists(failure)) File.Delete(failure);
        try
        {
            await StartProjectionAsync(); await WaitForFramesAsync(); Confirm();
            if(session is not { Confirmed:true,Completed:false } || applied==null)
                throw new InvalidOperationException("窗口找回测试未成功启用虚拟主屏。");
            var displays=DisplayManager.ListDisplays(false);
            var source=displays.Single(d=>d.DeviceName==applied.VirtualDeviceName);
            var sourceBounds=new Rectangle(source.X,source.Y,source.Width,source.Height);
            var work=source.WorkArea;
            var targetArea=work==null ? sourceBounds : Rectangle.FromLTRB(work.Left,work.Top,work.Right,work.Bottom);
            var blocked=new Rectangle(applied.PhysicalBounds.Left,applied.PhysicalBounds.Top,applied.PhysicalBounds.Width,applied.PhysicalBounds.Height);
            var others=displays.Where(d=>d.DeviceName!=applied.VirtualDeviceName && d.DeviceName!=applied.PhysicalDeviceName)
                .Select(d=>new Rectangle(d.X,d.Y,d.Width,d.Height)).ToArray();
            WindowPlacementGuardTests.VerifyOwnedWindows(targetArea,blocked,others);
            Native.SetCursorPos(sourceBounds.Left+sourceBounds.Width/3,sourceBounds.Top+sourceBounds.Height/3);
            if(!Native.GetCursorPos(out var pointerBefore)) throw new InvalidOperationException("无法读取截图兼容测试坐标。");
            EnterScreenClipPointerMode(sourceBounds,blocked,activeMode.Height);
            if(!screenClipPointerMode || !Native.GetCursorPos(out var during) || !blocked.Contains(during))
                throw new InvalidOperationException("截图兼容模式未切换到实体输出坐标。");
            // The real session timer must leave the temporary screenshot restriction intact.
            TickSession();
            if(!Native.GetClipCursor(out var duringRestriction) || duringRestriction.Top!=blocked.Top)
                throw new InvalidOperationException("桌面心跳覆盖了截图期间的鼠标区域。");
            ExitScreenClipPointerMode();
            if(screenClipPointerMode || !Native.GetCursorPos(out var after) ||
                Math.Abs(after.X-pointerBefore.X)>2 || Math.Abs(after.Y-pointerBefore.Y)>2)
                throw new InvalidOperationException("截图结束后未恢复原鼠标位置。");
            if(!Native.GetClipCursor(out var restoredRestriction) || restoredRestriction.Top!=sourceBounds.Top || restoredRestriction.Bottom>sourceBounds.Bottom)
                throw new InvalidOperationException("截图结束后鼠标区域未恢复虚拟主屏。");
            await StopProjectionAsync();
            File.WriteAllText(success,JsonSerializer.Serialize(new
            {
                Success=true,OnlyOwnedTestWindows=true,NormalWindowRecovered=true,MaximizedStatePreserved=true,
                MinimizedWindowsNotOpened=true,NoForegroundActivation=true,AdditionalMonitorWindowTested=others.Length>0,
                ScreenshotPointerRoundTrip=true,ScreenshotRestrictionPreservedDuringHeartbeat=true,
                RealSystemScreenshotTested=false,LayoutRestored=session==null
            },Program.JsonOptions));
        }
        catch(Exception ex)
        {
            Program.Log(ex.ToString()); await StopProjectionAsync(); File.WriteAllText(failure,ex.ToString());
        }
        finally { Close(); }
    }

    public async void RunTrayTest(string folder)
    {
        automatedTest=true;
        Directory.CreateDirectory(folder);
        string success=Path.Combine(folder,"tray-test-success.json");
        string failure=Path.Combine(folder,"tray-test-failure.txt");
        if(File.Exists(success)) File.Delete(success);
        if(File.Exists(failure)) File.Delete(failure);
        try
        {
            ShowControlPanel();
            IntPtr originalHandle=Handle;
            TrayRestoreTests.VerifyRoundTrips(this,GetControlPanelWorkingArea());
            await StartProjectionAsync();
            await WaitForFramesAsync();
            Confirm();
            if(session is not { Confirmed:true,Completed:false } || applied==null)
                throw new InvalidOperationException("托盘测试未启用虚拟主屏。");
            var virtualArea=GetControlPanelWorkingArea();
            TrayRestoreTests.VerifyRoundTrips(this,virtualArea);

            WindowState=FormWindowState.Minimized;
            await Task.Delay(100);
            if(Visible) throw new InvalidOperationException("控制面板未隐藏到托盘。");
            var reconnect=ReconnectProjectionAsync("托盘恢复回归测试");
            await Task.Delay(100);
            ShowControlPanel();
            TrayRestoreTests.AssertVisibleInArea(this,GetControlPanelWorkingArea(),originalHandle,"open during reconnect");
            await reconnect;
            await WaitForFramesAsync();
            if(session is not { Confirmed:true,Completed:false }) throw new InvalidOperationException("重连后会话未保留。");
            TrayRestoreTests.VerifyRoundTrips(this,GetControlPanelWorkingArea());
            if(!hotkeyRegistered || Handle!=originalHandle) throw new InvalidOperationException("托盘恢复丢失了紧急恢复快捷键的窗口句柄。");
            await StopProjectionAsync();
            ShowControlPanel();
            TrayRestoreTests.VerifyRoundTrips(this,GetControlPanelWorkingArea());
            File.WriteAllText(success,JsonSerializer.Serialize(new
            {
                Success=true,IdleRoundTrips=true,ActiveProjectionRoundTrips=true,
                CorruptMinimizedPosition=true,PhysicalMonitorPosition=true,
                OpenDuringReconnect=true,OpenAfterReconnect=true,
                HwndPreserved=true,NativeVisibleAndNotIconic=true,
                HotkeyRegistrationPreserved=hotkeyRegistered,VirtualWorkingArea=virtualArea,
                LayoutRestored=session==null
            },Program.JsonOptions));
        }
        catch(Exception ex)
        {
            Program.Log(ex.ToString());
            await StopProjectionAsync();
            File.WriteAllText(failure,ex.ToString());
        }
        finally { Close(); }
    }
    private void SimulatePowerNotification(int type)
    {
        var message=Message.Create(Handle,0x0218,new IntPtr(type),IntPtr.Zero);
        WndProc(ref message);
    }

    public async void RunShutdownTest(string folder)
    {
        automatedTest=true;
        Directory.CreateDirectory(folder);
        string success=Path.Combine(folder,"shutdown-test-success.json");
        string failure=Path.Combine(folder,"shutdown-test-failure.txt");
        if(File.Exists(success)) File.Delete(success);
        if(File.Exists(failure)) File.Delete(failure);
        try
        {
            string savedBefore=DisplayManager.GetPersistentLayoutSignature();
            await StartProjectionAsync(); await WaitForFramesAsync(); Confirm();
            if(session is not { Confirmed:true,Completed:false }) throw new InvalidOperationException("关机恢复测试未启用投射。");
            if(savedBefore!=DisplayManager.GetPersistentLayoutSignature()) throw new InvalidOperationException("临时投射改变了 Windows 保存的启动布局。");
            var query=Message.Create(Handle,0x0011,IntPtr.Zero,IntPtr.Zero);
            WndProc(ref query);
            if(!sessionEnding || query.Result!=new IntPtr(1)) throw new InvalidOperationException("关机会话门控失败。");
            var cancel=Message.Create(Handle,0x0016,IntPtr.Zero,IntPtr.Zero);
            WndProc(ref cancel);
            await StopProjectionAsync();
            if(session!=null || sessionEnding) throw new InvalidOperationException("取消关机后没有恢复安全空闲状态。");
            await StartProjectionAsync(); await WaitForFramesAsync(); Confirm();
            if(savedBefore!=DisplayManager.GetPersistentLayoutSignature()) throw new InvalidOperationException("重新启用改变了保存布局。");
            RestoreForSessionEnd(); // Exercise the real handler body without ending Windows.
            if(session is not { Completed:true }) throw new InvalidOperationException("关机恢复未在返回前完成。");
            if(DisplayManager.ListDisplays(false).Any(d=>d.IsPrimary && d.IsLikelyVirtual)) throw new InvalidOperationException("同步关机恢复仍留下虚拟主屏。");
            sessionEnding=false;
            await StopProjectionAsync();
            File.WriteAllText(success,JsonSerializer.Serialize(new
            {
                Success=true,TemporaryProjectionPreservedSavedLayout=true,QueryEndSessionGated=true,
                CanceledShutdownRestoredIdle=true,SynchronousEndSessionRecovery=true,ReenableAfterCancel=true,
                LayoutRestored=session==null,RealSystemRebootTested=false
            },Program.JsonOptions));
        }
        catch(Exception ex)
        {
            Program.Log(ex.ToString()); sessionEnding=false;
            await StopProjectionAsync(); File.WriteAllText(failure,ex.ToString());
        }
        finally { sessionEnding=false; Close(); }
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x0011) // WM_QUERYENDSESSION: prevent any queued enable/reconnect.
        {
            sessionEnding=true; stopRequested=true;
            reconnectCancellation?.Cancel();
            m.Result=new IntPtr(1);
            return;
        }
        if(m.Msg==0x0016) // WM_ENDSESSION: no async cleanup after returning.
        {
            if(m.WParam!=IntPtr.Zero)
            {
                RestoreForSessionEnd();
                allowClose=true;
                base.WndProc(ref m);
            }
            else
            {
                sessionEnding=false;
                _=StopProjectionAsync(); // A canceled shutdown leaves a safe, idle desktop.
            }
            return;
        }
        if(m.Msg==0x0312 && m.WParam.ToInt32()==5512) { _=StopProjectionAsync(); return; }
        if(m.Msg==0x0218)
        {
            try { HandlePowerNotification(m.WParam.ToInt32()); }
            catch(Exception ex) { Program.Log("Power notification failed: "+ex); }
        }
        base.WndProc(ref m);
    }

    private void RestoreForSessionEnd()
    {
        sessionEnding=true; stopRequested=true;
        reconnectCancellation?.Cancel();
        timer.Stop(); screenshotTimer.Stop(); screenshotPointerTimer.Stop();
        screenshotPointer?.Hide(); engine?.RequestStop();
        Native.ClipCursor(IntPtr.Zero);
        // GPU teardown can wait; display recovery must finish in this handler.
        if(session is { Completed:false })
        {
            bool restored=Recovery.Restore(session,Program.SessionPath,2000);
            Program.Log("Windows session ending: synchronous display recovery="+restored);
        }
        projection?.Hide();
    }
    private sealed record DisplayChoice(DisplayInfo Info)
    {
        public override string ToString()=>$"{(string.IsNullOrWhiteSpace(Info.MonitorName)?Info.AdapterName:Info.MonitorName)} · {Info.DeviceName} · {(Info.IsActive?$"{Info.Width}×{Info.Height}":"未启用")}";
    }
}
