using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TopViewport;

internal sealed record ScreenClipOverlay(IntPtr WindowHandle,int ProcessId,string ProcessName,Rectangle Bounds);
internal sealed record ScreenClipWindowMetadata(long Handle,int ProcessId,string ProcessName,string ClassName);
internal sealed record ScreenClipCandidateMetadata(ScreenClipWindowMetadata Window,Rectangle Bounds,long Style,long ExtendedStyle,
    bool Visible,bool Iconic,int CloakedResult,int Cloaked,bool TrustedProcess,bool ImageQuerySucceeded,int ImageQueryError,
    bool ForegroundRelated,bool MouseCaptureRelated,long MouseCaptureHandle,bool Accepted,string Rejection,string Discovery);
internal sealed record ScreenClipDiagnosticSnapshot(ScreenClipWindowMetadata Foreground,long ForegroundMouseCapture,
    int CursorFlags,Point CursorPosition,Rectangle CursorRestriction,List<ScreenClipCandidateMetadata> Candidates);

/// <summary>
/// Read-only recognition of Windows' interactive screen-clipping overlays and
/// pure coordinate conversion. This class never starts a capture, changes the
/// cursor restriction, activates a window, or reads captured image contents.
/// The caller owns the short-lived cursor handoff while an overlay is active.
/// </summary>
internal static class ScreenClipCompatibility
{
    private const long WsChild=0x40000000;
    private const long WsCaption=0x00c00000;
    private const int GwlStyle=-16;
    private const uint GaRootOwner=3;
    private const uint GwOwner=4;
    private const int DwmwaCloaked=14;
    private const int DwmwaExtendedFrameBounds=9;
    private static readonly string windowsDirectory=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string programFilesDirectory=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly Dictionary<int,CachedIdentity> processIdentities=new();

    private sealed record CachedIdentity(long ExpiresAt,string Name,bool Trusted,bool ImageQuerySucceeded,int ImageQueryError);

    internal readonly record struct Candidate(Rectangle Bounds,bool Visible,bool Iconic,bool Cloaked,
        bool Captionless,bool TrustedProcess,bool ForegroundRelated);

    // Private troubleshooting metadata only. No titles, executable paths, pixels,
    // keyboard state, document contents or browser locations enter this report.
    internal static ScreenClipDiagnosticSnapshot DescribeCandidates(Rectangle physicalBounds)
    {
        IntPtr foreground=GetForegroundWindow();
        var relatedPids=GetForegroundRelatedProcessIds(foreground);
        var foregroundMetadata=DescribeWindow(foreground);
        uint foregroundThread=GetWindowThreadProcessId(foreground,out _);
        IntPtr foregroundCapture=ReadMouseCapture(foregroundThread);
        var candidates=new List<ScreenClipCandidateMetadata>();
        var seen=new HashSet<IntPtr>();
        bool Describe(IntPtr window,string discovery)
        {
            if(window==IntPtr.Zero || !seen.Add(window)) return true;
            bool visible=IsWindowVisible(window),iconic=IsIconic(window);
            uint thread=GetWindowThreadProcessId(window,out uint pid);
            var identity=GetProcessIdentity(unchecked((int)pid));
            if(!GetWindowRect(window,out var rectangle)) return true;
            if(DwmGetWindowAttributeRect(window,DwmwaExtendedFrameBounds,out var frame,Marshal.SizeOf<NativeRect>())==0 && frame.Right>frame.Left && frame.Bottom>frame.Top) rectangle=frame;
            var bounds=rectangle.ToRectangle();
            bool covers=Rectangle.Intersect(bounds,physicalBounds).Width>=Math.Max(1,physicalBounds.Width-16) &&
                Rectangle.Intersect(bounds,physicalBounds).Height>=Math.Max(1,physicalBounds.Height-16);
            if(discovery!="foreground-chain" && !identity.Trusted && !IsScreenshotName(identity.Name) && !(visible && !iconic && covers)) return true;
            long style=GetWindowLongPtr(window,GwlStyle).ToInt64(),exStyle=GetWindowLongPtr(window,-20).ToInt64();
            int cloakedResult=DwmGetWindowAttribute(window,DwmwaCloaked,out int cloaked,sizeof(int));
            IntPtr mouseCapture=ReadMouseCapture(thread);
            bool foregroundRelated=relatedPids.Contains(unchecked((int)pid));
            bool captureRelated=CaptureBelongsToProcess(mouseCapture,pid) || CaptureBelongsToProcess(foregroundCapture,pid);
            var candidate=new Candidate(bounds,visible,iconic,cloakedResult!=0||cloaked!=0,(style&(WsChild|WsCaption))==0,identity.Trusted,foregroundRelated);
            bool accepted=IsActiveOverlayCandidate(candidate,physicalBounds);
            string rejection=!visible?"hidden":iconic?"minimized":!identity.Trusted?"untrusted-process":cloakedResult!=0?"cloaking-query-failed":cloaked!=0?"cloaked":
                (style&WsChild)!=0?"child":(style&WsCaption)!=0?"caption-style":!foregroundRelated?"not-foreground-related":!accepted?"does-not-cover-physical-output":"";
            candidates.Add(new ScreenClipCandidateMetadata(DescribeWindow(window),bounds,style,exStyle,visible,iconic,cloakedResult,cloaked,
                identity.Trusted,identity.ImageQuerySucceeded,identity.ImageQueryError,foregroundRelated,captureRelated,mouseCapture.ToInt64(),accepted,rejection,discovery));
            return true;
        }
        foreach(IntPtr window in GetForegroundRelatedWindows(foreground)) Describe(window,"foreground-chain");
        EnumWindows((window,_)=>Describe(window,"desktop-enumeration"),IntPtr.Zero);
        var cursor=new CursorInfo { Size=Marshal.SizeOf<CursorInfo>() };
        bool cursorRead=GetCursorInfo(ref cursor);
        bool clipRead=GetClipCursor(out var restriction);
        return new ScreenClipDiagnosticSnapshot(foregroundMetadata,foregroundCapture.ToInt64(),cursorRead?cursor.Flags:-1,
            cursorRead?cursor.Position:Point.Empty,clipRead?restriction.ToRectangle():Rectangle.Empty,candidates);
    }

