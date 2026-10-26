using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TopViewport;

internal static class ScreenClipCompatibilityTests
{
    internal static void AddChecks(Action<string,Action> check)
    {
        var source=new Rectangle(0,0,1920,1020);
        var physical=new Rectangle(0,1020,1920,1080);
        check("Screen clipping handoff preserves visible cursor coordinates and clamps the damaged strip",()=>
        {
            Expect(ScreenClipCompatibility.MapSourceToPhysical(new Point(350,250),source,physical,1020)==new Point(350,1270),"source to displayed cursor");
            Expect(ScreenClipCompatibility.MapPhysicalToSource(new Point(350,1270),source,physical,1020)==new Point(350,250),"displayed cursor back to source");
            Expect(ScreenClipCompatibility.MapPhysicalToSource(new Point(1919,2099),source,physical,1020)==new Point(1919,1019),"black strip clamps to last source row");
            Expect(ScreenClipCompatibility.MapSourceToPhysical(new Point(-100,-100),source,physical,1020)==new Point(0,1020),"source bounds clamp");
        });
        check("Screen clipping cursor mapping supports resizing, negative monitor origins and one-pixel regions",()=>
        {
            var smallSource=new Rectangle(-1280,20,1280,720);
            var output=new Rectangle(-1920,-1080,1920,1080);
            Expect(ScreenClipCompatibility.MapSourceToPhysical(smallSource.Location,smallSource,output,972)==output.Location,"negative-origin top corner");
            Expect(ScreenClipCompatibility.MapSourceToPhysical(new Point(-1,739),smallSource,output,972)==new Point(-1,-109),"scaled lower corner");
            Expect(ScreenClipCompatibility.MapPhysicalToSource(new Point(-1,-109),smallSource,output,972)==new Point(-1,739),"scaled reverse corner");
            var single=new Rectangle(5,6,1,1);
            Expect(ScreenClipCompatibility.MapSourceToPhysical(new Point(5,6),single,output,972)==output.Location,"single-pixel source avoids division by zero");
            bool rejected=false;
            try { ScreenClipCompatibility.MapSourceToPhysical(Point.Empty,source,physical,0); } catch(ArgumentOutOfRangeException) { rejected=true; }
            Expect(rejected,"invalid visible height fails safely");
        });
        check("Screen clipping requires a foreground-related visible trusted overlay covering the physical output",()=>
        {
            var active=new ScreenClipCompatibility.Candidate(physical,true,false,false,true,true,true);
            Expect(ScreenClipCompatibility.IsActiveOverlayCandidate(active,physical),"active physical overlay accepted");
            Expect(ScreenClipCompatibility.IsActiveOverlayCandidate(active with { Bounds=Rectangle.Union(source,physical) },physical),"whole virtual-desktop overlay accepted");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { Bounds=source },physical),"virtual-source-only overlay does not release physical pointer");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { Bounds=new Rectangle(0,1020,1920,60) },physical),"toolbar is insufficient");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { Visible=false },physical),"background hidden host rejected");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { Iconic=true },physical),"minimized host rejected");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { Cloaked=true },physical),"other virtual-desktop host rejected");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { ForegroundRelated=false },physical),"background visible host rejected");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { TrustedProcess=false },physical),"game overlay rejected");
            Expect(!ScreenClipCompatibility.IsActiveOverlayCandidate(active with { Captionless=false },physical),"maximized screenshot editor is not capture overlay");
        });
        check("Only Windows screenshot executables in trusted system package locations qualify",()=>
        {
            const string windows=@"C:\Windows";
            const string programFiles=@"C:\Program Files";
            Expect(ScreenClipCompatibility.IsTrustedScreenshotProcess("ScreenClippingHost",@"C:\Windows\SystemApps\Microsoft.Windows.ShellExperienceHost_test\ScreenClippingHost.exe",windows,programFiles),"Windows 10 clipping host");
            Expect(ScreenClipCompatibility.IsTrustedScreenshotProcess("SnippingTool",@"C:\Program Files\WindowsApps\Microsoft.ScreenSketch_1.0_x64__test\SnippingTool.exe",windows,programFiles),"packaged Windows snipping tool");
            Expect(ScreenClipCompatibility.IsTrustedScreenshotProcess("SnippingTool",@"C:\Windows\System32\SnippingTool.exe",windows,programFiles),"legacy system snipping tool");
            Expect(!ScreenClipCompatibility.IsTrustedScreenshotProcess("SnippingTool",@"C:\UntrustedApps\SnippingTool.exe",windows,programFiles),"same-name app outside trusted system locations rejected");
            Expect(!ScreenClipCompatibility.IsTrustedScreenshotProcess("Game",@"C:\Windows\System32\Game.exe",windows,programFiles),"other process rejected");
            Expect(!ScreenClipCompatibility.IsTrustedScreenshotProcess("SnippingTool",@"C:\Program Files\WindowsApps\ThirdPartyTool_test\SnippingTool.exe",windows,programFiles),"unrelated package rejected");
        });
        check("A self-created borderless topmost window never enables Windows screen-clipping handoff",()=>
        {
            var bounds=Screen.PrimaryScreen?.Bounds??new Rectangle(0,0,1920,1080);
            VerifyOwnOverlayRejected(bounds);
        });
    }

    // A synthetic app-owned window is safe to exercise without invoking Windows
    // screen capture or reading screen pixels. It never changes foreground focus,
    // mouse coordinates, or the global cursor restriction.
    internal static void VerifyOwnOverlayRejected(Rectangle physicalBounds)
    {
        if(!GetClipCursor(out var before)) throw new InvalidOperationException("Cannot read the pre-test cursor restriction.");
        using var fake=new OwnOverlayForm(physicalBounds);
        try
        {
            fake.Show();
            Application.DoEvents();
            bool detected=ScreenClipCompatibility.TryGetActiveOverlay(physicalBounds,out var overlay);
            Expect(!detected || overlay!.ProcessId!=Environment.ProcessId,"an app-owned overlay is not a system screenshot surface");
            if(!GetClipCursor(out var after)) throw new InvalidOperationException("Cannot read the post-test cursor restriction.");
            Expect(before.Left==after.Left && before.Top==after.Top && before.Right==after.Right && before.Bottom==after.Bottom,
                "read-only overlay recognition must not change cursor confinement");
        }
        finally { fake.Close(); }
    }

    private sealed class OwnOverlayForm:Form
    {
        internal OwnOverlayForm(Rectangle bounds)
        {
            FormBorderStyle=FormBorderStyle.None;
            StartPosition=FormStartPosition.Manual;
            Bounds=bounds;
            BackColor=Color.FromArgb(30,35,40);
            ShowInTaskbar=false;
            TopMost=true;
            AutoScaleMode=AutoScaleMode.None;
            Text="TopViewport synthetic overlay recognition check";
        }
        protected override bool ShowWithoutActivation=>true;
        protected override CreateParams CreateParams
        {
            get { var parameters=base.CreateParams; parameters.ExStyle|=0x08000000|0x00000080; return parameters; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out CursorRect rectangle);

    private static void Expect(bool condition,string description)
    {
        if(!condition) throw new InvalidOperationException("Screen clipping compatibility check failed: "+description);
    }
}
