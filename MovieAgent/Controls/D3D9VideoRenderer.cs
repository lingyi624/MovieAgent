using MovieAgent.FFmpegDecoder;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D9;
using Vortice.Mathematics;

namespace MovieAgent.Controls
{
 

    public class D3D9VideoRenderer : Image
    {
        private IDirect3D9Ex _d3d9Ex;
        private IDirect3DDevice9Ex _deviceEx;
        private D3DImage _d3dImage;

        // 软解渲染目标
        private IDirect3DTexture9 _renderTargetTexture;
        private IDirect3DSurface9 _renderTargetSurface;

        // 软解着色器
        private IDirect3DVertexShader9 _vertexShader;
        private IDirect3DPixelShader9 _pixelShader;
        private IDirect3DVertexBuffer9 _vertexBuffer;
        private IDirect3DTexture9 _yTexture, _uvTexture;
        private bool _shaderReady;

        private int _videoWidth, _videoHeight;
        private VideoScaleMode _scaleMode = VideoScaleMode.Fit;
        private readonly Dispatcher _uiDispatcher;

        private readonly ConcurrentQueue<(byte[] data, int stride, int width, int height)> _nv12Queue = new();
        private byte[] _nv12Buffer;
        private int _renderQueued;

        private volatile bool _stopRequested;
        private volatile bool _d3dInitialized;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        public D3D9VideoRenderer()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _d3dImage = new D3DImage();
            Source = _d3dImage;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => InitializeD3D9();
        private void OnUnloaded(object sender, RoutedEventArgs e) => CleanupD3D9();

        // ==================== 初始化 ====================
        private void InitializeD3D9()
        {
            if (_d3dInitialized) return;

            _d3d9Ex = D3D9.Direct3DCreate9Ex();
            if (_d3d9Ex == null)
                throw new InvalidOperationException("无法创建 IDirect3D9Ex");

            var pp = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = Format.X8R8G8B8,
                BackBufferCount = 1,
                PresentationInterval = PresentInterval.Immediate,
                MultiSampleType = MultisampleType.None,
                DeviceWindowHandle = GetDesktopWindow(),
                EnableAutoDepthStencil = false
            };

            var flags = CreateFlags.Multithreaded | CreateFlags.FpuPreserve;

            _deviceEx = _d3d9Ex.CreateDeviceEx(
                0, DeviceType.Hardware, pp.DeviceWindowHandle,
                flags | CreateFlags.HardwareVertexProcessing, pp);

            if (_deviceEx == null)
            {
                _deviceEx = _d3d9Ex.CreateDeviceEx(
                    0, DeviceType.Hardware, pp.DeviceWindowHandle,
                    flags | CreateFlags.SoftwareVertexProcessing, pp);
                if (_deviceEx == null)
                    throw new InvalidOperationException("无法创建 D3D9 设备");
            }

            CreateRenderTarget();
            CreateShaders();
            _d3dInitialized = true;
        }

        private void CreateRenderTarget()
        {
            SafeDispose(ref _renderTargetSurface);
            SafeDispose(ref _renderTargetTexture);
            _renderTargetTexture = _deviceEx.CreateTexture(1, 1, 1, Usage.RenderTarget, Format.X8R8G8B8, Pool.Default);
            _renderTargetSurface = _renderTargetTexture.GetSurfaceLevel(0);
        }

        private void EnsureRenderTargetSize(int width, int height)
        {
            var desc = _renderTargetTexture.GetLevelDesc(0);
            if (desc.Width >= width && desc.Height >= height)
                return;

            SafeDispose(ref _renderTargetSurface);
            SafeDispose(ref _renderTargetTexture);
            _renderTargetTexture = _deviceEx.CreateTexture(
                (uint)width, (uint)height, 1, Usage.RenderTarget, Format.X8R8G8B8, Pool.Default);
            _renderTargetSurface = _renderTargetTexture.GetSurfaceLevel(0);
        }

