 
//针对你提出的软解黑边不对称和硬解撕裂、拉动的问题，主要是由以下几个原因导致的：
//1. **软解黑边不对称**：软解渲染中调用 `RSSetViewport` 时，错误地将目标矩形的 `Right` 和 `Bottom`（绝对坐标）作为 `Width` 和 `Height`（相对大小）传入，导致视口大小计算错误、画面拉伸溢出。
//2. **硬解撕裂与拉动**：硬解渲染时在 `using` 块和 `finally` 中过早释放了输入纹理的 `inputView` 和外部解码器纹理引用。由于 D3D11 的 GPU 命令是异步执行的，资源在 `VideoProcessorBlt` 还未真正完成时就被释放或交还给了解码器，从而引发严重的画面撕裂和拉动。
//3. **资源频繁重建**：每帧硬解渲染都在重新获取 SwapChain BackBuffer 并重建 OutputView，不仅造成性能损耗，也易引发画面状态跳动。
//4. **缺少逐行扫描标识**：未显式设置视频流为逐行扫描，部分显卡驱动可能会错误地进行反交错处理，导致画面微抖。

//以下是经过深度优化与修复后的代码：

//```csharp
using MovieAgent.FFmpegDecoder;
using SharpGen.Runtime;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics; 

namespace MovieAgent.D3D11Window
{
    public class D3D11VideoRenderer : HwndHost
    {
        // Win32
        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
            WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProcDelegate _wndProc = WndProc;
        private static readonly IntPtr _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        private static bool _wndClassRegistered;

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPStr)] public string lpszClassName; public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        // D3D11 核心
        private ID3D11Device? _d3dDevice;
        private ID3D11DeviceContext? _d3dContext;
        private IDXGIFactory2? _dxgiFactory;
        private IDXGISwapChain1? _swapChain;
        private ID3D11RenderTargetView? _backBufferRtv;
        private ID3D11Texture2D? _backBufferTexture;

        // 视频处理器（硬解）
        private ID3D11VideoDevice? _videoDevice;
        private ID3D11VideoContext? _videoContext;
        private ID3D11VideoProcessor? _videoProcessor;
        private ID3D11VideoProcessorEnumerator? _vpEnumerator;
        private ID3D11VideoProcessorOutputView? _vpOutputView;
        private ID3D11VideoProcessorInputView? _lastInputView;
        private ID3D11Texture2D? _lastDecoderTexture;

        // 软解
        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11InputLayout? _inputLayout;
        private ID3D11Buffer? _vertexBuffer;
        private ID3D11SamplerState? _samplerState;
        private ID3D11Texture2D? _yTexture, _uvTexture;
        private ID3D11ShaderResourceView? _ySRV, _uvSRV;
        private bool _shaderPipelineReady;

        private IntPtr _hwnd, _parentHwnd;

        private bool _d3dInitialized, _disposed;
        private readonly object _resizeLock = new();
        private int _swapChainWidth, _swapChainHeight;
        private int _videoWidth, _videoHeight;

        private RawRect _cachedSourceRect, _cachedDestRect, _cachedOutputRect;
        private int _lastAppliedVideoW = -1, _lastAppliedVideoH = -1;
        private volatile bool _swapChainReady;

        // 软解队列
        private readonly ConcurrentQueue<(byte[] data, int stride, int width, int height)> _nv12Queue = new();
        private byte[]? _nv12Buffer;
        private int _renderQueued;
        private volatile bool _stopRequested;

        private VideoScaleMode _scaleMode = VideoScaleMode.Fit;

        public readonly Dispatcher _uiDispatcher;
        private static D3D11VideoRenderer? _currentRenderer;

        public D3D11VideoRenderer()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _currentRenderer = this;
            Loaded += (_, _) => { };
            Unloaded += (_, _) => CleanupD3D();
        }

        // ==================== HwndHost 重写 ====================
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            if (!_wndClassRegistered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = _wndProcPtr,
                    lpszClassName = "D3D11VideoRendererFinal"
                };
                if (RegisterClassEx(ref wc) == 0)
                {
                    DebugLogger.WriteLine($"窗口类注册失败: {Marshal.GetLastWin32Error()}");
                    return new HandleRef(this, IntPtr.Zero);
                }
                _wndClassRegistered = true;
            }

            _parentHwnd = hwndParent.Handle;

            _swapChainWidth = GetSystemMetrics(0); // SM_CXSCREEN
            _swapChainHeight = GetSystemMetrics(1); // SM_CYSCREEN
            DebugLogger.WriteLine($"交换链物理尺寸: {_swapChainWidth}x{_swapChainHeight}");

            _hwnd = CreateWindowEx(0, "D3D11VideoRendererFinal", "",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, _swapChainWidth, _swapChainHeight,
                _parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                DebugLogger.WriteLine($"创建子窗口失败: {Marshal.GetLastWin32Error()}");
                return new HandleRef(this, IntPtr.Zero);
            }

            InitializeD3D();
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            CleanupD3D();
            if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        }

        // ==================== 初始化 ====================
        private void InitializeD3D()
        {
            CreateDeviceAndContext();
            CreateSwapChain();
            _swapChainReady = true;
            CreateVideoProcessor();
            CreateShaderPipeline();
            _d3dInitialized = true;
            DebugLogger.WriteLine("D3D11 初始化成功");
        }

        private void CreateDeviceAndContext()
        {
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, 
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out _d3dDevice, out var fl, out _d3dContext);
            if (result.Failure)
            {
                DebugLogger.WriteLine("硬件设备创建失败，尝试 WARP...");
                result = D3D11.D3D11CreateDevice(null, DriverType.Warp, flags,
                    new[] { FeatureLevel.Level_11_0 }, out _d3dDevice, out fl, out _d3dContext);
                result.CheckError();
            }
            DebugLogger.WriteLine($"D3D11 设备就绪, FeatureLevel: {fl}");

            _videoDevice = _d3dDevice!.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _d3dContext!.QueryInterface<ID3D11VideoContext>();

            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            _dxgiFactory = adapter.GetParent<IDXGIFactory2>();

            using var dxgiDevice1 = _d3dDevice.QueryInterface<IDXGIDevice1>();
            dxgiDevice1.MaximumFrameLatency = 1;

            using var multiThread = _d3dDevice.QueryInterface<ID3D11Multithread>();
            multiThread.SetMultithreadProtected(true);
        }

        private void CreateSwapChain()
        {
            var desc = new SwapChainDescription1
            {
                BufferCount = 2,
                Width = (uint)_swapChainWidth,
                Height = (uint)_swapChainHeight,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                SwapEffect = SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore
            };
            _swapChain = _dxgiFactory!.CreateSwapChainForHwnd(_d3dDevice!, _hwnd, desc);
            _dxgiFactory.MakeWindowAssociation(_parentHwnd, WindowAssociationFlags.IgnoreAll);
            CreateBackBufferRtv();
            DebugLogger.WriteLine($"交换链创建: {_swapChainWidth}x{_swapChainHeight}");
        }

        private void CreateBackBufferRtv()
        {
            _backBufferRtv?.Dispose();
            _backBufferTexture?.Dispose();
            _backBufferTexture = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            _backBufferRtv = _d3dDevice!.CreateRenderTargetView(_backBufferTexture);
            CreateVpOutputView();
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // ==================== 视口计算 ====================
        public void SetScaleMode(VideoScaleMode mode)
        {
            if (_scaleMode != mode) { _scaleMode = mode; _lastAppliedVideoW = -1; }
        }

        private void CalculateDestRect(int videoW, int videoH, out RawRect dest, out RawRect output)
        {
            int dstW = _swapChainWidth, dstH = _swapChainHeight;
            output = new RawRect(0, 0, dstW, dstH);

            if (_scaleMode == VideoScaleMode.Stretch)
            {
                dest = output;
                return;
            }

            float vidAspect = (float)videoW / videoH;
            float dstAspect = (float)dstW / dstH;
            int drawW, drawH;

            if (_scaleMode == VideoScaleMode.Zoom)
            {
                if (vidAspect > dstAspect)
                {
                    drawH = dstH;
                    drawW = (int)(dstH * vidAspect);
                }
                else
                {
                    drawW = dstW;
                    drawH = (int)(dstW / vidAspect);
                }
            }
            else // Fit
            {
                if (vidAspect > dstAspect)
                {
                    drawW = dstW;
                    drawH = (int)(dstW / vidAspect) & ~1;
                }
                else
                {
                    drawH = dstH;
                    drawW = (int)(dstH * vidAspect) & ~1;
                }
            }

            int offX = (dstW - drawW) / 2;
            int offY = (dstH - drawH) / 2;
            dest = new RawRect(offX, offY, offX + drawW, offY + drawH);
        }

        private void UpdateViewportOnce()
        {
            if (_videoWidth <= 0 || _videoHeight <= 0) return;
            if (_videoWidth != _lastAppliedVideoW || _videoHeight != _lastAppliedVideoH)
            {
                _lastAppliedVideoW = _videoWidth; _lastAppliedVideoH = _videoHeight;
                CalculateDestRect(_videoWidth, _videoHeight, out var dest, out var output);
                _cachedSourceRect = new RawRect(0, 0, _videoWidth, _videoHeight);
                _cachedDestRect = dest;
                _cachedOutputRect = output;
                if (_videoContext != null && _videoProcessor != null)
                {
                    _videoContext.VideoProcessorSetStreamSourceRect(_videoProcessor, 0, true, _cachedSourceRect);
                    _videoContext.VideoProcessorSetStreamDestRect(_videoProcessor, 0, true, _cachedDestRect);
                    _videoContext.VideoProcessorSetOutputTargetRect(_videoProcessor, true, _cachedOutputRect);
                }
            }
        }

        // ==================== 视频处理器（硬解） ====================
        private void CreateVideoProcessor()
        {
            if (_videoDevice == null) return;
            var vpDesc = new VideoProcessorContentDescription
            {
                Usage = VideoUsage.PlaybackNormal,
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(60, 1),
                OutputFrameRate = new Rational(60, 1),
                InputWidth = 1,
                InputHeight = 1,
                OutputWidth = (uint)_swapChainWidth,
                OutputHeight = (uint)_swapChainHeight
            };
            _vpEnumerator = _videoDevice.CreateVideoProcessorEnumerator(vpDesc);
            _videoProcessor = _videoDevice.CreateVideoProcessor(_vpEnumerator, 0);
            ConfigureVideoProcessorDefaults();
            CreateVpOutputView();
        }

        private void RecreateVideoProcessor(int vw, int vh)
        {
            if (_videoDevice == null) return;
            SafeDispose(ref _videoProcessor); SafeDispose(ref _vpEnumerator); SafeDispose(ref _vpOutputView);
            var vpDesc = new VideoProcessorContentDescription
            {
                Usage = VideoUsage.PlaybackNormal,
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)vw,
                InputHeight = (uint)vh,
                OutputWidth = (uint)_swapChainWidth,
                OutputHeight = (uint)_swapChainHeight,
                InputFrameRate = new Rational(60, 1),
                OutputFrameRate = new Rational(60, 1)
            };
            try
            {
                _vpEnumerator = _videoDevice.CreateVideoProcessorEnumerator(vpDesc);
                _videoProcessor = _videoDevice.CreateVideoProcessor(_vpEnumerator, 0);
                ConfigureVideoProcessorDefaults();
                CreateVpOutputView();
            }
            catch (Exception ex) { DebugLogger.WriteLine($"VideoProcessor 重建失败: {ex.Message}"); }
        }

        private void ConfigureVideoProcessorDefaults()
        {
            if (_videoProcessor == null || _videoContext == null) return;
            _videoContext.VideoProcessorSetOutputColorSpace(_videoProcessor, new VideoProcessorColorSpace
            { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 2 });
            _videoContext.VideoProcessorSetOutputBackgroundColor(_videoProcessor, false,
                new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor, 0, false);
            _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor, 0, VideoFrameFormat.Progressive);
        }

        private void CreateVpOutputView()
        {
            _vpOutputView?.Dispose();
            if (_videoDevice == null || _vpEnumerator == null || _backBufferTexture == null) return;
            using var resource = _backBufferTexture.QueryInterface<ID3D11Resource>();
            var outputDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
            };
            _vpOutputView = _videoDevice.CreateVideoProcessorOutputView(resource, _vpEnumerator, outputDesc);
        }

        // ==================== 硬解渲染（最终版） ====================
        public void RenderD3D11VATexture(IntPtr texturePtr, int width, int height, uint arraySlice = 0)
        {
            if (_stopRequested || !_d3dInitialized || _videoContext == null || _videoDevice == null ||
                _vpEnumerator == null || _videoProcessor == null) return;
            if (!_swapChainReady) return;

            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(() => RenderD3D11VATexture(texturePtr, width, height, arraySlice));
                return;
            }
            if (texturePtr == IntPtr.Zero) return;

            lock (_resizeLock)
            {
                // 释放上一帧的输入资源，确保 GPU 已完成上一帧的 Blt
                _lastInputView?.Dispose();
                _lastInputView = null;
                _lastDecoderTexture?.Release();
                _lastDecoderTexture = null;

                ID3D11Texture2D? decoderTexture = null;
                try
                {
                    decoderTexture = new ID3D11Texture2D(texturePtr);
                    decoderTexture.AddRef();   // 防止外部提前释放

                    var texDesc = decoderTexture.Description;
                    if ((_vpEnumerator.CheckVideoProcessorFormat(texDesc.Format) & VideoProcessorFormatSupport.Input) == 0) return;
                    if ((texDesc.BindFlags & (BindFlags.Decoder | BindFlags.VideoEncoder)) == 0) return;

                    _videoWidth = width; _videoHeight = height;
                    if (_lastAppliedVideoW != width || _lastAppliedVideoH != height)
                        RecreateVideoProcessor(width, height);
                    UpdateViewportOnce();

                    if (_vpOutputView == null) return;

                    uint safeIndex = (uint)Math.Min(arraySlice, texDesc.ArraySize - 1);
                    var inputDesc = new VideoProcessorInputViewDescription
                    {
                        FourCC = 0x3231564E,
                        ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = safeIndex }
                    };

                    var inputView = _videoDevice.CreateVideoProcessorInputView(decoderTexture, _vpEnumerator, inputDesc);
                    if (inputView == null) return;

                    _lastInputView = inputView;
                    _lastDecoderTexture = decoderTexture;
                    decoderTexture = null; // 转移所有权，防止 finally 释放

                    _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor, 0, false);
                    _videoContext.VideoProcessorSetStreamColorSpace(_videoProcessor, 0, new VideoProcessorColorSpace
                    { Usage = 0, YCbCr_xvYCC = 0, Nominal_Range = 2, RGB_Range = 0, YCbCr_Matrix = 1 });

                    var streams = new[]
                    {
                        new VideoProcessorStream
                        {
                            Enable = true, OutputIndex = 0, InputFrameOrField = 0,
                            PastFrames = 0, FutureFrames = 0, InputSurface = _lastInputView
                        }
                    };

                    var result = _videoContext.VideoProcessorBlt(_videoProcessor, _vpOutputView, 0, 1, streams);
                    if (result.Failure)
                    {
                        DebugLogger.WriteLine($"Blt 失败: 0x{result.Code:X}");
                        return;
                    }

                    _d3dContext.Flush();
                    _swapChain!.Present(1, PresentFlags.None);
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"硬解渲染异常: {ex.Message}");
                }
                finally
                {
                    decoderTexture?.Release(); // 如果未转移所有权(中途失败退出)，则释放
                }
            }
        }

        // ==================== 软解渲染（保持原有队列+丢弃逻辑） ====================
        private const string NV12_VS = @"
