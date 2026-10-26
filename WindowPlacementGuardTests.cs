using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using GN=TopViewport.WindowPlacementGuard.GuardNative;

namespace TopViewport;

internal static class WindowPlacementGuardTests
{
    internal static void AddChecks(Action<string,Action> check)
    {
        var target=new Rectangle(0,40,1920,940);
        var output=new Rectangle(0,1020,1920,1080);
        var other=new Rectangle(1920,0,1920,1080);
        check("Covered physical output windows are recovered, usable virtual windows are untouched",()=>
        {
            Require(WindowPlacementGuard.NeedsRecovery(new Rectangle(80,1120,900,600),target,output,Array.Empty<Rectangle>()),"Covered window was not selected");
            Require(!WindowPlacementGuard.NeedsRecovery(new Rectangle(80,120,900,600),target,output,Array.Empty<Rectangle>()),"Visible virtual window was selected");
            Require(!WindowPlacementGuard.NeedsRecovery(new Rectangle(80,900,900,600),target,output,Array.Empty<Rectangle>()),"Partly visible virtual window was selected");
        });
        check("Window recovery never takes a window from an additional usable monitor",()=>
        {
            Require(!WindowPlacementGuard.NeedsRecovery(new Rectangle(2020,120,900,600),target,output,new[]{other}),"Other real-monitor window was selected");
            Require(!WindowPlacementGuard.NeedsRecovery(new Rectangle(1850,1120,900,600),target,output,new[]{new Rectangle(1920,1080,1920,1080)}),"Straddling real-monitor window was selected");
        });
        check("Window recovery fits oversized and fully off-desktop remembered bounds",()=>
        {
            Require(WindowPlacementGuard.NeedsRecovery(new Rectangle(-10000,8000,900,600),target,output,Array.Empty<Rectangle>()),"Offscreen window was not selected");
            Require(target.Contains(WindowPlacementGuard.Fit(new Rectangle(20,1120,2400,1600),target,output)),"Oversized rescue did not fit");
        });
        check("Window placement workspace conversion handles top and left taskbars without creep",()=>
        {
            var monitor=new Rectangle(-1920,100,1920,1080);
            var work=new Rectangle(-1880,132,1880,1048);
            var screen=new Rectangle(-1740,200,800,600);
            var workspace=WindowPlacementGuard.ScreenToWorkspace(screen,monitor,work);
            Require(workspace.Location==new Point(-1780,168),"Workspace offset did not subtract taskbar edges");
            Require(WindowPlacementGuard.WorkspaceToScreen(workspace,monitor,work)==screen,"Workspace round trip crept");
        });
        check("Recovery of a self-created window preserves visibility, activation and minimized state",()=>
        {
            var area=Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0,0,1920,1040);
            var fakeBlocked=new Rectangle(area.Right+200,area.Bottom+200,1200,900);
            VerifyNormalAndMinimized(area,fakeBlocked);
        });
        check("Window recovery excludes tool, topmost, owned, own-process and borderless fullscreen windows",()=>
        {
            var area=Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0,0,1920,1040);
            var blocked=new Rectangle(area.Right+200,area.Bottom+200,1200,900);
            var guard=new WindowPlacementGuard(excludedProcessId:0,settleMilliseconds:0);
            using var parent=new TestWindow();
            parent.Bounds=new Rectangle(area.Left+20,area.Top+20,400,250); parent.Show(); Pump(40);
            using var ownWindow=new TestWindow();
            ownWindow.Show(); SetBounds(ownWindow,blocked.Left+30,blocked.Top+40,500,300); Pump(40);
            Require(new WindowPlacementGuard().RecoverHandles(new[]{ownWindow.Handle},area,blocked,Array.Empty<Rectangle>(),true,true)==0,"Own process window was selected");
            ownWindow.Hide();
            foreach(var kind in new[]{"tool","topmost","owned","fullscreen","noactivate"})
            {
                using TestWindow excluded=kind=="noactivate" ? new NoActivateWindow() : new TestWindow();
                if(kind=="tool") excluded.FormBorderStyle=FormBorderStyle.FixedToolWindow;
                if(kind=="topmost") excluded.TopMost=true;
                if(kind=="owned") excluded.Owner=parent;
                if(kind=="fullscreen") excluded.FormBorderStyle=FormBorderStyle.None;
                excluded.Show(); SetBounds(excluded,blocked.Left+40,blocked.Top+50,600,400); Pump(40);
                Require(guard.RecoverHandles(new[]{excluded.Handle},area,blocked,Array.Empty<Rectangle>(),true,true)==0,"Excluded "+kind+" window was selected");
                excluded.Close();
            }
            ownWindow.Close(); parent.Close();
        });
    }

    // Call only inside the app's integration-test process after its own topology
    // is enabled. This method only passes explicitly created test HWNDs.
    internal static void VerifyOwnedWindows(Rectangle targetWorkArea,Rectangle blockedPhysicalBounds,
        IReadOnlyList<Rectangle> otherAccessibleMonitors)
    {
        VerifyNormalAndMinimized(targetWorkArea,blockedPhysicalBounds);
        var guard=new WindowPlacementGuard(excludedProcessId:0,settleMilliseconds:0);
        using var window=new TestWindow();
        window.Bounds=new Rectangle(blockedPhysicalBounds.Left+80,blockedPhysicalBounds.Top+80,700,450);
        window.Show(); Pump(100);
        SetBounds(window,blockedPhysicalBounds.Left+80,blockedPhysicalBounds.Top+80,700,450);
        window.WindowState=FormWindowState.Maximized; Pump(150);
        Require(GN.IsZoomed(window.Handle),"Maximized test window did not maximize");
        using var foregroundAnchor=new TestWindow();
        foregroundAnchor.Bounds=new Rectangle(targetWorkArea.Left+30,targetWorkArea.Top+40,320,180);
        foregroundAnchor.Show(); foregroundAnchor.Activate(); Pump(100);
        var originalForeground=GN.GetForegroundWindow();
        Require(originalForeground!=window.Handle,"Maximized test did not establish a separate foreground window");
        int recovered=guard.RecoverHandles(new[]{window.Handle},targetWorkArea,blockedPhysicalBounds,otherAccessibleMonitors,true,true);
        Pump(250);
        Require(recovered==1,"Maximized output window was not recovered");
        Require(GN.IsZoomed(window.Handle),"Recovery lost the maximized window state");
        Require(GN.GetForegroundWindow()==originalForeground,"Maximized recovery took foreground activation");
        Require(IntersectsUsably(window.Handle,targetWorkArea),"Maximized window did not reach virtual work area");
        GN.ShowWindowAsync(window.Handle,4); Pump(100);
        guard.Reset();
        guard.RecoverHandles(new[]{window.Handle},targetWorkArea,blockedPhysicalBounds,otherAccessibleMonitors,true,false);
        Pump(100);
        Require(IntersectsUsably(window.Handle,targetWorkArea),"Restoring the maximized window returned it to the blocked monitor");
        if(otherAccessibleMonitors.Count>0)
        {
            guard.Reset();
            var other=otherAccessibleMonitors[0];
            SetBounds(window,other.Left+20,other.Top+30,Math.Min(700,other.Width-30),Math.Min(450,other.Height-40));
            Pump(100);
            Require(guard.RecoverHandles(new[]{window.Handle},targetWorkArea,blockedPhysicalBounds,otherAccessibleMonitors,true,true)==0,
                "Another real-monitor test window was taken");
        }
        foregroundAnchor.Close();
        window.Close();
    }

    private static void VerifyNormalAndMinimized(Rectangle target,Rectangle blocked)
    {
        var guard=new WindowPlacementGuard(excludedProcessId:0,settleMilliseconds:0);
        using var window=new TestWindow();
        window.Bounds=new Rectangle(target.Left+20,target.Top+30,Math.Min(700,target.Width-40),Math.Min(450,target.Height-50));
        window.Show(); Pump(80);
        SetBounds(window,blocked.Left+80,blocked.Top+90,700,450); Pump(80);
        var foreground=GN.GetForegroundWindow();
        Require(guard.RecoverHandles(new[]{window.Handle},target,blocked,Array.Empty<Rectangle>(),true,true)==1,"Normal hidden window was not recovered");
        Pump(150);
        Require(IntersectsUsably(window.Handle,target),"Normal window did not reach work area");
        Require(GN.GetForegroundWindow()==foreground,"Normal recovery took foreground activation");

        guard.Reset();
        SetBounds(window,blocked.Left+60,blocked.Top+70,650,400); Pump(60);
        window.WindowState=FormWindowState.Minimized; Pump(80);
        foreground=GN.GetForegroundWindow();
        Require(guard.RecoverHandles(new[]{window.Handle},target,blocked,Array.Empty<Rectangle>(),true,false)==0,
            "Automatic scan unminimized a taskbar window");
        Require(guard.RecoverHandles(new[]{window.Handle},target,blocked,Array.Empty<Rectangle>(),true,true)==1,
            "Minimized restore placement was not repaired");
        Pump(150);
        Require(GN.IsIconic(window.Handle),"Repair unexpectedly opened a minimized taskbar window");
        Require(GN.GetForegroundWindow()==foreground,"Minimized recovery took foreground activation");
        var repairedPlacement=GN.Placement.Create(); GN.GetWindowPlacement(window.Handle,ref repairedPlacement);
        var restoredMonitor=new GN.MonitorInfo { Size=System.Runtime.InteropServices.Marshal.SizeOf<GN.MonitorInfo>() };
        GN.GetMonitorInfo(GN.MonitorFromWindow(window.Handle,2),ref restoredMonitor);
        var repairedScreenBounds=WindowPlacementGuard.WorkspaceToScreen(repairedPlacement.NormalPosition.ToRectangle(),
            restoredMonitor.Bounds.ToRectangle(),restoredMonitor.WorkArea.ToRectangle());
        Require(target.Contains(repairedScreenBounds),"Minimized native restore placement was not in the target work area");
        // Emulate the OS restoring its native placement. Assigning the managed
        // WinForms WindowState property deliberately reapplies WinForms' own
        // cached bounds and is a separate late self-positioning-app scenario.
        GN.ShowWindowAsync(window.Handle,4); Pump(150); // SW_SHOWNOACTIVATE
        // Applications may deliberately reapply their private cached restore
        // bounds during WM_SIZE. The next visible-window scan must repair that
        // late relocation without reopening or activating an unrelated window.
        guard.Reset();
        guard.RecoverHandles(new[]{window.Handle},target,blocked,Array.Empty<Rectangle>(),true,false);
        Pump(150);
        GN.GetWindowRect(window.Handle,out var reopenedRect);
        Require(IntersectsUsably(window.Handle,target),"Minimized window reopened on the blocked monitor; stored="+repairedPlacement.NormalPosition.ToRectangle()+"; actual="+reopenedRect.ToRectangle()+"; target="+target);

        guard.Reset();
        window.Hide();
        SetBounds(window,blocked.Left+50,blocked.Top+60,500,300);
        Require(guard.RecoverHandles(new[]{window.Handle},target,blocked,Array.Empty<Rectangle>(),true,true)==0,"Intentionally hidden window was opened");
        window.Close();
    }

    private static void SetBounds(Form window,int x,int y,int width,int height)
        => GN.SetWindowPos(window.Handle,IntPtr.Zero,x,y,width,height,0x10|0x4|0x200);
    private static bool IntersectsUsably(IntPtr hwnd,Rectangle area)
    {
        if(!GN.IsWindowVisible(hwnd) || GN.IsIconic(hwnd) || !GN.GetWindowRect(hwnd,out var rect)) return false;
        var visible=Rectangle.Intersect(rect.ToRectangle(),area);
        return visible.Width>=Math.Min(120,area.Width) && visible.Height>=Math.Min(80,area.Height);
    }
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static void Pump(int milliseconds)
    {
        long deadline=Environment.TickCount64+milliseconds;
        while(Environment.TickCount64<deadline){Application.DoEvents();Thread.Sleep(5);}
    }
    private class TestWindow : Form
    {
        public TestWindow()
        {
            Text="TopViewport window recovery test";
            StartPosition=FormStartPosition.Manual;ShowInTaskbar=true;
            BackColor=Color.FromArgb(36,97,150);
        }
        protected override bool ShowWithoutActivation=>true;
    }
    private sealed class NoActivateWindow : TestWindow
    {
        protected override CreateParams CreateParams
        { get {var parameters=base.CreateParams;parameters.ExStyle|=0x08000000;return parameters;} }
    }
}
