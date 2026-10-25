#nullable disable
// COM pointers and delegates are initialized and released together on the capture thread.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TopViewport
{
    /// <summary>
    /// Captures one Windows monitor with Windows Graphics Capture and presents it
    /// in the upper part of a different monitor. All frame transfers and resizing
    /// stay on the GPU. The source must be a separate, extended virtual display.
    /// This class never installs a driver or changes the Windows display topology.
    /// </summary>
    public sealed class ProjectionEngine : IDisposable
    {
        private readonly object gate = new object();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private Thread worker;
        private int stopJoinAttempted;
        private volatile string status = "未启动";
        private long framesPresented;
        private long lastLoopProgressTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        private long lastLoopAwakeTicks = Recovery.AwakeNowTicks;
        private SnapshotRequest pendingSnapshot;

        private sealed class SnapshotRequest
        {
            public string Path;
            public Exception Error;
            public readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        }

        public string Status { get { return status; } }
        public long FramesPresented { get { return Interlocked.Read(ref framesPresented); } }
        public long LastLoopAwakeTicks { get { return Interlocked.Read(ref lastLoopAwakeTicks); } }
        public DateTimeOffset LastLoopProgress
        {
            get { return new DateTimeOffset(Interlocked.Read(ref lastLoopProgressTicks), TimeSpan.Zero); }
        }
        public bool IsRunning { get { lock (gate) { return worker != null && worker.IsAlive; } } }

        /// <summary>Explicit diagnostic export of this engine's rendered output, never another window or monitor.</summary>
        public void SaveSnapshotBmp(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("必须指定 BMP 路径。", "path");
            if (!IsRunning) throw new InvalidOperationException("GPU 投射未启动。");
            var request = new SnapshotRequest { Path = System.IO.Path.GetFullPath(path) };
            if (Interlocked.CompareExchange(ref pendingSnapshot, request, null) != null)
                throw new InvalidOperationException("已有截图导出请求。");
            if (!request.Ready.Wait(TimeSpan.FromSeconds(10)))
            {
                Interlocked.CompareExchange(ref pendingSnapshot, null, request);
                throw new TimeoutException("GPU 输出截图超时。");
            }
            if (request.Error != null) throw new InvalidOperationException("GPU 输出截图失败。", request.Error);
        }

        /// <param name="targetHwnd">An already created, borderless physical-monitor-sized HWND.</param>
        /// <param name="sourceGdiName">Exact virtual source name, for example \\.\DISPLAY2.</param>
        /// <param name="physicalBounds">Physical monitor bounds in physical desktop pixels.</param>
        /// <param name="sourceBounds">Virtual monitor bounds in physical desktop pixels.</param>
        /// <param name="visibleHeight">Output height, measured from the top of the physical monitor.</param>
        public void Start(IntPtr targetHwnd, string sourceGdiName, Rectangle physicalBounds,
                          Rectangle sourceBounds, int visibleHeight)
        {
            if (string.IsNullOrWhiteSpace(sourceGdiName)) throw new ArgumentException("必须指定虚拟显示器。", "sourceGdiName");
            if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0)
                throw new ArgumentException("显示器尺寸无效。");
            StartCore(targetHwnd, sourceGdiName, IntPtr.Zero, physicalBounds, visibleHeight);
        }

        /// <summary>Captures an explicit, non-minimized source window for isolated rendering tests.</summary>
        public void StartForWindow(IntPtr targetHwnd, IntPtr sourceHwnd, Rectangle targetBounds, int visibleHeight)
        {
            if (sourceHwnd == IntPtr.Zero || sourceHwnd == targetHwnd)
                throw new ArgumentException("测试源必须是独立且已创建的窗口。", "sourceHwnd");
            StartCore(targetHwnd, null, sourceHwnd, targetBounds, visibleHeight);
        }

        private void StartCore(IntPtr targetHwnd, string sourceGdiName, IntPtr sourceHwnd,
                               Rectangle physicalBounds, int visibleHeight)
        {
            if (targetHwnd == IntPtr.Zero) throw new ArgumentException("投射窗口尚未创建。", "targetHwnd");
            if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
                throw new ArgumentException("显示器尺寸无效。");
            if (visibleHeight <= 0 || visibleHeight > physicalBounds.Height)
                throw new ArgumentOutOfRangeException("visibleHeight");
            if (Environment.OSVersion.Version < new Version(10, 0, 18362))
                throw new PlatformNotSupportedException("GPU 桌面投射需要 Windows 10 1903 或更新版本。");

            Stop();
            var ready = new ManualResetEventSlim(false);
            Exception startupError = null;
            lock (gate)
            {
                if (worker != null && worker.IsAlive)
                    throw new InvalidOperationException("上一次投射仍在停止，请稍后重试。");
                stop.Reset();
                Interlocked.Exchange(ref stopJoinAttempted, 0);
                Interlocked.Exchange(ref framesPresented, 0);
                MarkLoopProgress();
                status = "正在初始化 GPU 桌面投射…";
                worker = new Thread(delegate()
                {
                    Run(targetHwnd, sourceGdiName, sourceHwnd, physicalBounds, visibleHeight,
                        delegate(Exception error) { startupError = error; ready.Set(); });
                });
                worker.Name = "TopViewport GPU capture";
                worker.IsBackground = true;
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
            // Do not dispose this event here: startup may still complete after timeout.
            if (!ready.Wait(TimeSpan.FromSeconds(15)))
            {
                Stop();
                throw new TimeoutException("GPU 捕获初始化超时，已请求停止。");
            }
            if (startupError != null)
            {
                Stop();
                throw new InvalidOperationException("无法启动 GPU 桌面投射：" + startupError.Message, startupError);
            }
        }

        public void Stop()
        {
            Thread thread;
            lock (gate) { stop.Set(); thread = worker; }
            // A second Stop/Dispose must not delay display recovery by waiting another five seconds.
            if (Interlocked.Exchange(ref stopJoinAttempted, 1) == 0 && thread != null && thread != Thread.CurrentThread) thread.Join(5000);
            lock (gate)
            {
                if (worker == thread && (thread == null || !thread.IsAlive)) worker = null;
            }
            status = thread != null && thread.IsAlive ? "正在停止 GPU 投射…" : "已停止";
        }

        // Power notifications must return promptly; joining is done off the UI thread.
        public void RequestStop() { stop.Set(); }

        public void Dispose() { Stop(); }

        private void MarkLoopProgress()
        {
            Interlocked.Exchange(ref lastLoopProgressTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            Interlocked.Exchange(ref lastLoopAwakeTicks, Recovery.AwakeNowTicks);
        }

        private void Run(IntPtr hwnd, string sourceName, IntPtr sourceHwnd, Rectangle physicalBounds, int visibleHeight,
                         Action<Exception> signalStartup)
        {
            bool initialized = false;
            bool roInitialized = false;
            try
            {
                Check(Native.RoInitialize(1), "初始化 Windows Runtime");
                roInitialized = true;
                while (!stop.WaitOne(0))
                {
                    MarkLoopProgress();
                    try
                    {
                        using (var session = new CaptureSession(hwnd, sourceName, sourceHwnd, physicalBounds, visibleHeight))
                        {
                            status = "GPU 投射已启动（Windows Graphics Capture，含鼠标）";
                            if (!initialized) { initialized = true; signalStartup(null); }
                            while (!stop.WaitOne(0))
                            {
                                // Updated even on a legitimately static desktop. A blocking native
                                // Present/Map/WinRT call leaves this stale for the UI watchdog.
                                MarkLoopProgress();
                                SnapshotRequest snapshot = Interlocked.Exchange(ref pendingSnapshot, null);
                                if (snapshot != null)
                                {
                                    try { session.SaveSnapshotBmp(snapshot.Path); }
                                    catch (Exception ex) { snapshot.Error = ex; }
                                    finally { snapshot.Ready.Set(); }
                                }
                                if (session.PresentNextFrame())
                                {
                                    Interlocked.Increment(ref framesPresented);
                                    status = "GPU 投射中 · " + session.SourceWidth + " × " + session.SourceHeight +
                                             " → " + physicalBounds.Width + " × " + visibleHeight;
                                }
                                else stop.WaitOne(2);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!initialized) { signalStartup(ex); return; }
                        if (stop.WaitOne(0)) break;
                        MarkLoopProgress();
                        status = "GPU 捕获中断，正在重建：" + ex.Message;
                        // Display mode changes and GPU resets invalidate capture resources.
                        // Rebuild the complete graph on this same MTA thread.
                        if (stop.WaitOne(1000)) break;
                    }
                }
            }
            catch (Exception ex)
            {
                status = "GPU 投射失败：" + ex.Message;
                if (!initialized) signalStartup(ex);
            }
            finally
            {
                SnapshotRequest snapshot = Interlocked.Exchange(ref pendingSnapshot, null);
                if (snapshot != null)
                {
                    snapshot.Error = new InvalidOperationException("GPU 投射已停止。");
                    snapshot.Ready.Set();
                }
                if (roInitialized) Native.RoUninitialize();
            }
        }

        private sealed class CaptureSession : IDisposable
        {
            private IntPtr device, context, swapChain, backBuffer, renderTarget;
            private IntPtr winrtDevice, item, framePool, captureSession;
            private IntPtr vertexShader, pixelShader, sampler, sampledTexture, sampledView;
            private int sampledWidth, sampledHeight;
            private readonly int outputWidth, outputHeight, fullHeight;
            private SizeInt32 poolSize;
            private readonly IntPtr[] renderTargets = new IntPtr[1];
            private readonly IntPtr[] resources = new IntPtr[1];
            private readonly IntPtr[] samplers = new IntPtr[1];
            private readonly float[] clearColor = { 0, 0, 0, 1 };

            // Retained delegates avoid allocating COM wrappers in the frame loop.
            private GetObjectDelegate tryGetFrame, getSurface;
            private GetSizeDelegate getFrameSize;
            private RecreatePoolDelegate recreatePool;
            private CopyRegionDelegate copyRegion;
            private ClearRtvDelegate clearRtv;
            private SetTargetsDelegate setRenderTargets;
            private SetPointerArrayDelegate setResources, setSamplers;
            private SetShaderDelegate setVs, setPs;
            private SetViewportDelegate setViewport;
            private SetTopologyDelegate setTopology;
            private DrawDelegate draw;
            private PresentDelegate present;

            public int SourceWidth { get; private set; }
            public int SourceHeight { get; private set; }

            public CaptureSession(IntPtr hwnd, string sourceName, IntPtr sourceHwnd, Rectangle physicalBounds, int visibleHeight)
            {
                outputWidth = physicalBounds.Width;
                outputHeight = visibleHeight;
                fullHeight = physicalBounds.Height;
                try
                {
                    IntPtr sourceMonitor = sourceHwnd == IntPtr.Zero ? FindMonitor(sourceName) : IntPtr.Zero;
                    if (sourceHwnd == IntPtr.Zero && sourceMonitor == IntPtr.Zero)
                        throw new InvalidOperationException("找不到捕获源 " + sourceName + "，请先启用虚拟显示器。");
                    if (sourceHwnd == IntPtr.Zero && sourceMonitor == Native.MonitorFromWindow(hwnd, 2))
                        throw new InvalidOperationException("捕获源和投射屏不能是同一显示器，以免出现画面递归。");

                    var desc = new SwapChainDesc();
                    desc.BufferDesc.Width = (uint)physicalBounds.Width;
                    desc.BufferDesc.Height = (uint)physicalBounds.Height;
                    desc.BufferDesc.Format = 87; // DXGI_FORMAT_B8G8R8A8_UNORM, matching WGC.
                    desc.SampleDesc.Count = 1;
                    desc.BufferUsage = 0x20; // DXGI_USAGE_RENDER_TARGET_OUTPUT
                    desc.BufferCount = 2;
                    desc.OutputWindow = hwnd;
                    desc.Windowed = 1;
                    desc.SwapEffect = 4; // DXGI_SWAP_EFFECT_FLIP_DISCARD
                    uint featureLevel;
                    Check(Native.D3D11CreateDeviceAndSwapChain(IntPtr.Zero, 1, IntPtr.Zero, 0x20,
                        IntPtr.Zero, 0, 7, ref desc, out swapChain, out device, out featureLevel, out context),
                        "创建 D3D11 GPU 设备");

                    // Prevent Alt+Enter on this presentation window from switching the physical output.
                    IntPtr factory = IntPtr.Zero;
                    try
                    {
                        Guid factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                        Check(Method<GetParentDelegate>(swapChain, 6)(swapChain, ref factoryGuid, out factory), "查询 DXGI 工厂");
                        Check(Method<MakeAssociationDelegate>(factory, 8)(factory, hwnd, 2), "锁定窗口呈现模式");
                    }
                    finally { Release(ref factory); }

                    Guid textureGuid = Native.IidTexture2D;
                    Check(Method<GetBufferDelegate>(swapChain, 9)(swapChain, 0, ref textureGuid, out backBuffer), "获取 GPU 后缓冲区");
                    Check(Method<CreateViewDelegate>(device, 9)(device, backBuffer, IntPtr.Zero, out renderTarget), "创建 GPU 呈现目标");
                    renderTargets[0] = renderTarget;
                    CompileShaders();
                    var samplerDesc = new SamplerDesc();
                    samplerDesc.Filter = 0x15; // D3D11_FILTER_MIN_MAG_MIP_LINEAR
                    samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = 3; // CLAMP
                    samplerDesc.MaxAnisotropy = 1;
                    samplerDesc.ComparisonFunc = 1;
                    samplerDesc.MaxLOD = float.MaxValue;
                    Check(Method<CreateSamplerDelegate>(device, 23)(device, ref samplerDesc, out sampler), "创建 GPU 采样器");
                    samplers[0] = sampler;
                    CacheRenderingMethods();

                    IntPtr dxgiDevice = IntPtr.Zero;
                    try
                    {
                        dxgiDevice = Query(device, Native.IidDxgiDevice);
                        Check(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out winrtDevice), "创建 WGC GPU 设备");
                    }
                    finally { Release(ref dxgiDevice); }

                    IntPtr itemFactory = IntPtr.Zero, poolFactory = IntPtr.Zero;
                    try
                    {
                        itemFactory = ActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", Native.IidCaptureInterop);
                        Guid itemGuid = Native.IidCaptureItem;
                        Check(Method<CreateCaptureItemDelegate>(itemFactory, sourceHwnd == IntPtr.Zero ? 4 : 3)(
                                itemFactory, sourceHwnd == IntPtr.Zero ? sourceMonitor : sourceHwnd, ref itemGuid, out item),
                            "创建虚拟显示器捕获源");
                        Check(Method<GetSizeDelegate>(item, 7)(item, out poolSize), "查询捕获尺寸");
                        if (poolSize.Width <= 0 || poolSize.Height <= 0) throw new InvalidOperationException("捕获源尺寸无效。");
                        poolFactory = ActivationFactory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", Native.IidFramePoolStatics2);
                        Check(Method<CreateFramePoolDelegate>(poolFactory, 6)(poolFactory, winrtDevice, 87, 2, poolSize, out framePool),
                            "创建自由线程 GPU 捕获缓冲区");
                    }
                    finally { Release(ref poolFactory); Release(ref itemFactory); }

                    Check(Method<CreateSessionDelegate>(framePool, 10)(framePool, item, out captureSession), "创建桌面捕获会话");
                    // WGC captures the hardware cursor by default. Enable explicitly where supported.
                    IntPtr cursorSettings = IntPtr.Zero;
                    try
                    {
                        cursorSettings = TryQuery(captureSession, Native.IidCaptureSession2);
                        if (cursorSettings != IntPtr.Zero)
                            Check(Method<SetBooleanDelegate>(cursorSettings, 7)(cursorSettings, 1), "启用鼠标捕获");
                    }
                    finally { Release(ref cursorSettings); }
                    tryGetFrame = Method<GetObjectDelegate>(framePool, 7);
                    recreatePool = Method<RecreatePoolDelegate>(framePool, 6);
                    // A black initial frame hides the damaged lower area before capture arrives.
                    clearRtv(context, renderTarget, clearColor);
                    Check(present(swapChain, 1, 0), "呈现初始画面");
                    Check(Method<SimpleHResultDelegate>(captureSession, 6)(captureSession), "开始 GPU 桌面捕获");
                }
                catch { Dispose(); throw; }
            }

            private void CacheRenderingMethods()
            {
                copyRegion = Method<CopyRegionDelegate>(context, 46);
                clearRtv = Method<ClearRtvDelegate>(context, 50);
                setRenderTargets = Method<SetTargetsDelegate>(context, 33);
                setResources = Method<SetPointerArrayDelegate>(context, 8);
                setSamplers = Method<SetPointerArrayDelegate>(context, 10);
                setVs = Method<SetShaderDelegate>(context, 11);
                setPs = Method<SetShaderDelegate>(context, 9);
                setViewport = Method<SetViewportDelegate>(context, 44);
                setTopology = Method<SetTopologyDelegate>(context, 24);
                draw = Method<DrawDelegate>(context, 13);
                present = Method<PresentDelegate>(swapChain, 8);
            }

            public bool PresentNextFrame()
            {
                IntPtr frame = IntPtr.Zero, surface = IntPtr.Zero, access = IntPtr.Zero, texture = IntPtr.Zero;
                try
                {
                    Check(tryGetFrame(framePool, out frame), "读取 GPU 捕获帧");
                    if (frame == IntPtr.Zero) return false;
                    if (getSurface == null)
                    {
                        getSurface = Method<GetObjectDelegate>(frame, 6);
                        getFrameSize = Method<GetSizeDelegate>(frame, 8);
                    }
                    SizeInt32 content;
                    Check(getFrameSize(frame, out content), "读取桌面尺寸");
                    if (content.Width <= 0 || content.Height <= 0) return false;
                    if (content.Width != poolSize.Width || content.Height != poolSize.Height)
                    {
                        // WGC reports a changed ContentSize before its old buffers have been
                        // replaced. Never present that truncated transitional frame: keep the
                        // previous complete frame until a new buffer fits the entire desktop.
                        Close(frame);
                        Release(ref frame);
                        poolSize = content;
                        Check(recreatePool(framePool, winrtDevice, 87, 2, poolSize), "适配游戏切换后的显示尺寸");
                        return false;
                    }
                    Check(getSurface(frame, out surface), "读取 GPU 捕获表面");
                    access = Query(surface, Native.IidDxgiAccess);
                    Guid textureGuid = Native.IidTexture2D;
                    Check(Method<GetInterfaceDelegate>(access, 3)(access, ref textureGuid, out texture), "读取 D3D11 捕获纹理");
                    Texture2DDesc captureDesc;
                    Method<GetTextureDescDelegate>(texture, 10)(texture, out captureDesc);
                    if (captureDesc.Width < content.Width || captureDesc.Height < content.Height)
                        throw new InvalidOperationException("GPU 捕获缓冲区小于完整桌面，正在重建以保留完整画面。");
                    int width = content.Width;
                    int height = content.Height;
                    EnsureSampleTexture(width, height);

                    // Unbind the SRV before writing the same texture on the next frame.
                    resources[0] = IntPtr.Zero;
                    setResources(context, 0, 1, resources);
                    var box = new D3DBox { Right = (uint)width, Bottom = (uint)height, Back = 1 };
                    copyRegion(context, sampledTexture, 0, 0, 0, 0, texture, 0, ref box);
                    RenderCachedFrame();
                    Check(present(swapChain, 1, 0), "呈现 GPU 桌面");
                    SourceWidth = content.Width;
                    SourceHeight = content.Height;

                    return true;
                }
                finally
                {
                    Release(ref texture);
                    Release(ref access);
                    Release(ref surface);
                    if (frame != IntPtr.Zero) Close(frame);
                    Release(ref frame);
                }
            }

            private void RenderCachedFrame()
            {
                clearRtv(context, renderTarget, clearColor);
                if (sampledView == IntPtr.Zero) return;
                setRenderTargets(context, 1, renderTargets, IntPtr.Zero);
                var viewport = new D3DViewport { Width = outputWidth, Height = outputHeight, MaxDepth = 1 };
                setViewport(context, 1, ref viewport);
                setTopology(context, 4); // D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST
                setVs(context, vertexShader, IntPtr.Zero, 0);
                setPs(context, pixelShader, IntPtr.Zero, 0);
                resources[0] = sampledView;
                setResources(context, 0, 1, resources);
                setSamplers(context, 0, 1, samplers);
                draw(context, 3, 0);
            }

            public void SaveSnapshotBmp(string path)
            {
                if (sampledTexture == IntPtr.Zero) throw new InvalidOperationException("尚未收到完整桌面帧。");
                IntPtr staging = IntPtr.Zero;
                bool mapped = false;
                try
                {
                    // Re-render because flip-discard makes a presented backbuffer's old contents undefined.
                    RenderCachedFrame();
                    var desc = new Texture2DDesc();
                    desc.Width = (uint)outputWidth; desc.Height = (uint)fullHeight;
                    desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = 87;
                    desc.SampleDesc.Count = 1; desc.Usage = 3; desc.CPUAccessFlags = 0x20000;
                    Check(Method<CreateTextureDelegate>(device, 5)(device, ref desc, IntPtr.Zero, out staging), "创建截图读取缓冲区");
                    Method<CopyResourceDelegate>(context, 47)(context, staging, backBuffer);
                    MappedSubresource data;
                    Check(Method<MapDelegate>(context, 14)(context, staging, 0, 1, 0, out data), "读取 GPU 输出截图");
                    mapped = true;
                    using (var writer = new System.IO.BinaryWriter(System.IO.File.Create(path)))
                    {
                        int rowBytes = checked(outputWidth * 4);
                        int imageBytes = checked(rowBytes * fullHeight);
                        writer.Write((ushort)0x4d42); writer.Write(checked(54 + imageBytes));
                        writer.Write(0); writer.Write(54); writer.Write(40); writer.Write(outputWidth);
                        writer.Write(-fullHeight); writer.Write((ushort)1); writer.Write((ushort)32);
                        writer.Write(0); writer.Write(imageBytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                        var row = new byte[rowBytes];
                        for (int y = 0; y < fullHeight; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Data, checked(y * (int)data.RowPitch)), row, 0, rowBytes);
                            writer.Write(row);
                        }
                    }
                }
                finally
                {
                    if (mapped) Method<UnmapDelegate>(context, 15)(context, staging, 0);
                    Release(ref staging);
                }
            }

            private void EnsureSampleTexture(int width, int height)
            {
                if (sampledTexture != IntPtr.Zero && sampledWidth == width && sampledHeight == height) return;
                resources[0] = IntPtr.Zero;
                setResources(context, 0, 1, resources);
                Release(ref sampledView);
                Release(ref sampledTexture);
                var desc = new Texture2DDesc();
                desc.Width = (uint)width; desc.Height = (uint)height;
                desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = 87;
                desc.SampleDesc.Count = 1; desc.BindFlags = 8; // D3D11_BIND_SHADER_RESOURCE
                Check(Method<CreateTextureDelegate>(device, 5)(device, ref desc, IntPtr.Zero, out sampledTexture), "创建 GPU 缩放纹理");
                Check(Method<CreateViewDelegate>(device, 7)(device, sampledTexture, IntPtr.Zero, out sampledView), "创建 GPU 纹理视图");
                sampledWidth = width; sampledHeight = height;
            }

            private void CompileShaders()
            {
                const string shader =
                    "Texture2D image : register(t0); SamplerState imageSampler : register(s0);" +
                    "struct V { float4 position : SV_Position; float2 uv : TEXCOORD0; };" +
                    "V VS(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2);" +
                    "o.uv = uv; o.position = float4(uv * float2(2,-2) + float2(-1,1),0,1); return o; }" +
                    "float4 PS(V p) : SV_Target { return image.Sample(imageSampler,p.uv); }";
                vertexShader = CompileShader(shader, "VS", "vs_4_0", 12);
                pixelShader = CompileShader(shader, "PS", "ps_4_0", 15);
            }

            private IntPtr CompileShader(string source, string entry, string profile, int createSlot)
            {
                IntPtr blob = IntPtr.Zero, errors = IntPtr.Zero;
                try
                {
                    int hr = Native.D3DCompile(source, new UIntPtr((uint)Encoding.ASCII.GetByteCount(source)),
                        null, IntPtr.Zero, IntPtr.Zero, entry, profile, 1 << 15, 0, out blob, out errors);
                    if (hr < 0)
                    {
                        string details = string.Empty;
                        if (errors != IntPtr.Zero)
                        {
                            var bytes = Method<GetBlobSizeDelegate>(errors, 4)(errors).ToUInt64();
                            details = Marshal.PtrToStringAnsi(Method<GetBlobPointerDelegate>(errors, 3)(errors), (int)Math.Min(bytes, 8192UL));
                        }
                        throw new InvalidOperationException("GPU 着色器编译失败：" + details);
                    }
                    IntPtr shader;
                    Check(Method<CreateShaderDelegate>(device, createSlot)(device,
                        Method<GetBlobPointerDelegate>(blob, 3)(blob), Method<GetBlobSizeDelegate>(blob, 4)(blob),
                        IntPtr.Zero, out shader), "创建 GPU 着色器");
                    return shader;
                }
                finally { Release(ref errors); Release(ref blob); }
            }

            public void Dispose()
            {
                // Close the producer first. Only this capture thread accesses these COM pointers.
                Close(captureSession); Release(ref captureSession);
                Close(framePool); Release(ref framePool);
                Release(ref item); Release(ref winrtDevice);
                Release(ref sampledView); Release(ref sampledTexture);
                Release(ref sampler); Release(ref pixelShader); Release(ref vertexShader);
                Release(ref renderTarget); Release(ref backBuffer);
                Release(ref swapChain); Release(ref context); Release(ref device);
            }
        }

        private static IntPtr FindMonitor(string sourceName)
        {
            IntPtr result = IntPtr.Zero;
            Native.MonitorEnum callback = delegate(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data)
            {
                var info = new MonitorInfoEx(); info.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
                if (Native.GetMonitorInfo(monitor, ref info) && string.Equals(info.Device, sourceName, StringComparison.OrdinalIgnoreCase))
                { result = monitor; return false; }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }

        private static T Method<T>(IntPtr instance, int slot) where T : class
        {
            if (instance == IntPtr.Zero) throw new InvalidOperationException("GPU 接口为空。");
            IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static IntPtr Query(IntPtr instance, Guid iid)
        {
            IntPtr result;
            Check(Marshal.QueryInterface(instance, in iid, out result), "查询 GPU 接口 " + iid);
            return result;
        }

        private static IntPtr TryQuery(IntPtr instance, Guid iid)
        {
            IntPtr result;
            return Marshal.QueryInterface(instance, in iid, out result) >= 0 ? result : IntPtr.Zero;
        }

        private static IntPtr ActivationFactory(string className, Guid iid)
        {
            IntPtr text = IntPtr.Zero, factory;
            Check(Native.WindowsCreateString(className, className.Length, out text), "创建 Windows Runtime 类名");
            try
            {
                Check(Native.RoGetActivationFactory(text, ref iid, out factory), "启用 " + className);
                return factory;
            }
            finally { if (text != IntPtr.Zero) Native.WindowsDeleteString(text); }
        }

        private static void Close(IntPtr instance)
        {
            if (instance == IntPtr.Zero) return;
            IntPtr closable = TryQuery(instance, Native.IidClosable);
            if (closable == IntPtr.Zero) return;
            try { Method<SimpleHResultDelegate>(closable, 6)(closable); }
            finally { Release(ref closable); }
        }

        private static void Release(ref IntPtr instance)
        {
            IntPtr p = instance; instance = IntPtr.Zero;
            if (p != IntPtr.Zero) Marshal.Release(p);
        }

        private static void Check(int hr, string operation)
        {
            if (hr < 0) throw new COMException(operation + "（0x" + hr.ToString("X8") + "）", hr);
        }

        [StructLayout(LayoutKind.Sequential)] private struct SizeInt32 { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator, Denominator; }
        [StructLayout(LayoutKind.Sequential)] private struct ModeDesc
        { public uint Width, Height; public Rational RefreshRate; public uint Format, ScanlineOrdering, Scaling; }
        [StructLayout(LayoutKind.Sequential)] private struct SampleDesc { public uint Count, Quality; }
        [StructLayout(LayoutKind.Sequential)] private struct SwapChainDesc
        {
            public ModeDesc BufferDesc; public SampleDesc SampleDesc;
            public uint BufferUsage, BufferCount; public IntPtr OutputWindow;
            public int Windowed; public uint SwapEffect, Flags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format; public SampleDesc SampleDesc;
            public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct SamplerDesc
        {
            public uint Filter, AddressU, AddressV, AddressW;
            public float MipLODBias; public uint MaxAnisotropy, ComparisonFunc;
            public float Border0, Border1, Border2, Border3, MinLOD, MaxLOD;
        }
        [StructLayout(LayoutKind.Sequential)] private struct D3DBox { public uint Left, Top, Front, Right, Bottom, Back; }
        [StructLayout(LayoutKind.Sequential)] private struct D3DViewport { public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth; }
        [StructLayout(LayoutKind.Sequential)] private struct MappedSubresource { public IntPtr Data; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfoEx
        {
            public int Size; public NativeRect Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleHResultDelegate(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObjectDelegate(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSizeDelegate(IntPtr self, out SizeInt32 value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetParentDelegate(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MakeAssociationDelegate(IntPtr self, IntPtr hwnd, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateCaptureItemDelegate(IntPtr self, IntPtr monitor, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFramePoolDelegate(IntPtr self, IntPtr device, uint format, int buffers, SizeInt32 size, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RecreatePoolDelegate(IntPtr self, IntPtr device, uint format, int buffers, SizeInt32 size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSessionDelegate(IntPtr self, IntPtr item, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetBooleanDelegate(IntPtr self, byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferDelegate(IntPtr self, uint buffer, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateViewDelegate(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTextureDelegate(IntPtr self, ref Texture2DDesc desc, IntPtr data, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSamplerDelegate(IntPtr self, ref SamplerDesc desc, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateShaderDelegate(IntPtr self, IntPtr code, UIntPtr length, IntPtr linkage, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetTextureDescDelegate(IntPtr self, out Texture2DDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyRegionDelegate(IntPtr self, IntPtr dst, uint dstSubresource, uint x, uint y, uint z, IntPtr src, uint srcSubresource, ref D3DBox box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResourceDelegate(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapDelegate(IntPtr self, IntPtr resource, uint subresource, uint map, uint flags, out MappedSubresource data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint subresource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearRtvDelegate(IntPtr self, IntPtr view, [In] float[] color);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetPointerArrayDelegate(IntPtr self, uint start, uint count, [In] IntPtr[] values);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTargetsDelegate(IntPtr self, uint count, [In] IntPtr[] views, IntPtr depthStencil);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetShaderDelegate(IntPtr self, IntPtr shader, IntPtr instances, uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewportDelegate(IntPtr self, uint count, ref D3DViewport viewport);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTopologyDelegate(IntPtr self, uint topology);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawDelegate(IntPtr self, uint vertexCount, uint startVertex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentDelegate(IntPtr self, uint interval, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetBlobPointerDelegate(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr GetBlobSizeDelegate(IntPtr self);

        private static class Native
        {
            internal static readonly Guid IidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            internal static readonly Guid IidTexture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            internal static readonly Guid IidCaptureInterop = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            internal static readonly Guid IidCaptureItem = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            internal static readonly Guid IidFramePoolStatics2 = new Guid("589b103f-6bbc-5df5-a991-02e28b3b66d5");
            internal static readonly Guid IidCaptureSession2 = new Guid("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");
            internal static readonly Guid IidDxgiAccess = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
            internal static readonly Guid IidClosable = new Guid("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);
            [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
            [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
            [DllImport("combase.dll")] internal static extern int RoInitialize(uint type);
            [DllImport("combase.dll")] internal static extern void RoUninitialize();
            [DllImport("combase.dll", CharSet = CharSet.Unicode)] internal static extern int WindowsCreateString(string source, int length, out IntPtr value);
            [DllImport("combase.dll")] internal static extern int WindowsDeleteString(IntPtr value);
            [DllImport("combase.dll")] internal static extern int RoGetActivationFactory(IntPtr className, ref Guid iid, out IntPtr value);
            [DllImport("d3d11.dll")] internal static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr value);
            [DllImport("d3d11.dll")] internal static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter, uint driverType, IntPtr software, uint flags,
                IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, ref SwapChainDesc swapChainDesc, out IntPtr swapChain,
                out IntPtr device, out uint featureLevel, out IntPtr context);
            [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)] internal static extern int D3DCompile(
                [MarshalAs(UnmanagedType.LPStr)] string source, UIntPtr sourceSize, string sourceName,
                IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
        }
    }
}
