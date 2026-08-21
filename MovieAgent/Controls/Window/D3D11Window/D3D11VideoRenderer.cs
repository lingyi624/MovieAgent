using MovieAgent.FFmpegDecoder;
using SharpGen.Runtime;
using System;
using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
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

        private const uint WM_SIZE = 0x0005;
        private const int SIZE_RESTORED = 0;

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
        private ID3D11VideoContext1? _videoContext1; // Win10 1703+，支持 DXGI_COLOR_SPACE 精确色彩空间（HDR色调映射必需）
        private int _lastColorSpaceMode = -1; // -1未设置, 0=SDR, 1=HDR PQ直通(着色器色调映射), 2=HDR驱动色调映射
        private ID3D11VideoProcessor? _videoProcessor;
        private ID3D11VideoProcessorEnumerator? _vpEnumerator;
        private ID3D11VideoProcessorOutputView? _vpOutputView;
        
        // 五缓冲输入视图：GPU 异步执行 Blt，必须保证释放前 GPU 已完成
        // 槽位 0=当前帧, 1-4=前4帧(GPU可能在使用)
        private const int InputViewSlots = 5;
        private readonly ID3D11VideoProcessorInputView?[] _inputViews = new ID3D11VideoProcessorInputView?[InputViewSlots];
        private int _inputViewIndex = 0;

        // 软解
        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11InputLayout? _inputLayout;
        private ID3D11Buffer? _vertexBuffer;
        private ID3D11SamplerState? _samplerState;
        private ID3D11Texture2D? _yTexture, _uvTexture;
        private ID3D11ShaderResourceView? _ySRV, _uvSRV;
        private bool _shaderPipelineReady;

        // HDR色调映射管线（PQ直通 + 着色器HDR→SDR）
        // 候选顺序：FP16（全精度）→ UNorm16 → UNorm10（HDR10线上格式）。
        // 驱动可能对CheckVideoProcessorFormat报告支持但实际Blt返回E_FAIL（如Intel拒绝UNorm16作PQ输出），
        // 因此Blt失败时通过 _hdrFormatIdx 运行时切换到下一个候选格式。
        private static readonly Format[] HdrOutputFormats =
            { Format.R16G16B16A16_Float, Format.R16G16B16A16_UNorm, Format.R10G10B10A2_UNorm };
        private ID3D11PixelShader? _hdrPixelShader;
        private ID3D11Texture2D? _hdrTexture;          // Float/UNorm16/UNorm10，存放PQ编码的BT.2020 RGB
        private ID3D11ShaderResourceView? _hdrSrv;
        private ID3D11VideoProcessorOutputView? _vpOutputViewHdr;
        private int _hdrTexW, _hdrTexH;
        private Format _hdrFormat = Format.R16G16B16A16_Float; // VP输出中间纹理格式（按驱动支持度探测）
        private int _hdrFormatIdx;                     // 当前候选索引（Blt运行时故障转移）
        private bool _hdrPipelineUnavailable;          // 所有候选格式都不支持时回退驱动色调映射
        private bool _isDolbyVision;                   // 杜比视界内容（DV使用与HDR10相同的PQ EOTF，自定义着色器同样适用）

        /// <summary>设置杜比视界标志（由解码器引擎在检测到DV时调用）</summary>
        public bool IsDolbyVision
        {
            get => _isDolbyVision;
            set => _isDolbyVision = value;
        }

        // HDR 传输特性（PQ/HLG）：SDR 10bit 视频同样输出 P010 纹理，
        // 不能仅凭 P010 判定 HDR——SDR 数据被当 PQ 解码会产生漫画风格青黄色偏。
        private bool _isPqTransfer;

        /// <summary>设置HDR传输特性标志（PQ/HLG/DV，由解码器根据 color_trc / DOVI 配置判定）</summary>
        public bool IsPqTransfer
        {
            get => _isPqTransfer;
            set => _isPqTransfer = value;
        }

        // ICtCp 专用管线（杜比视界 Profile 5）：帧数据为 ICtCp 色彩空间而非 YCbCr，
        // VideoProcessor 的 YCbCr 矩阵会将其打乱（绿色/紫色画面），必须绕过 VP 直接采样 P010 平面，
        // 在着色器内完成 ICtCp→LMS→PQ EOTF→BT.2020→BT.709→色调映射 的全流程。
        private bool _isIctcpInput;
        private ID3D11PixelShader? _ictcpPixelShader;
        private readonly ID3D11ShaderResourceView?[] _ictcpYSrvs = new ID3D11ShaderResourceView?[InputViewSlots];  // P010 plane0 (I)
        private readonly ID3D11ShaderResourceView?[] _ictcpUVSrvs = new ID3D11ShaderResourceView?[InputViewSlots]; // P010 plane1 (Ct,Cp)
        private bool _ictcpPipelineBroken;              // 着色器编译/SRV创建失败时回退VP路径

        /// <summary>设置ICtCp输入标志（解码器首帧检测frame->colorspace==ICTCP后由外部传入）</summary>
        public bool IsIctcpInput
        {
            get => _isIctcpInput;
            set => _isIctcpInput = value;
        }

        // 杜比视界 RPU 元数据（ycc_to_rgb_matrix），用于 ICtCp 着色器替代标准矩阵
        private DoviRenderMetadata? _doviMetadata;
        private ID3D11Buffer? _doviConstantBuffer;
        private bool _doviConstantBufferDirty = true;
        // RPU 重塑曲线常量缓冲（b1: rs_meta[3] + rs_pivots[6] + rs_coeffs[24] + rs_mmr[144] = 177 float4 = 2832 字节）
        private ID3D11Buffer? _doviReshapeBuffer;

        /// <summary>设置杜比视界 RPU 渲染元数据（每帧同步）</summary>
        public DoviRenderMetadata? DoviMetadata
        {
            get => _doviMetadata;
            set
            {
                _doviMetadata = value;
                _doviConstantBufferDirty = true;
            }
        }

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
        private volatile bool _isSeeking; // Seek期间阻止渲染，避免旧帧导致音画不同步
        private bool _colorDiagLogged; // 首次渲染时输出色彩管线诊断日志
        private int _renderFrameCount;  // 渲染帧计数器（用于周期性诊断日志）

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
        private const int WM_MOUSEMOVE = 0x0200;

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
            _cleanupDone = false; // 设备重建成功，允许后续（如再次设备丢失时）重新清理
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
            try
            {
                _videoContext1 = _videoContext.QueryInterface<ID3D11VideoContext1>();
                DebugLogger.WriteLine("D3D11VideoContext1 可用 — 现代色彩空间API（支持HDR）");
            }
            catch
            {
                _videoContext1 = null; // 旧系统不支持，回退旧色彩空间API
                DebugLogger.WriteLine("D3D11VideoContext1 不可用 — 回退旧色彩空间API（Stream Nominal_Range=1 确保Limited→Full）");
            }

            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            _dxgiFactory = adapter.GetParent<IDXGIFactory2>();

            using var dxgiDevice1 = _d3dDevice.QueryInterface<IDXGIDevice1>();
            dxgiDevice1.MaximumFrameLatency = 1;
            // 三重缓冲 + MaxFrameLatency=1：GPU 有 3 个缓冲可轮转但只预排队 1 帧，
            // 既保证低延迟又避免 BufferCount=2 时因帧未就绪导致的重复显示（闪烁）

            using var multiThread = _d3dDevice.QueryInterface<ID3D11Multithread>();
            multiThread.SetMultithreadProtected(true);
        }

        private void CreateSwapChain()
        {
            var desc = new SwapChainDescription1
            {
                BufferCount = 3, // 三重缓冲：消除视频播放时的帧抖动，避免双重缓冲下帧未就绪时重复显示同一帧
                Width = (uint)_swapChainWidth,
                Height = (uint)_swapChainHeight,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                SwapEffect = SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None // 视频播放不需要 AllowTearing，VSYNC 同步更稳定
            };
            _swapChain = _dxgiFactory!.CreateSwapChainForHwnd(_d3dDevice!, _hwnd, desc);
            _dxgiFactory.MakeWindowAssociation(_parentHwnd, WindowAssociationFlags.IgnoreAll);
            CreateBackBufferRtv();
            DebugLogger.WriteLine($"交换链创建: {_swapChainWidth}x{_swapChainHeight}");

            //// 获取当前输出
            //using var output = _swapChain.GetContainingOutput();
            //var desc2 = output.Description;
            //// 检查 HDR 支持：通过 IDXGIOutput6 查询色彩空间
            //var output6 = output.QueryInterface<IDXGIOutput6>();
            //if (output6 != null)
            //{
            //    var desc1 = output6.Description1;
            //    // desc1.ColorSpace 是否为 HDR 空间，或者通过 CheckHardwareCompositionSupport 等判断
            //    // 但直接判断更简单：检查系统是否报告 HDR 支持（需要 Windows 10 1703+）
            //}

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
            // 处理 WM_SIZE：当子窗口尺寸变化时，调整交换链尺寸
            // 这是消除画面抖动的关键：交换链尺寸必须与显示区域匹配
            if (msg == WM_SIZE && _currentRenderer != null)
            {
                int newWidth = (int)(lParam & 0xFFFF);
                int newHeight = (int)((lParam >> 16) & 0xFFFF);
                if (newWidth > 0 && newHeight > 0)
                {
                    _currentRenderer.OnWindowSizeChanged(newWidth, newHeight);
                }
            } 
                    return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    HandleMouseMove(lParam);
                    // 如果你不希望此消息继续被默认处理，可以将 handled 设为 true
                     handled = true;
                    break; 
            }

            // 调用基类方法，确保其他消息能被正常处理
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }
        private void HandleMouseMove(IntPtr lParam)
        {
            // 解析坐标（可选，如果不需要可以省略）
            int x = lParam.ToInt32() & 0xFFFF;
            int y = (lParam.ToInt32() >> 16) & 0xFFFF;

            // 创建鼠标移动事件参数（使用 MouseEventArgs）
            var args = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.MouseMoveEvent,
                Source = this
            };

            // 通过 InputManager 引发事件
            InputManager.Current.ProcessInput(args);
        }
        // ==================== 交换链尺寸调整 ====================
        private void OnWindowSizeChanged(int newWidth, int newHeight)
        {
            if (_d3dDevice == null || _swapChain == null || !_d3dInitialized) return;
            if (newWidth == _swapChainWidth && newHeight == _swapChainHeight) return;
            if (newWidth <= 0 || newHeight <= 0) return;

            // 必须在 UI 线程执行，因为涉及 D3D 资源重建
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnWindowSizeChanged(newWidth, newHeight));
                return;
            }

            try
            {
                // 强制 GPU 完成所有挂起命令
                _d3dContext?.ClearState();
                _d3dContext?.Flush();

                // 释放依赖于交换链缓冲区的资源
                SafeDispose(ref _vpOutputView);
                SafeDispose(ref _backBufferRtv);
                SafeDispose(ref _backBufferTexture);

                // 调整交换链缓冲区尺寸
                _swapChain.ResizeBuffers(3, (uint)newWidth, (uint)newHeight,
                    Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                _swapChainWidth = newWidth;
                _swapChainHeight = newHeight;

                // 重建 RTV 和 VpOutputView
                CreateBackBufferRtv();

                // 重建 VideoProcessor 以适配新尺寸
                if (_videoWidth > 0 && _videoHeight > 0)
                {
                    _lastAppliedVideoW = -1; // 强制更新视口
                    RecreateVideoProcessor(_videoWidth, _videoHeight);
                }

                DebugLogger.WriteLine($"交换链尺寸调整: {newWidth}x{newHeight}");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"交换链尺寸调整失败: {ex.Message}");
            }
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
            _lastColorSpaceMode = -1; // 新处理器需重新设置色彩空间
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
            ApplyColorSpace(false, false); // 默认SDR色彩空间
            _videoContext.VideoProcessorSetOutputBackgroundColor(_videoProcessor, false,
                new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor, 0, false);
            _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor, 0, VideoFrameFormat.Progressive);
        }

        /// <summary>
        /// 配置视频处理器色彩空间。
        /// 模式0 SDR (NV12/BT.709/Limited): 输入=YcbcrStudioG22LeftP709，输出=sRGB。
        /// 模式1 HDR PQ直通 (P010/BT.2020/PQ): 输入=YcbcrStudioG2084LeftP2020，输出=RgbFullG2084NoneP2020，
        ///           VP只做YCbCr→RGB矩阵转换，PQ曲线保持原样写入RGBA16F，由自定义着色器做HDR→SDR色调映射。
        /// 模式2 HDR驱动色调映射（回退）: 输入=BT.2020/PQ，输出=sRGB，驱动内置HDR→SDR（白点偏低，画面偏灰）。
        /// 注意：旧版 VideoProcessorSetStreamColorSpace 结构体无法表达 BT.2020/PQ。
        /// </summary>
        private void ApplyColorSpace(bool isHdr, bool pqPassthrough)
        {
            if (_videoProcessor == null || _videoContext == null) return;

            int mode = !isHdr ? 0 : (pqPassthrough ? 1 : 2);
            if (_lastColorSpaceMode == mode) return;

            if (_videoContext1 != null)
            {
                try
                {
                    switch (mode)
                    {
                        case 1:
                            _videoContext1.VideoProcessorSetStreamColorSpace1(_videoProcessor, 0, ColorSpaceType.YcbcrStudioG2084LeftP2020);
                            _videoContext1.VideoProcessorSetOutputColorSpace1(_videoProcessor, ColorSpaceType.RgbFullG2084NoneP2020);
                            DebugLogger.WriteLine($"色彩空间(现代API): HDR PQ直通(BT.2020) → RGBA16F，着色器色调映射 [mode={mode}]");
                            break;
                        case 2:
                            _videoContext1.VideoProcessorSetStreamColorSpace1(_videoProcessor, 0, ColorSpaceType.YcbcrStudioG2084LeftP2020);
                            _videoContext1.VideoProcessorSetOutputColorSpace1(_videoProcessor, ColorSpaceType.RgbFullG22NoneP709);
                            DebugLogger.WriteLine($"色彩空间(现代API): HDR 输入(BT.2020/PQ) → SDR 输出(sRGB)，驱动色调映射 [mode={mode}]");
                            break;
                        default:
                            _videoContext1.VideoProcessorSetStreamColorSpace1(_videoProcessor, 0, ColorSpaceType.YcbcrStudioG22LeftP709);
                            _videoContext1.VideoProcessorSetOutputColorSpace1(_videoProcessor, ColorSpaceType.RgbFullG22NoneP709);
                            DebugLogger.WriteLine($"色彩空间(现代API): SDR 输入(BT.709/Studio,Limited) → RGB Full(sRGB) [mode={mode}]");
                            break;
                    }
                    _lastColorSpaceMode = mode;
                    return;
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"ColorSpace1 设置失败，回退旧API: {ex.Message}");
                    _videoContext1 = null;
                }
            }

            // 旧API回退（无法表达HDR，仅SDR正确）
            // Nominal_Range=1 表示源是Limited Range(16-235)，VideoProcessor会做Limited→Full扩展
            _videoContext.VideoProcessorSetStreamColorSpace(_videoProcessor, 0, new VideoProcessorColorSpace
            { Usage = 0, YCbCr_xvYCC = 0, Nominal_Range = 1, RGB_Range = 0, YCbCr_Matrix = 1 });
            _videoContext.VideoProcessorSetOutputColorSpace(_videoProcessor, new VideoProcessorColorSpace
            { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 0 });
            DebugLogger.WriteLine($"色彩空间(旧API): 输入Limited→输出Full, BT.709 [mode={mode}]");
            _lastColorSpaceMode = mode;
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
        // 注意：FFmpegDecoderEngine 在 av_frame_unref 之前已将 ArraySlice 复制到私有纹理
        // 渲染器收到的 texturePtr 指向 FFmpeg 端的私有纹理（非数组），可直接使用，无需再复制
        public void RenderD3D11VATexture(IntPtr texturePtr, int width, int height, uint arraySlice = 0)
        {
            if (_stopRequested || _isSeeking || !_d3dInitialized || _d3dContext == null || _d3dDevice == null ||
                _videoContext == null || _videoDevice == null ||
                _vpEnumerator == null || _videoProcessor == null) return;
            if (!_swapChainReady || _swapChain == null) return;

            // 调用方已在 UI 线程调度，无需二次切换；若不在 UI 线程，简单丢弃以避免并发问题
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(DispatcherPriority.Render,
                    () => RenderD3D11VATexture(texturePtr, width, height, arraySlice));
                return;
            }
            if (texturePtr == IntPtr.Zero) return;

            // 检测设备丢失（如设备切换、驱动升级、TDR 等）
            if (CheckDeviceLost()) return;

            try
            {
                // 释放"3 帧前"使用的 InputView（环形缓冲：当前槽位即将被复用）
                _inputViews[_inputViewIndex]?.Dispose();
                _inputViews[_inputViewIndex] = null;
                _ictcpYSrvs[_inputViewIndex]?.Dispose();
                _ictcpYSrvs[_inputViewIndex] = null;
                _ictcpUVSrvs[_inputViewIndex]?.Dispose();
                _ictcpUVSrvs[_inputViewIndex] = null;

                // 用传入的纹理（FFmpeg 端私有纹理）创建 InputView，不持有引用，不释放
                var frameTexture = new ID3D11Texture2D(texturePtr);
                Texture2DDescription texDesc;
                try
                {
                    texDesc = frameTexture.Description;
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"获取纹理描述失败（停止中?），丢弃帧: {ex.Message}");
                    return;
                }
                if ((_vpEnumerator.CheckVideoProcessorFormat(texDesc.Format) & VideoProcessorFormatSupport.Input) == 0) return;
                if ((texDesc.BindFlags & (BindFlags.Decoder | BindFlags.VideoEncoder)) == 0) return;

                _videoWidth = width; _videoHeight = height;
                if (_lastAppliedVideoW != width || _lastAppliedVideoH != height)
                    RecreateVideoProcessor(width, height);
                UpdateViewportOnce();

                if (_vpOutputView == null) return;

                // 色彩管线诊断日志（首次 + 每120帧周期性输出）
                _renderFrameCount++;
                bool shouldDiag = !_colorDiagLogged || (_renderFrameCount % 120 == 0);
                if (shouldDiag)
                {
                    _colorDiagLogged = true;
                    DebugLogger.WriteLine($"===== COLOR PIPELINE DIAG (frame #{_renderFrameCount}) =====");
                    DebugLogger.WriteLine($"  icpInput   = {_isIctcpInput}");
                    DebugLogger.WriteLine($"  dolbyVision = {_isDolbyVision}");
                    DebugLogger.WriteLine($"  hdrPipeUnavail = {_hdrPipelineUnavailable}");
                    DebugLogger.WriteLine($"  icpBroken  = {_ictcpPipelineBroken}");
                    DebugLogger.WriteLine($"  vpCtx1     = {(_videoContext1 != null)}");
                    DebugLogger.WriteLine($"  csMode     = {_lastColorSpaceMode} (0=SDR,1=PQ-passthrough,2=driver-TM)");
                    DebugLogger.WriteLine($"  hdrFmt     = {_hdrFormat}");
                    DebugLogger.WriteLine($"  hdrTex     = {(_hdrTexture != null)}");
                    DebugLogger.WriteLine($"  hdrSrv     = {(_hdrSrv != null)}");
                    DebugLogger.WriteLine($"  hdrPS      = {(_hdrPixelShader != null)}");
                    DebugLogger.WriteLine($"  ictcpPS    = {(_ictcpPixelShader != null)}");
                    DebugLogger.WriteLine($"  swapChainW = {_swapChainWidth}");
                    DebugLogger.WriteLine($"  swapChainH = {_swapChainHeight}");
                    DebugLogger.WriteLine($"  videoW     = {_videoWidth}");
                    DebugLogger.WriteLine($"  videoH     = {_videoHeight}");
                    DebugLogger.WriteLine($"  texFmt     = {texDesc.Format}");
                    DebugLogger.WriteLine($"  texW       = {texDesc.Width}");
                    DebugLogger.WriteLine($"  texH       = {texDesc.Height}");
                    // RPU metadata dump
                    if (_doviMetadata != null)
                    {
                        var dm = _doviMetadata;
                        DebugLogger.WriteLine($"  dvMeta.Present = {dm.MetadataPresent}");
                        DebugLogger.WriteLine($"  dvMeta.HasMatrix = {dm.HasValidMatrix}");
                        if (dm.YccToRgbMatrix?.Length >= 9)
                            DebugLogger.WriteLine($"  dvMeta.Matrix = [{dm.YccToRgbMatrix[0]:F4} {dm.YccToRgbMatrix[1]:F4} {dm.YccToRgbMatrix[2]:F4}; {dm.YccToRgbMatrix[3]:F4} {dm.YccToRgbMatrix[4]:F4} {dm.YccToRgbMatrix[5]:F4}; {dm.YccToRgbMatrix[6]:F4} {dm.YccToRgbMatrix[7]:F4} {dm.YccToRgbMatrix[8]:F4}]");
                        if (dm.YccToRgbOffset?.Length >= 3)
                            DebugLogger.WriteLine($"  dvMeta.Offset = [{dm.YccToRgbOffset[0]:F4} {dm.YccToRgbOffset[1]:F4} {dm.YccToRgbOffset[2]:F4}]");
                        DebugLogger.WriteLine($"  dvMeta.HasReshape = {dm.HasValidReshape}");
                        if (dm.HasValidReshape && dm.Reshape != null)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                var rc = dm.Reshape[c];
                                if (rc != null && rc.NumPivots >= 2)
                                    DebugLogger.WriteLine($"  dvMeta.Reshape[{c}] np={rc.NumPivots} piv=[{string.Join(",", rc.Pivots.Take(rc.NumPivots).Select(p => $"{p:F4}"))}]");
                            }
                        }
                    }
                    else
                    {
                        DebugLogger.WriteLine($"  dvMeta     = null");
                    }
                    DebugLogger.WriteLine($"  bt2020to709 = [1.660 -0.588 -0.073; -0.125 1.133 -0.008; -0.018 -0.101 1.119] (row-first init)");
                    DebugLogger.WriteLine($"  toneMap    = BT.2446-1 Method A (k=10, REF_WHITE=100)");
                    DebugLogger.WriteLine("================================");
                }

                // 杜比视界 Profile 5（ICtCp 色彩空间）：绕过 VideoProcessor 的 YCbCr 矩阵，
                // 直接采样 P010 平面由 ICtCp 着色器完成全流程转换。
                // VP 路径会把 ICtCp 当作 BT.2020 YCbCr 解读，画面严重偏绿/偏紫。
                // 管线失败（PlaneSlice SRV 不支持/着色器编译失败）时回退 HDR10 路径。
                // SupportDolbyVisionDecoding=false：DV 解码已禁用，ICtCp 路径整体短路，
                // 所有 DV 内容统一走下方 HDR10 管线（PQ 直通 + 着色器色调映射）。
                if (FFmpegDecoderEngine.SupportDolbyVisionDecoding && _isIctcpInput
                    && texDesc.Format == Format.P010 && !_ictcpPipelineBroken && EnsureIctcpPipeline())
                {
                    if (_renderFrameCount <= 3 || _renderFrameCount % 120 == 0)
                        DebugLogger.WriteLine($"[ICtCp] 进入ICtCp渲染路径 (frame #{_renderFrameCount})");
                    if (RenderIctcpFrame(frameTexture))
                    {
                        _swapChain!.Present(1, PresentFlags.None);
                        _inputViewIndex = (_inputViewIndex + 1) % InputViewSlots;
                        return;
                    }
                    // ICtCp渲染失败，回退HDR10路径
                    DebugLogger.WriteLine($"[ICtCp] 渲染失败，回退HDR10路径 (frame #{_renderFrameCount})");
                }

                // 传入的纹理已经是 FFmpeg 端的私有纹理（ArraySize=1），arraySlice 应为 0
                uint safeIndex = (uint)Math.Min(arraySlice, texDesc.ArraySize - 1);
                var inputDesc = new VideoProcessorInputViewDescription
                {
                    FourCC = 0, // 0 = 使用纹理原生格式，自动适配 NV12/P010 等
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = safeIndex }
                };

                var inputView = _videoDevice.CreateVideoProcessorInputView(frameTexture, _vpEnumerator, inputDesc);
                if (inputView == null) return;

                _inputViews[_inputViewIndex] = inputView;

                _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor, 0, false);

                // 检测HDR格式：P010 只是 10bit YUV 载体，SDR 10bit 蓝光同样输出 P010。
                // 必须结合传输特性（PQ/HLG）或杜比视界标志判定，否则 SDR 数据被当
                // PQ 解码 + BT.2020→BT.709 二次色域变换，产生漫画风格青黄色偏。
                // PQ直通模式：VP只做矩阵转换，PQ曲线保持，由自定义着色器色调映射（管线不可用回退驱动映射）
                // 杜比视界使用与HDR10相同的PQ EOTF，自定义着色器同样适用（驱动色调映射无法正确处理DV的ICtCp色彩空间）
                bool isHdrInput = texDesc.Format == Format.P010 && (_isPqTransfer || _isDolbyVision);
                bool isDolbyVision = _isDolbyVision;
                if (isDolbyVision)
                    DebugLogger.WriteLine("[HDR] 杜比视界内容，使用自定义HDR着色器管线（PQ解码+BT.2020→BT.709色调映射）");
                ApplyColorSpace(isHdrInput, isHdrInput && EnsureHdrPipeline());
                // mode 1 = PQ直通生效：Blt输出到RGBA16F中间纹理，随后着色器色调映射到后缓冲
                bool pqPassthrough = _lastColorSpaceMode == 1 && _vpOutputViewHdr != null;

                var streams = new[]
                {
                    new VideoProcessorStream
                    {
                        Enable = true, OutputIndex = 0, InputFrameOrField = 0,
                        PastFrames = 0, FutureFrames = 0, InputSurface = _inputViews[_inputViewIndex]
                    }
                };

                var result = _videoContext.VideoProcessorBlt(_videoProcessor,
                    pqPassthrough ? _vpOutputViewHdr! : _vpOutputView, 0, 1, streams);

                // PQ直通Blt运行时故障转移：驱动可能探测支持但实际拒绝该中间格式（E_FAIL），
                // 逐个切换候选格式重试；全部失败则永久回退驱动色调映射（mode 2）
                if (pqPassthrough && result.Failure)
                {
                    while (result.Failure && TryNextHdrFormat())
                        result = _videoContext.VideoProcessorBlt(_videoProcessor, _vpOutputViewHdr!, 0, 1, streams);
                    if (result.Failure)
                    {
                        _hdrPipelineUnavailable = true;
                        ApplyColorSpace(true, false);
                        pqPassthrough = false;
                        DebugLogger.WriteLine("所有HDR中间格式Blt均失败，回退驱动色调映射");
                        result = _videoContext.VideoProcessorBlt(_videoProcessor, _vpOutputView, 0, 1, streams);
                    }
                }
                if (result.Failure)
                {
                    DebugLogger.WriteLine($"Blt 失败: 0x{result.Code:X}");
                    return;
                }

                // PQ直通：着色器完成 PQ解码→BT.2020→BT.709→色调映射→sRGB 后缓冲
                if (pqPassthrough)
                    RenderHdrToneMap();

                // 切换到下一个槽位：确保当前帧 InputView 在后续 2 帧内不被释放
                // Present(1) 启用 VSYNC，与 SwapChain 配合实现稳定帧节奏
                _swapChain!.Present(1, PresentFlags.None);

                _inputViewIndex = (_inputViewIndex + 1) % InputViewSlots;
            }
            catch (SharpGenException ex)
            {
                DebugLogger.WriteLine($"硬解渲染 SharpGen 异常: {ex.Message} (ResultCode=0x{ex.ResultCode:X})");
                HandleDeviceLost();
            }
            catch (Exception ex)
            {
                // 停止过程中资源已释放，NullReference/ExternalComponent异常属于正常清理竞态，不需记录
                if (!_stopRequested)
                    DebugLogger.WriteLine($"硬解渲染异常: {ex.Message}");
            }
        }

        // ==================== 设备丢失处理 ====================
        private bool CheckDeviceLost()
        {
            if (_d3dDevice == null) return true;
            try
            {
                // 检测设备removed：GetDeviceRemovedReason 不会抛异常
                var reason = _d3dDevice.DeviceRemovedReason;
                if (reason.Failure)
                {
                    DebugLogger.WriteLine($"D3D11 设备丢失: 0x{reason.Code:X}");
                    HandleDeviceLost();
                    return true;
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private void HandleDeviceLost()
        {
            try
            {
                DebugLogger.WriteLine("D3D11 设备丢失，尝试重建...");
                CleanupD3D();
                _stopRequested = false;
                _d3dInitialized = false;
                _lastAppliedVideoW = -1;
                _lastAppliedVideoH = -1;
                InitializeD3D();
                DebugLogger.WriteLine("D3D11 设备重建成功");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D11 设备重建失败: {ex.Message}");
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
    // Limited -> full range: Y [16,235]->[0,1], UV [16,240] center 128 -> [-0.5,+0.5]
    // NOTE: UV must scale by 224/255, else chroma amplitude -12 pct (washed out, dull green)
    // WARNING: keep this shader source ASCII-only. Vortice Compiler.Compile marshals to ANSI
    // but passes char count as byte length, so any non-ASCII char truncates the tail.
    y = (y - 16.0 / 255.0) * (255.0 / 219.0);
    float u = (uv.x - 128.0 / 255.0) * (255.0 / 224.0);
    float v = (uv.y - 128.0 / 255.0) * (255.0 / 224.0);
    // BT.709 coefficients (HD/4K content)
    float r = saturate(y + 1.5748 * v);
    float g = saturate(y - 0.1873 * u - 0.4681 * v);
    float b = saturate(y + 1.8556 * u);
    return float4(r, g, b, 1);
}";

        // HDR色调映射：PQ(ST.2084)解码 → BT.2020→BT.709 色域压缩 → 色调映射到SDR → gamma 2.2
        // 输入纹理 = VP输出（YCbCr BT.2020 PQ → RGB BT.2020 PQ，驱动只做矩阵转换不做色调映射）
        // HDR tone mapping: PQ(ST.2084) decode -> BT.2020->BT.709 gamut -> tone map -> sRGB gamma
        // BT.2020->BT.709 matrix from ITU-R BT.2087 (XYZ-bridged, linear-light domain).
        // Negative coeffs are correct: BT.2020 gamut is wider, so BT.709 primaries need negative
        // contributions from the other two BT.2020 primaries to reconstruct pure BT.709 colors.
        // ITU-R BT.2446-1 Method A: standard HDR -> SDR tone mapping.
        // Diffuse white: 100 cd/m2 (SDR reference). Peak: 1000 cd/m2 (default;
        // content metadata MaxCLL overrides this when available).
        // OOTF: gamma 1.2 (BT.1886 display model). Tone curve:
        //   V = log10(1 + (k-1) * p^0.5) / log10(k)   where k = peak / diffuse_white
        // WARNING: keep this shader source ASCII-only. Non-ASCII chars truncate the byte stream
        // in Vortice Compiler.Compile ANSI marshalling, causing X3004 compile errors.
        private const string HDR_TONE_MAP_PS = @"
        Texture2D<float4> texHdr : register(t0);
        SamplerState samplerState : register(s0);
        static const float PQ_M1 = 2610.0 / 16384.0;
        static const float PQ_M2 = 2523.0 / 4096.0 * 128.0;
        static const float PQ_C1 = 3424.0 / 4096.0;
        static const float PQ_C2 = 2413.0 / 4096.0 * 32.0;
        static const float PQ_C3 = 2392.0 / 4096.0 * 32.0;
        static const float TARGET_WHITE_NITS = 250.0;
        static const float KNEE = 2.0;
        static const float ROLLOFF = 1.5;

        float pq_to_nits(float x) {
            float p = pow(saturate(x), 1.0 / PQ_M2);
            float num = max(p - PQ_C1, 0.0);
            float den = max(PQ_C2 - PQ_C3 * p, 1e-6);
            return pow(num / den, 1.0 / PQ_M1) * 10000.0;
        }

        // Gamut compression: desaturate out-of-gamut colors toward luminance axis.
        // The inverse BT.2020->BT.709 matrix produces negative values for colors outside
        // BT.709 gamut. Blend them toward luminance instead of hard-clipping.
        float3 compress_gamut(float3 rgb) {
            float luma = dot(rgb, float3(0.2627, 0.6780, 0.0593));
            float maxComp = max(max(rgb.r, rgb.g), rgb.b);
            float minComp = min(min(rgb.r, rgb.g), rgb.b);
            if (minComp < 0.0) {
                float scale = -minComp / max(maxComp - minComp, 1e-6);
                rgb = lerp(rgb, float3(luma, luma, luma), saturate(scale));
            }
            return rgb;
        }

        float4 main_ps(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET {
            float4 c = texHdr.Sample(samplerState, tex);
            // 1. PQ decode to linear luminance (nits, BT.2020 primaries)
            float3 lin = float3(pq_to_nits(c.r), pq_to_nits(c.g), pq_to_nits(c.b));
            // 2. BT.2020 -> BT.709 gamut conversion (ITU-R BT.2087, linear-light)
            // HLSL initializer fills ROW-first: {row0, row1, row2}. mul(M,v).x = dot(row0,v).
            float3x3 bt2020to709 = { 1.660496, -0.587656, -0.072840, -0.124547, 1.132895, -0.008348, -0.018154, -0.100597, 1.118751 };
            float3 lin709 = mul(bt2020to709, lin);
            // 3. Gamut compression: handle BT.2020 colors outside BT.709 gamut (negative channels)
            lin709 = compress_gamut(lin709);
            // 4. Normalize: reference white -> 1.0
            float3 sdr = lin709 / TARGET_WHITE_NITS;
            // 5. Tone mapping: linear below KNEE, soft roll-off above
            sdr = max(sdr, 0.0);
            float3 over = max(sdr - KNEE, 0.0);
            sdr = min(sdr, KNEE) + over / (1.0 + over * ROLLOFF);
            sdr = saturate(sdr);
            // 6. Shadow detail lift (mild, prevents black crush)
            float lumaPre = dot(sdr, float3(0.2126, 0.7152, 0.0722));
            sdr = sdr + 0.02 * saturate(1.0 - lumaPre * 2.0);
            sdr = saturate(sdr);
            // 7. Mid-tone contrast boost
            sdr = (sdr - 0.5) * 1.05 + 0.5;
            sdr = saturate(sdr);
            // 8. sRGB gamma 2.2 encode
            sdr = pow(sdr, 1.0 / 2.2);
            // 9. Saturation compensation (BT.2020->BT.709 reduces perceived saturation)
            float luma = dot(sdr, float3(0.2126, 0.7152, 0.0722));
            sdr = lerp(float3(luma, luma, luma), sdr, 1.20);
            sdr = saturate(sdr);
            return float4(sdr, 1.0);
        }";

        // ICtCp 专用像素着色器（杜比视界 Profile 5）：
        // 直接采样 P010 平面（plane0=I, plane1=(Ct,Cp)），绕过 VideoProcessor 的 YCbCr 矩阵。
        // 转换链: ICtCp(信号域) -> LMS(信号域) -> PQ EOTF -> 线性LMS(nits) -> BT.2020 RGB -> BT.709 -> SDR
        // 矩阵来源 ITU-R BT.2100: ICtCp->LMS 取 Table 5 逆矩阵(280/2356, 4096/2356)；
        // LMS->RGB2020 取 BT.2100 Table 4 正矩阵的逆（行列和=1 已验证）。
        // WARNING: keep this shader source ASCII-only. Vortice Compiler.Compile marshals to ANSI
        // but passes char count as byte length, so any non-ASCII char truncates the tail.
        private const string ICTCP_PS = @"
        Texture2D<float> texY : register(t0);
        Texture2D<float2> texUV : register(t1);
        SamplerState samplerState : register(s0);

        // DOVI RPU metadata: ycc_to_rgb_matrix + offset from AV_FRAME_DATA_DOVI_METADATA
        // row0: (M00, M01, M02, -), row1: (M10, M11, M12, -), row2: (M20, M21, M22, -)
        // offset_flag: (off0, off1, off2, hasValidMatrix)
        cbuffer DoviParams : register(b0) {
            float4 dovi_matrix_row0;
            float4 dovi_matrix_row1;
            float4 dovi_matrix_row2;
            float4 dovi_offset_flag;
        }

        // DOVI RPU reshaping curves (libplacebo-compatible packed layout).
        // rs_meta[c]   = (num_pivots, lo, hi, 0); num_pivots < 2 disables reshaping.
        // rs_pivots    = 3 channels x 2 float4: inner pivots [1..7], 1e9 sentinel padded.
        // rs_coeffs    = 3 channels x 8 segments: (c0, c1, c2, kind); kind=0 -> polynomial
        //                (c0..c2 = x^0..x^2 coefs), kind=1..3 -> MMR order (c0 = constant).
        // rs_mmr       = 3 channels x 8 segments x 6 float4, fixed stride per segment.
        //                rows: [0..1]=order1 (linear + cross), [2..3]=order2, [4..5]=order3.
        //                Zero-filled for orders below mmr_order (contributes nothing).
        cbuffer DoviReshape : register(b1) {
            float4 rs_meta[3];
            float4 rs_pivots[6];
            float4 rs_coeffs[24];
            float4 rs_mmr[144];
        }

        static const float PQ_M1 = 2610.0 / 16384.0;
        static const float PQ_M2 = 2523.0 / 4096.0 * 128.0;
        static const float PQ_C1 = 3424.0 / 4096.0;
        static const float PQ_C2 = 2413.0 / 4096.0 * 32.0;
        static const float PQ_C3 = 2392.0 / 4096.0 * 32.0;
        // Matches libplacebo pl_shader_decode_color: only negatives are clamped to zero;
        // values above 1.0 from the ycc_to_rgb matrix pass through unclamped and are
        // handled later by tone mapping (saturating here would cap them at 10000 nits
        // and flatten bright detail).
        float pq_eotf(float x) {
            float p = pow(max(x, 0.0), 1.0 / PQ_M2);
            float num = max(p - PQ_C1, 0.0);
            float den = max(PQ_C2 - PQ_C3 * p, 1e-6);
            return pow(num / den, 1.0 / PQ_M1);
        }

        // Input is in BT.709 space, use BT.709 luma coeffs
        float3 compress_gamut(float3 rgb) {
            float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
            float maxComp = max(max(rgb.r, rgb.g), rgb.b);
            float minComp = min(min(rgb.r, rgb.g), rgb.b);
            if (minComp < 0.0) {
                float scale = -minComp / max(maxComp - minComp, 1e-6);
                rgb = lerp(rgb, float3(luma, luma, luma), saturate(scale));
            }
            return rgb;
        }

        // MMR reshaping evaluation, ported from libplacebo reshape_mmr (src/shaders/colorspace.c).
        // sig = clamped BL signal (I, C, P) in [0,1]. Base index uses fixed stride 6 float4
        // per segment so unused order rows (zero-filled) contribute nothing.
        float rs_mmr_eval(int ch, int seg, float3 sig, float w0, float order) {
            int base = ch * 48 + seg * 6;
            float s = w0;
            float4 sigX;
            sigX.xyz = sig.xxy * sig.yzz;
            sigX.w = sigX.x * sig.z;
            s += dot(rs_mmr[base + 0].xyz, sig);
            s += dot(rs_mmr[base + 1], sigX);
            if (order >= 2.0) {
                float3 sig2 = sig * sig;
                float4 sigX2 = sigX * sigX;
                s += dot(rs_mmr[base + 2].xyz, sig2);
                s += dot(rs_mmr[base + 3], sigX2);
                if (order >= 3.0) {
                    s += dot(rs_mmr[base + 4].xyz, sig2 * sig);
                    s += dot(rs_mmr[base + 5], sigX2 * sigX);
                }
            }
            return s;
        }

        // Piecewise reshaping of the BL signal, ported from libplacebo pl_shader_dovi_reshape.
        // Segment selection is branchless via sentinel-padded pivots (1e9 = beyond range).
        // The winning segment index equals the number of pivots passed (sum of tests).
        float rs_reshape_channel(int ch, float3 sig, float s) {
            int np = (int)rs_meta[ch].x;
            int seg = 0;
            float4 coeffs;
            if (np > 2) {
                float4 p0 = rs_pivots[ch * 2];
                float4 p1 = rs_pivots[ch * 2 + 1];
                float t0 = s >= p0.x ? 1.0 : 0.0;
                float t1 = s >= p0.y ? 1.0 : 0.0;
                float t2 = s >= p0.z ? 1.0 : 0.0;
                float t3 = s >= p0.w ? 1.0 : 0.0;
                float t4 = s >= p1.x ? 1.0 : 0.0;
                float t5 = s >= p1.y ? 1.0 : 0.0;
                float t6 = s >= p1.z ? 1.0 : 0.0;
                seg = (int)(t0 + t1 + t2 + t3 + t4 + t5 + t6);
                int cb = ch * 8;
                coeffs = lerp(
                    lerp(lerp(rs_coeffs[cb + 0], rs_coeffs[cb + 1], t0),
                         lerp(rs_coeffs[cb + 2], rs_coeffs[cb + 3], t2), t1),
                    lerp(lerp(rs_coeffs[cb + 4], rs_coeffs[cb + 5], t4),
                         lerp(rs_coeffs[cb + 6], rs_coeffs[cb + 7], t6), t5),
                    t3);
            } else {
                coeffs = rs_coeffs[ch * 8];
            }
            float y;
            if (coeffs.w == 0.0) {
                y = (coeffs.z * s + coeffs.y) * s + coeffs.x;
            } else {
                y = rs_mmr_eval(ch, seg, sig, coeffs.x, coeffs.w);
            }
            return y;
        }

        // Full 3-channel reshaping. All channels MUST be evaluated from the ORIGINAL
        // signal: MMR curves are multivariate polynomials in (I, P, T), so overwriting
        // one channel before the others are computed corrupts their inputs (hue shift).
        // Output IS clamped to the pivot bounds [pivots[0], pivots[np-1]], exactly as
        // libplacebo pl_shader_dovi_reshape does: unclamped reshape output overflows
        // the ycc_to_rgb matrix input range, causing brightness blowout and green cast.
        float3 dovi_reshape(float3 sig) {
            float3 orig = clamp(sig, 0.0, 1.0);
            float3 res = orig;
            [unroll]
            for (int c = 0; c < 3; c++) {
                if (rs_meta[c].x >= 2.0) {
                    float y = rs_reshape_channel(c, orig, orig[c]);
                    res[c] = clamp(y, rs_meta[c].y, rs_meta[c].z);
                }
            }
            return res;
        }

        float4 main_ps(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET {
            float I = saturate(texY.Sample(samplerState, tex));
            float2 ctcp = texUV.Sample(samplerState, tex);

            float3 sig = float3(I, ctcp.x, ctcp.y);
            // DV P5: BL signal is reshaped IPTPQc2 - undo reshaping first (poly/MMR curves)
            if (rs_meta[0].x >= 2.0) sig = dovi_reshape(sig);

            float3 lin;
            // RPU ycc_to_rgb_matrix + offset (AV_FRAME_DATA_DOVI_METADATA): converts the
            // inverse-reshaped DV P5 IPT-PQ-c2 signal to PQ-domain RGB.
            // The offset is the chroma centering offset: Ct/Cp are in [0,1] but the
            // matrix expects them centered around 0, so we subtract offset BEFORE mul.
            if (dovi_offset_flag.w > 0.5)
            {
                sig -= dovi_offset_flag.xyz; // center Ct/Cp around 0 (offset = [0, 0.5, 0.5])
                float3 rgb_pq;
                rgb_pq.r = dot(dovi_matrix_row0.xyz, sig);
                rgb_pq.g = dot(dovi_matrix_row1.xyz, sig);
                rgb_pq.b = dot(dovi_matrix_row2.xyz, sig);
                lin = float3(pq_eotf(rgb_pq.r), pq_eotf(rgb_pq.g), pq_eotf(rgb_pq.b)) * 10000.0;
            }
            else
            {
                // Fallback: standard BT.2100 ICtCp->LMS->RGB (consistent pair), chroma centered
                float Ct = sig.y - 0.5;
                float Cp = sig.z - 0.5;
                // BT.2100 Table 5 inverse: ICtCp -> LMS (PQ domain)
                float L = sig.x + 0.008609 * Ct + 0.111029 * Cp;
                float M = sig.x - 0.008609 * Ct - 0.111029 * Cp;
                float S = sig.x + 0.560031 * Ct - 0.320627 * Cp;
                // PQ EOTF -> linear LMS (nits)
                float3 lms = float3(pq_eotf(L), pq_eotf(M), pq_eotf(S)) * 10000.0;
                // LMS -> BT.2020 RGB: inverse of BT.2100 Table 4 LMS matrix
                // [1688 2146 262; 683 2951 462; 78 1802 2216]/4096 inverted
                lin = float3(
                     3.476 * lms.x - 2.609 * lms.y + 0.133 * lms.z,
                    -0.900 * lms.x + 2.266 * lms.y - 0.366 * lms.z,
                     0.609 * lms.x - 1.751 * lms.y + 2.141 * lms.z);
            }

            // DV P5 -> HDR10 static rendering: RPU ycc_to_rgb_matrix replaces the standard
            // BT.2100 ICtCp->LMS->RGB conversion. Its output is closest to BT.2020
            // primaries (the RPU matrix is content-specific, tuned for the mastering
            // display, but the standard BT.2020->BT.709 gamut conversion + compress_gamut
            // provides the most neutral fallback for any mastering display primaries).
            float3x3 bt2020to709 = { 1.660496, -0.587656, -0.072840, -0.124547, 1.132895, -0.008348, -0.018154, -0.100597, 1.118751 };

            float3 lin709 = mul(bt2020to709, lin);
            lin709 = compress_gamut(lin709);
            lin709 = max(lin709, 0.0);

            // Reinhard tone mapping: reference white = 100 nits (BT.2408 SDR standard)
            float3 hdr = lin709 / 100.0;
            float3 sdr = hdr / (hdr + 1.0);
            sdr = saturate(sdr);

            // Mild saturation boost in linear space (before gamma encode)
            float luma = dot(sdr, float3(0.2126, 0.7152, 0.0722));
            sdr = lerp(float3(luma, luma, luma), sdr, 1.05);
            sdr = saturate(sdr);

            sdr = pow(sdr, 1.0 / 2.2);
            return float4(sdr, 1.0);
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
                DebugLogger.WriteLine("着色器管线就绪");
            }
            catch (Exception ex) { DebugLogger.WriteLine($"着色器管线创建失败: {ex.Message}"); CleanupShaderPipeline(); }
        }

        /// <summary>
        /// 确保HDR色调映射管线就绪：RGBA16F中间纹理 + SRV + VP输出视图 + HDR像素着色器。
        /// VP将P010解码帧转为PQ编码的BT.2020 RGB写入中间纹理（不做色调映射），
        /// 随后由 RenderHdrToneMap 着色器完成 HDR→SDR。
        /// </summary>
        private bool EnsureHdrPipeline()
        {
            if (_hdrPipelineUnavailable) return false;
            if (_d3dDevice == null || _vpEnumerator == null || _videoDevice == null) return false;

            try
            {
                // 探测VP输出中间纹理格式：PQ信号本身是0-1编码，Float16并非必需，
                // UNorm16（16bit精度）/R10G10B10A2同样能无损承载PQ曲线，Intel核显
                // 普遍不支持Float16作VP输出（实测0x0），但对UNorm格式支持良好。
                // 从_hdrFormatIdx开始探测（Blt运行时故障转移后跳过已失败格式）
                bool formatOk = false;
                for (int i = _hdrFormatIdx; i < HdrOutputFormats.Length; i++)
                {
                    var fmt = HdrOutputFormats[i];
                    var support = _vpEnumerator.CheckVideoProcessorFormat(fmt);
                    if ((support & VideoProcessorFormatSupport.Output) != 0)
                    {
                        if (_hdrTexture == null || _hdrFormat != fmt)
                        {
                            _hdrFormat = fmt;
                            SafeDispose(ref _vpOutputViewHdr);
                            SafeDispose(ref _hdrSrv);
                            SafeDispose(ref _hdrTexture);
                        }
                        _hdrFormatIdx = i;
                        formatOk = true;
                        break;
                    }
                }
                if (!formatOk)
                {
                    _hdrPipelineUnavailable = true;
                    DebugLogger.WriteLine("HDR着色器管线不可用：驱动不支持任何Float16/UNorm16/UNorm10 VP输出格式，回退驱动色调映射");
                    return false;
                }

                if (!_shaderPipelineReady) CreateShaderPipeline();
                if (!_shaderPipelineReady)
                {
                    _hdrPipelineUnavailable = true;
                    DebugLogger.WriteLine("HDR管线不可用：基础着色器管线未就绪（详见'着色器管线创建失败'日志），回退驱动色调映射");
                    return false;
                }

                // 编译HDR色调映射着色器（一次性）
                if (_hdrPixelShader == null)
                {
                    var blob = Compiler.Compile(HDR_TONE_MAP_PS, "main_ps", "PixelShader", "ps_5_0");
                    _hdrPixelShader = _d3dDevice.CreatePixelShader(blob.Span);
                }

                // 中间纹理尺寸与交换链同步
                if (_hdrTexture == null || _hdrTexW != _swapChainWidth || _hdrTexH != _swapChainHeight)
                {
                    SafeDispose(ref _vpOutputViewHdr);
                    SafeDispose(ref _hdrSrv);
                    SafeDispose(ref _hdrTexture);
                    _hdrTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)_swapChainWidth,
                        Height = (uint)_swapChainHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = _hdrFormat,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
                    });
                    _hdrSrv = _d3dDevice.CreateShaderResourceView(_hdrTexture);
                    _hdrTexW = _swapChainWidth; _hdrTexH = _swapChainHeight;

                    using var resource = _hdrTexture.QueryInterface<ID3D11Resource>();
                    _vpOutputViewHdr = _videoDevice.CreateVideoProcessorOutputView(resource, _vpEnumerator,
                        new VideoProcessorOutputViewDescription
                        {
                            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
                        });
                    DebugLogger.WriteLine($"HDR色调映射管线就绪: {_hdrTexW}x{_hdrTexH} {_hdrFormat}");
                }
                return _hdrTexture != null && _hdrSrv != null && _vpOutputViewHdr != null && _hdrPixelShader != null;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"HDR管线创建失败，回退驱动色调映射: {ex.Message}");
                _hdrPipelineUnavailable = true;
                return false;
            }
        }

        /// <summary>
        /// Blt运行时失败后切换到下一个HDR中间纹理格式。
        /// 驱动可能对CheckVideoProcessorFormat报告支持，但实际PQ色彩空间Blt返回E_FAIL
        /// （如Intel Iris Xe对R16G16B16A16_UNorm）。切换后由调用方重建管线并重试Blt。
        /// 返回false表示候选已穷尽，应永久回退驱动色调映射。
        /// </summary>
        private bool TryNextHdrFormat()
        {
            if (_hdrFormatIdx + 1 >= HdrOutputFormats.Length) return false;
            _hdrFormatIdx++;
            DebugLogger.WriteLine($"HDR中间格式{_hdrFormat} Blt失败，尝试下一个: {HdrOutputFormats[_hdrFormatIdx]}");
            SafeDispose(ref _vpOutputViewHdr);
            SafeDispose(ref _hdrSrv);
            SafeDispose(ref _hdrTexture);
            _hdrTexW = _hdrTexH = 0;
            return EnsureHdrPipeline();
        }

        /// <summary>
        /// HDR色调映射着色器通道：中间纹理(PQ BT.2020) → SDR sRGB 后缓冲。
        /// 中间纹理已由VP写入含黑边的完整画面，全屏采样即可保持几何不变。
        /// </summary>
        private void RenderHdrToneMap()
        {
            if (_d3dContext == null || _backBufferRtv == null || _hdrSrv == null ||
                _vertexShader == null || _hdrPixelShader == null || _inputLayout == null ||
                _vertexBuffer == null || _samplerState == null) return;

            _d3dContext.OMSetRenderTargets(_backBufferRtv);
            _d3dContext.RSSetViewport(new Viewport(0, 0, _swapChainWidth, _swapChainHeight));

            _d3dContext.VSSetShader(_vertexShader);
            _d3dContext.PSSetShader(_hdrPixelShader);
            _d3dContext.IASetInputLayout(_inputLayout);
            _d3dContext.PSSetShaderResources(0, new[] { _hdrSrv });
            _d3dContext.PSSetSampler(0, _samplerState);
            _d3dContext.IASetVertexBuffer(0, _vertexBuffer, 16, 0);
            _d3dContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _d3dContext.Draw(4, 0);

            // 解绑SRV，避免下一帧VP写入纹理时仍被绑定
            _d3dContext.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());

            // 像素级读回诊断（前3帧 + 每600帧）：定位色偏发生在VP/着色器/显示哪个阶段
            if (_renderFrameCount <= 3 || _renderFrameCount % 600 == 0)
                DiagReadbackHdr();
        }

        // ==================== 像素级读回诊断（定位色偏阶段） ====================
        // 读取中间纹理/解码纹理/后缓冲中心像素，与着色器数学镜像对比：
        // - 中间纹理RGB不等 → VP阶段问题；相等但后缓冲不等 → 着色器问题；
        // - 后缓冲与expected一致 → 显示/交换链问题。
        private static double PqToNits(double x)
        {
            const double m1 = 2610.0 / 16384.0, m2 = 2523.0 / 4096.0 * 128.0;
            const double c1 = 3424.0 / 4096.0, c2 = 2413.0 / 4096.0 * 32.0, c3 = 2392.0 / 4096.0 * 32.0;
            double p = Math.Pow(Math.Clamp(x, 0.0, 1.0), 1.0 / m2);
            double num = Math.Max(p - c1, 0.0);
            double den = Math.Max(c2 - c3 * p, 1e-6);
            return Math.Pow(num / den, 1.0 / m1) * 10000.0;
        }

        // 着色器数学镜像：BT.2020线性nits → BT.709矩阵(row-first) → compress_gamut → BT.2446-1 → gamma2.2
        private static (double r, double g, double b) ExpectedSdr(double rN, double gN, double bN)
        {
            double r = 1.660496 * rN - 0.587656 * gN - 0.072840 * bN;
            double g = -0.124547 * rN + 1.132895 * gN - 0.008348 * bN;
            double b = -0.018154 * rN - 0.100597 * gN + 1.118751 * bN;
            double luma0 = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            double maxC = Math.Max(r, Math.Max(g, b)), minC = Math.Min(r, Math.Min(g, b));
            if (minC < 0)
            {
                double s = Math.Min(-minC / Math.Max(maxC - minC, 1e-6), 1.0);
                r += (luma0 - r) * s; g += (luma0 - g) * s; b += (luma0 - b) * s;
            }
            r = Math.Max(r, 0); g = Math.Max(g, 0); b = Math.Max(b, 0);
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            double e2 = Math.Pow(Math.Max(luma / 100.0, 0.0), 1.2);
            double V = Math.Log10(1.0 + 9.0 * Math.Sqrt(e2));
            double scale = e2 > 1e-6 ? V / e2 / 100.0 : 0.0;
            r = Math.Min(Math.Max(r * scale, 0.0), 1.0);
            g = Math.Min(Math.Max(g * scale, 0.0), 1.0);
            b = Math.Min(Math.Max(b * scale, 0.0), 1.0);
            return (Math.Pow(r, 1 / 2.2), Math.Pow(g, 1 / 2.2), Math.Pow(b, 1 / 2.2));
        }

        private ID3D11Texture2D? CreateStagingCopy(ID3D11Texture2D src)
        {
            try
            {
                var d = src.Description;
                var staging = _d3dDevice!.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = d.ArraySize,
                    Format = d.Format, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read
                });
                _d3dContext!.CopyResource(src, staging);
                return staging;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[DIAG] staging copy失败: {ex.Message}");
                return null;
            }
        }

        private void LogBackbufferCenter(string tag)
        {
            if (_backBufferTexture == null || _d3dContext == null) return;
            var st = CreateStagingCopy(_backBufferTexture);
            if (st == null) return;
            using (st)
            {
                var m = _d3dContext.Map(st, 0, MapMode.Read);
                try
                {
                    int cx = (int)st.Description.Width / 2, cy = (int)st.Description.Height / 2;
                    nint off = (nint)cy * (nint)m.RowPitch + (nint)cx * 4;
                    int v = Marshal.ReadInt32(m.DataPointer, (int)off);
                    int b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                    DebugLogger.WriteLine($"{tag} backbuffer center R={r} G={g} B={b} /255=({r / 255.0:F3},{g / 255.0:F3},{b / 255.0:F3})");
                }
                finally { _d3dContext.Unmap(st, 0); }
            }
        }

        /// <summary>HDR10路径诊断：读中间PQ纹理中心像素 + 后缓冲实际输出</summary>
        private void DiagReadbackHdr()
        {
            try
            {
                if (_hdrTexture == null || _d3dContext == null) return;
                var st = CreateStagingCopy(_hdrTexture);
                if (st == null) return;
                double r10 = 0, g10 = 0, b10 = 0;
                string fmtName;
                using (st)
                {
                    fmtName = st.Description.Format.ToString();
                    var m = _d3dContext.Map(st, 0, MapMode.Read);
                    try
                    {
                        int cx = (int)st.Description.Width / 2, cy = (int)st.Description.Height / 2;
                        bool is16 = st.Description.Format == Format.R16G16B16A16_UNorm;
                        nint off = (nint)cy * (nint)m.RowPitch + (nint)cx * (is16 ? 8 : 4);
                        if (is16)
                        {
                            r10 = (ushort)Marshal.ReadInt16(m.DataPointer, (int)off) / 65535.0;
                            g10 = (ushort)Marshal.ReadInt16(m.DataPointer, (int)off + 2) / 65535.0;
                            b10 = (ushort)Marshal.ReadInt16(m.DataPointer, (int)off + 4) / 65535.0;
                        }
                        else
                        {
                            uint v = (uint)Marshal.ReadInt32(m.DataPointer, (int)off);
                            r10 = (v & 0x3FF) / 1023.0;
                            g10 = ((v >> 10) & 0x3FF) / 1023.0;
                            b10 = ((v >> 20) & 0x3FF) / 1023.0;
                        }
                    }
                    finally { _d3dContext.Unmap(st, 0); }
                }
                double rn = PqToNits(r10), gn = PqToNits(g10), bn = PqToNits(b10);
                var exp = ExpectedSdr(rn, gn, bn);
                DebugLogger.WriteLine($"[DIAG-HDR] inter({fmtName}) pq=({r10:F3},{g10:F3},{b10:F3}) nits=({rn:F1},{gn:F1},{bn:F1}) expected_sdr=({exp.r:F3},{exp.g:F3},{exp.b:F3})");
                _d3dContext.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                LogBackbufferCenter("[DIAG-HDR]");
            }
            catch (Exception ex) { DebugLogger.WriteLine($"[DIAG-HDR] 读回失败: {ex.Message}"); }
        }

        /// <summary>ICtCp路径诊断：读P010解码纹理中心像素(I,Ct,Cp) + 后缓冲实际输出</summary>
        private void DiagReadbackIctcp(ID3D11Texture2D frameTexture)
        {
            try
            {
                if (_d3dContext == null) return;
                var st = CreateStagingCopy(frameTexture);
                if (st == null) return;
                double I = 0, Ct = 0, Cp = 0;
                using (st)
                {
                    var d = st.Description;
                    int cx = (int)d.Width / 2, cy = (int)d.Height / 2;
                    var my = _d3dContext.Map(st, 0, MapMode.Read);
                    I = (ushort)Marshal.ReadInt16(my.DataPointer, (int)((nint)cy * (nint)my.RowPitch + (nint)cx * 2)) / 65535.0;
                    _d3dContext.Unmap(st, 0);
                    var muv = _d3dContext.Map(st, 1, MapMode.Read);
                    nint off = (nint)(cy / 2) * (nint)muv.RowPitch + (nint)(cx / 2) * 4;
                    Ct = (ushort)Marshal.ReadInt16(muv.DataPointer, (int)off) / 65535.0;
                    Cp = (ushort)Marshal.ReadInt16(muv.DataPointer, (int)off + 2) / 65535.0;
                    _d3dContext.Unmap(st, 1);
                }
                var dm = _doviMetadata;
                if (dm?.HasValidMatrix == true && dm.YccToRgbMatrix?.Length >= 9 && dm.YccToRgbOffset?.Length >= 3)
                {
                    var M = dm.YccToRgbMatrix; var O = dm.YccToRgbOffset;
                    double s0 = I - O[0], s1 = Ct - O[1], s2 = Cp - O[2];
                    double rp = M[0] * s0 + M[1] * s1 + M[2] * s2;
                    double gp = M[3] * s0 + M[4] * s1 + M[5] * s2;
                    double bp = M[6] * s0 + M[7] * s1 + M[8] * s2;
                    double rn = PqToNits(rp), gn = PqToNits(gp), bn = PqToNits(bp);
                    var exp = ExpectedSdr(rn, gn, bn);
                    DebugLogger.WriteLine($"[DIAG-ICP] sig(I={I:F4} Ct={Ct:F4} Cp={Cp:F4}) 未reshape→rgb_pq=({rp:F3},{gp:F3},{bp:F3}) nits=({rn:F1},{gn:F1},{bn:F1}) expected_sdr=({exp.r:F3},{exp.g:F3},{exp.b:F3})");
                }
                else
                {
                    DebugLogger.WriteLine($"[DIAG-ICP] sig(I={I:F4} Ct={Ct:F4} Cp={Cp:F4}) 无RPU矩阵");
                }
                _d3dContext.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                LogBackbufferCenter("[DIAG-ICP]");
            }
            catch (Exception ex) { DebugLogger.WriteLine($"[DIAG-ICP] 读回失败: {ex.Message}"); }
        }

        /// <summary>
        /// 确保ICtCp渲染管线就绪：编译ICtCp着色器（复用基础管线顶点/采样器）。
        /// 任一环节失败置位 _ictcpPipelineBroken，永久回退HDR10（VP）路径。
        /// </summary>
        private bool EnsureIctcpPipeline()
        {
            if (_ictcpPipelineBroken) return false;
            if (_d3dDevice == null) return false;
            try
            {
                if (!_shaderPipelineReady) CreateShaderPipeline();
                if (!_shaderPipelineReady)
                {
                    _ictcpPipelineBroken = true;
                    DebugLogger.WriteLine("ICtCp管线不可用：基础着色器管线未就绪，回退HDR10路径");
                    return false;
                }
                if (_ictcpPixelShader == null)
                {
                    var blob = Compiler.Compile(ICTCP_PS, "main_ps", "PixelShader", "ps_5_0");
                    _ictcpPixelShader = _d3dDevice.CreatePixelShader(blob.Span);
                    DebugLogger.WriteLine("ICtCp着色器编译就绪（杜比视界Profile 5直通渲染）");
                }
                return _ictcpPixelShader != null;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"ICtCp管线创建失败，回退HDR10路径: {ex.Message}");
                _ictcpPipelineBroken = true;
                return false;
            }
        }

        /// <summary>
        /// 更新 DOVI 常量缓冲，将 RPU ycc_to_rgb_matrix 上传到 GPU。
        /// 仅在 _doviConstantBufferDirty 为 true 时重建缓冲。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DoviCb
        {
            public float M00; public float M01; public float M02; public float _pad0;
            public float M10; public float M11; public float M12; public float _pad1;
            public float M20; public float M21; public float M22; public float _pad2;
            public float Off0; public float Off1; public float Off2; public float HasValid;
        }

        private void UpdateDoviConstantBuffer()
        {
            if (_d3dDevice == null) return;

            // 惰性创建：首次调用时创建缓冲（即使没有 RPU 数据，也需要一个全零缓冲）
            if (_doviConstantBuffer == null)
            {
                var desc = new BufferDescription(64, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None);
                _doviConstantBuffer = _d3dDevice.CreateBuffer(desc);
            }

            if (!_doviConstantBufferDirty) return;
            _doviConstantBufferDirty = false;

            var cb = new DoviCb();
            if (_doviMetadata?.HasValidMatrix == true)
            {
                var m = _doviMetadata.YccToRgbMatrix;
                var o = _doviMetadata.YccToRgbOffset;
                cb.M00 = m[0]; cb.M01 = m[1]; cb.M02 = m[2];
                cb.M10 = m[3]; cb.M11 = m[4]; cb.M12 = m[5];
                cb.M20 = m[6]; cb.M21 = m[7]; cb.M22 = m[8];
                cb.Off0 = o[0]; cb.Off1 = o[1]; cb.Off2 = o[2];
                cb.HasValid = 1.0f;
            }
            // else: 全零缓冲，HasValid=0.0f → 着色器走 fallback (BT.2100)

            unsafe
            {
                DoviCb* p = &cb;
                _d3dContext!.UpdateSubresource(_doviConstantBuffer, 0, default, (IntPtr)p, 0, 0);
            }

            UpdateDoviReshapeBuffer();
        }

        /// <summary>
        /// 打包上传 RPU 重塑曲线到 b1 常量缓冲（libplacebo 布局）：
        ///   rs_meta[c]   = (num_pivots, pivots[0], pivots[np-1], 0)
        ///   rs_pivots    = 每通道 8 个内部 pivot [1..np-2]，剩余用 1e9 哨兵填充
        ///   rs_coeffs    = poly: (c0,c1,c2,0)；MMR: (w0,0,0,order)
        ///   rs_mmr       = 每段 6 个 float4，m 阶占 (2m, 2m+1) 两行，超出阶数补零
        /// 无曲线数据时上传全零 → 着色器跳过重塑（保持旧行为）。
        /// </summary>
        private void UpdateDoviReshapeBuffer()
        {
            if (_d3dDevice == null || _d3dContext == null) return;

            if (_doviReshapeBuffer == null)
            {
                var desc = new BufferDescription(2832, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None);
                _doviReshapeBuffer = _d3dDevice.CreateBuffer(desc);
            }

            // float 布局（float4 索引 → float 偏移）：rs_meta[0..11] rs_pivots[12..35] rs_coeffs[36..131] rs_mmr[132..707]
            var f = new float[708];
            var rs = _doviMetadata?.Reshape;
            if (rs != null && rs.Length == 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    var ch = rs[c];
                    if (ch == null || ch.NumPivots < 2) continue;
                    int np = ch.NumPivots;

                    f[c * 4 + 0] = np;
                    f[c * 4 + 1] = ch.Pivots[0];
                    f[c * 4 + 2] = ch.Pivots[np - 1];

                    for (int i = 0; i < 8; i++)
                        f[12 + c * 8 + i] = (i + 1 <= np - 2) ? ch.Pivots[i + 1] : 1e9f;

                    int segs = Math.Min(np - 1, 8);
                    for (int j = 0; j < segs; j++)
                    {
                        int cbf = 36 + (c * 8 + j) * 4;
                        if (ch.Method[j] == 0)
                        {
                            f[cbf] = ch.PolyCoef[j * 3];
                            f[cbf + 1] = ch.PolyCoef[j * 3 + 1];
                            f[cbf + 2] = ch.PolyCoef[j * 3 + 2];
                            f[cbf + 3] = 0f; // kind=0: polynomial
                        }
                        else
                        {
                            f[cbf] = ch.MmrConstant[j];
                            f[cbf + 3] = ch.MmrOrder[j]; // kind=MMR order [1,3]
                            int order = Math.Max(1, Math.Min(3, ch.MmrOrder[j]));
                            int mbf = 132 + (c * 48 + j * 6) * 4;
                            for (int m = 0; m < 3; m++)
                            {
                                bool act = m < order;
                                int src = (j * 3 + m) * 7;
                                f[mbf + m * 8 + 0] = act ? ch.MmrCoef[src] : 0f;
                                f[mbf + m * 8 + 1] = act ? ch.MmrCoef[src + 1] : 0f;
                                f[mbf + m * 8 + 2] = act ? ch.MmrCoef[src + 2] : 0f;
                                f[mbf + m * 8 + 3] = 0f;
                                f[mbf + m * 8 + 4] = act ? ch.MmrCoef[src + 3] : 0f;
                                f[mbf + m * 8 + 5] = act ? ch.MmrCoef[src + 4] : 0f;
                                f[mbf + m * 8 + 6] = act ? ch.MmrCoef[src + 5] : 0f;
                                f[mbf + m * 8 + 7] = act ? ch.MmrCoef[src + 6] : 0f;
                            }
                        }
                    }
                }
            }

            unsafe
            {
                fixed (float* p = f)
                {
                    _d3dContext.UpdateSubresource(_doviReshapeBuffer, 0, default, (IntPtr)p, 0, 0);
                }
            }
        }

        /// <summary>
        /// ICtCp直通渲染：直接采样帧纹理P010平面（plane0=I, plane1=(Ct,Cp)），
        /// 着色器内完成 ICtCp→RGB（RPU矩阵或标准BT.2100）→PQ EOTF→BT.2020→BT.709→SDR 全流程。  [updated: RPU matrix support]
        /// 平面SRV格式转换：R16_UNORM采样返回v16/65535，P010高10位存储 → v10/1023。
        /// 返回false表示SRV创建失败，调用方回退VP路径。
        /// </summary>
        private bool RenderIctcpFrame(ID3D11Texture2D frameTexture)
        {
            if (_d3dContext == null || _d3dDevice == null || _backBufferRtv == null ||
                _vertexShader == null || _ictcpPixelShader == null || _inputLayout == null ||
                _vertexBuffer == null || _samplerState == null) return false;

            try
            {
                int slot = _inputViewIndex;
                // PlaneSlice平面视图（D3D11.1，走原生COM调用）：plane0按R16_UNorm采I，plane1按R16G16_UNorm采(Ct,Cp)
                IntPtr ySrvPtr = NativeD3D11.CreatePlaneSRV(_d3dDevice.NativePointer, frameTexture.NativePointer, (uint)Format.R16_UNorm, 0);
                IntPtr uvSrvPtr = NativeD3D11.CreatePlaneSRV(_d3dDevice.NativePointer, frameTexture.NativePointer, (uint)Format.R16G16_UNorm, 1);
                if (ySrvPtr == IntPtr.Zero || uvSrvPtr == IntPtr.Zero)
                {
                    if (ySrvPtr != IntPtr.Zero) Marshal.Release(ySrvPtr);
                    if (uvSrvPtr != IntPtr.Zero) Marshal.Release(uvSrvPtr);
                    DebugLogger.WriteLine("ICtCp平面SRV创建失败（驱动不支持PlaneSlice），回退HDR10路径");
                    _ictcpPipelineBroken = true;
                    return false;
                }
                _ictcpYSrvs[slot] = new ID3D11ShaderResourceView(ySrvPtr);
                _ictcpUVSrvs[slot] = new ID3D11ShaderResourceView(uvSrvPtr);

                // 更新 DOVI 常量缓冲（RPU ycc_to_rgb_matrix）
                UpdateDoviConstantBuffer();

                // 前3帧 + 每120帧打印ICtCp渲染状态
                if (_renderFrameCount <= 3 || _renderFrameCount % 120 == 0)
                {
                    DebugLogger.WriteLine($"[ICtCp] RenderIctcpFrame slot={slot} frameTexture=0x{frameTexture.NativePointer:X} " +
                        $"ySrv=0x{ySrvPtr:X} uvSrv=0x{uvSrvPtr:X} " +
                        $"doviCb={(_doviConstantBuffer != null)} doviReshapeCb={(_doviReshapeBuffer != null)} " +
                        $"hasMatrix={(_doviMetadata?.HasValidMatrix == true)}");
                }

                // 黑边清屏 + 视口按目标矩形（复用VP路径的宽高比letterbox计算）
                _d3dContext.ClearRenderTargetView(_backBufferRtv, new Color4(0, 0, 0, 1));
                var r = _cachedDestRect;
                _d3dContext.RSSetViewport(new Viewport(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));

                _d3dContext.OMSetRenderTargets(_backBufferRtv);
                _d3dContext.VSSetShader(_vertexShader);
                _d3dContext.PSSetShader(_ictcpPixelShader);
                _d3dContext.IASetInputLayout(_inputLayout);
                _d3dContext.PSSetShaderResources(0, new[] { _ictcpYSrvs[slot]!, _ictcpUVSrvs[slot]! });
                _d3dContext.PSSetSampler(0, _samplerState);
                _d3dContext.PSSetConstantBuffer(0, _doviConstantBuffer); // b0: DOVI matrix
                _d3dContext.PSSetConstantBuffer(1, _doviReshapeBuffer);  // b1: DOVI reshape curves
                _d3dContext.IASetVertexBuffer(0, _vertexBuffer, 16, 0);
                _d3dContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                _d3dContext.Draw(4, 0);

                // 解绑SRV，允许FFmpeg端复用纹理
                _d3dContext.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());

                // 像素级读回诊断（前3帧 + 每600帧）
                if (_renderFrameCount <= 3 || _renderFrameCount % 600 == 0)
                    DiagReadbackIctcp(frameTexture);
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"ICtCp渲染失败，回退HDR10路径: {ex.Message}");
                _ictcpYSrvs[_inputViewIndex]?.Dispose();
                _ictcpYSrvs[_inputViewIndex] = null;
                _ictcpUVSrvs[_inputViewIndex]?.Dispose();
                _ictcpUVSrvs[_inputViewIndex] = null;
                _ictcpPipelineBroken = true;
                return false;
            }
        }

        public void UpdateFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height,
            int yStride = 0, int uStride = 0, int vStride = 0)
        {
            if (_stopRequested || !_swapChainReady) return;
            if (yPlane == null || uPlane == null || vPlane == null || width <= 0 || height <= 0) return;

            byte[] nv12 = ConvertYUV420PToNV12(yPlane, uPlane, vPlane, width, height, yStride, uStride, vStride);
            // 限制队列深度，防止渲染线程跟不上时内存无限增长
            while (_nv12Queue.Count > 3)
                _nv12Queue.TryDequeue(out _);
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

        /// <summary>
        /// 重置渲染缓冲（Seek 后调用）。
        /// 清空三缓冲 InputView、软解队列，并设置Seek标记阻止渲染旧帧。
        /// </summary>
        public void ResetBuffers()
        {
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, ResetBuffers);
                return;
            }
            try
            {
                _isSeeking = true;
                for (int i = 0; i < InputViewSlots; i++)
                {
                    _inputViews[i]?.Dispose();
                    _inputViews[i] = null;
                }
                _inputViewIndex = 0;
                // 清空软解队列，丢弃所有旧帧
                while (_nv12Queue.TryDequeue(out _)) ;
                Interlocked.Exchange(ref _renderQueued, 0);
                // 强制 GPU 完成所有挂起命令，确保旧帧不再被使用
                _d3dContext?.ClearState();
                _d3dContext?.Flush();
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"重置渲染缓冲失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Seek完成后调用，允许渲染新帧
        /// </summary>
        public void OnSeekCompleted()
        {
            _isSeeking = false;
        }

        // ==================== 原生平面SRV（P/Invoke） ====================
        // Vortice 3.x 的 D3D11 Texture2DShaderResourceView 未映射 D3D11.1 的 PlaneSlice 字段，
        // 无法表达 P010 平面视图。此处按 D3D11_TEX2D_SRV1 原生布局直接走 COM vtable 创建。
        // ID3D11Device vtable 槽位：0-2=IUnknown, 3=CreateBuffer, 4=CreateTexture1D,
        // 5=CreateTexture2D, 6=CreateTexture3D, 7=CreateShaderResourceView。
        private static class NativeD3D11
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct SRVDescTex2DPlane   // D3D11_SHADER_RESOURCE_VIEW_DESC + D3D11_TEX2D_SRV1
            {
                public uint Format;           // DXGI_FORMAT
                public uint ViewDimension;    // D3D11_SRV_DIMENSION_TEXTURE2D = 4
                public uint MostDetailedMip;
                public uint MipLevels;        // 0xFFFFFFFF = 全部mip
                public uint PlaneSlice;
            }

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int CreateSRVDelegate(IntPtr device, IntPtr resource, ref SRVDescTex2DPlane desc, out IntPtr srv);

            public static IntPtr CreatePlaneSRV(IntPtr device, IntPtr texture, uint dxgiFormat, uint planeSlice)
            {
                var desc = new SRVDescTex2DPlane
                {
                    Format = dxgiFormat,
                    ViewDimension = 4,
                    MostDetailedMip = 0,
                    MipLevels = 0xFFFFFFFF,
                    PlaneSlice = planeSlice
                };
                var vtable = Marshal.ReadIntPtr(device);
                var fnPtr = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size);
                var createSrv = Marshal.GetDelegateForFunctionPointer<CreateSRVDelegate>(fnPtr);
                int hr = createSrv(device, texture, ref desc, out IntPtr srv);
                if (hr != 0)
                    DebugLogger.WriteLine($"CreatePlaneSRV失败: hr=0x{hr:X8}, format={dxgiFormat}, plane={planeSlice}");
                return hr == 0 ? srv : IntPtr.Zero;
            }
        }

        // ==================== 清理 ====================
        private bool _cleanupDone;

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
            if (_cleanupDone) return; // Unloaded/DestroyWindowCore/Dispose 可能先后触发，防止重复释放
            _cleanupDone = true;

            _stopRequested = true;
            _colorDiagLogged = false;
            _renderFrameCount = 0;
            _d3dInitialized = false;
            _swapChainReady = false; // 与对象释放保持一致，防止清理后被误用
            _d3dContext?.ClearState(); _d3dContext?.Flush();
            CleanupShaderPipeline();
            CleanupYUVTextures();

            // 释放三缓冲输入视图与ICtCp平面SRV
            for (int i = 0; i < InputViewSlots; i++)
            {
                _inputViews[i]?.Dispose();
                _inputViews[i] = null;
                _ictcpYSrvs[i]?.Dispose();
                _ictcpYSrvs[i] = null;
                _ictcpUVSrvs[i]?.Dispose();
                _ictcpUVSrvs[i] = null;
            }
            _inputViewIndex = 0;
            SafeDispose(ref _ictcpPixelShader);
            SafeDispose(ref _doviConstantBuffer);

            SafeDispose(ref _vpOutputView);
            SafeDispose(ref _backBufferRtv); SafeDispose(ref _backBufferTexture);
            SafeDispose(ref _swapChain); SafeDispose(ref _dxgiFactory);
            SafeDispose(ref _videoProcessor); SafeDispose(ref _vpEnumerator);
            SafeDispose(ref _videoContext1);
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
