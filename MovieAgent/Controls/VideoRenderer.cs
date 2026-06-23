using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.D3DCompiler;

namespace MovieAgent.Controls;

/// <summary>
/// 基于 D3D11 VideoProcessor 的零拷贝视频渲染器
/// 支持 D3D11VA 硬件解码 NV12 纹理直通 + HDR 色彩空间
/// 流水线: NV12纹理 → VideoProcessorBlt → SwapChain → 显示 (全程GPU零拷贝)
/// </summary>
public class VideoRenderer : HwndHost
{
    // Win32 窗口常量
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private static ILoggerService? _logger;
    private static readonly object _loggerLock = new();

    private ILoggerService Logger
    {
        get
        {
            if (_logger == null)
            {
                lock (_loggerLock)
                {
                    _logger ??= new SimpleLogger();
                }
            }
            return _logger;
        }
    }

    // ==================== D3D11 核心 ====================
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGIFactory2? _dxgiFactory;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _backBufferRtv;

    // ==================== VideoProcessor ====================
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorEnumerator? _vpEnumerator;

    // ==================== CPU上传路径 - VideoProcessor 方案 ====================
    private ID3D11Texture2D? _nv12VPTexture;       // 默认纹理 (GPU only, VideoProcessor 输入)
    private ID3D11Texture2D? _nv12StagingTexture;   // 暂存纹理 (CPU 可写, 用于上传)
    private ID3D11VideoProcessorInputView? _nv12VPInputView;

    // ==================== CPU上传路径 - Shader 渲染管线 ====================
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11SamplerState? _samplerState;
    private ID3D11ShaderResourceView? _nv12YSRV;
    private ID3D11ShaderResourceView? _nv12UVSRV;
    private bool _shaderPipelineReady;

    // ==================== 窗口 ====================
    private IntPtr _hwnd;
    private IntPtr _parentHwnd;
    private static bool _wndClassRegistered;
    private static readonly WndProcDelegate _wndProc = WndProc;
    private static readonly IntPtr _wndProcPtr;

    // ==================== 状态 ====================
    private bool _d3dInitialized;
    private bool _disposed;
    private int _lastWidth;
    private int _lastHeight;

    // 视频尺寸
    private int _videoWidth;
    private int _videoHeight;

    // 帧数据 (CPU上传路径)
    private readonly object _lockObj = new();
    private byte[]? _pendingNV12Data;
    private int _pendingNV12Stride;
    private int _frameWidth;
    private int _frameHeight;

    // HDR 色彩空间
    private bool _isHdr;
    private VideoProcessorColorSpace _colorSpace;

    private readonly Dispatcher _uiDispatcher;

    static VideoRenderer()
    {
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
    }

