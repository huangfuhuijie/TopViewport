using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TopViewport;

/// <summary>
/// Returns ordinary application windows from the monitor covered by our
/// projection to the interactive virtual desktop. Never reads titles, URLs,
/// document contents, or captures pixels. Other usable monitors are protected.
/// </summary>
public sealed class WindowPlacementGuard
{
    private readonly int excludedProcessId;
    private readonly int settleMilliseconds;
    private readonly Dictionary<WindowIdentity, Observation> observations = new();
    private readonly Dictionary<WindowIdentity, long> recentlyMoved = new();
    private readonly record struct WindowIdentity(IntPtr Hwnd,uint Pid,uint ThreadId,string ClassName);
    private sealed record Observation(Rectangle Bounds,long StableSince);

    public WindowPlacementGuard(int excludedProcessId=-1,int settleMilliseconds=700)
    {
        this.excludedProcessId=excludedProcessId<0 ? Environment.ProcessId : excludedProcessId;
        this.settleMilliseconds=Math.Max(0,settleMilliseconds);
    }

    public void Reset() { observations.Clear(); recentlyMoved.Clear(); }

    public int RecoverWindows(Rectangle targetWorkArea,Rectangle blockedPhysicalBounds,
        IReadOnlyList<Rectangle> otherAccessibleMonitors,bool immediate=false,bool includeMinimized=false)
    {
        var handles=new List<IntPtr>();
        GuardNative.EnumWindows((hwnd,_)=> { handles.Add(hwnd); return true; },IntPtr.Zero);
        return RecoverHandles(handles,targetWorkArea,blockedPhysicalBounds,otherAccessibleMonitors,immediate,includeMinimized);
    }

    // Tests supply only their own windows; this path never enumerates user windows.
    internal int RecoverHandles(IEnumerable<IntPtr> handles,Rectangle targetWorkArea,Rectangle blockedPhysicalBounds,
        IReadOnlyList<Rectangle> otherAccessibleMonitors,bool immediate=false,bool includeMinimized=false)
    {
        if(targetWorkArea.Width<=0 || targetWorkArea.Height<=0 || blockedPhysicalBounds.Width<=0 || blockedPhysicalBounds.Height<=0)
            return 0;
        if((GuardNative.GetAsyncKeyState(1)&0x8000)!=0) return 0; // Do not fight a drag or a held click.
        long now=Environment.TickCount64;
        var seen=new HashSet<WindowIdentity>();
        int moved=0;
        foreach(var hwnd in handles)
        {
            if(!TryReadApplicationWindow(hwnd,includeMinimized,out var identity,out var bounds,out var placement,out bool minimized,out bool maximized)) continue;
            seen.Add(identity);
            if(!NeedsRecovery(bounds,targetWorkArea,blockedPhysicalBounds,otherAccessibleMonitors))
            { observations.Remove(identity); continue; }
            if(recentlyMoved.TryGetValue(identity,out long lastMove) && now-lastMove<3000) continue;
            if(!observations.TryGetValue(identity,out var observation) || observation.Bounds!=bounds)
            {
                observation=new Observation(bounds,now); observations[identity]=observation;
            }
            if(!immediate && now-observation.StableSince<settleMilliseconds) continue;

            // A destroyed/reused HWND must not redirect a stale recovery to another process.
            uint currentThread=GuardNative.GetWindowThreadProcessId(hwnd,out uint currentPid);
            if(currentPid!=identity.Pid || currentThread!=identity.ThreadId || !GuardNative.IsWindow(hwnd)) continue;
            var monitor=ReadMonitor(hwnd);
            var normal=WorkspaceToScreen(placement.NormalPosition.ToRectangle(),monitor.Bounds,monitor.WorkArea);
            if(normal.Width<=0 || normal.Height<=0 || normal.Left<=-30000 || normal.Top<=-30000)
                normal=new Rectangle(bounds.Location,new Size(Math.Min(960,targetWorkArea.Width),Math.Min(700,targetWorkArea.Height)));
            var fittedNormal=Fit(normal,targetWorkArea,blockedPhysicalBounds);
            var targetMonitor=ReadMonitor(targetWorkArea);
            placement.NormalPosition=new GuardNative.Rect(ScreenToWorkspace(fittedNormal,targetMonitor.Bounds,targetMonitor.WorkArea));
            placement.Flags|=0x4; // WPF_ASYNCWINDOWPLACEMENT: never wait on a hung application.
            placement.MinPosition=new GuardNative.Point(-1,-1);
            placement.MaxPosition=new GuardNative.Point(-1,-1);
            // SHOWNA leaves a visible normal/maximized window unactivated. The
            // minimized variant repairs its remembered restore position without
            // opening a taskbar item the user deliberately minimized.
            placement.ShowCmd=minimized ? 7u : 8u; // SW_SHOWMINNOACTIVE / SW_SHOWNA
            bool placed=GuardNative.SetWindowPlacement(hwnd,ref placement);
            if(!minimized)
            {
                Rectangle actualTarget=maximized ? targetWorkArea : Fit(bounds,targetWorkArea,blockedPhysicalBounds);
                placed=GuardNative.SetWindowPos(hwnd,IntPtr.Zero,actualTarget.X,actualTarget.Y,actualTarget.Width,actualTarget.Height,
                    0x10|0x4|0x200|0x4000) || placed; // NOACTIVATE|NOZORDER|NOOWNERZORDER|ASYNCWINDOWPOS
            }
            if(placed)
            {
                moved++;
                // A minimized app may overwrite native restore bounds when the
                // user opens it. Do not delay that later visible-window repair.
                if(!minimized) recentlyMoved[identity]=now;
                observations.Remove(identity);
            }
        }
        foreach(var key in observations.Keys.Where(k=>!seen.Contains(k)).ToArray()) observations.Remove(key);
        foreach(var key in recentlyMoved.Keys.Where(k=>!seen.Contains(k) || now-recentlyMoved[k]>30000).ToArray()) recentlyMoved.Remove(key);
        return moved;
    }