    private static ScreenClipWindowMetadata DescribeWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window,out uint pid);
        var identity=GetProcessIdentity(unchecked((int)pid));
        var name=new StringBuilder(256);
        if(window!=IntPtr.Zero) GetClassName(window,name,name.Capacity);
        return new ScreenClipWindowMetadata(window.ToInt64(),unchecked((int)pid),identity.Name,name.ToString());
    }
    private static IntPtr ReadMouseCapture(uint thread)
    {
        if(thread==0) return IntPtr.Zero;
        var info=new GuiThreadInfo { Size=Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread,ref info)?info.Capture:IntPtr.Zero;
    }
    private static bool CaptureBelongsToProcess(IntPtr capture,uint processId)
    {
        if(capture==IntPtr.Zero || processId==0) return false;
        GetWindowThreadProcessId(capture,out uint capturePid);
        return capturePid==processId;
    }
    private static bool IsScreenshotName(string name)=>string.Equals(name,"ScreenClippingHost",StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name,"SnippingTool",StringComparison.OrdinalIgnoreCase) || string.Equals(name,"ScreenSketch",StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches an actual visible, borderless screen-clipping surface covering
    /// the physical projection monitor. A background process alone is never
    /// sufficient. DWM-cloaked or minimized surfaces are not active overlays.
    /// Call from the UI thread; all native calls in this method are read-only.
    /// </summary>
    internal static bool TryGetActiveOverlay(Rectangle physicalBounds,
        [NotNullWhen(true)] out ScreenClipOverlay? overlay)
    {
        overlay=null;
        if(physicalBounds.Width<=0 || physicalBounds.Height<=0) return false;
        IntPtr foreground=GetForegroundWindow();
        if(foreground==IntPtr.Zero) return false;
        var relatedPids=GetForegroundRelatedProcessIds(foreground);
        if(relatedPids.Count==0) return false;
        bool recognizedForeground=false;
        foreach(int pid in relatedPids)
            if(GetProcessIdentity(pid).Trusted) { recognizedForeground=true; break; }
        if(!recognizedForeground) return false;

        ScreenClipOverlay? Inspect(IntPtr window)
        {
            if(window==IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return null;
            GetWindowThreadProcessId(window,out uint ownerPid);
            int processId=unchecked((int)ownerPid);
            if(!relatedPids.Contains(processId)) return null;
            var identity=GetProcessIdentity(processId);
            if(!identity.Trusted) return null;
            long style=GetWindowLongPtr(window,GwlStyle).ToInt64();
            if((style&WsChild)!=0 || (style&WsCaption)!=0) return null;
            if(DwmGetWindowAttribute(window,DwmwaCloaked,out int cloaked,sizeof(int))!=0 || cloaked!=0) return null;
            if(!GetWindowRect(window,out var rectangle)) return null;
            if(DwmGetWindowAttributeRect(window,DwmwaExtendedFrameBounds,out var visibleRectangle,Marshal.SizeOf<NativeRect>())==0 &&
                visibleRectangle.Right>visibleRectangle.Left && visibleRectangle.Bottom>visibleRectangle.Top)
                rectangle=visibleRectangle;
            var bounds=rectangle.ToRectangle();
            var candidate=new Candidate(bounds,true,false,false,true,true,true);
            return IsActiveOverlayCandidate(candidate,physicalBounds) ? new ScreenClipOverlay(window,processId,identity.Name,bounds) : null;
        }
        // EnumWindows intentionally omits active UWP top-level surfaces on
        // Windows 8+. The screenshot host can become foreground while its HWND
        // disappears from that enumeration. Examine the authoritative foreground
        // chain directly, retaining every trust, cloak, style and coverage check.
        ScreenClipOverlay? found=null;
        foreach(IntPtr window in GetForegroundRelatedWindows(foreground))
        {
            found=Inspect(window);
            if(found!=null) break;
        }
        if(found==null) EnumWindows((window,_)=> { found=Inspect(window); return found==null; },IntPtr.Zero);
        overlay=found;
        if(processIdentities.Count>64)
        {
            long now=Environment.TickCount64;
            var expired=new List<int>();
            foreach(var item in processIdentities) if(item.Value.ExpiresAt<now) expired.Add(item.Key);
            foreach(int pid in expired) processIdentities.Remove(pid);
        }
        return overlay!=null;
    }

    internal static bool IsActiveOverlayCandidate(Candidate candidate,Rectangle physicalBounds)
    {
        if(!candidate.Visible || candidate.Iconic || candidate.Cloaked || !candidate.Captionless ||
            !candidate.TrustedProcess || !candidate.ForegroundRelated || physicalBounds.Width<=0 || physicalBounds.Height<=0)
            return false;
        var bounds=candidate.Bounds;
        if(bounds.Width<=0 || bounds.Height<=0) return false;
        // Full-screen surfaces may differ by a few invisible border pixels.
        // A toolbar, editor window, or overlay on the virtual source must not
        // release the cursor confinement on the physical output monitor.
        const int tolerance=8;
        if(bounds.Left>physicalBounds.Left+tolerance || bounds.Top>physicalBounds.Top+tolerance ||
            bounds.Right<physicalBounds.Right-tolerance || bounds.Bottom<physicalBounds.Bottom-tolerance) return false;
        var intersection=Rectangle.Intersect(bounds,physicalBounds);
        return (long)intersection.Width*intersection.Height>=
            (long)physicalBounds.Width*physicalBounds.Height*95/100;
    }

    /// <summary>Maps the virtual cursor to the same visible point on the output monitor.</summary>
    internal static Point MapSourceToPhysical(Point point,Rectangle source,Rectangle physical,int visibleHeight)
    {
        var visible=GetVisibleOutputBounds(source,physical,visibleHeight);
        return MapPoint(point,source,visible);
    }

    /// <summary>Maps output cursor coordinates back; the reserved black strip clamps to the last source row.</summary>
    internal static Point MapPhysicalToSource(Point point,Rectangle source,Rectangle physical,int visibleHeight)
    {
        var visible=GetVisibleOutputBounds(source,physical,visibleHeight);
        return MapPoint(point,visible,source);
    }

    internal static Rectangle GetVisibleOutputBounds(Rectangle source,Rectangle physical,int visibleHeight)
    {
        if(source.Width<=0 || source.Height<=0) throw new ArgumentException("Source bounds must have positive dimensions.",nameof(source));
        if(physical.Width<=0 || physical.Height<=0) throw new ArgumentException("Output bounds must have positive dimensions.",nameof(physical));
        if(visibleHeight<=0 || visibleHeight>physical.Height) throw new ArgumentOutOfRangeException(nameof(visibleHeight));
        return new Rectangle(physical.Left,physical.Top,physical.Width,visibleHeight);
    }

    private static Point MapPoint(Point point,Rectangle from,Rectangle to)
        => new(MapAxis(point.X,from.Left,from.Width,to.Left,to.Width),MapAxis(point.Y,from.Top,from.Height,to.Top,to.Height));

    private static int MapAxis(int coordinate,int fromOrigin,int fromSize,int toOrigin,int toSize)
    {
        long offset=Math.Clamp((long)coordinate-fromOrigin,0L,fromSize-1L);
        if(fromSize==1 || toSize==1) return toOrigin;
        return checked(toOrigin+(int)((offset*(toSize-1L)+(fromSize-1L)/2)/(fromSize-1L)));
    }

    internal static bool IsTrustedScreenshotProcess(string name,string executablePath,string windowsRoot,string programFilesRoot)
    {
        if(!string.Equals(name,"ScreenClippingHost",StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name,"SnippingTool",StringComparison.OrdinalIgnoreCase)) return false;
        if(string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(windowsRoot)) return false;
        try
        {
            string full=Path.GetFullPath(executablePath);
            if(!string.Equals(Path.GetFileNameWithoutExtension(full),name,StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(full),".exe",StringComparison.OrdinalIgnoreCase)) return false;
            string systemApps=Path.GetFullPath(Path.Combine(windowsRoot,"SystemApps"))+Path.DirectorySeparatorChar;
            string system32=Path.GetFullPath(Path.Combine(windowsRoot,"System32"))+Path.DirectorySeparatorChar;
            if(full.StartsWith(systemApps,StringComparison.OrdinalIgnoreCase)) return true;
            if(string.Equals(name,"SnippingTool",StringComparison.OrdinalIgnoreCase) &&
                string.Equals(full,Path.Combine(system32,"SnippingTool.exe"),StringComparison.OrdinalIgnoreCase)) return true;
            if(!string.IsNullOrWhiteSpace(programFilesRoot))
            {
                string windowsApps=Path.GetFullPath(Path.Combine(programFilesRoot,"WindowsApps"))+Path.DirectorySeparatorChar;
                if(full.StartsWith(windowsApps,StringComparison.OrdinalIgnoreCase))
                {
                    string package=full[windowsApps.Length..].Split(Path.DirectorySeparatorChar)[0];
                    return package.StartsWith("Microsoft.ScreenSketch_",StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch(ArgumentException) { }
        catch(NotSupportedException) { }
        catch(PathTooLongException) { }
        return false;
    }

    private static CachedIdentity GetProcessIdentity(int processId)
    {
        long now=Environment.TickCount64;
        if(processIdentities.TryGetValue(processId,out var cached) && cached.ExpiresAt>=now) return cached;
        string name="";
        bool trusted=false;
        bool imageQuerySucceeded=false;
        int imageQueryError=0;
        try
        {
            using var process=Process.GetProcessById(processId);
            name=process.ProcessName;
            if(string.Equals(name,"ScreenClippingHost",StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name,"SnippingTool",StringComparison.OrdinalIgnoreCase))
            {
                string? path=GetProcessImagePath(processId,out imageQueryError);
                imageQuerySucceeded=path!=null;
                trusted=IsTrustedScreenshotProcess(name,path??"",windowsDirectory,programFilesDirectory);
            }
        }
        catch(Exception ex) when(ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
        { }
        var result=new CachedIdentity(now+2000,name,trusted,imageQuerySucceeded,imageQueryError);
        processIdentities[processId]=result;
        return result;
    }

    private static string? GetProcessImagePath(int processId,out int error)
    {
        // UWP processes can deny VM_READ/MainModule access while still granting
        // limited identity queries. Never request debug or full process rights.
        const uint processQueryLimitedInformation=0x1000;
        error=0;
        IntPtr process=OpenProcess(processQueryLimitedInformation,false,processId);
        if(process==IntPtr.Zero) { error=Marshal.GetLastWin32Error(); return null; }
        try
        {
            var imagePath=new StringBuilder(32768);
            uint length=(uint)imagePath.Capacity;
            if(QueryFullProcessImageName(process,0,imagePath,ref length)) return imagePath.ToString();
            error=Marshal.GetLastWin32Error();
            return null;
        }
        finally { CloseHandle(process); }
    }

    private static HashSet<int> GetForegroundRelatedProcessIds(IntPtr foreground)
    {
        var result=new HashSet<int>();
        foreach(IntPtr window in GetForegroundRelatedWindows(foreground))
        {
            if(window!=IntPtr.Zero) { GetWindowThreadProcessId(window,out uint pid); if(pid!=0) result.Add(unchecked((int)pid)); }
        }
        return result;
    }

    private static HashSet<IntPtr> GetForegroundRelatedWindows(IntPtr foreground)
    {
        var result=new HashSet<IntPtr>();
        if(foreground==IntPtr.Zero) return result;
        result.Add(foreground);
        IntPtr rootOwner=GetAncestor(foreground,GaRootOwner);
        if(rootOwner!=IntPtr.Zero) result.Add(rootOwner);
        IntPtr owner=GetWindow(foreground,GwOwner);
        for(int i=0;i<8 && owner!=IntPtr.Zero;i++) { result.Add(owner); owner=GetWindow(owner,GwOwner); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left,Top,Right,Bottom;
        public Rectangle ToRectangle()=>Rectangle.FromLTRB(Left,Top,Right,Bottom);
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size,Flags;
        public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;
        public NativeRect CaretBounds;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo
    {
        public int Size,Flags;
        public IntPtr Cursor;
        public Point Position;
    }
    private delegate bool EnumWindowsProc(IntPtr window,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback,IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId,ref GuiThreadInfo info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder className,int maximum);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder imageName,ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeRect(IntPtr window,int attribute,out NativeRect value,int size);
}