    public VideoRenderer()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) { }
    private void OnUnloaded(object sender, RoutedEventArgs e) => CleanupD3D();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hwnd != IntPtr.Zero && _d3dInitialized)
        {
            int w = (int)e.NewSize.Width;
            int h = (int)e.NewSize.Height;
            if (w > 0 && h > 0 && (w != _lastWidth || h != _lastHeight))
            {
                _lastWidth = w;
                _lastHeight = h;
                MoveWindow(_hwnd, 0, 0, w, h, true);
                ResizeSwapChain(w, h);
            }
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 转发鼠标消息到顶层窗口，使 WPF 能响应鼠标移动显示控制栏
        if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
            msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_MBUTTONDOWN)
        {
            var root = GetAncestor(hWnd, GA_ROOT);
            if (root != IntPtr.Zero)
            {
                PostMessage(root, msg, wParam, lParam);
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ==================== HwndHost ====================

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (!_wndClassRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _wndProcPtr,
                hInstance = IntPtr.Zero,
                lpszClassName = "D3D11VideoRendererVP",
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero
            };

            if (RegisterClassEx(ref wc) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"窗口类注册失败: {err}");
                return new HandleRef(this, IntPtr.Zero);
            }
            _wndClassRegistered = true;
        }

        int width = Math.Max(1, (int)ActualWidth);
        int height = Math.Max(1, (int)ActualHeight);

        _parentHwnd = hwndParent.Handle;

        _hwnd = CreateWindowEx(
            0, "D3D11VideoRendererVP", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, width, height,
            _parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Debug.WriteLine($"创建窗口失败: {err}");
            return new HandleRef(this, IntPtr.Zero);
        }

        _lastWidth = width;
        _lastHeight = height;
        InitializeD3D();

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        CleanupD3D();
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
    }

    // ==================== D3D11 初始化 ====================

    private void InitializeD3D()
    {
        try
        {
            CreateDeviceAndSwapChain();
            CreateVideoProcessor();
            CreateShaderPipeline();
            _d3dInitialized = true;
            Logger.Debug("D3D11 VideoProcessor + Shader 管线初始化成功");
        }
        catch (Exception ex)
        {
            Logger.Debug($"D3D11 初始化失败: {ex.Message}");
            _d3dInitialized = false;
        }
    }

    private void CreateDeviceAndSwapChain()
    {
        _swapChain?.Dispose();
        _swapChain = null;
        _dxgiFactory?.Dispose();
        _dxgiFactory = null;

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport | DeviceCreationFlags.Debug;

        var scDesc = default(SwapChainDescription);
        scDesc.BufferCount = 2;
        scDesc.BufferDescription = new ModeDescription
        {
            Width = (uint)Math.Max(1, _lastWidth),
            Height = (uint)Math.Max(1, _lastHeight),
            Format = Format.R8G8B8A8_UNorm,
            RefreshRate = new Rational(60, 1),
            ScanlineOrdering = ModeScanlineOrder.Unspecified,
            Scaling = ModeScaling.Unspecified
        };
        scDesc.BufferUsage = Usage.RenderTargetOutput;
        scDesc.OutputWindow = _hwnd;
        scDesc.SampleDescription = new SampleDescription(1, 0);
        scDesc.SwapEffect = 0; // Discard
        scDesc.Windowed = true;
        scDesc.Flags = SwapChainFlags.None;

        Logger.Debug($"SwapChainDesc: BufferCount={scDesc.BufferCount}, BufferUsage={scDesc.BufferUsage}, " +
            $"Width={scDesc.BufferDescription.Width}, Height={scDesc.BufferDescription.Height}, " +
            $"Format={scDesc.BufferDescription.Format}, SwapEffect={scDesc.SwapEffect} (raw={(int)scDesc.SwapEffect}), " +
            $"SampleDesc.Count={scDesc.SampleDescription.Count}, SampleDesc.Quality={scDesc.SampleDescription.Quality}, " +
            $"HWND=0x{_hwnd:X}");

        var result = D3D11.D3D11CreateDeviceAndSwapChain(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            flags,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 },
            scDesc,
            out var swapChain,
            out _d3dDevice,
            out _,
            out _d3dContext
        );

        result.CheckError();
        _swapChain = swapChain;

        if (_d3dDevice == null || _swapChain == null || _d3dContext == null)
            throw new InvalidOperationException("D3D11CreateDeviceAndSwapChain 返回 null");

        Logger.Debug($"D3D11CreateDeviceAndSwapChain result={result}, swapChain type={_swapChain.GetType().FullName}");

        _videoDevice = _d3dDevice.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _d3dContext.QueryInterface<ID3D11VideoContext>();

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        _dxgiFactory = adapter.GetParent<IDXGIFactory2>();

        CreateBackBuffer();
        Logger.Debug($"D3D11CreateDeviceAndSwapChain 成功: {_lastWidth}x{_lastHeight}");
    }

    private ID3D11Texture2D? GetBackBuffer()
    {
        if (_swapChain == null) return null;

        IntPtr swapChainPtr = Marshal.GetComInterfaceForObject(_swapChain, typeof(IDXGISwapChain));
        IntPtr vtablePtr = Marshal.ReadIntPtr(swapChainPtr);
        IntPtr getBufferPtr = Marshal.ReadIntPtr(vtablePtr, 8 * IntPtr.Size); // index 8 = GetBuffer
        var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(getBufferPtr);

        Guid iid = typeof(ID3D11Texture2D).GUID;
        int hr = getBuffer(swapChainPtr, 0, ref iid, out IntPtr backBufferPtr);
        Logger.Debug($"GetBackBuffer: native GetBuffer hr=0x{hr:X8}, ptr=0x{backBufferPtr:X}");

        Marshal.Release(swapChainPtr);

        if (hr < 0)
        {
            Logger.Debug($"GetBackBuffer failed: HRESULT 0x{hr:X8}");
            return null;
        }

        return ComObject.As<ID3D11Texture2D>(backBufferPtr);
    }

    private void CreateBackBuffer()
    {
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;

        using var backBuffer = GetBackBuffer();
        if (backBuffer == null)
            throw new InvalidOperationException("CreateBackBuffer: GetBackBuffer returned null");

        _backBufferRtv = _d3dDevice!.CreateRenderTargetView(backBuffer);
        Logger.Debug($"CreateBackBuffer success: {_lastWidth}x{_lastHeight}");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr thisPtr, int buffer, ref Guid riid, out IntPtr ppSurface);

    // ==================== VideoProcessor ====================

    private void CreateVideoProcessor()
    {
        var vpDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = 1920,
            InputHeight = 1080,
            InputFrameRate = new Rational(60u, 1u),
            OutputWidth = 1920,
            OutputHeight = 1080,
            OutputFrameRate = new Rational(60u, 1u),
            Usage = VideoUsage.PlaybackNormal
        };

        _vpEnumerator = _videoDevice!.CreateVideoProcessorEnumerator(vpDesc);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_vpEnumerator, 0);

        // 默认 SDR 色彩空间 (BT.709, 有限范围 16-235)
        _colorSpace = new VideoProcessorColorSpace
        {
            Usage = 0,          // Playback
            YCbCr_xvYCC = 0,
            Nominal_Range = 1,  // 16-235 (MPEG range, FFmpeg 默认输出)
            RGB_Range = 0,      // Full range
            YCbCr_Matrix = 1    // BT.709
        };
    }

    private void RecreateVideoProcessor(int width, int height, int fps = 60)
    {
        var vpDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            InputFrameRate = new Rational((uint)fps, 1u),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            OutputFrameRate = new Rational((uint)fps, 1u),
            Usage = VideoUsage.PlaybackNormal
        };

        _videoProcessor?.Dispose();
        _vpEnumerator?.Dispose();

        _vpEnumerator = _videoDevice!.CreateVideoProcessorEnumerator(vpDesc);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_vpEnumerator, 0);
    }

    private void ResizeSwapChain(int width, int height)
    {
        if (_swapChain == null || _d3dDevice == null || _d3dContext == null) return;

        _backBufferRtv?.Dispose();
        _backBufferRtv = null;

        _d3dContext.ClearState();
        _d3dContext.Flush();

        _swapChain.ResizeBuffers(2, (uint)width, (uint)height,
            Format.R8G8B8A8_UNorm, SwapChainFlags.None);

        CreateBackBuffer();
    }

    // ==================== Shader 渲染管线 (CPU上传路径) ====================

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
    float u = uv.x - 0.5;
    float v = uv.y - 0.5;
    // BT.709 limited range (16-235) to full range
    y = (y - 0.0627451) * 1.16438;
    float r = saturate(y + 1.79274 * v);
    float g = saturate(y - 0.213249 * u - 0.532909 * v);
    float b = saturate(y + 2.11240 * u);
    return float4(r, g, b, 1);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct VertexPositionTexture
    {
        public float X, Y;
        public float U, V;
    }

    private void CreateShaderPipeline()
    {
        if (_d3dDevice == null) return;

        // 编译顶点着色器
        var vsBlob = Compiler.Compile(NV12_VS, null, null, "main_vs", "vs_4_0",
            ShaderFlags.PackMatrixRowMajor | ShaderFlags.OptimizationLevel3);
        _vertexShader = _d3dDevice.CreateVertexShader(vsBlob.Span);

        // 编译像素着色器
        var psBlob = Compiler.Compile(NV12_PS, null, null, "main_ps", "ps_4_0",
            ShaderFlags.PackMatrixRowMajor | ShaderFlags.OptimizationLevel3);
        _pixelShader = _d3dDevice.CreatePixelShader(psBlob.Span);

        // 创建输入布局
        var inputElements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
        };
        _inputLayout = _d3dDevice.CreateInputLayout(inputElements, vsBlob.Span);

        // 创建全屏四边形顶点缓冲
        var vertices = new[]
        {
            new VertexPositionTexture { X = -1, Y = -1, U = 0, V = 1 },
            new VertexPositionTexture { X = -1, Y =  1, U = 0, V = 0 },
            new VertexPositionTexture { X =  1, Y = -1, U = 1, V = 1 },
            new VertexPositionTexture { X =  1, Y =  1, U = 1, V = 0 },
        };

        var bufferDesc = new BufferDescription
        {
            ByteWidth = (uint)(vertices.Length * 16), // 4 floats * 4 bytes
            BindFlags = BindFlags.VertexBuffer,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
        try
        {
            _vertexBuffer = _d3dDevice.CreateBuffer(bufferDesc, new SubresourceData(handle.AddrOfPinnedObject()));
        }
        finally
        {
            handle.Free();
        }

        // 创建采样器状态 (线性插值)
        var samplerDesc = new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue
        };
        _samplerState = _d3dDevice.CreateSamplerState(samplerDesc);

        _shaderPipelineReady = true;
        Logger.Debug("Shader 渲染管线初始化成功");
    }

    private void CleanupShaderPipeline()
    {
        _shaderPipelineReady = false;
        _vertexShader?.Dispose(); _vertexShader = null;
        _pixelShader?.Dispose(); _pixelShader = null;
        _inputLayout?.Dispose(); _inputLayout = null;
        _vertexBuffer?.Dispose(); _vertexBuffer = null;
        _samplerState?.Dispose(); _samplerState = null;
        _nv12YSRV?.Dispose(); _nv12YSRV = null;
        _nv12UVSRV?.Dispose(); _nv12UVSRV = null;
    }

    // ==================== CPU 上传 NV12 纹理 (Shader 方案) ====================

    private void EnsureNV12ShaderTexture(int width, int height)
    {
        if (_videoWidth == width && _videoHeight == height && _nv12VPTexture != null && _nv12YSRV != null)
            return;

        CleanupNV12ShaderTexture();
        _videoWidth = width;
        _videoHeight = height;

        if (_d3dDevice == null) return;

        try
        {
            // 创建 Staging 纹理 (CPU 可写)
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None
            };
            _nv12StagingTexture = _d3dDevice.CreateTexture2D(stagingDesc);

            // 创建默认纹理 (GPU only, ShaderResourceView)
            var defaultDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            _nv12VPTexture = _d3dDevice.CreateTexture2D(defaultDesc);

            // 创建 Y 平面 SRV (R8, subresource 0)
            var ySrvDesc = new ShaderResourceViewDescription
            {
                Format = Format.R8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
            };
            _nv12YSRV = _d3dDevice.CreateShaderResourceView(_nv12VPTexture, ySrvDesc);

            // 创建 UV 平面 SRV (R8G8, subresource 1)
            var uvSrvDesc = new ShaderResourceViewDescription
            {
                Format = Format.R8G8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
            };
            _nv12UVSRV = _d3dDevice.CreateShaderResourceView(_nv12VPTexture, uvSrvDesc);

            Logger.Debug($"NV12 Shader texture created: {width}x{height}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"NV12 Shader texture creation failed: {ex.Message}");
            CleanupNV12ShaderTexture();
        }
    }

    private void CleanupNV12ShaderTexture()
    {
        _nv12YSRV?.Dispose(); _nv12YSRV = null;
        _nv12UVSRV?.Dispose(); _nv12UVSRV = null;
        _nv12VPTexture?.Dispose(); _nv12VPTexture = null;
        _nv12StagingTexture?.Dispose(); _nv12StagingTexture = null;
    }

    // ==================== CPU 上传 NV12 纹理 (VideoProcessor 方案) ====================

    private void EnsureNV12VPTexture(int width, int height)
    {
        if (_videoWidth == width && _videoHeight == height && _nv12VPTexture != null && _nv12VPInputView != null)
            return;

        CleanupNV12VPTexture();
        _videoWidth = width;
        _videoHeight = height;

        if (_d3dDevice == null || _videoDevice == null || _vpEnumerator == null) return;

        try
        {
            // 创建 Staging 纹理 (CPU 可写, 用于上传)
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None
            };
            _nv12StagingTexture = _d3dDevice.CreateTexture2D(stagingDesc);

            // 创建默认纹理 (GPU only, 用于 VideoProcessor)
            var defaultDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            _nv12VPTexture = _d3dDevice.CreateTexture2D(defaultDesc);

            // 创建 VideoProcessorInputView
            var inputDesc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView
                {
                    MipSlice = 0,
                    ArraySlice = 0
                }
            };
            _nv12VPInputView = _videoDevice.CreateVideoProcessorInputView(
                _nv12VPTexture, _vpEnumerator, inputDesc);

            Logger.Debug($"NV12 VP texture created: {width}x{height}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"NV12 VP texture creation failed: {ex.Message}");
            CleanupNV12VPTexture();
        }
    }

    private void CleanupNV12VPTexture()
    {
        _nv12VPInputView?.Dispose(); _nv12VPInputView = null;
        _nv12VPTexture?.Dispose(); _nv12VPTexture = null;
        _nv12StagingTexture?.Dispose(); _nv12StagingTexture = null;
    }

    // ==================== 帧更新接口 ====================

    /// <summary>
    /// 更新帧数据 (YUV420P, CPU上传路径)
    /// </summary>
    public void UpdateFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height,
        int yStride = 0, int uStride = 0, int vStride = 0)
    {
        if (yPlane == null || uPlane == null || vPlane == null || width <= 0 || height <= 0)
            return;

        // 默认 stride = width (无padding)
        if (yStride <= 0) yStride = width;
        if (uStride <= 0) uStride = width / 2;
        if (vStride <= 0) vStride = width / 2;

        // 将 YUV420P 三个平面合并为 NV12（正确处理 stride）
        byte[] nv12 = ConvertYUV420PToNV12(yPlane, uPlane, vPlane, width, height, yStride, uStride, vStride);

        lock (_lockObj)
        {
            _pendingNV12Data = nv12;
            _pendingNV12Stride = width;
            _frameWidth = width;
            _frameHeight = height;
        }

        _uiDispatcher.BeginInvoke(DispatcherPriority.Render, RenderNV12Frame);
    }

    /// <summary>
    /// 零拷贝渲染 D3D11VA 硬件解码纹理
    /// 直接使用解码器输出的 NV12 GPU 纹理，无需 CPU 拷贝
    /// </summary>
    public unsafe void RenderD3D11VATexture(IntPtr nv12TexturePtr, int width, int height, uint arrayIndex = 0)
    {
        if (!_d3dInitialized || _d3dContext == null || _videoContext == null || _videoProcessor == null)
            return;

        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(DispatcherPriority.Render,
                () => RenderD3D11VATexture(nv12TexturePtr, width, height, arrayIndex));
            return;
        }

        try
        {
            _videoWidth = width;
            _videoHeight = height;

            using var backBuffer = GetBackBuffer();
            if (backBuffer == null) return;

            // 创建解码器输出纹理的输入视图
            var inputDesc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView
                {
                    MipSlice = 0,
                    ArraySlice = arrayIndex
                }
            };

            using var inputView = _videoDevice!.CreateVideoProcessorInputView(
                new ID3D11Texture2D(nv12TexturePtr), _vpEnumerator!, inputDesc);

            // 创建 SwapChain backbuffer 的输出视图
            var outputDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            };

            using var outputView = _videoDevice.CreateVideoProcessorOutputView(
                backBuffer, _vpEnumerator!, outputDesc);

            // 设置色彩空间
            _videoContext.VideoProcessorSetStreamColorSpace(_videoProcessor, 0, _colorSpace);

            // 设置流帧格式
            _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor, 0, VideoFrameFormat.Progressive);

            // 设置目标矩形 (全屏)
            _videoContext.VideoProcessorSetStreamSourceRect(_videoProcessor, 0, true, new RawRect(0, 0, width, height));
            _videoContext.VideoProcessorSetStreamDestRect(_videoProcessor, 0, true, new RawRect(0, 0, _lastWidth, _lastHeight));

            _videoContext.VideoProcessorSetOutputTargetRect(_videoProcessor, true, new RawRect(0, 0, _lastWidth, _lastHeight));
            _videoContext.VideoProcessorSetOutputColorSpace(_videoProcessor, new VideoProcessorColorSpace
            {
                Usage = 0,
                RGB_Range = 0,
                YCbCr_Matrix = 1,
                Nominal_Range = 1  // 16-235 有限范围
            });

            // 执行 VideoProcessorBlt (GPU硬件转换, 零拷贝)
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                PpPastSurfaces = null,
                InputSurface = inputView,
                PpFutureSurfaces = null
            };

            _videoContext.VideoProcessorBlt(_videoProcessor, outputView, 0, 1, new[] { stream });

            // 呈现
            _swapChain!.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            Logger.Debug($"D3D11VA渲染失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 设置 HDR 色彩空间 (BT.2020 + SMPTE2084)
    /// </summary>
    public void SetHDRColorSpace(bool isHdr)
    {
        _isHdr = isHdr;
        _colorSpace = isHdr
            ? new VideoProcessorColorSpace
            {
                Usage = 0,
                YCbCr_xvYCC = 0,
                Nominal_Range = 1,  // Normal (16-235)
                RGB_Range = 0,
                YCbCr_Matrix = 2    // BT.2020
            }
            : new VideoProcessorColorSpace
            {
                Usage = 0,
                YCbCr_xvYCC = 0,
                Nominal_Range = 1,  // 16-235 (MPEG range)
                RGB_Range = 0,
                YCbCr_Matrix = 1    // BT.709
            };
    }

    /// <summary>
    /// 获取 D3D11 设备 (供 FFmpeg D3D11VA 解码器共享)
    /// </summary>
    public ID3D11Device? GetDevice() => _d3dDevice;

    /// <summary>
    /// 获取 D3D11 设备上下文
    /// </summary>
    public ID3D11DeviceContext? GetDeviceContext() => _d3dContext;

    public void Clear()
    {
        lock (_lockObj)
        {
            _pendingNV12Data = null;
        }

        if (_d3dContext != null && _backBufferRtv != null && CheckAccess())
        {
            try
            {
                _d3dContext.ClearRenderTargetView(_backBufferRtv, new Color4(0, 0, 0, 1));
                _swapChain?.Present(1, PresentFlags.None);
            }
            catch { }
        }
    }

    // ==================== Shader 渲染 (CPU上传路径) ====================

    private unsafe void RenderNV12Frame()
    {
        if (!_d3dInitialized || _d3dContext == null || !_shaderPipelineReady)
            return;

        byte[]? nv12Data;
        int stride, width, height;

        lock (_lockObj)
        {
            if (_pendingNV12Data == null) return;
            nv12Data = _pendingNV12Data;
            stride = _pendingNV12Stride;
            width = _frameWidth;
            height = _frameHeight;
            _pendingNV12Data = null;
        }

        try
        {
            EnsureNV12ShaderTexture(width, height);

            if (_nv12StagingTexture == null || _nv12VPTexture == null || _nv12YSRV == null)
                return;

            // 1. 上传 NV12 数据到 Staging 纹理 (Y 和 UV 是独立 subresource)
            fixed (byte* pData = nv12Data)
            {
                int ySize = stride * height;

                // 上传 Y 平面 (subresource 0)
                var yMapped = _d3dContext.Map(_nv12StagingTexture, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                byte* yDst = (byte*)yMapped.DataPointer;
                int yDstStride = (int)yMapped.RowPitch;
                int copyBytes = Math.Min(stride, yDstStride);

                for (int y = 0; y < height; y++)
                {
                    byte* rowSrc = pData + y * stride;
                    byte* rowDst = yDst + y * yDstStride;
                    Buffer.MemoryCopy(rowSrc, rowDst, (uint)copyBytes, (uint)copyBytes);
                    if (yDstStride > stride)
                        new Span<byte>(rowDst + stride, yDstStride - stride).Clear();
                }
                _d3dContext.Unmap(_nv12StagingTexture, 0);

                // 上传 UV 平面 (subresource 1)
                var uvMapped = _d3dContext.Map(_nv12StagingTexture, 1, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                byte* uvDst = (byte*)uvMapped.DataPointer;
                int uvDstStride = (int)uvMapped.RowPitch;
                copyBytes = Math.Min(stride, uvDstStride);

                byte* uvSrc = pData + ySize;
                for (int y = 0; y < height / 2; y++)
                {
                    byte* rowSrc = uvSrc + y * stride;
                    byte* rowDst = uvDst + y * uvDstStride;
                    Buffer.MemoryCopy(rowSrc, rowDst, (uint)copyBytes, (uint)copyBytes);
                    if (uvDstStride > stride)
                        new Span<byte>(rowDst + stride, uvDstStride - stride).Clear();
                }
                _d3dContext.Unmap(_nv12StagingTexture, 1);
            }

            // 2. 从 Staging 拷贝到 Default 纹理
            _d3dContext.CopyResource(_nv12VPTexture, _nv12StagingTexture);

            // 3. 使用 Shader 管线渲染
            _d3dContext.OMSetRenderTargets(_backBufferRtv);
            _d3dContext.RSSetViewport(new Viewport(0, 0, _lastWidth, _lastHeight));

            // 清空背景
            _d3dContext.ClearRenderTargetView(_backBufferRtv, new Color4(0, 0, 0, 1));

            // 设置着色器
            _d3dContext.VSSetShader(_vertexShader!);
            _d3dContext.PSSetShader(_pixelShader!);
            _d3dContext.IASetInputLayout(_inputLayout!);

            // 设置 SRV (Y 平面和 UV 平面)
            _d3dContext.PSSetShaderResources(0, new[] { _nv12YSRV });
            _d3dContext.PSSetShaderResources(1, new[] { _nv12UVSRV });
            _d3dContext.PSSetSampler(0, _samplerState!);

            // 设置顶点缓冲
            uint stride_ = 16; // 4 floats * 4 bytes
            _d3dContext.IASetVertexBuffer(0, _vertexBuffer!, stride_, 0);
            _d3dContext.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleStrip);

            // 绘制全屏四边形
            _d3dContext.Draw(4, 0);

            // 清理 SRV 绑定
            _d3dContext.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());
            _d3dContext.PSSetShaderResources(1, Array.Empty<ID3D11ShaderResourceView>());

            _swapChain!.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            Logger.Debug($"NV12 Shader渲染失败: {ex.Message}");
        }
    }

    // ==================== YUV420P → NV12 转换 ====================

    private static byte[] ConvertYUV420PToNV12(byte[] y, byte[] u, byte[] v, int width, int height,
        int yStride, int uStride, int vStride)
    {
        int ySize = width * height;
        int uvWidth = width / 2;
        int uvHeight = height / 2;
        int uvSize = uvWidth * uvHeight;
        byte[] nv12 = new byte[ySize + uvSize * 2];

        // Y 平面: 逐行拷贝，跳过 padding 字节
        for (int row = 0; row < height; row++)
        {
            int srcOffset = row * yStride;
            int dstOffset = row * width;
            Array.Copy(y, srcOffset, nv12, dstOffset, width);
        }

        // UV 交错平面: 逐行拷贝 U 和 V，跳过 padding 字节
        int uvDstOffset = ySize;
        for (int row = 0; row < uvHeight; row++)
        {
            int uSrcOffset = row * uStride;
            int vSrcOffset = row * vStride;
            for (int col = 0; col < uvWidth; col++)
            {
                nv12[uvDstOffset + (row * uvWidth + col) * 2] = u[uSrcOffset + col];
                nv12[uvDstOffset + (row * uvWidth + col) * 2 + 1] = v[vSrcOffset + col];
            }
        }

        return nv12;
    }

    // ==================== 清理 ====================

    private void CleanupD3D()
    {
        _d3dInitialized = false;

        if (_d3dContext != null)
        {
            _d3dContext.ClearState();
            _d3dContext.Flush();
        }

        CleanupShaderPipeline();
        CleanupNV12ShaderTexture();
        CleanupNV12VPTexture();

        _backBufferRtv?.Dispose(); _backBufferRtv = null;
        _swapChain?.Dispose(); _swapChain = null;
        _dxgiFactory?.Dispose(); _dxgiFactory = null;
        _videoProcessor?.Dispose(); _videoProcessor = null;
        _vpEnumerator?.Dispose(); _vpEnumerator = null;
        _videoContext?.Dispose(); _videoContext = null;
        _videoDevice?.Dispose(); _videoDevice = null;
        _d3dContext?.Dispose(); _d3dContext = null;
        _d3dDevice?.Dispose(); _d3dDevice = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                CleanupD3D();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    // ==================== 结构体 ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ==================== Win32 API ====================

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    private const uint GA_ROOT = 2;

    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
}