    internal static bool NeedsRecovery(Rectangle bounds,Rectangle targetWorkArea,Rectangle blockedPhysicalBounds,
        IReadOnlyList<Rectangle> otherAccessibleMonitors)
    {
        if(bounds.Width<=0 || bounds.Height<=0) return false;
        // Even a partially visible window on another real monitor is intentional.
        if(otherAccessibleMonitors.Any(other=>other.IntersectsWith(bounds))) return false;
        var visible=Rectangle.Intersect(bounds,targetWorkArea);
        if(visible.Width>=Math.Min(64,bounds.Width) && visible.Height>=Math.Min(32,bounds.Height)) return false;
        if(blockedPhysicalBounds.IntersectsWith(bounds)) return true;
        // A completely off-desktop remembered position is also inaccessible.
        return visible.Width<=0 || visible.Height<=0;
    }

    internal static Rectangle Fit(Rectangle bounds,Rectangle target,Rectangle output)
    {
        int width=Math.Max(1,Math.Min(bounds.Width,target.Width));
        int height=Math.Max(1,Math.Min(bounds.Height,target.Height));
        bool onOutput=output.IntersectsWith(bounds);
        long preferredX=onOutput ? (long)target.Left+bounds.Left-output.Left : bounds.Left;
        long preferredY=onOutput ? (long)target.Top+bounds.Top-output.Top : bounds.Top;
        return new Rectangle((int)Math.Clamp(preferredX,target.Left,(long)target.Right-width),
            (int)Math.Clamp(preferredY,target.Top,(long)target.Bottom-height),width,height);
    }

    internal static Rectangle WorkspaceToScreen(Rectangle bounds,Rectangle monitorBounds,Rectangle workArea)
        => new(bounds.X+workArea.Left-monitorBounds.Left,bounds.Y+workArea.Top-monitorBounds.Top,bounds.Width,bounds.Height);
    internal static Rectangle ScreenToWorkspace(Rectangle bounds,Rectangle monitorBounds,Rectangle workArea)
        => new(bounds.X-workArea.Left+monitorBounds.Left,bounds.Y-workArea.Top+monitorBounds.Top,bounds.Width,bounds.Height);