        // ==================== 着色器 ====================
        private const string NV12_VS = @"
struct VS_INPUT  { float3 pos : POSITION; float2 tex : TEXCOORD0; };
struct PS_INPUT  { float4 pos : POSITION; float2 tex : TEXCOORD0; };
PS_INPUT main_vs(VS_INPUT input) {
    PS_INPUT output;
    output.pos = float4(input.pos, 1);
    output.tex = input.tex;
    return output;
}";

        private const string NV12_PS = @"
sampler2D texY : register(s0);
sampler2D texUV : register(s1);
float4 main_ps(float4 pos : POSITION, float2 tex : TEXCOORD0) : COLOR {
    float y = tex2D(texY, tex).r;
    float2 uv = float2(tex2D(texUV, tex).r, tex2D(texUV, tex).a);
    float u = uv.x - 0.5, v = uv.y - 0.5;
    float r = saturate(y + 1.402 * v);
    float g = saturate(y - 0.344 * u - 0.714 * v);
    float b = saturate(y + 1.772 * u);
    return float4(r, g, b, 1);
}";

        private void CreateShaders()
        {
            try
            { 
                var vsBlob = Compiler.Compile(NV12_VS, "main_vs", "VertexShader", "vs_2_0");
                var psBlob = Compiler.Compile(NV12_PS, "main_ps", "PixelShader", "ps_2_0");
                _vertexShader = _deviceEx.CreateVertexShader(vsBlob.Span);
                _pixelShader = _deviceEx.CreatePixelShader(psBlob.Span);

                float[] verts = {
                    -1, -1, 0, 0, 1,
                    -1,  1, 0, 0, 0,
                     1, -1, 0, 1, 1,
                     1,  1, 0, 1, 0
                };

                _vertexBuffer = _deviceEx.CreateVertexBuffer(
     (uint)(verts.Length * sizeof(float)),
     Usage.WriteOnly,
     VertexFormat.Position | VertexFormat.Texture1,
     Pool.Default  // 将 Pool.Managed 改为 Pool.Default
 );

                // 显式 Lock<byte>，返回 Span<byte>，避免类型推断错误
                var data = _vertexBuffer.Lock<byte>(0, 0, LockFlags.None);
                unsafe
                {
                    fixed (byte* p = data)
                    {
                        Marshal.Copy(verts, 0, (IntPtr)p, verts.Length);
                    }
                }
                _vertexBuffer.Unlock();

                _shaderReady = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"着色器创建失败: {ex.Message}");
                _shaderReady = false;
            }
        }
       
        // ==================== 硬解：零拷贝设置外部 Surface ====================
        /// <summary>
        /// 将解码器输出的 RGB Surface 直接设为 D3DImage 后缓冲。
        /// 要求 Surface 必须与此控件使用同一个 D3D9 设备，格式为 X8R8G8B8。
        /// </summary>
        public void RenderHardwareSurface(IntPtr surfacePtr, int width, int height)
        {
            if (!_d3dInitialized || _deviceEx == null || surfacePtr == IntPtr.Zero)
                return;

            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(() => RenderHardwareSurface(surfacePtr, width, height));
                return;
            }
             try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surfacePtr);
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _d3dImage.Unlock();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"硬解表面设置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取 D3D9 设备，供外部解码器使用以共享设备。
        /// </summary>
        public IDirect3DDevice9Ex GetDevice() => _deviceEx;

        // ==================== 软解入口（YUV420P → NV12 → 渲染） ====================
        public void UpdateFrame(byte[] yPlane, byte[] uPlane, byte[] vPlane,
            int width, int height, int yStride = 0, int uStride = 0, int vStride = 0)
        {
            if (_stopRequested || !_d3dInitialized) return;
            if (yPlane == null || uPlane == null || vPlane == null || width <= 0 || height <= 0) return;

            byte[] nv12 = ConvertYUV420PToNV12(yPlane, uPlane, vPlane, width, height, yStride, uStride, vStride);
            _nv12Queue.Enqueue((nv12, width, width, height));

            if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
        }

        private void RenderYUVFrame()
        {
            if (_stopRequested)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }

            if (!_d3dInitialized || _deviceEx == null || !_shaderReady)
                goto Reschedule;

            byte[] latestData = null;
            int stride = 0, w = 0, h = 0;
            while (_nv12Queue.TryDequeue(out var frame))
            {
                latestData = frame.data;
                stride = frame.stride;
                w = frame.width;
                h = frame.height;
            }

            if (latestData == null)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }

            try
            {
                EnsureYUVTextures(w, h);
                if (_yTexture == null || _uvTexture == null) return;

                unsafe
                {
                    fixed (byte* p = latestData)
                    {
                        int ySize = stride * h;
                        UploadPlane(_yTexture, (IntPtr)p, stride, w, h);
                        UploadPlane(_uvTexture, (IntPtr)(p + ySize), stride, w / 2, h / 2);
                    }
                }

                EnsureRenderTargetSize(w, h);
                _deviceEx.SetRenderTarget(0, _renderTargetSurface);
                _deviceEx.BeginScene();

                // 使用 Vortice.Mathematics.Color，避免与 System.Windows.Media.Color 冲突
                _deviceEx.Clear(ClearFlags.Target, new Vortice.Mathematics.Color(0, 0, 0, 255), 1, 0);
                int screenW = (int)ActualWidth;
                int screenH = (int)ActualHeight;
                var vp = CalculateViewport(w, h,screenW, screenH);
                _deviceEx.Viewport = vp;

                _deviceEx.VertexFormat = VertexFormat.Position | VertexFormat.Texture1;
                _deviceEx.SetStreamSource(0, _vertexBuffer, 0, 5 * sizeof(float));
                _deviceEx.VertexShader = _vertexShader;
                _deviceEx.PixelShader = _pixelShader;

                // 设置采样器过滤模式
                _deviceEx.SetSamplerState(0, SamplerState.MinFilter, (int)TextureFilter.Linear);
                _deviceEx.SetSamplerState(0, SamplerState.MagFilter, (int)TextureFilter.Linear);
                _deviceEx.SetSamplerState(1, SamplerState.MinFilter, (int)TextureFilter.Linear);
                _deviceEx.SetSamplerState(1, SamplerState.MagFilter, (int)TextureFilter.Linear);

                _deviceEx.SetTexture(0, _yTexture);
                _deviceEx.SetTexture(1, _uvTexture);

                _deviceEx.DrawPrimitive(PrimitiveType.TriangleStrip, 0, 2);
                _deviceEx.EndScene();

                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _renderTargetSurface.NativePointer);
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, w, h));
                _d3dImage.Unlock();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"软解渲染异常: {ex.Message}");
            }

            if (_stopRequested)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }

            if (!_nv12Queue.IsEmpty)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
            else
                Interlocked.Exchange(ref _renderQueued, 0);
            return;

        Reschedule:
            if (_stopRequested)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }
            if (!_nv12Queue.IsEmpty)
                _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, RenderYUVFrame);
            else
                Interlocked.Exchange(ref _renderQueued, 0);
        }

        private unsafe void UploadPlane(IDirect3DTexture9 texture, IntPtr data, int srcStride,
            int width, int height)
        {
            var rect = texture.LockRect(0, LockFlags.None);
            byte* dst = (byte*)rect.DataPointer;
            byte* src = (byte*)data;
            int dstPitch = rect.Pitch;
            for (int i = 0; i < height; i++)
            {
                Buffer.MemoryCopy(src + i * srcStride, dst + i * dstPitch, width, width);
            }
            texture.UnlockRect(0);
        }

        private void EnsureYUVTextures(int width, int height)
        {
            if (_yTexture != null && _uvTexture != null && _videoWidth == width && _videoHeight == height)
                return;

            SafeDispose(ref _yTexture);
            SafeDispose(ref _uvTexture);
            _videoWidth = width;
            _videoHeight = height;

            _yTexture = _deviceEx.CreateTexture((uint)width, (uint)height, 1, Usage.None,
                Format.L8, Pool.Managed);
            _uvTexture = _deviceEx.CreateTexture((uint)(width / 2), (uint)(height / 2), 1, Usage.None,
                Format.A8L8, Pool.Managed);
        }

        private Vortice.Direct3D9.Viewport CalculateViewport(int videoW, int videoH, int screenW, int screenH)
        {
            // 防止除零
            if (videoH == 0 || screenH == 0)
                return new Vortice.Direct3D9.Viewport { X = 0, Y = 0, Width = screenW, Height = screenH, MinZ = 0, MaxZ = 1 };

            float vidAspect = (float)videoW / videoH;
            float scrAspect = (float)screenW / screenH;

            int x, y, w, h;

            switch (_scaleMode)
            {
                case VideoScaleMode.Stretch:
                    x = 0; y = 0; w = screenW; h = screenH;
                    break;

                case VideoScaleMode.Fit:
                    if (vidAspect > scrAspect)
                    {
                        // 宽度填满，高度按比例缩放
                        w = screenW;
                        h = (int)(screenW / vidAspect);
                    }
                    else
                    {
                        // 高度填满，宽度按比例缩放
                        h = screenH;
                        w = (int)(screenH * vidAspect);
                    }
                    // 确保宽高为偶数（对齐要求）
                    if (w % 2 != 0) w--;
                    if (h % 2 != 0) h--;
                    // 计算居中偏移
                    x = (screenW - w) / 2;
                    y = (screenH - h) / 2;
                    break;

                case VideoScaleMode.Zoom:
                    if (vidAspect > scrAspect)
                    {
                        // 高度填满，宽度超出裁剪
                        h = screenH;
                        w = (int)(screenH * vidAspect);
                    }
                    else
                    {
                        // 宽度填满，高度超出裁剪
                        w = screenW;
                        h = (int)(screenW / vidAspect);
                    }
                    if (w % 2 != 0) w--;
                    if (h % 2 != 0) h--;
                    x = (screenW - w) / 2;
                    y = (screenH - h) / 2;
                    break;

                default:
                    x = y = 0; w = screenW; h = screenH;
                    break;
            }

            // 边界保护
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;
            if (x < 0) x = 0;
            if (y < 0) y = 0;

            return new Vortice.Direct3D9.Viewport
            {
                X = x,
                Y = y,
                Width = w,
                Height = h,
                MinZ = 0.0f,
                MaxZ = 1.0f
            };
        }

        public void SetScaleMode(VideoScaleMode mode) => _scaleMode = mode;

        // ==================== NV12 转换 ====================
        private byte[] ConvertYUV420PToNV12(byte[] y, byte[] u, byte[] v,
            int width, int height, int yStride, int uStride, int vStride)
        {
            int ySize = width * height, uvWidth = width / 2, uvHeight = height / 2;
            int neededSize = ySize + uvWidth * uvHeight * 2;
            if (_nv12Buffer == null || _nv12Buffer.Length < neededSize)
                _nv12Buffer = new byte[neededSize];
            byte[] nv12 = _nv12Buffer;

            for (int row = 0; row < height; row++)
                Array.Copy(y, row * yStride, nv12, row * width, width);

            int uvOff = ySize;
            for (int row = 0; row < uvHeight; row++)
                for (int col = 0; col < uvWidth; col++)
                {
                    nv12[uvOff + row * uvWidth * 2 + col * 2] = u[row * uStride + col];
                    nv12[uvOff + row * uvWidth * 2 + col * 2 + 1] = v[row * vStride + col];
                }

            return nv12;
        }

        // ==================== 清理 ====================
        private void CleanupD3D9()
        {
            _stopRequested = true;
            _d3dInitialized = false;

            SafeDispose(ref _vertexShader);
            SafeDispose(ref _pixelShader);
            SafeDispose(ref _vertexBuffer);
            SafeDispose(ref _yTexture);
            SafeDispose(ref _uvTexture);
            SafeDispose(ref _renderTargetSurface);
            SafeDispose(ref _renderTargetTexture);
            SafeDispose(ref _deviceEx);
            SafeDispose(ref _d3d9Ex);
        }

        private static void SafeDispose<T>(ref T? obj) where T : class, IDisposable
        {
            obj?.Dispose();
            obj = null;
        }
    }
}