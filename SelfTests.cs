using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace TopViewport;

internal static class SelfTests
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var checks=new List<object>();
        int failures=0;
        void Check(string name,Action action)
        {
            try { action(); checks.Add(new { name,pass=true,error="" }); }
            catch(Exception ex) { failures++; checks.Add(new { name,pass=false,error=ex.ToString() }); }
        }
        DisplayIdentityTests.AddChecks(Check);
        DisplayPersistenceTests.AddChecks(Check);
        WindowPlacementGuardTests.AddChecks(Check);
        ScreenClipCompatibilityTests.AddChecks(Check);
        ScreenshotCursorOverlayTests.AddChecks(Check);
        Check("Enumerate active physical display and mode",()=>
        {
            var displays=DisplayManager.ListDisplays();
            if(!displays.Any(d=>d.IsActive && d.IsPrimary && d.Width>0 && d.Height>0)) throw new Exception("No valid active primary display");
            var primary=displays.First(d=>d.IsPrimary);
            if(!DisplayManager.ListModes(primary.DeviceName).Any(m=>m.Width==primary.Width && m.Height==primary.Height)) throw new Exception("Current mode missing from mode enumeration");
        });
        Check("Native topology snapshot survives persistent JSON round trip",()=>
        {
            var original=DisplayManager.CaptureSnapshot();
            string file=Path.Combine(folder,"snapshot-roundtrip.json");
            DisplayManager.SaveSnapshot(original,file);
            var loaded=DisplayManager.LoadSnapshot(file);
            if(original.Displays.Count!=loaded.Displays.Count || loaded.NativeTopology==null || original.NativeTopology==null || !loaded.NativeTopology.Paths.SequenceEqual(original.NativeTopology.Paths) || !loaded.NativeTopology.Modes.SequenceEqual(original.NativeTopology.Modes)) throw new Exception("Snapshot persistence did not preserve native arrays");
        });
        Check("Driver package pins and read-only enumeration",()=>
        {
            var status=DriverSetup.GetStatus();
            if(!status.BundledFilesValid) throw new Exception("Driver bundle validation failed: "+status.Message);
        });
        Check("Sleep-aware watchdog and isolated recovery state policies",()=>
        {
            RecoveryTests.Run(folder);
        });
        Check("Recovery parent identity includes process start time",()=>
        {
            using var p=Process.GetCurrentProcess();
            var state=new SessionState { ParentPid=p.Id,ParentStartTicks=p.StartTime.ToUniversalTime().Ticks,Completed=true };
            if(!Recovery.ParentAlive(state)) throw new Exception("Live parent not recognized");
            state.ParentStartTicks++;
            if(Recovery.ParentAlive(state)) throw new Exception("Reused PID incorrectly recognized");
            // The first Write creates an isolated state file. A stale test file
            // from an earlier run must not make this a cross-session write.
            string file=Path.Combine(folder,"completed-session-"+Guid.NewGuid().ToString("N")+".json"); Recovery.Write(state,file);
            if(Recovery.RunWatchdog(file)!=0) throw new Exception("Completed watchdog did not exit");
            state.Completed=false; Recovery.Write(state,file);
            if(Recovery.Read(file)?.Completed!=true || !state.Completed) throw new Exception("Late heartbeat undid completed recovery");
        });
        Check("WGC GPU projection keeps whole source in top 90 percent and black bottom",()=>
        {
            var bounds=Screen.PrimaryScreen?.Bounds ?? new Rectangle(0,0,1920,1080);
            using var source=new PatternForm { Bounds=new Rectangle(bounds.Left+20,bounds.Top+30,640,360) };
            using var target=new ProjectionForm(new Rectangle(bounds.Left+700,bounds.Top+30,800,600));
            using var engine=new ProjectionEngine();
            try
            {
                source.Show(); target.Show(); Pump(350);
                engine.StartForWindow(target.Handle,source.Handle,target.Bounds,540);
                Pump(2200);
                if(engine.FramesPresented<3) throw new Exception("No GPU frames: "+engine.Status);
                string bmp=Path.Combine(folder,"projection-90-percent.bmp"); engine.SaveSnapshotBmp(bmp);
                using(var image=new Bitmap(bmp))
                {
                    AssertColor(image,15,15,Color.Red,"top left source corner");
                    AssertColor(image,785,15,Color.Lime,"top right source corner");
                    AssertColor(image,15,460,Color.Blue,"bottom left source area");
                    AssertColor(image,785,460,Color.Gold,"bottom right source area");
                    AssertColor(image,400,535,Color.White,"source bottom edge preserved");
                    AssertColor(image,400,570,Color.Black,"reserved bottom region");
                }
                source.Size=new Size(640,400); source.Invalidate(); Pump(1500);
                string resized=Path.Combine(folder,"projection-resized-source.bmp"); engine.SaveSnapshotBmp(resized);
                using(var image=new Bitmap(resized))
                {
                    AssertColor(image,400,535,Color.White,"resized source bottom edge preserved");
                    AssertColor(image,400,570,Color.Black,"black bottom after resize");
                }
                File.WriteAllText(Path.Combine(folder,"gpu-statistics.json"),JsonSerializer.Serialize(new { engine.Status, engine.FramesPresented,SourceResized=true,TargetWidth=800,TargetHeight=600,VisibleHeight=540 },Program.JsonOptions));
                engine.Stop();
                if(engine.IsRunning) throw new Exception("GPU worker did not stop");
            }
            finally { engine.Stop(); target.Close(); source.Close(); Pump(50); }
        });
        Check("Control panel renders without requiring a virtual display",()=>
        {
            using var form=new MainForm();
            form.Show(); Pump(300);
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
            bitmap.Save(Path.Combine(folder,"control-panel.png"));
            form.Close();
        });
        Check("Control panel recovers from tray and offscreen placement without replacing its HWND",()=>
        {
            using var form=new MainForm();
            form.Show(); Pump(150);
            var display=DisplayManager.ListDisplays(false).First(x=>x.IsPrimary);
            var area=display.WorkArea;
            var bounds=area==null ? new Rectangle(display.X,display.Y,display.Width,display.Height)
                : Rectangle.FromLTRB(area.Left,area.Top,area.Right,area.Bottom);
            TrayRestoreTests.VerifyRoundTrips(form,bounds);
            form.Close();
        });
        File.WriteAllText(Path.Combine(folder,"selftest-results.json"),JsonSerializer.Serialize(new { Passed=checks.Count-failures,Failed=failures,Checks=checks },Program.JsonOptions));
        return failures==0 ? 0 : 1;
    }
    private static void AssertColor(Bitmap image,int x,int y,Color expected,string description)
    {
        Color actual=image.GetPixel(x,y);
        if(Math.Abs(actual.R-expected.R)>12 || Math.Abs(actual.G-expected.G)>12 || Math.Abs(actual.B-expected.B)>12)
            throw new Exception($"{description} at {x},{y}: expected {expected}, got {actual}");
    }
    private static void Pump(int milliseconds)
    {
        var clock=Stopwatch.StartNew();
        while(clock.ElapsedMilliseconds<milliseconds) { Application.DoEvents(); Thread.Sleep(8); }
    }
    private sealed class PatternForm : Form
    {
        public PatternForm() { FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual; ShowInTaskbar=false; DoubleBuffered=true; AutoScaleMode=AutoScaleMode.None; }
        protected override void OnPaint(PaintEventArgs e)
        {
            int w=ClientSize.Width,h=ClientSize.Height;
            e.Graphics.FillRectangle(Brushes.Red,0,0,w/2,h/2);
            e.Graphics.FillRectangle(Brushes.Lime,w/2,0,w-w/2,h/2);
            e.Graphics.FillRectangle(Brushes.Blue,0,h/2,w/2,h-h/2);
            e.Graphics.FillRectangle(Brushes.Gold,w/2,h/2,w-w/2,h-h/2);
            e.Graphics.FillRectangle(Brushes.White,0,h-16,w,16);
            using var font=new Font("Segoe UI",18,FontStyle.Bold);
            e.Graphics.DrawString("TopViewport · complete source",font,Brushes.Black,new PointF(24,h/2-24));
        }
    }
}