struct VS_INPUT { float2 pos : POSITION; float2 tex : TEXCOORD; };
struct PS_INPUT { float4 pos : SV_POSITION; float2 tex : TEXCOORD; };
PS_INPUT main_vs(VS_INPUT input) {
    PS_INPUT output;
    output.pos = float4(input.pos, 0, 1);
    output.tex = input.tex;
    return output;
}";

        private const string NV12_PS = @"
Texture2D<float> texY : register(t0);
Texture2D<float2> texUV : register(t1);
SamplerState samplerState : register(s0);
float4 main_ps(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET {
    float y = texY.Sample(samplerState, tex);
    float2 uv = texUV.Sample(samplerState, tex);
    float u = uv.x - 0.5, v = uv.y - 0.5;
    float r = saturate(y + 1.402 * v);
    float g = saturate(y - 0.344 * u - 0.714 * v);
    float b = saturate(y + 1.772 * u);
    return float4(r, g, b, 1);
}";

        private void CreateShaderPipeline()
        {
            if (_d3dDevice == null) return;
            try
            {
                var vsBlob = Compiler.Compile(NV12_VS, "main_vs", "VertexShader", "vs_5_0");
                var psBlob = Compiler.Compile(NV12_PS, "main_ps", "PixelShader", "ps_5_0");
                _vertexShader = _d3dDevice.CreateVertexShader(vsBlob.Span);
                _pixelShader = _d3dDevice.CreatePixelShader(psBlob.Span);

                var elements = new[] {
                    new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
                };
                _inputLayout = _d3dDevice.CreateInputLayout(elements, vsBlob.Span);

                var vertexData = new float[] { -1, -1, 0, 1, -1, 1, 0, 0, 1, -1, 1, 1, 1, 1, 1, 0 };
                var vbDesc = new BufferDescription((uint)(vertexData.Length * sizeof(float)), BindFlags.VertexBuffer,
                    ResourceUsage.Default, CpuAccessFlags.None);
                var handle = GCHandle.Alloc(vertexData, GCHandleType.Pinned);
                try { _vertexBuffer = _d3dDevice.CreateBuffer(vbDesc, new SubresourceData(handle.AddrOfPinnedObject(), (uint)(vertexData.Length * sizeof(float)))); }
                finally { handle.Free(); }

                var samplerDesc = new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    MinLOD = 0,
                    MaxLOD = float.MaxValue
                };
                _samplerState = _d3dDevice.CreateSamplerState(samplerDesc);
                _shaderPipelineReady = true;
                System.Diagnostics.Debug.WriteLine("着色器管线就绪");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"着色器管线创建失败: {ex.Message}"); CleanupShaderPipeline(); }
        }

        public void UpdateFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height,
            int yStride = 0, int uStride = 0, int vStride = 0)
        {
            if (_stopRequested || !_swapChainReady) return;
            if (yPlane == null || uPlane == null || vPlane == null || width <= 0 || height <= 0) return;

            byte[] nv12 = ConvertYUV420PToNV12(yPlane, uPlane, vPlane, width, height, yStride, uStride, vStride);
            _nv12Queue.Enqueue((nv12, width, width, height));

            if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
        }

        private void RenderYUVFrame()
        {
            if (_stopRequested) { Interlocked.Exchange(ref _renderQueued, 0); return; }
            if (!_swapChainReady || !_d3dInitialized || _d3dContext == null || !_shaderPipelineReady || _backBufferRtv == null)
                goto Reschedule;

            byte[]? latestData = null;
            int stride = 0, w = 0, h = 0;
            while (_nv12Queue.TryDequeue(out var frame)) { latestData = frame.data; stride = frame.stride; w = frame.width; h = frame.height; }
            if (latestData == null) { Interlocked.Exchange(ref _renderQueued, 0); return; }

            try
            {
                EnsureYUVTextures(w, h);
                if (_yTexture == null || _uvTexture == null) return;

                unsafe
                {
                    fixed (byte* p = latestData)
                    {
                        int ySize = stride * h;
                        _d3dContext.UpdateSubresource(_yTexture, 0, new Box(0, 0, 0, w, h, 1), (IntPtr)p, (uint)stride, 0);
                        _d3dContext.UpdateSubresource(_uvTexture, 0, new Box(0, 0, 0, w / 2, h / 2, 1), (IntPtr)(p + ySize), (uint)stride, 0);
                    }
                }

                CalculateDestRect(w, h, out var dest, out _);
                _d3dContext.OMSetRenderTargets(_backBufferRtv);
                _d3dContext.RSSetViewport(new Viewport(0, 0, _swapChainWidth, _swapChainHeight));
                _d3dContext.ClearRenderTargetView(_backBufferRtv, new Color4(0, 0, 0));

                // 修复黑边不对称：Viewport第三和第四个参数是Width和Height，而非Right和Bottom绝对坐标
                _d3dContext.RSSetViewport(new Viewport(dest.Left, dest.Top, dest.Right - dest.Left, dest.Bottom - dest.Top));

                _d3dContext.VSSetShader(_vertexShader);
                _d3dContext.PSSetShader(_pixelShader);
                _d3dContext.IASetInputLayout(_inputLayout);
                _d3dContext.PSSetShaderResources(0, new[] { _ySRV });
                _d3dContext.PSSetShaderResources(1, new[] { _uvSRV });
                _d3dContext.PSSetSampler(0, _samplerState);
                _d3dContext.IASetVertexBuffer(0, _vertexBuffer, 16, 0);
                _d3dContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                _d3dContext.Draw(4, 0);

                _d3dContext.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());
                _d3dContext.PSSetShaderResources(1, Array.Empty<ID3D11ShaderResourceView>());

                // 开启垂直同步防止软解撕裂
                _swapChain!.Present(1, PresentFlags.None);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"软解渲染异常: {ex.Message}"); }
            finally
            {
                if (_stopRequested) { Interlocked.Exchange(ref _renderQueued, 0); }
                if (!_nv12Queue.IsEmpty)
                    _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
                else
                    Interlocked.Exchange(ref _renderQueued, 0);
            }
            return;

        Reschedule:
            if (_stopRequested) { Interlocked.Exchange(ref _renderQueued, 0); return; }
            if (!_nv12Queue.IsEmpty)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
            else
                Interlocked.Exchange(ref _renderQueued, 0);
        }

        private void EnsureYUVTextures(int width, int height)
        {
            if (_yTexture != null && _uvTexture != null && _videoWidth == width && _videoHeight == height) return;
            CleanupYUVTextures();
            _videoWidth = width; _videoHeight = height;
            if (_d3dDevice == null) return;

            _yTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource
            });
            _ySRV = _d3dDevice.CreateShaderResourceView(_yTexture);

            _uvTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)(width / 2),
                Height = (uint)(height / 2),
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource
            });
            _uvSRV = _d3dDevice.CreateShaderResourceView(_uvTexture);
        }

        private byte[] ConvertYUV420PToNV12(byte[] y, byte[] u, byte[] v, int width, int height,
            int yStride, int uStride, int vStride)
        {
            int ySize = width * height, uvWidth = width / 2, uvHeight = height / 2;
            int neededSize = ySize + uvWidth * uvHeight * 2;
            if (_nv12Buffer == null || _nv12Buffer.Length < neededSize) _nv12Buffer = new byte[neededSize];
            byte[] nv12 = _nv12Buffer;
            for (int row = 0; row < height; row++) Array.Copy(y, row * yStride, nv12, row * width, width);
            int uvOff = ySize;
            for (int row = 0; row < uvHeight; row++)
                for (int col = 0; col < uvWidth; col++)
                {
                    nv12[uvOff + row * uvWidth * 2 + col * 2] = u[row * uStride + col];
                    nv12[uvOff + row * uvWidth * 2 + col * 2 + 1] = v[row * vStride + col];
                }
            return nv12;
        }

        // ==================== 公共 API ====================
        public ID3D11Device? GetDevice() => _d3dDevice;

        public void ClearScreen()
        {
            if (_stopRequested || !_d3dInitialized || _backBufferRtv == null || _d3dContext == null) return;
            _d3dContext.ClearRenderTargetView(_backBufferRtv, new Color4(0, 0, 0));
            _d3dContext.Flush();
            _swapChain?.Present(1, PresentFlags.None);
        }

        // ==================== 清理 ====================
        private void CleanupShaderPipeline()
        {
            _shaderPipelineReady = false;
            SafeDispose(ref _vertexShader); SafeDispose(ref _pixelShader);
            SafeDispose(ref _inputLayout); SafeDispose(ref _vertexBuffer);
            SafeDispose(ref _samplerState);
        }

        private void CleanupYUVTextures()
        {
            SafeDispose(ref _ySRV); SafeDispose(ref _uvSRV);
            SafeDispose(ref _yTexture); SafeDispose(ref _uvTexture);
        }

        private void CleanupD3D()
        {
            _stopRequested = true;
            _d3dInitialized = false;
            _d3dContext?.ClearState(); _d3dContext?.Flush();
            CleanupShaderPipeline();
            CleanupYUVTextures();

            _lastInputView?.Dispose();
            _lastInputView = null;
            _lastDecoderTexture?.Release();
            _lastDecoderTexture = null;

            SafeDispose(ref _vpOutputView);
            SafeDispose(ref _backBufferRtv); SafeDispose(ref _backBufferTexture);
            SafeDispose(ref _swapChain); SafeDispose(ref _dxgiFactory);
            SafeDispose(ref _videoProcessor); SafeDispose(ref _vpEnumerator);
            SafeDispose(ref _videoContext); SafeDispose(ref _videoDevice);
            SafeDispose(ref _d3dContext); SafeDispose(ref _d3dDevice);
        }

        private static void SafeDispose<T>(ref T? obj) where T : class, IDisposable
        {
            obj?.Dispose();
            obj = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing) CleanupD3D();
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
