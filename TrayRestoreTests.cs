using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace TopViewport;

// Exercises only the supplied app window. It never changes the display layout,
// power state, or another process's windows. Call after the control panel is shown.
internal static class TrayRestoreTests
{
    internal static void VerifyRoundTrips(MainForm form, Rectangle usableArea, int cycles=3)
    {
        IntPtr originalHandle=form.Handle;
        for(int cycle=0;cycle<cycles;cycle++)
        {
            form.WindowState=FormWindowState.Minimized;
            Pump(100);
            if(form.Visible) throw new InvalidOperationException("Minimizing did not hide the control panel to the tray.");
            Restore(form);
            Pump(100);
            AssertVisibleInArea(form,usableArea,originalHandle,"tray round trip "+cycle);
        }

        // Windows uses -32000 for minimized windows. A corrupted normal position
        // must not be reused as the location of a restored control panel.
        form.Location=new Point(-32000,-32000);
        form.WindowState=FormWindowState.Minimized;
        Pump(100);
        Restore(form);
        Pump(100);
        AssertVisibleInArea(form,usableArea,originalHandle,"off-screen restore position");

        // The output monitor is below the virtual primary. Restoring on that
        // monitor would put the panel behind the topmost projection surface.
        form.Location=new Point(usableArea.Left+30,usableArea.Bottom+30);
        form.WindowState=FormWindowState.Minimized;
        Pump(100);
        Restore(form);
        Pump(100);
        AssertVisibleInArea(form,usableArea,originalHandle,"wrong-monitor restore position");
    }

    private static void Restore(MainForm form)
    {
        var method=typeof(MainForm).GetMethod("ShowControlPanel",BindingFlags.Instance|BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Control-panel tray action was not found.");
        try { method.Invoke(form,null); }
        catch(TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    internal static void AssertVisibleInArea(MainForm form,Rectangle usableArea,IntPtr originalHandle,string scenario)
    {
        if(!form.Visible || form.WindowState!=FormWindowState.Normal)
            throw new InvalidOperationException(scenario+": control panel is not shown normally.");
        if(!Native.IsWindowVisible(form.Handle) || Native.IsIconic(form.Handle))
            throw new InvalidOperationException(scenario+": native window is hidden or still minimized.");
        if(form.Handle!=originalHandle)
            throw new InvalidOperationException(scenario+": HWND was recreated, invalidating the registered recovery hotkey.");
        if(!Native.GetWindowRect(form.Handle,out var native))
            throw new InvalidOperationException(scenario+": failed to read the restored window bounds.");
        var actual=Rectangle.FromLTRB(native.Left,native.Top,native.Right,native.Bottom);
        // At a normal desktop resolution the entire panel should be reachable.
        // If the screen is smaller than the window minimum, require the title bar
        // and at least 100 pixels to remain visible so the panel can be moved.
        if(actual.Width<=usableArea.Width && actual.Height<=usableArea.Height)
        {
            if(!usableArea.Contains(actual))
                throw new InvalidOperationException(scenario+": panel is outside the usable screen: "+actual+"; expected "+usableArea);
        }
        else if(actual.Top<usableArea.Top || actual.Top>usableArea.Bottom-100 ||
                actual.Left<usableArea.Left || actual.Left>usableArea.Right-100)
            throw new InvalidOperationException(scenario+": panel title bar is unreachable.");
    }

    private static void Pump(int milliseconds)
    {
        long deadline=Environment.TickCount64+milliseconds;
        while(Environment.TickCount64<deadline) { Application.DoEvents(); Thread.Sleep(5); }
    }
}
