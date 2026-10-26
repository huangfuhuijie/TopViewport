using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TopViewport;

/// <summary>
/// A physical-monitor pointer marker used only during Windows screen clipping.
/// It never owns input, changes the system cursor, or reads the desktop image.
/// </summary>
internal sealed class ScreenshotCursorOverlay:Form
{
    internal const int MarkerSize=32;
    internal bool CaptureExclusionApplied { get; private set; }
    internal Point LastScreenPosition { get; private set; }

    internal ScreenshotCursorOverlay()
    {
        FormBorderStyle=FormBorderStyle.None;
        StartPosition=FormStartPosition.Manual;
        ShowInTaskbar=false;
        TopMost=true;
        AutoScaleMode=AutoScaleMode.None;
        ClientSize=new Size(MarkerSize,MarkerSize);
        BackColor=Color.Magenta;
        TransparencyKey=Color.Magenta;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
    }

    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters=base.CreateParams;
            // Layered + transparent sends mouse hit tests through this window;
            // NOACTIVATE also prevents focus changes on show or pointer clicks.
            parameters.ExStyle|=0x00080000|0x00000020|0x08000000|0x00000080;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 10 2004+ can leave this marker out of the selected image.
        // Older builds or unsupported capture paths may reject the request.
        CaptureExclusionApplied=SetWindowDisplayAffinity(Handle,0x11);
    }

    internal void ShowAt(Point screenPosition)
    {
        if(IsDisposed) return;
        MoveTo(screenPosition);
        if(!Visible) Show();
        PositionWithoutActivation(screenPosition);
    }

    internal void MoveTo(Point screenPosition)
    {
        if(IsDisposed) return;
        LastScreenPosition=screenPosition;
        if(IsHandleCreated) PositionWithoutActivation(screenPosition);
        else Location=new Point(screenPosition.X-MarkerSize/2,screenPosition.Y-MarkerSize/2);
    }

    private void PositionWithoutActivation(Point screenPosition)
    {
        SetWindowPos(Handle,new IntPtr(-1),screenPosition.X-MarkerSize/2,screenPosition.Y-MarkerSize/2,
            MarkerSize,MarkerSize,0x0010|0x0200); // NOACTIVATE | NOOWNERZORDER
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode=SmoothingMode.None;
        using var outline=new Pen(Color.Black,3);
        using var center=new Pen(Color.White,1);
        int middle=MarkerSize/2;
        e.Graphics.DrawLine(outline,3,middle,MarkerSize-4,middle);
        e.Graphics.DrawLine(outline,middle,3,middle,MarkerSize-4);
        e.Graphics.DrawLine(center,3,middle,MarkerSize-4,middle);
        e.Graphics.DrawLine(center,middle,3,middle,MarkerSize-4);
    }

    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x0084) { message.Result=new IntPtr(-1); return; } // NCHITTEST -> HTTRANSPARENT
        if(message.Msg==0x0021) { message.Result=new IntPtr(3); return; } // MOUSEACTIVATE -> MA_NOACTIVATE
        base.WndProc(ref message);
    }

    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowDisplayAffinity(IntPtr window,uint affinity);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(IntPtr window,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
}
