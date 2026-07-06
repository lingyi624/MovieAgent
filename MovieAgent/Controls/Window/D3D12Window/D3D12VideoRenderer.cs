﻿﻿﻿using MovieAgent.FFmpegDecoder;
using NAudio.CoreAudioApi;
using NAudio.Utils;
using SharpGen.Runtime;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.Dxc;
using Vortice.DXGI;
using Vortice.Mathematics;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace MovieAgent.D3D12Window
{
    public class D3D12VideoRenderer : HwndHost
    {
        private const uint WS_CHILD = 0x40000000,
                           WS_VISIBLE = 0x10000000,
                           WS_CLIPSIBLINGS = 0x04000000,
                           WS_CLIPCHILDREN = 0x02000000;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProcDelegate _wndProc = WndProc;
        private static readonly IntPtr _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        private static bool _wndClassRegistered;

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll")]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // D3D12 核心对象
        private ID3D12Device? _device;
        private ID3D12CommandQueue? _commandQueue;
        private IDXGIFactory2? _dxgiFactory;
        private IDXGISwapChain3? _swapChain;
        private ID3D12Resource?[] _backBuffers = new ID3D12Resource?[2];
        private ID3D12DescriptorHeap? _rtvHeap;
        private int _rtvDescriptorSize;

        // 帧同步（修正 fence 逻辑）
        private ID3D12Fence? _fence;
        private ulong _fenceValue;          // 下一个要等待的值
        private AutoResetEvent? _frameEvent;
        private uint _currentBackBufferIndex;

        // 命令记录
        private ID3D12CommandAllocator? _commandAllocator;
        private ID3D12GraphicsCommandList? _commandList;

        // 管线（支持 NV12 双平面采样）
        private ID3D12RootSignature? _rootSignature;
        private ID3D12PipelineState? _pipelineState;
        private ID3D12Resource? _vertexBuffer;
        private ID3D12DescriptorHeap? _srvHeap;      // 2 个槽位：Y(r8_unorm) 和 UV(r8g8_unorm)
        private ID3D12DescriptorHeap? _samplerHeap;
        private bool _pipelineReady;
        private VertexBufferView _vertexBufferView;

        // 窗口与尺寸
        private IntPtr _hwnd, _parentHwnd;
        private bool _initialized, _disposed;
        private readonly object _resizeLock = new();
        private int _swapChainWidth, _swapChainHeight;
        private int _videoWidth, _videoHeight;
        private volatile bool _swapChainReady;

        // 视口缓存
        private RawRect _cachedDestRect;
        private VideoScaleMode _scaleMode = VideoScaleMode.Fit;

        // 软解队列（NV12 数据）
        private readonly ConcurrentQueue<(byte[] data, int width, int height)> _nv12Queue = new();
        private byte[]? _nv12Buffer;           // 可重用缓冲区
        private int _renderQueued;
        private volatile bool _stopRequested;

        // 纹理资源（NV12 格式）
        private ID3D12Resource? _nv12Texture;       // 内部 NV12 纹理（软解用）
        private ID3D12Resource? _nv12UploadBuffer;  // 上传缓冲区
        private ID3D12Resource? _hardwareNv12Internal; // 硬解拷贝的 NV12 纹理
        private bool _nv12TextureInCopyDest;         // 跟踪 NV12 纹理当前状态

        public readonly Dispatcher _uiDispatcher;

        public D3D12VideoRenderer()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            Loaded += (_, _) => { };
            Unloaded += (_, _) => Cleanup();
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            if (!_wndClassRegistered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = _wndProcPtr,
                    lpszClassName = "D3D12VideoRenderer"
                };
                if (RegisterClassEx(ref wc) == 0)
                    throw new InvalidOperationException("窗口类注册失败");
                _wndClassRegistered = true;
            }

            _parentHwnd = hwndParent.Handle;
            // 使用父窗口实际客户区大小，而非屏幕尺寸
            Win32Point pt = new Win32Point();
            Win32Funcs.GetClientRect(_parentHwnd, out Win32Rect rect);
            _swapChainWidth = rect.Right - rect.Left;
            _swapChainHeight = rect.Bottom - rect.Top;
            if (_swapChainWidth <= 0) _swapChainWidth = 1;
            if (_swapChainHeight <= 0) _swapChainHeight = 1;

            _hwnd = CreateWindowEx(0, "D3D12VideoRenderer", "",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                0, 0, _swapChainWidth, _swapChainHeight,
                _parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("创建子窗口失败");

            InitializeD3D12();
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Cleanup();
            if (hwnd.Handle != IntPtr.Zero)
                DestroyWindow(hwnd.Handle);
        }

        private void InitializeD3D12()
        {
            CreateDevice();
            CreateSwapChain();
            CreateRtvHeap();
            CreateFence();
            CreateShaderPipeline();
            _initialized = true;
            _swapChainReady = true;
            ClearScreen();
            DebugLogger.WriteLine("D3D12 初始化完成");
        }

        private void CreateDevice()
        {
#if DEBUG
            try
            {
                var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
                debug?.EnableDebugLayer();
                var debug5 = debug?.QueryInterface<ID3D12Debug5>();
                debug5?.SetEnableAutoName(true);
              }
            catch { }
#endif
            SafeDispose(ref _commandQueue);
            SafeDispose(ref _swapChain);
            SafeDispose(ref _device);

            Result factoryResult = DXGI.CreateDXGIFactory2(false, out IDXGIFactory2 factory);
            factoryResult.CheckError();
            _dxgiFactory = factory;

            IDXGIAdapter1? adapter = null;
            using var factory1 = factory.QueryInterface<IDXGIFactory1>();
            for (int i = 0; ; i++)
            {
                Result result = factory1.EnumAdapters1((uint)i, out IDXGIAdapter1 a);
                if (result.Failure || a == null) break;
                AdapterDescription1 desc = a.Description1;
                if ((desc.Flags & AdapterFlags.Software) == 0&& (desc.VendorId == 0x10DE && desc.Description.Contains("RTX 3050")))
                {
                    adapter = a;
                    break;
                }
                a.Dispose();
            }
            //foreach (var adapter in adapters)
            //{
            //    var desc = adapter.Description;
            //    // 根据 VendorId (0x10DE = NVIDIA) 或 描述包含 "NVIDIA" 来锁定
            //    if (desc.VendorId == 0x10DE && desc.Description.Contains("RTX 3050"))
            //    {
            //        // 用这个 adapter 去创建设备
            //        var device = D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_12_0);
            //        break;
            //    }
            //}


            if (adapter == null)
                throw new InvalidOperationException("未找到可用的硬件适配器");

            Result resultDevice = D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_12_0, out ID3D12Device device);
            resultDevice.CheckError();
            _device = device;
            adapter.Dispose();

            _commandQueue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct)
            {
                Priority = (int)CommandQueuePriority.High
            });
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
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.AllowTearing // 可启用撕裂以降低延迟
            };
            _swapChain = _dxgiFactory!.CreateSwapChainForHwnd(_commandQueue!, _hwnd, desc)
                .QueryInterface<IDXGISwapChain3>();
            _dxgiFactory.MakeWindowAssociation(_parentHwnd, WindowAssociationFlags.IgnoreAll);
            _currentBackBufferIndex = _swapChain.CurrentBackBufferIndex;
        }

        private void CreateRtvHeap()
        {
            _rtvHeap = _device!.CreateDescriptorHeap(new DescriptorHeapDescription
            {
                DescriptorCount = 2,
                Type = DescriptorHeapType.RenderTargetView
            });
            _rtvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            CpuDescriptorHandle handle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
            for (int i = 0; i < 2; i++)
            {
                _backBuffers[i] = _swapChain!.GetBuffer<ID3D12Resource>((uint)i);
                _device.CreateRenderTargetView(_backBuffers[i]!, null, handle);
                handle += _rtvDescriptorSize;
            }
        }

        private void CreateFence()
        {
            _fence = _device!.CreateFence(0, FenceFlags.None);
            _frameEvent = new AutoResetEvent(false);
            _fenceValue = 0;       // 初始为 0，首帧无需等待
        }

        // 处理窗口大小变化（由 HwndHost 重写）
        protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            if (_swapChain == null || !_initialized || !_swapChainReady) return;

            int newWidth = (int)rcBoundingBox.Width;
            int newHeight = (int)rcBoundingBox.Height;
            if (newWidth <= 0) newWidth = 1;
            if (newHeight <= 0) newHeight = 1;

            if (newWidth == _swapChainWidth && newHeight == _swapChainHeight)
                return;

            lock (_resizeLock)
            {
                WaitForGpu(); // 等待所有 GPU 命令完成

                // 释放旧的 RTV
                for (int i = 0; i < _backBuffers.Length; i++)
                {
                    _backBuffers[i]?.Dispose();
                    _backBuffers[i] = null;
                }
                _rtvHeap?.Dispose();

                // 调整交换链
                _swapChain.ResizeBuffers(2, (uint)newWidth, (uint)newHeight, Format.B8G8R8A8_UNorm,
                    SwapChainFlags.AllowTearing);
                _swapChainWidth = newWidth;
                _swapChainHeight = newHeight;
                _currentBackBufferIndex = _swapChain.CurrentBackBufferIndex;

                // 重建 RTV 堆
                CreateRtvHeap();
            }
        }

        public void SetScaleMode(VideoScaleMode mode)
        {
            if (_scaleMode != mode)
            {
                _scaleMode = mode;
                _videoWidth = 0; // 强制重新计算视口
            }
        }

        private RawRect CalculateDestRect(int videoW, int videoH)
        {
            int dstW = _swapChainWidth, dstH = _swapChainHeight;
            if (_scaleMode == VideoScaleMode.Stretch)
                return new RawRect(0, 0, dstW, dstH);

            float vidAspect = (float)videoW / videoH;
            float dstAspect = (float)dstW / dstH;
            int drawW, drawH;

            if (_scaleMode == VideoScaleMode.Zoom)
            {
                if (vidAspect > dstAspect) { drawH = dstH; drawW = (int)(dstH * vidAspect); }
                else { drawW = dstW; drawH = (int)(dstW / vidAspect); }
            }
            else // Fit
            {
                if (vidAspect > dstAspect) { drawW = dstW; drawH = (int)(dstW / vidAspect) & ~1; }
                else { drawH = dstH; drawW = (int)(dstH * vidAspect) & ~1; }
            }

            int offX = (dstW - drawW) / 2;
            int offY = (dstH - drawH) / 2;
            return new RawRect(offX, offY, offX + drawW, offY + drawH);
        }

        // ----------------------------------------------------------
        // 新着色器：接收 NV12 双平面（Y 为 R8, UV 为 R8G8）
        // ----------------------------------------------------------
        private const string ShaderSource = @"