    private bool TryReadApplicationWindow(IntPtr hwnd,bool includeMinimized,out WindowIdentity identity,out Rectangle bounds,
        out GuardNative.Placement placement,out bool minimized,out bool maximized)
    {
        identity=default; bounds=default; placement=GuardNative.Placement.Create(); minimized=maximized=false;
        if(hwnd==IntPtr.Zero || !GuardNative.IsWindowVisible(hwnd)) return false;
        uint thread=GuardNative.GetWindowThreadProcessId(hwnd,out uint pid);
        if(pid==0 || pid==excludedProcessId) return false;
        long style=GuardNative.GetWindowLongPtr(hwnd,-16).ToInt64();
        long exStyle=GuardNative.GetWindowLongPtr(hwnd,-20).ToInt64();
        if((style&0x40000000L)!=0 || (exStyle&(0x80|0x08000000|0x8))!=0) return false; // CHILD, TOOLWINDOW, NOACTIVATE, TOPMOST
        var className=new StringBuilder(256);
        if(GuardNative.GetClassName(hwnd,className,className.Capacity)<=0) return false;
        string name=className.ToString();
        if(name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768" or "tooltips_class32" or "MultitaskingViewFrame") return false;
        // Borderless/exclusive fullscreen windows must keep their own display
        // choice. APPWINDOW alone is insufficient: games use it too.
        if((style&0x00C00000L)!=0x00C00000L && (style&0x40000L)==0) return false;
        if(GuardNative.DwmGetWindowAttribute(hwnd,14,out int cloaked,sizeof(int))!=0 || cloaked!=0) return false;
        IntPtr owner=GuardNative.GetWindow(hwnd,4);
        if(owner!=IntPtr.Zero) return false; // Owned dialogs/popups keep their application's policy.
        if(!GuardNative.GetWindowPlacement(hwnd,ref placement)) return false;
        minimized=GuardNative.IsIconic(hwnd); maximized=GuardNative.IsZoomed(hwnd);
        if(minimized && !includeMinimized) return false;
        if(minimized)
        {
            var monitor=ReadMonitor(hwnd);
            bounds=WorkspaceToScreen(placement.NormalPosition.ToRectangle(),monitor.Bounds,monitor.WorkArea);
        }
        else
        {
            if(!GuardNative.GetWindowRect(hwnd,out var rect)) return false;
            bounds=rect.ToRectangle();
        }
        identity=new WindowIdentity(hwnd,pid,thread,name);
        return bounds.Width>0 && bounds.Height>0;
    }

    private static (Rectangle Bounds,Rectangle WorkArea) ReadMonitor(IntPtr hwnd)
        => ReadMonitorInfo(GuardNative.MonitorFromWindow(hwnd,2));
    private static (Rectangle Bounds,Rectangle WorkArea) ReadMonitor(Rectangle rectangle)
    {
        var rect=new GuardNative.Rect(rectangle);
        return ReadMonitorInfo(GuardNative.MonitorFromRect(ref rect,2));
    }
    private static (Rectangle Bounds,Rectangle WorkArea) ReadMonitorInfo(IntPtr monitor)
    {
        var info=new GuardNative.MonitorInfo { Size=Marshal.SizeOf<GuardNative.MonitorInfo>() };
        if(monitor!=IntPtr.Zero && GuardNative.GetMonitorInfo(monitor,ref info))
            return (info.Bounds.ToRectangle(),info.WorkArea.ToRectangle());
        return (Rectangle.Empty,Rectangle.Empty);
    }

    internal static class GuardNative
    {
        internal delegate bool EnumWindowsProc(IntPtr hwnd,IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X,Y; public Point(int x,int y){X=x;Y=y;} }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public int Left,Top,Right,Bottom;
            public Rect(Rectangle r){Left=r.Left;Top=r.Top;Right=r.Right;Bottom=r.Bottom;}
            public Rectangle ToRectangle()=>Rectangle.FromLTRB(Left,Top,Right,Bottom);
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Placement
        {
            public int Length; public uint Flags,ShowCmd; public Point MinPosition,MaxPosition; public Rect NormalPosition;
            public static Placement Create()=>new() {Length=Marshal.SizeOf<Placement>()};
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size;public Rect Bounds,WorkArea;public uint Flags; }
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback,IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd,StringBuilder name,int count);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd,uint command);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd,out Rect rectangle);
        [DllImport("user32.dll")] internal static extern bool GetWindowPlacement(IntPtr hwnd,ref Placement placement);
        [DllImport("user32.dll")] internal static extern bool SetWindowPlacement(IntPtr hwnd,ref Placement placement);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromRect(ref Rect rectangle,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr hwnd,int command);
    }
}
