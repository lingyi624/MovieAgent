using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice;
using Vortice.Direct3D9;
using Vortice.Mathematics;

namespace MovieAgent.Controls.Window.D3D9Window
{
    /// <summary>视频缩放模式</summary>
 
    /// <summary>
    /// 基于 D3D9 + D3DImage 的高性能视频渲染器。
    /// 支持硬件解码表面直接渲染，以及软件 YUV420P 帧输入。
    /// </summary>
    public class D3DImageVideoRenderer : FrameworkElement
    {
        // ============== WPF 呈现 ==============
        private readonly D3DImage _d3dImage;
        private readonly Image _image;

        // ============== D3D9 核心 ==============
        private IDirect3D9Ex? _d3d9;
        private IDirect3DDevice9Ex? _d3dDevice;
        private IDirect3DSwapChain9? _swapChain;
        private IDirect3DSurface9? _backBuffer;

        // ============== 软件缩放/YUV 资源 ==============
        private IDirect3DSurface9? _systemMemorySurface;
        private IDirect3DTexture9? _renderTargetTexture;
        private IDirect3DSurface9? _renderTargetSurface;

        // ============== 状态 ==============
        private volatile bool _d3dInitialized;
        private volatile bool _stopRequested;
        private readonly object _renderLock = new();      // D3D 绘制锁
        private readonly object _queueLock = new();       // YUV 帧缓冲锁

        private int _videoWidth, _videoHeight;
        private int _renderWidth = 1, _renderHeight = 1;

        private VideoScaleMode _scaleMode = VideoScaleMode.Fit;

        // ============== 帧队列（仅保留最新一帧） ==============
        private byte[]? _latestFrameData;
        private int _latestFrameStride, _latestFrameWidth, _latestFrameHeight;
        private int _renderTaskQueued;                    // 0=空闲，1=已调度
        private static readonly ArrayPool<byte> FramePool = ArrayPool<byte>.Shared;

        public readonly Dispatcher UIDispatcher;

        // ============== 备用隐藏窗口 ==============
        private HwndSource? _fallbackHwndSource;

        // ============== 构造函数 ==============
        public D3DImageVideoRenderer()
        {
            UIDispatcher = Dispatcher.CurrentDispatcher;

            _d3dImage = new D3DImage();
            _image = new Image { Source = _d3dImage, Stretch = Stretch.None };

            AddVisualChild(_image);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ============== 布局重写（显式使用 System.Windows.Size） ==============
        protected override Visual GetVisualChild(int index) => _image;
        protected override int VisualChildrenCount => 1;

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            _image.Measure(availableSize);
            return _image.DesiredSize;
        }

        protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
        {
            _image.Arrange(new System.Windows.Rect(finalSize));
            _renderWidth = Math.Max(1, (int)finalSize.Width);
            _renderHeight = Math.Max(1, (int)finalSize.Height);
            return finalSize;
        }