struct VSInput {
    float2 pos : POSITION;
    float2 uv  : TEXCOORD;
};
struct PSInput {
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD;
};

PSInput VSMain(VSInput input) {
    PSInput o;
    o.pos = float4(input.pos, 0, 1);
    o.uv = input.uv;
    return o;
}

Texture2D<float>  texY  : register(t0);
Texture2D<float2> texUV : register(t1);
SamplerState samp : register(s0);

float4 PSMain(PSInput input) : SV_TARGET {
    float y  = texY.Sample(samp, input.uv);
    float2 uv = texUV.Sample(samp, input.uv);
    float u = uv.x - 0.5;
    float v = uv.y - 0.5;
    // BT.601 limited range
    float r = y + 1.402 * v;
    float g = y - 0.344136 * u - 0.714136 * v;
    float b = y + 1.772 * u;
    return float4(r, g, b, 1.0);
}";

        private void CreateShaderPipeline()
        {
            if (_device == null) return;

            try
            {
                // 编译着色器（使用 DXC）
                ReadOnlyMemory<byte> vsBytecode = CompileFromMemory(DxcShaderStage.Vertex, ShaderSource, "VSMain");
                ReadOnlyMemory<byte> psBytecode = CompileFromMemory(DxcShaderStage.Pixel, ShaderSource, "PSMain");
                if (vsBytecode.IsEmpty || psBytecode.IsEmpty)
                    throw new InvalidOperationException("着色器编译失败");

                // 输入布局
                var inputElements = new[]
                {
                    new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
                };

                // 根签名：两个描述符表（SRV: Y+UV, Sampler）
                var srvRange = new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0);
                var samplerRange = new DescriptorRange(DescriptorRangeType.Sampler, 1, 0);
                var rootParams = new[]
                {
                    new RootParameter(new RootDescriptorTable(new[] { srvRange }), ShaderVisibility.Pixel),
                    new RootParameter(new RootDescriptorTable(new[] { samplerRange }), ShaderVisibility.Pixel)
                };

                var rootSigDesc = new RootSignatureDescription(
                    RootSignatureFlags.AllowInputAssemblerInputLayout,
                    rootParams,
                    null);

                var versionedDesc = new VersionedRootSignatureDescription(rootSigDesc);
                Blob blob;
                string error = D3D12.D3D12SerializeVersionedRootSignature(versionedDesc, out blob);
                if (!string.IsNullOrEmpty(error)) throw new Exception(error);
                _rootSignature = _device.CreateRootSignature(blob.AsBytes());
                blob.Dispose();

                // SRV 描述符堆（Shader Visible，2 个槽位）
                var srvHeapDesc = new DescriptorHeapDescription
                {
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    DescriptorCount = 2,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                    NodeMask = 0
                };
                _srvHeap = _device.CreateDescriptorHeap(srvHeapDesc);

                // Sampler 描述符堆
                var samplerHeapDesc = new DescriptorHeapDescription
                {
                    Type = DescriptorHeapType.Sampler,
                    DescriptorCount = 1,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                    NodeMask = 0
                };
                _samplerHeap = _device.CreateDescriptorHeap(samplerHeapDesc);

                var samplerDesc = new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    MipLODBias = 0,
                    MaxAnisotropy = 1,
                    ComparisonFunction = ComparisonFunction.Never,
                    BorderColor = new Color4(0, 0, 0, 0),
                    MinLOD = 0,
                    MaxLOD = float.MaxValue
                };
                _device.CreateSampler(ref samplerDesc, _samplerHeap.GetCPUDescriptorHandleForHeapStart());

                // 全屏四边形顶点缓冲区（NDC -1..1，UV 0..1）
                float[] vertices = {
                    -1, -1,   0, 0,  // 左下
                    -1,  1,   0, 1,  // 左上
                     1, -1,   1, 0,  // 右下
                     1,  1,   1, 1   // 右上
                };
                int vertexSize = 16; // 2 floats position + 2 floats texcoord
                int vertexCount = 4;
                ulong bufferSize = (ulong)(vertexSize * vertexCount);

                _vertexBuffer = CreateDefaultBuffer(vertices, vertexSize, bufferSize);
                _vertexBufferView = new VertexBufferView
                {
                    BufferLocation = _vertexBuffer.GPUVirtualAddress,
                    StrideInBytes = (uint)vertexSize,
                    SizeInBytes = (uint)bufferSize
                };

                // 管线状态
                var psoDesc = new GraphicsPipelineStateDescription
                {
                    RootSignature = _rootSignature,
                    VertexShader = vsBytecode,
                    PixelShader = psBytecode,
                    InputLayout = new InputLayoutDescription(inputElements),
                    SampleMask = uint.MaxValue,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    RasterizerState = RasterizerDescription.CullNone,
                    BlendState = BlendDescription.Opaque,
                    DepthStencilState = new DepthStencilDescription
                    {
                        DepthEnable = false,
                        DepthWriteMask = DepthWriteMask.Zero
                    },
                    RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
                    DepthStencilFormat = Format.Unknown,
                    SampleDescription = SampleDescription.Default
                };
                _pipelineState = _device.CreateGraphicsPipelineState(psoDesc);

                _pipelineReady = true;
                DebugLogger.WriteLine("D3D12 渲染管线及资源创建成功");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"管线创建失败: {ex.Message}");
                CleanupPipeline();
            }
        }

        // 辅助：创建默认堆资源并上传数据
        private ID3D12Resource CreateDefaultBuffer<T>(T[] data, int elementSize, ulong totalBytes) where T : unmanaged
        {
            var defaultDesc = ResourceDescription.Buffer(totalBytes, ResourceFlags.None);
            var defaultHeap = new HeapProperties(HeapType.Default);
            var defaultRes = _device!.CreateCommittedResource(defaultHeap, HeapFlags.None, defaultDesc,
                ResourceStates.CopyDest, null);

            var uploadDesc = ResourceDescription.Buffer(totalBytes, ResourceFlags.None);
            var uploadHeap = new HeapProperties(HeapType.Upload);
            var uploadRes = _device.CreateCommittedResource(uploadHeap, HeapFlags.None, uploadDesc,
                ResourceStates.GenericRead, null);

            unsafe
            {
                void* mapped;
                uploadRes.Map(0, &mapped).CheckError();
                fixed (void* src = data)
                {
                    Buffer.MemoryCopy(src, mapped, (long)totalBytes, (long)totalBytes);
                }
                uploadRes.Unmap(0, null);
            }

            // 使用临时命令列表拷贝
            var cmdAlloc = _device.CreateCommandAllocator(CommandListType.Direct);
            var cmdList = _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, cmdAlloc, null);
            cmdList.CopyResource(defaultRes, uploadRes);
            cmdList.ResourceBarrierTransition(defaultRes, ResourceStates.CopyDest, ResourceStates.GenericRead);
            cmdList.Close();
            _commandQueue!.ExecuteCommandList(cmdList);
            // 等待拷贝完成
            var tempFence = _device.CreateFence(0, FenceFlags.None);
            _commandQueue.Signal(tempFence, 1);
            while (tempFence.CompletedValue < 1) { }
            cmdAlloc.Dispose();
            cmdList.Dispose();
            uploadRes.Dispose();
            tempFence.Dispose();
            return defaultRes;
        }

        private static ReadOnlyMemory<byte> CompileFromMemory(DxcShaderStage stage, string source, string entryPoint)
        {
            using IDxcResult results = DxcCompiler.Compile(stage, source, entryPoint);
            if (results.GetStatus().Failure)
            {
                throw new Exception(results.GetErrors());
            }
            return results.GetObjectBytecodeMemory();
        }

        // 确保命令列表可用
        private void EnsureCommandList()
        {
            if (_commandAllocator == null)
                _commandAllocator = _device!.CreateCommandAllocator(CommandListType.Direct);
            if (_commandList == null)
            {
                _commandList = _device!.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _commandAllocator, null);
                _commandList.Close();
            }
        }

        // 等待上一帧完成（新 fence 模型）
        private void WaitForPreviousFrame()
        {
            if (_fence!.CompletedValue < _fenceValue)
            {
                _fence.SetEventOnCompletion(_fenceValue, _frameEvent!.SafeWaitHandle.DangerousGetHandle());
                _frameEvent.WaitOne();
            }
            _currentBackBufferIndex = _swapChain!.CurrentBackBufferIndex;
        }

        // 提交当前命令并移动 fence
        private void ExecuteAndSignal()
        {
            _commandList!.Close();
            _commandQueue!.ExecuteCommandList(_commandList);
            _fenceValue++;
            _commandQueue.Signal(_fence!, _fenceValue);
        }

        // 等待 GPU 空闲（用于尺寸变更等）
        private void WaitForGpu()
        {
            if (_fence == null || _commandQueue == null) return;
            ulong waitFence = _fenceValue + 1;
            _commandQueue.Signal(_fence, waitFence);
            if (_fence.CompletedValue < waitFence)
            {
                _fence.SetEventOnCompletion(waitFence, _frameEvent!.SafeWaitHandle.DangerousGetHandle());
                _frameEvent.WaitOne();
            }
            _fenceValue = waitFence; // 保持一致性
        }

        // ------------------------------------------------------------------
        // 公共接口：传入 YUV420P 平面（解码器提供）
        // ------------------------------------------------------------------
        public void UpdateFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height,
            int yStride = 0, int uStride = 0, int vStride = 0)
        {
            if (_stopRequested || !_swapChainReady) return;
            if (yPlane == null || uPlane == null || vPlane == null || width <= 0 || height <= 0) return;
            if (_device == null || _swapChain == null || _commandQueue == null) return;

            // 转换为 NV12 供 GPU 直接使用
            byte[] nv12 = ConvertYUV420PToNV12(yPlane, uPlane, vPlane, width, height, yStride, uStride, vStride);
            _nv12Queue.Enqueue((nv12, width, height));

            if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderSoft);
        }

        // 硬解接口（外部纹理句柄）- 注意：D3D11 纹理不能直接在 D3D12 中使用
        // 由于 FFmpeg 的 D3D11VA 解码器输出的是 D3D11 纹理，D3D12 硬解路径需要使用 DXGI 共享句柄
        // 当前实现使用 CPU 回退路径：读取像素数据然后重新上传
        public void RenderHardwareTexture(IntPtr texturePtr, int width, int height,
            uint subresourceIndex = 0, bool isTextureArray = false)
        {
            DebugLogger.WriteLine("D3D12 硬解路径暂不支持 D3D11 纹理，回退到 CPU 路径");
            // 对于 D3D11 纹理，需要通过 DXGI 共享句柄才能在 D3D12 中使用
            // 当前方案：记录错误并返回，由调用方处理
            // 实际生产环境需要实现 DXGI 共享资源或保持使用 D3D11 渲染器
        }
        private static uint Align256(uint value) => (value + 255) & ~255u;
        // 软解渲染（从队列取 NV12 数据，直接上传到 GPU）
        private void RenderSoft()
        {
            if (_stopRequested) { Interlocked.Exchange(ref _renderQueued, 0); return; }
            if (!_swapChainReady || !_pipelineReady || _commandQueue == null)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }

            byte[]? latest = null;
            int w = 0, h = 0;
            while (_nv12Queue.TryDequeue(out var f)) { latest = f.data; w = f.width; h = f.height; }
            if (latest == null) { Interlocked.Exchange(ref _renderQueued, 0); return; }

            try
            {
                // 确保 NV12 纹理尺寸匹配
                if (_nv12Texture == null || _nv12Texture.Description.Width != (ulong)w || _nv12Texture.Description.Height != (ulong)h)
                {
                    WaitForGpu();
                    _nv12Texture?.Dispose();
                    _nv12UploadBuffer?.Dispose();
                    var texDesc = ResourceDescription.Texture2D(Format.NV12, (uint)w, (uint)h, 1, 1);
                    _nv12Texture = _device!.CreateCommittedResource(
                        new HeapProperties(HeapType.Default), HeapFlags.None,
                        texDesc, ResourceStates.CopyDest);
                    _nv12TextureInCopyDest = true; // 新创建时状态为 CopyDest
                }

                // 上传 NV12 数据（使用 Upload Buffer + CopyTextureRegion）
                int ySize = w * h;
                int uvSize = (w / 2) * (h / 2);
                int alignedYRowPitch = (int)Align256((uint)w);
                int alignedUVRowPitch = (int)Align256((uint)(w)); // NV12 UV 行字节数 = w
                ulong uploadSize = (ulong)(alignedYRowPitch * h + alignedUVRowPitch * (h / 2));

                if (_nv12UploadBuffer == null || _nv12UploadBuffer.Description.Width < uploadSize)
                {
                    _nv12UploadBuffer?.Dispose();
                    _nv12UploadBuffer = _device.CreateCommittedResource(
                        new HeapProperties(HeapType.Upload), HeapFlags.None,
                        ResourceDescription.Buffer(uploadSize),
                        ResourceStates.GenericRead);
                }

                // 按行对齐填充上传缓冲区
                byte[] alignedData = new byte[uploadSize];
                // Y 平面
                for (int row = 0; row < h; row++)
                {
                    Buffer.BlockCopy(latest, row * w, alignedData, row * alignedYRowPitch, w);
                }
                int uvBase = alignedYRowPitch * h;
                // UV 平面（NV12 交错，在 latest 中 UV 起始于 ySize）
                for (int row = 0; row < h / 2; row++)
                {
                    Buffer.BlockCopy(latest, ySize + row * w, alignedData, uvBase + row * alignedUVRowPitch, w);
                }

                unsafe
                {
                    void* pDst;
                    _nv12UploadBuffer.Map(0, &pDst).CheckError();
                    Marshal.Copy(alignedData, 0, (IntPtr)pDst, alignedData.Length);
                    _nv12UploadBuffer.Unmap(0);
                }

                EnsureCommandList();
                WaitForPreviousFrame(); // 先等待上一帧完成
                _commandAllocator!.Reset(); // 然后再重置分配器
                _commandList!.Reset(_commandAllocator, _pipelineState);
                _currentBackBufferIndex = _swapChain!.CurrentBackBufferIndex;
                ID3D12Resource backBuffer = _backBuffers[_currentBackBufferIndex]!;

                _commandList.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
                
                // 根据当前状态进行正确的转换
                if (!_nv12TextureInCopyDest)
                {
                    _commandList.ResourceBarrierTransition(_nv12Texture, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
                    _nv12TextureInCopyDest = true;
                }

                // 拷贝 Y 平面
                var yFootprint = new PlacedSubresourceFootPrint
                {
                    Offset = 0,
                    Footprint = new SubresourceFootPrint
                    {
                        Format = Format.R8_UNorm,
                        Width = (uint)w,
                        Height = (uint)h,
                        Depth = 1,
                        RowPitch = (uint)alignedYRowPitch
                    }
                };
                var yDst = new TextureCopyLocation(_nv12Texture, 0); // Subresource 0 = Y
                var ySrc = new TextureCopyLocation(_nv12UploadBuffer, yFootprint);
                _commandList.CopyTextureRegion(yDst, 0, 0, 0, ySrc, null);

                // 拷贝 UV 平面
                var uvFootprint = new PlacedSubresourceFootPrint
                {
                    Offset = (ulong)(alignedYRowPitch * h),
                    Footprint = new SubresourceFootPrint
                    {
                        Format = Format.R8G8_UNorm,
                        Width = (uint)(w / 2),
                        Height = (uint)(h / 2),
                        Depth = 1,
                        RowPitch = (uint)alignedUVRowPitch
                    }
                };
                var uvDst = new TextureCopyLocation(_nv12Texture, 1); // Subresource 1 = UV
                var uvSrc = new TextureCopyLocation(_nv12UploadBuffer, uvFootprint);
                _commandList.CopyTextureRegion(uvDst, 0, 0, 0, uvSrc, null);

                _commandList.ResourceBarrierTransition(_nv12Texture, ResourceStates.CopyDest,
                    ResourceStates.PixelShaderResource);
                _nv12TextureInCopyDest = false; // 更新状态

                // 创建 SRV（仅在纹理尺寸变化后需要，这里每次都创建以确保安全，可优化为按需）
                CreateNv12ShaderResourceViews(_nv12Texture, w, h);

                // 视口与绘制
                RawRect dest = CalculateDestRect(w, h);
                _commandList.RSSetViewport(new Viewport(dest.Left, dest.Top, dest.Right - dest.Left, dest.Bottom - dest.Top));
                _commandList.RSSetScissorRect(dest);
                _commandList.OMSetRenderTargets(GetCurrentRtvHandle(), null);
                _commandList.ClearRenderTargetView(GetCurrentRtvHandle(), new Color4(0, 0, 0, 1));

                _commandList.SetDescriptorHeaps(new ID3D12DescriptorHeap[] { _srvHeap!, _samplerHeap! });
                _commandList.SetGraphicsRootSignature(_rootSignature);
                _commandList.SetGraphicsRootDescriptorTable(0, _srvHeap!.GetGPUDescriptorHandleForHeapStart());
                _commandList.SetGraphicsRootDescriptorTable(1, _samplerHeap!.GetGPUDescriptorHandleForHeapStart());
                _commandList.IASetVertexBuffers(0, _vertexBufferView);
                _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                _commandList.DrawInstanced(4, 1, 0, 0);

                _commandList.ResourceBarrierTransition(backBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

                ExecuteAndSignal();
                _swapChain.Present(1, PresentFlags.None);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"软解渲染异常: {ex.Message}");
            }
            finally
            {
                if (!_nv12Queue.IsEmpty)
                    _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderSoft);
                else
                    Interlocked.Exchange(ref _renderQueued, 0);
            }
        }

        // 为 NV12 纹理创建 Y（R8）和 UV（R8G8）的 SRV，填充到 SRV 堆
        private void CreateNv12ShaderResourceViews(ID3D12Resource nv12Texture, int width, int height)
        {
            int srvSize = (int)_device!.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            CpuDescriptorHandle handle = _srvHeap!.GetCPUDescriptorHandleForHeapStart();

            // Y 平面 SRV
            var ySrv = new ShaderResourceViewDescription
            {
                Format = Format.R8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0 }
            };
            _device.CreateShaderResourceView(nv12Texture, ySrv, handle);
            DebugLogger.WriteLine($"设备移除原因3.1: 0x{_device.DeviceRemovedReason.Code:X8},设备状态Failure:{_device.DeviceRemovedReason.Failure}");

            handle += srvSize;

            // UV 平面 SRV
            var uvSrv = new ShaderResourceViewDescription
            {
                Format = Format.R8G8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 1 }
            };
            _device.CreateShaderResourceView(nv12Texture, uvSrv, handle);
            DebugLogger.WriteLine($"设备移除原因3.2: 0x{_device.DeviceRemovedReason.Code:X8},设备状态Failure:{_device.DeviceRemovedReason.Failure}");


        }

        private CpuDescriptorHandle GetCurrentRtvHandle()
        {
            CpuDescriptorHandle handle = _rtvHeap!.GetCPUDescriptorHandleForHeapStart();
            handle += (int)_currentBackBufferIndex * _rtvDescriptorSize;
            return handle;
        }

        public void ClearScreen()
        {
            if (_device?.DeviceRemovedReason.Failure ?? true) { _initialized = false; return; }
            if (!_initialized || _swapChain == null || _commandQueue == null) return;

            try
            {
                EnsureCommandList();
                _commandAllocator!.Reset();
                _commandList!.Reset(_commandAllocator, _pipelineState);
                WaitForPreviousFrame();
                _currentBackBufferIndex = _swapChain.CurrentBackBufferIndex;
                ID3D12Resource backBuffer = _backBuffers[_currentBackBufferIndex]!;

                _commandList.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
                _commandList.ClearRenderTargetView(GetCurrentRtvHandle(), new Color4(0, 0, 0, 1));
                _commandList.ResourceBarrierTransition(backBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

                ExecuteAndSignal();
                _swapChain.Present(1, PresentFlags.None);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"ClearScreen 异常: {ex.Message}");
            }
        }

        // YUV420P → NV12 转换（高性能，重用缓冲区）
        private byte[] ConvertYUV420PToNV12(byte[] y, byte[] u, byte[] v, int w, int h,
            int yStride, int uStride, int vStride)
        {
            int ySize = w * h;
            int uvW = w / 2, uvH = h / 2;
            int needed = ySize + uvW * uvH * 2;
            if (_nv12Buffer == null || _nv12Buffer.Length < needed)
                _nv12Buffer = new byte[needed];
            byte[] nv12 = _nv12Buffer;

            // 拷贝 Y 平面（处理 stride）
            if (yStride == 0 || yStride == w)
                Array.Copy(y, 0, nv12, 0, ySize);
            else
                for (int row = 0; row < h; row++)
                    Array.Copy(y, row * yStride, nv12, row * w, w);

            int uvOff = ySize;
            if (uStride == 0) uStride = uvW;
            if (vStride == 0) vStride = uvW;

            // 交错 UV
            for (int row = 0; row < uvH; row++)
            {
                int uvRowBase = uvOff + row * uvW * 2;
                for (int col = 0; col < uvW; col++)
                {
                    nv12[uvRowBase + col * 2] = u[row * uStride + col];
                    nv12[uvRowBase + col * 2 + 1] = v[row * vStride + col];
                }
            }
            return nv12;
        }

        public ID3D12Device? GetDevice() => _device;

        private void CleanupPipeline()
        {
            _pipelineReady = false;
            SafeDispose(ref _rootSignature);
            SafeDispose(ref _pipelineState);
            SafeDispose(ref _vertexBuffer);
            SafeDispose(ref _samplerHeap);
            SafeDispose(ref _srvHeap);
        }

        private void CleanupYUVTextures()
        {
            SafeDispose(ref _nv12Texture);
            SafeDispose(ref _nv12UploadBuffer);
            SafeDispose(ref _hardwareNv12Internal);
        }

        private void Cleanup()
        {
            _stopRequested = true;
            _initialized = false;

            if (_fence != null && _commandQueue != null)
            {
                try
                {
                    WaitForGpu();
                }
                catch { }
            }

            CleanupPipeline();
            CleanupYUVTextures();
            SafeDispose(ref _commandList);
            SafeDispose(ref _commandAllocator);
            SafeDispose(ref _fence);
            SafeDispose(ref _frameEvent);
            SafeDispose(ref _rtvHeap);

            for (int i = 0; i < _backBuffers.Length; i++)
                SafeDispose(ref _backBuffers[i]);

            SafeDispose(ref _swapChain);
            SafeDispose(ref _dxgiFactory);
            SafeDispose(ref _commandQueue);
            SafeDispose(ref _device);
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
                if (disposing) Cleanup();
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // 简单 DTO 示例（实际项目中已存在）
        public class FrameData
        {
            public byte[]? YPlane, UPlane, VPlane;
            public int YStride, UStride, VStride;
            public int Width, Height;
        }
    }

    // 辅助类型（需根据项目实际情况调整）
    public enum VideoScaleMode { Fit, Stretch, Zoom }

    // 模拟 Win32 交互（实际应使用 System.Windows 现有方法）
    internal static class Win32Funcs
    {
        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);
    }
    internal struct Win32Rect { public int Left, Top, Right, Bottom; }
    internal struct Win32Point { public int X, Y; }
}