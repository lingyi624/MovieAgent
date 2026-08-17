using MovieAgent.FFmpegDecoder;
using SharpGen.Runtime;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D9;
using Vortice.Mathematics;

namespace MovieAgent.Controls.Window.D3D9Window
{
    public class D3D9VideoRenderer : HwndHost
    {
        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
            WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;

        private const uint WM_SIZE = 0x0005;
        private const int WM_MOUSEMOVE = 0x0200;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProcDelegate _wndProc = WndProc;
        private static readonly IntPtr _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        private static bool _wndClassRegistered;
        private const uint WS_EX_TRANSPARENT = 0x00000020;

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPStr)] public string lpszClassName; public IntPtr hIconSm;
        }

        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        private IDirect3D9Ex? _d3d9;
        private IDirect3DDevice9Ex? _d3dDevice;
        private IDirect3DSwapChain9? _swapChain;
        private IDirect3DSurface9? _backBuffer;

        private IDirect3DSurface9? _systemMemorySurface;
        private IDirect3DTexture9? _renderTargetTexture;
        private IDirect3DSurface9? _renderTargetSurface;
        private IDirect3DQuery9? _frameQuery; // GPU 事件查询，用于帧同步防止画面抖动

        private IntPtr _hwnd, _parentHwnd;
        private bool _d3dInitialized, _disposed;
        private readonly object _resizeLock = new();
        private int _swapChainWidth, _swapChainHeight;
        private int _videoWidth, _videoHeight;

        private readonly ConcurrentQueue<(byte[] data, int stride, int width, int height)> _yuvQueue = new();
        private readonly ConcurrentQueue<(IntPtr surfacePtr, int width, int height)> _hwFrameQueue = new();
        private const int MaxHardwareFrames = 3;
        private byte[]? _yuvBuffer;
        private int _renderQueued;
        private int _hwRenderQueued;
        private volatile bool _stopRequested;

        private VideoScaleMode _scaleMode = VideoScaleMode.Fit;

        public readonly Dispatcher _uiDispatcher;
        private static D3D9VideoRenderer? _currentRenderer;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
    int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOZORDER = 0x0004;

        public D3D9VideoRenderer()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _currentRenderer = this;
            Loaded += (_, _) => { };
            Unloaded += (_, _) => CleanupD3D();
        }
        protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            if (_hwnd != IntPtr.Zero)
            {
                SetWindowPos(_hwnd, IntPtr.Zero,
                    (int)rcBoundingBox.X, (int)rcBoundingBox.Y,
                    (int)rcBoundingBox.Width, (int)rcBoundingBox.Height,
                    SWP_NOZORDER);

                lock (_resizeLock)
                {
                    _swapChainWidth = (int)rcBoundingBox.Width;
                    _swapChainHeight = (int)rcBoundingBox.Height;
                }
            }
        }
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            if (!_wndClassRegistered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = _wndProcPtr,
                    lpszClassName = "D3D9VideoRenderer"
                };
                if (RegisterClassEx(ref wc) == 0)
                {
                    DebugLogger.WriteLine($"窗口类注册失败: {Marshal.GetLastWin32Error()}");
                    return new HandleRef(this, IntPtr.Zero);
                }
                _wndClassRegistered = true;
            }

            _parentHwnd = hwndParent.Handle;

            _swapChainWidth = GetSystemMetrics(0);
            _swapChainHeight = GetSystemMetrics(1);
       

            _hwnd = CreateWindowEx(0, "D3D9VideoRenderer", "",
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

        private void InitializeD3D()
        {
            CreateD3D9Device();
            _d3dInitialized = true;
            DebugLogger.WriteLine("D3D9 初始化成功");
        }

        private void CreateD3D9Device()
        {
            try
            {
                _d3d9 = D3D9.Direct3DCreate9Ex();
                if (_d3d9 != null)
                {
                    var exPresentParams = new PresentParameters
                    {
                        Windowed = true,
                        SwapEffect = SwapEffect.Discard,
                        BackBufferFormat = Format.X8R8G8B8,
                        BackBufferCount = 2,
                        DeviceWindowHandle = _hwnd,
                        PresentationInterval = PresentInterval.One
                    };

                    try
                    {
                        _d3dDevice = _d3d9.CreateDeviceEx(0, DeviceType.Hardware, _hwnd,
                            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                            exPresentParams);
                    }
                    catch
                    {
                        DebugLogger.WriteLine("硬件设备创建失败，尝试软件设备...");
                        _d3dDevice = _d3d9.CreateDeviceEx(0, DeviceType.Software, _hwnd,
                            CreateFlags.SoftwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                            exPresentParams);
                    }

                    _swapChain = _d3dDevice.GetSwapChain(0);
                    _backBuffer = _swapChain.GetBackBuffer(0);
                    _frameQuery = _d3dDevice.CreateQuery(QueryType.Event);
                    DebugLogger.WriteLine("D3D9Ex 设备创建成功");
                    return;
                }

                DebugLogger.WriteLine("Direct3DCreate9Ex 失败，尝试使用 Direct3DCreate9");
                var d3d9 = D3D9.Direct3DCreate9();
                if (d3d9 == null) throw new Exception("Direct3D9 创建失败");

                var presentParams = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    BackBufferFormat = Format.X8R8G8B8,
                    BackBufferCount = 2,
                    DeviceWindowHandle = _hwnd,
                    PresentationInterval = PresentInterval.One
                };

                IDirect3DDevice9 device;
                try
                {
                    device = d3d9.CreateDevice(0, DeviceType.Hardware, _hwnd,
                        CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded, presentParams);
                }
                catch
                {
                    DebugLogger.WriteLine("硬件设备创建失败，尝试软件设备...");
                    device = d3d9.CreateDevice(0, DeviceType.Software, _hwnd,
                        CreateFlags.SoftwareVertexProcessing | CreateFlags.Multithreaded, presentParams);
                }

                _d3dDevice = device.QueryInterface<IDirect3DDevice9Ex>();
                d3d9.Dispose();
                _swapChain = _d3dDevice.GetSwapChain(0);
                _backBuffer = _swapChain.GetBackBuffer(0);
                _frameQuery = _d3dDevice.CreateQuery(QueryType.Event);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D9 设备创建失败: {ex.Message}");
                throw;
            }
        }

        private bool CheckDeviceLost()
        {
            if (_d3dDevice == null) return true;
            
            try
            {
                _d3dDevice.TestCooperativeLevel();
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                DebugLogger.WriteLine($"D3D9 设备状态异常: {ex.Message}");
                ResetDevice();
                return true;
            }
            catch
            {
                return true;
            }
            return false;
        }

        private void ResetDevice()
        {
            try
            {
                CleanupBGRResources();
                
                var exPresentParams = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    BackBufferFormat = Format.X8R8G8B8,
                    BackBufferCount = 2,
                    DeviceWindowHandle = _hwnd,
                    PresentationInterval = PresentInterval.One
                };

                _d3dDevice?.Reset(ref exPresentParams);
                _swapChain = _d3dDevice?.GetSwapChain(0);
                _backBuffer = _swapChain?.GetBackBuffer(0);
                DebugLogger.WriteLine("D3D9 设备重置成功");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D9 设备重置失败: {ex.Message}");
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
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

        public void SetScaleMode(VideoScaleMode mode)
        {
            if (_scaleMode != mode) { _scaleMode = mode; }
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    HandleMouseMove(lParam);
                    handled = true;
                    break;
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        private void HandleMouseMove(IntPtr lParam)
        {
            int x = lParam.ToInt32() & 0xFFFF;
            int y = (lParam.ToInt32() >> 16) & 0xFFFF;

            var args = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.MouseMoveEvent,
                Source = this
            };
            InputManager.Current.ProcessInput(args);
        }

        private void OnWindowSizeChanged(int newWidth, int newHeight)
        {
            if (_d3dDevice == null || _swapChain == null || !_d3dInitialized) return;
            if (newWidth == _swapChainWidth && newHeight == _swapChainHeight) return;
            if (newWidth <= 0 || newHeight <= 0) return;

            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnWindowSizeChanged(newWidth, newHeight));
                return;
            }

            try
            {
                var presentParams = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    BackBufferFormat = Format.X8R8G8B8,
                    BackBufferCount = 2,
                    DeviceWindowHandle = _hwnd,
                    PresentationInterval = PresentInterval.One
                };
                _d3dDevice.Reset(ref presentParams);

                _swapChain = _d3dDevice.GetSwapChain(0);
                _backBuffer = _swapChain.GetBackBuffer(0);
                _swapChainWidth = newWidth;
                _swapChainHeight = newHeight;

                DebugLogger.WriteLine($"D3D9 交换链尺寸调整: {newWidth}x{newHeight}");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D9 交换链尺寸调整失败: {ex.Message}");
            }
        }

        private void CalculateDestRect(int videoW, int videoH, out Vortice.Direct3D9.Rect sourceRect, out Vortice.Direct3D9.Rect destRect)
        {
            int dstW = _swapChainWidth, dstH = _swapChainHeight;

            sourceRect = new Vortice.Direct3D9.Rect(0, 0, videoW, videoH);

            if (_scaleMode == VideoScaleMode.Stretch)
            {
                destRect = new Vortice.Direct3D9.Rect(0, 0, dstW, dstH);
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
            else
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
            destRect = new Vortice.Direct3D9.Rect(offX, offY, offX + drawW, offY + drawH);
        }

        public void Initialize()
        {
        }

        public void RenderHardwareFrame(IntPtr surfacePtr, int width, int height)
        {
            if (surfacePtr == IntPtr.Zero || width <= 0 || height <= 0) return;

            while (_hwFrameQueue.Count >= MaxHardwareFrames)
            {
                _hwFrameQueue.TryDequeue(out _);
            }

            _hwFrameQueue.Enqueue((surfacePtr, width, height));

            if (Interlocked.CompareExchange(ref _hwRenderQueued, 1, 0) == 0)
            {
                _uiDispatcher.BeginInvoke(DispatcherPriority.Render, RenderHardwareFrameInternal);
            }
        }

        private void RenderHardwareFrameInternal()
        {
            if (_stopRequested) { Interlocked.Exchange(ref _hwRenderQueued, 0); return; }
            if (!_d3dInitialized || _d3dDevice == null || _swapChain == null || _backBuffer == null)
            {
                if (!_hwFrameQueue.IsEmpty)
                {
                    _uiDispatcher.BeginInvoke(DispatcherPriority.Render, RenderHardwareFrameInternal);
                }
                else
                {
                    Interlocked.Exchange(ref _hwRenderQueued, 0);
                }
                return;
            }

            if (CheckDeviceLost())
            {
                if (!_hwFrameQueue.IsEmpty)
                {
                    _uiDispatcher.BeginInvoke(DispatcherPriority.Render, RenderHardwareFrameInternal);
                }
                else
                {
                    Interlocked.Exchange(ref _hwRenderQueued, 0);
                }
                return;
            }

            // 只渲染最新帧，丢弃中间帧，防止 GPU 积压多个帧导致画面抖动
            (IntPtr surfacePtr, int width, int height) latestFrame = default;
            bool hasFrame = false;
            while (_hwFrameQueue.TryDequeue(out var frame))
            {
                latestFrame = frame;
                hasFrame = true;
            }

            if (!hasFrame) { Interlocked.Exchange(ref _hwRenderQueued, 0); return; }

            IDirect3DSurface9? decoderSurface = null;
            try
            {
                decoderSurface = new IDirect3DSurface9(latestFrame.surfacePtr);
                decoderSurface.AddRef();

                _videoWidth = latestFrame.width;
                _videoHeight = latestFrame.height;

                CalculateDestRect(latestFrame.width, latestFrame.height, out var srcRect, out var dstRect);

                // 等待上一帧 GPU 渲染完成，防止多帧同时渲染
                WaitForGpuFrame();

                _d3dDevice.Clear(ClearFlags.Target, new Color(0, 0, 0), 1.0f, 0);

                _d3dDevice.StretchRect(decoderSurface, srcRect, _backBuffer, dstRect, TextureFilter.Linear);

                _swapChain.Present(Present.None);

                // 标记当前 Present 在 GPU 命令流中的位置，供下一帧等待
                IssueFrameQuery();
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D9 硬解渲染异常: {ex.Message}");
            }
            finally
            {
                decoderSurface?.Release();
            }

            if (!_hwFrameQueue.IsEmpty)
            {
                _uiDispatcher.BeginInvoke(DispatcherPriority.Render, RenderHardwareFrameInternal);
            }
            else
            {
                Interlocked.Exchange(ref _hwRenderQueued, 0);
            }
        }

        public void RenderSoftwareFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height)
        {
            UpdateFrame(yPlane, uPlane, vPlane, width, height);
        }

        public void UpdateFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height,
            int yStride = 0, int uStride = 0, int vStride = 0)
        {
            if (_stopRequested) return;
            if (yPlane == null || uPlane == null || vPlane == null || width <= 0 || height <= 0) return;

            byte[] bgr = ConvertYUV420PToBGR(yPlane, uPlane, vPlane, width, height, yStride, uStride, vStride);
            _yuvQueue.Enqueue((bgr, width * 4, width, height));

            if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
        }

        private void RenderYUVFrame()
        {
            if (_stopRequested) { Interlocked.Exchange(ref _renderQueued, 0); return; }
            if (!_d3dInitialized || _d3dDevice == null || _backBuffer == null)
                goto Reschedule;

            byte[]? latestData = null;
            int stride = 0, w = 0, h = 0;
            while (_yuvQueue.TryDequeue(out var frame)) { latestData = frame.data; stride = frame.stride; w = frame.width; h = frame.height; }
            if (latestData == null) { Interlocked.Exchange(ref _renderQueued, 0); return; }

            try
            {
                EnsureBGRResources(w, h);
                if (_systemMemorySurface == null || _renderTargetSurface == null) return;

                var rect = _systemMemorySurface.LockRect(LockFlags.None);
                try
                {
                    unsafe
                    {
                        byte* dst = (byte*)rect.DataPointer;
                        fixed (byte* src = latestData)
                        {
                            int dstPitch = rect.Pitch;
                            int bytesPerRow = w * 4;
                            for (int y = 0; y < h; y++)
                            {
                                Buffer.MemoryCopy(src + y * stride, dst + y * dstPitch, bytesPerRow, bytesPerRow);
                            }
                        }
                    }
                }
                finally { _systemMemorySurface.UnlockRect(); }

                CalculateDestRect(w, h, out var srcRect, out var dstRect);

                // 等待上一帧 GPU 渲染完成，防止多帧同时渲染
                WaitForGpuFrame();

                _d3dDevice.Clear(ClearFlags.Target, new Color(0, 0, 0), 1.0f, 0);

                _d3dDevice.UpdateSurface(_systemMemorySurface, srcRect, _renderTargetSurface, new Int2(0, 0));

                _d3dDevice.StretchRect(_renderTargetSurface, srcRect, _backBuffer, dstRect, TextureFilter.Linear);

                _swapChain?.Present(Present.None);

                // 标记当前 Present 在 GPU 命令流中的位置，供下一帧等待
                IssueFrameQuery();
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"D3D9 软解渲染异常: {ex.Message}");
            }
            finally
            {
                if (_stopRequested) { Interlocked.Exchange(ref _renderQueued, 0); }
                if (!_yuvQueue.IsEmpty)
                    _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
                else
                    Interlocked.Exchange(ref _renderQueued, 0);
            }
            return;

        Reschedule:
            if (_stopRequested) { Interlocked.Exchange(ref _renderQueued, 0); return; }
            if (!_yuvQueue.IsEmpty)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
            else
                Interlocked.Exchange(ref _renderQueued, 0);
        }

        private void EnsureBGRResources(int width, int height)
        {
            if (_systemMemorySurface != null && _renderTargetSurface != null && _videoWidth == width && _videoHeight == height) return;
            CleanupBGRResources();
            _videoWidth = width;
            _videoHeight = height;
            if (_d3dDevice == null) return;

            _systemMemorySurface = _d3dDevice.CreateOffscreenPlainSurface(
                (uint)width, (uint)height,
                Format.X8R8G8B8,
                Pool.SystemMemory);

            _renderTargetTexture = _d3dDevice.CreateTexture(
                (uint)width, (uint)height, 1,
                Usage.RenderTarget,
                Format.X8R8G8B8,
                Pool.Default);

            _renderTargetSurface = _renderTargetTexture.GetSurfaceLevel(0);
        }

        private unsafe byte[] ConvertYUV420PToBGR(byte[] y, byte[] u, byte[] v, int width, int height,
            int yStride, int uStride, int vStride)
        {
            int size = width * height * 4;
            if (_yuvBuffer == null || _yuvBuffer.Length < size) _yuvBuffer = new byte[size];

            if (yStride == 0) yStride = width;
            if (uStride == 0) uStride = width / 2;
            if (vStride == 0) vStride = width / 2;

            fixed (byte* py = y, pu = u, pv = v, pbgr = _yuvBuffer)
            {
                int uvWidth = width / 2;
                int uvHeight = height / 2;

                for (int yRow = 0; yRow < height; yRow++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int uvRow = yRow / 2;
                        int uvCol = x / 2;

                        byte yVal = py[yRow * yStride + x];
                        byte uVal = pu[uvRow * uStride + uvCol];
                        byte vVal = pv[uvRow * vStride + uvCol];

                        int c = yVal - 16;
                        int d = uVal - 128;
                        int e = vVal - 128;

                        int r = (298 * c + 409 * e + 128) >> 8;
                        int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                        int b = (298 * c + 516 * d + 128) >> 8;

                        int idx = (yRow * width + x) * 4;
                        pbgr[idx] = (byte)Math.Clamp(b, 0, 255);
                        pbgr[idx + 1] = (byte)Math.Clamp(g, 0, 255);
                        pbgr[idx + 2] = (byte)Math.Clamp(r, 0, 255);
                        pbgr[idx + 3] = 255;
                    }
                }
            }

            return _yuvBuffer;
        }

        public IDirect3DDevice9Ex? Device => _d3dDevice;

        public void ClearScreen()
        {
            if (_stopRequested || !_d3dInitialized || _backBuffer == null || _d3dDevice == null) return;
            _d3dDevice.Clear(ClearFlags.Target, new Color(0, 0, 0), 1.0f, 0);
            _swapChain?.Present(Present.None);
        }

        private void CleanupBGRResources()
        {
            _renderTargetSurface?.Dispose();
            _renderTargetSurface = null;
            _renderTargetTexture?.Dispose();
            _renderTargetTexture = null;
            _systemMemorySurface?.Dispose();
            _systemMemorySurface = null;
        }

        private void WaitForGpuFrame()
        {
            if (_frameQuery == null) return;
            try
            {
                // 等待 GPU 完成上一帧的渲染（flush=true 会阻塞直到 GPU 完成）
                _frameQuery.GetData(out bool _, true);
            }
            catch { /* 查询可能因设备丢失而失败，忽略 */ }
        }

        private void IssueFrameQuery()
        {
            if (_frameQuery == null) return;
            try
            {
                _frameQuery.Issue(Issue.End);
            }
            catch { /* 忽略 */ }
        }

        private void CleanupD3D()
        {
            _stopRequested = true;
            _d3dInitialized = false;

            CleanupBGRResources();

            _frameQuery?.Dispose();
            _frameQuery = null;
            _backBuffer?.Dispose();
            _swapChain?.Dispose();
            _d3dDevice?.Dispose();
            _d3d9?.Dispose();
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

    public enum VideoScaleMode
    {
        Fit,
        Stretch,
        Zoom
    }
}