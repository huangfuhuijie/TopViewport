using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TopViewport;

internal static class ScreenshotCursorOverlayTests
{
    internal static void AddChecks(Action<string,Action> check)
    {
        check("Screenshot pointer marker stays click-through without taking focus or changing cursor restrictions",()=>
        {
            IntPtr foreground=GetForegroundWindow();
            if(!GetClipCursor(out var before)) throw new InvalidOperationException("Cannot read cursor restriction before marker test.");
            var screen=Screen.PrimaryScreen?.Bounds??new Rectangle(0,0,1920,1080);
            var point=new Point(screen.Left+screen.Width/2,screen.Top+screen.Height/2);
            using var marker=new ScreenshotCursorOverlay();
            marker.ShowAt(point);
            Application.DoEvents();
            long styles=GetWindowLongPtr(marker.Handle,-20).ToInt64();
            const long expected=0x00080000|0x00000020|0x08000000|0x00000080;
            Expect((styles&expected)==expected,"layered, transparent, no-activate and tool window styles");
            Expect(GetForegroundWindow()==foreground,"show must not activate marker");
            Expect(SendMessage(marker.Handle,0x0084,IntPtr.Zero,IntPtr.Zero)==new IntPtr(-1),"hit tests pass through marker");
            Expect(SendMessage(marker.Handle,0x0021,IntPtr.Zero,IntPtr.Zero)==new IntPtr(3),"mouse activation is rejected");
            Expect(marker.Visible && marker.Bounds.Contains(point),"marker is centered at requested screen coordinate");
            marker.MoveTo(new Point(point.X+11,point.Y-7));
            Expect(marker.LastScreenPosition==new Point(point.X+11,point.Y-7),"move follows screen coordinate");
            Expect(marker.Location==new Point(point.X+11-ScreenshotCursorOverlay.MarkerSize/2,point.Y-7-ScreenshotCursorOverlay.MarkerSize/2),"marker window follows without cursor mutation");
            marker.Hide();
            Expect(!marker.Visible,"marker hides after clipping ends");
            Expect(GetForegroundWindow()==foreground,"move/hide must preserve foreground focus");
            if(!GetClipCursor(out var after)) throw new InvalidOperationException("Cannot read cursor restriction after marker test.");
            Expect(before.Equals(after),"marker must not change cursor confinement");
        });
    }

    private static void Expect(bool condition,string detail)
    {
        if(!condition) throw new InvalidOperationException("Screenshot pointer marker check failed: "+detail);
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out CursorRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
}