        // ============== 生命周期 ==============
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_d3dInitialized) return;
            InitializeD3D();
            _d3dInitialized = true;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CleanupD3D();
        }

        // ============== D3D9 初始化 ==============
        private void InitializeD3D()
        {
            CreateD3D9Device();
            DebugLogger.WriteLine("D3D9 D3DImage 初始化成功");
        }

        private void CreateD3D9Device()
        {
            try
            {
                var hwnd = GetWindowHandle();

                _d3d9 = D3D9.Direct3DCreate9Ex();
                if (_d3d9 != null)
                {
                    var pp = new PresentParameters
                    {
                        Windowed = true,
                        SwapEffect = SwapEffect.Discard,
                        BackBufferFormat = Format.X8R8G8B8,
                        BackBufferCount = 1,
                        DeviceWindowHandle = hwnd,
                        PresentationInterval = PresentInterval.Immediate
                    };

                    try
                    {
                        _d3dDevice = _d3d9.CreateDeviceEx(0, DeviceType.Hardware, hwnd,
                            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded, pp);
                    }
                    catch
                    {
                        DebugLogger.WriteLine("硬件设备创建失败，尝试软件设备...");
                        _d3dDevice = _d3d9.CreateDeviceEx(0, DeviceType.Software, hwnd,
                            CreateFlags.SoftwareVertexProcessing | CreateFlags.Multithreaded, pp);
                    }

                    _swapChain = _d3dDevice.GetSwapChain(0);
                    _backBuffer = _swapChain.GetBackBuffer(0);
                    DebugLogger.WriteLine("D3D9Ex 设备创建成功");
                    return;
                }

                // 回退到标准 D3D9
                DebugLogger.WriteLine("Direct3DCreate9Ex 失败，尝试 Direct3DCreate9");
                using var d3d9 = D3D9.Direct3DCreate9();
                if (d3d9 == null) throw new Exception("Direct3D9 创建失败");

                var ppStd = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    BackBufferFormat = Format.X8R8G8B8,
                    BackBufferCount = 1,
                    DeviceWindowHandle = hwnd
                };

                IDirect3DDevice9 device;
                try
                {
                    device = d3d9.CreateDevice(0, DeviceType.Hardware, hwnd,
                        CreateFlags.HardwareVertexProcessing, ppStd);
                }
                catch
                {
                    device = d3d9.CreateDevice(0, DeviceType.Software, hwnd,
                        CreateFlags.SoftwareVertexProcessing, ppStd);
                }

                _d3dDevice = device.QueryInterface<IDirect3DDevice9Ex>();
                _swapChain = _d3dDevice.GetSwapChain(0);
                _backBuffer = _swapChain.GetBackBuffer(0);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D9 设备创建失败: {ex.Message}");
                throw;
            }
        }

        private IntPtr GetWindowHandle()
        {
            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (hwndSource != null)
                return hwndSource.Handle;

            // 备用隐藏窗口
            _fallbackHwndSource = new HwndSource(
                new HwndSourceParameters("D3DImageFallback")
                {
                    WindowStyle = 0,
                    ParentWindow = IntPtr.Zero,
                    PositionX = -10000,
                    PositionY = -10000,
                    Width = 1,
                    Height = 1
                });
            return _fallbackHwndSource.Handle;
        }

         
         // ============== 缩放模式 ==============
        public void SetScaleMode(VideoScaleMode mode)
        {
            _scaleMode = mode;
        }

        // ============== 目标矩形计算 ==============
        private void CalculateDestRect(int videoW, int videoH,
            out Vortice.Direct3D9.Rect srcRect, out Vortice.Direct3D9.Rect dstRect)
        {
            srcRect = new Vortice.Direct3D9.Rect(0, 0, videoW, videoH);
            int dstW = _renderWidth, dstH = _renderHeight;

            if (_scaleMode == VideoScaleMode.Stretch)
            {
                dstRect = new Vortice.Direct3D9.Rect(0, 0, dstW, dstH);
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
            dstRect = new Vortice.Direct3D9.Rect(offX, offY, offX + drawW, offY + drawH);
        }

        // ============== 硬件解码表面渲染 ==============
        public void RenderD3D9VATexture(IntPtr surfacePtr, int width, int height)
        {
            if (_stopRequested || !_d3dInitialized) return;

            if (!UIDispatcher.CheckAccess())
            {
                UIDispatcher.BeginInvoke(() => RenderD3D9VATexture(surfacePtr, width, height));
                return;
            }
            if (surfacePtr == IntPtr.Zero) return;

            lock (_renderLock)
            {
                

                IDirect3DSurface9? decoderSurface = null;
                try
                {
                    decoderSurface = new IDirect3DSurface9(surfacePtr);
                    decoderSurface.AddRef();

                    _videoWidth = width;
                    _videoHeight = height;

                    CalculateDestRect(width, height, out var srcRect, out var dstRect);
                    _d3dDevice!.Clear(ClearFlags.Target, new Vortice.Mathematics.Color(0, 0, 0), 1.0f, 0);
                    _d3dDevice.StretchRect(decoderSurface, srcRect, _backBuffer, dstRect, TextureFilter.Linear);
                    UpdateD3DImage();
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"D3D9 VA渲染异常: {ex.Message}");
                }
                finally
                {
                    decoderSurface?.Release();
                }
            }
        }

        // ============== 软件 YUV420P 帧输入 ==============
        public void UpdateFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane,
            int width, int height,
            int yStride = 0, int uStride = 0, int vStride = 0)
        {
            if (_stopRequested) return;
            if (yPlane == null || uPlane == null || vPlane == null || width <= 0 || height <= 0) return;

            int bgrSize = width * height * 4;
            byte[] bgr = FramePool.Rent(bgrSize);

            // 转换并更新最新帧
            lock (_queueLock)
            {
                ConvertYUV420PToBGR(yPlane, uPlane, vPlane, width, height,
                    yStride, uStride, vStride, bgr);

                if (_latestFrameData != null)
                    FramePool.Return(_latestFrameData);

                _latestFrameData = bgr;
                _latestFrameStride = width * 4;
                _latestFrameWidth = width;
                _latestFrameHeight = height;
            }

            // 调度渲染
            if (Interlocked.CompareExchange(ref _renderTaskQueued, 1, 0) == 0)
                UIDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
        }

        // ============== 渲染最新 YUV 帧 ==============
        private void RenderYUVFrame()
        {
            if (_stopRequested || !_d3dInitialized)
            {
                Interlocked.Exchange(ref _renderTaskQueued, 0);
                return;
            }

            byte[]? frameData;
            int stride, w, h;

            lock (_queueLock)
            {
                if (_latestFrameData == null)
                {
                    Interlocked.Exchange(ref _renderTaskQueued, 0);
                    return;
                }
                frameData = _latestFrameData;
                _latestFrameData = null;
                stride = _latestFrameStride;
                w = _latestFrameWidth;
                h = _latestFrameHeight;
            }

            bool shouldReschedule = false;
            try
            {
                lock (_renderLock)
                {
 
                    EnsureBGRResources(w, h);
                    if (_systemMemorySurface == null || _renderTargetSurface == null) return;

                    // 拷贝到系统内存表面
                    var rect = _systemMemorySurface.LockRect(LockFlags.None);
                    try
                    {
                        unsafe
                        {
                            byte* dst = (byte*)rect.DataPointer;
                            int dstPitch = rect.Pitch;
                            int bytesPerRow = w * 4;

                            fixed (byte* src = frameData)
                            {
                                for (int y = 0; y < h; y++)
                                {
                                    Buffer.MemoryCopy(src + y * stride,
                                                      dst + y * dstPitch,
                                                      bytesPerRow, bytesPerRow);
                                }
                            }
                        }
                    }
                    finally
                    {
                        _systemMemorySurface.UnlockRect();
                    }

                    // 上传到渲染目标并拉伸
                    _d3dDevice!.UpdateSurface(_systemMemorySurface, new Vortice.Direct3D9.Rect(0, 0, w, h), _renderTargetSurface, new Int2(0, 0));
                    CalculateDestRect(w, h, out var srcRect, out var dstRect);
                    _d3dDevice.Clear(ClearFlags.Target, new Vortice.Mathematics.Color(0, 0, 0), 1.0f, 0);
                    _d3dDevice.StretchRect(_renderTargetSurface, srcRect, _backBuffer, dstRect, TextureFilter.Linear);
                    UpdateD3DImage();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D9 YUV渲染异常: {ex.Message}");
            }
            finally
            {
                // 归还帧缓冲
                if (frameData != null)
                    FramePool.Return(frameData);

                lock (_queueLock)
                {
                    shouldReschedule = _latestFrameData != null;
                }

                if (shouldReschedule && !_stopRequested)
                    UIDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
                else
                    Interlocked.Exchange(ref _renderTaskQueued, 0);
            }
        }

        // ============== 更新 D3DImage（不调用 Present） ==============
        private void UpdateD3DImage()
        {
            if (_backBuffer == null || _d3dDevice == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)UpdateD3DImage);
                return;
            }

            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _backBuffer.NativePointer);

                int dirtyW = _videoWidth > 0 ? _videoWidth : _renderWidth;
                int dirtyH = _videoHeight > 0 ? _videoHeight : _renderHeight;
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, dirtyW, dirtyH));
            }
            finally
            {
                _d3dImage.Unlock();
            }
        }

        // ============== BGR 离屏资源管理 ==============
        private void EnsureBGRResources(int width, int height)
        {
            if (_systemMemorySurface != null && _videoWidth == width && _videoHeight == height)
                return;

            CleanupBGRResources();
            _videoWidth = width;
            _videoHeight = height;
            if (_d3dDevice == null) return;

            _systemMemorySurface = _d3dDevice.CreateOffscreenPlainSurface(
                (uint)width, (uint)height, Format.X8R8G8B8, Pool.SystemMemory);

            _renderTargetTexture = _d3dDevice.CreateTexture(
                (uint)width, (uint)height, 1, Usage.RenderTarget, Format.X8R8G8B8, Pool.Default);

            _renderTargetSurface = _renderTargetTexture.GetSurfaceLevel(0);
        }

        // ============== YUV420P → BGR 快速转换 ==============
        private static unsafe void ConvertYUV420PToBGR(
            byte[] y, byte[] u, byte[] v,
            int width, int height,
            int yStride, int uStride, int vStride,
            byte[] output)
        {
            if (yStride == 0) yStride = width;
            if (uStride == 0) uStride = width / 2;
            if (vStride == 0) vStride = width / 2;

            fixed (byte* py = y, pu = u, pv = v, pbgr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = py + row * yStride;
                    byte* bgrRow = pbgr + row * width * 4;

                    for (int x = 0; x < width; x++)
                    {
                        byte yVal = yRow[x];
                        int uvIdx = (row / 2) * uStride + (x / 2);
                        byte uVal = pu[uvIdx];
                        byte vVal = pv[uvIdx];

                        int c = yVal - 16;
                        int d = uVal - 128;
                        int e = vVal - 128;

                        int r = (298 * c + 409 * e + 128) >> 8;
                        int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                        int b = (298 * c + 516 * d + 128) >> 8;

                        int idx = x * 4;
                        bgrRow[idx] = ClampByte(b);
                        bgrRow[idx + 1] = ClampByte(g);
                        bgrRow[idx + 2] = ClampByte(r);
                        bgrRow[idx + 3] = 255;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ClampByte(int value)
        {
            if ((uint)value > 255)
                return (byte)(value < 0 ? 0 : 255);
            return (byte)value;
        }

        // ============== 公共属性 ==============
        public IDirect3DDevice9Ex? Device => _d3dDevice;

        public void ClearScreen()
        {
            if (_stopRequested || !_d3dInitialized || _backBuffer == null || _d3dDevice == null) return;

            lock (_renderLock)
            {
                 _d3dDevice.Clear(ClearFlags.Target, new Vortice.Mathematics.Color(0, 0, 0), 1.0f, 0);
                UpdateD3DImage();
            }
        }

        // ============== 清理 ==============
        private void CleanupBGRResources()
        {
            _systemMemorySurface?.Dispose();
            _systemMemorySurface = null;
            _renderTargetSurface?.Dispose();
            _renderTargetSurface = null;
            _renderTargetTexture?.Dispose();
            _renderTargetTexture = null;
        }

        private void CleanupD3D()
        {
            _stopRequested = true;
            _d3dInitialized = false;

            // 归还帧池缓冲
            lock (_queueLock)
            {
                if (_latestFrameData != null)
                {
                    FramePool.Return(_latestFrameData);
                    _latestFrameData = null;
                }
            }

            // 解除 D3DImage 绑定
            if (Dispatcher.CheckAccess())
            {
                try
                {
                    _d3dImage.Lock();
                    _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                }
                finally { _d3dImage.Unlock(); }
            }

            CleanupBGRResources();

            _backBuffer?.Dispose();
            _backBuffer = null;
            _swapChain?.Dispose();
            _swapChain = null;
            _d3dDevice?.Dispose();
            _d3dDevice = null;
            _d3d9?.Dispose();
            _d3d9 = null;

            _fallbackHwndSource?.Dispose();
            _fallbackHwndSource = null;
        }
    }

    // 如果项目中已有 DebugLogger，可移除此内部类
    internal static class DebugLogger
    {
        public static void WriteLine(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[D3DImageVideo] {message}");
        }
    }
}