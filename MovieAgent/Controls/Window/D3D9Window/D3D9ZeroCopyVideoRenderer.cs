using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice.Direct3D9;

namespace MovieAgent.Controls.Window.D3D9Window
{
    public class D3D9ZeroCopyVideoRenderer : FrameworkElement
    {
        private D3DImage _d3dImage = new D3DImage();
        private IDirect3D9Ex? _d3d9;
        private IDirect3DDevice9Ex? _device;
        private IDirect3DSurface9? _sharedSurface;      // D3DImage 的后备缓冲
        private IDirect3DSurface9? _nv12TempSurface;     // 用于 NV12→RGB 的中间表面

        // 用于 NV12→RGB 的像素着色器
        private IDirect3DPixelShader9? _yuvToRgbShader;
        private IDirect3DVertexShader9? _fullscreenQuadVS;
        private IDirect3DVertexBuffer9? _fullscreenQuadVB;

        private int _surfaceWidth, _surfaceHeight;
        private bool _isInitialized;

         public IDirect3DDevice9Ex? Device => _device;
        public D3D9ZeroCopyVideoRenderer()
        {
            CompositionTarget.Rendering += OnRendering;
        }

        public void Initialize()
        {
            // 创建 D3D9Ex 设备（显式选择 NVIDIA 显卡）
            _d3d9 = D3D9.Direct3DCreate9Ex();
            var adapter = GetNvidiaAdapter(_d3d9);  // 根据显卡信息选择 NVIDIA 适配器
            var pp = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                BackBufferFormat = Format.X8R8G8B8,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                DeviceWindowHandle = IntPtr.Zero,
                PresentationInterval = PresentInterval.Immediate,
                MultiSampleType = MultisampleType.None
            };

            _device = _d3d9.CreateDeviceEx((uint)adapter, DeviceType.Hardware, IntPtr.Zero,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded, pp);

            _isInitialized = true;
        }

        // 选择 NVIDIA 适配器（索引 1，因为 Intel 通常是 0）
        private static int GetNvidiaAdapter(IDirect3D9Ex d3d)
        {
            uint count = d3d.AdapterCount;
            for (uint i = 0; i < count; i++)
            {
                var id = d3d.GetAdapterIdentifier(i);
                if (id.Description.Contains("NVIDIA")) return (int)i;
            }
            return 0; // 退回到 Intel
        }

        // 设置 D3DImage 后备缓冲（在 UI 线程调用）
        public void SetBackBufferSurface(IDirect3DSurface9 surface, int width, int height)
        {
            _surfaceWidth = width;
            _surfaceHeight = height;

            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer);
            _d3dImage.Unlock();
        }

        // 硬解零拷贝渲染：直接使用解码器提供的 NV12 表面，GPU 转换到共享表面
        public void RenderHardwareFrame(IntPtr decoderSurfacePtr, int width, int height)
        {
            if (!_isInitialized) return;
            Dispatcher.Invoke(() =>
            {
                var nv12Surface = new IDirect3DSurface9(decoderSurfacePtr);
                nv12Surface.AddRef(); // 防止被解码器释放

                EnsureSharedSurface(width, height);
                ConvertNV12ToRGBInGPU(nv12Surface, _sharedSurface, width, height);
                nv12Surface.Release();

                // 通知 WPF 刷新
                _d3dImage.Lock();
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _d3dImage.Unlock();
            });
        }

        // 软解零拷贝：上传 YUV 到动态纹理，GPU 转换为 RGB 到共享表面
        public void RenderSoftwareFrame(byte[] y, byte[] u, byte[] v, int width, int height)
        {
            if (!_isInitialized) return;
            Dispatcher.Invoke(() =>
            {
                EnsureSharedSurface(width, height);

                // 创建或重用 NV12 动态纹理，上传原始 YUV 数据
                using var nv12Tex = CreateNV12Texture(y, u, v, width, height);
                using var nv12Surface = nv12Tex.GetSurfaceLevel(0);

                ConvertNV12ToRGBInGPU(nv12Surface, _sharedSurface, width, height);

                _d3dImage.Lock();
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _d3dImage.Unlock();
            });
        }

        private void EnsureSharedSurface(int width, int height)
        {
            if (_sharedSurface != null && _surfaceWidth == width && _surfaceHeight == height)
                return;

            _sharedSurface?.Dispose();
            _sharedSurface = _device!.CreateRenderTarget(
                (uint)width, (uint)height, Format.X8R8G8B8, MultisampleType.None, 0, true);
            SetBackBufferSurface(_sharedSurface, width, height);
        }

        // GPU 端的 NV12→RGB 转换（使用 StretchRect 或自定义 shader）
        private void ConvertNV12ToRGBInGPU(IDirect3DSurface9 nv12Surface, IDirect3DSurface9 rgbSurface, int width, int height)
        {
            // 简单方案：D3D9 不支持直接 NV12→RGB 的 StretchRect，需要两步：
            // 1. NV12 → YUY2/UYVY (StretchRect 支持) 
            // 2. YUY2 → RGB (StretchRect 支持)
            // 这里使用临时 YUY2 表面实现
            var yuy2Tex = _device!.CreateTexture((uint)width, (uint)height, 1, Usage.RenderTarget, Format.X8R8G8B8, Pool.Default);
            using var yuy2Surface = yuy2Tex.GetSurfaceLevel(0);

            // StretchRect: NV12 -> YUY2
          //  _device.StretchRect(nv12Surface, yuy2Surface, TextureFilter.Linear);
            // StretchRect: YUY2 -> X8R8G8B8
            //_device.StretchRect(yuy2Surface, rgbSurface, TextureFilter.Linear);

            // 获取 NV12 表面和 YUY2 表面的尺寸
           // int width = 1920, height = 1080; // 假设视频尺寸
            var srcRect = new Vortice.Direct3D9.Rect(0, 0, width, height);
            var dstRect = new Vortice.Direct3D9.Rect(0, 0, width, height);

            _device.StretchRect(nv12Surface, srcRect, yuy2Surface, dstRect, TextureFilter.Linear);
            _device.StretchRect(yuy2Surface, srcRect, rgbSurface, dstRect, TextureFilter.Linear);



            yuy2Tex.Dispose();
        }

        // UI 事件：当 WPF 合成器需要刷新时调用
        private void OnRendering(object? sender, EventArgs e)
        {
            // D3DImage 会在 AddDirtyRect 后自动请求合成，无需额外操作
        }

        // 辅助：创建动态 NV12 纹理（上传软解 YUV 数据）
        private IDirect3DTexture9 CreateNV12Texture(byte[] y, byte[] u, byte[] v, int width, int height)
        {

            var tex = _device!.CreateTexture((uint)width, (uint)height, 1, 0, Format.X8R8G8B8, Pool.SystemMemory);
            var surf = tex.GetSurfaceLevel(0);
            var rect = surf.LockRect(LockFlags.None);
            unsafe
            {
                byte* dst = (byte*)rect.DataPointer;
                int yPitch = rect.Pitch;
                int uvPitch = yPitch; // NV12 UV 行跨步通常等于 Y 行跨步
                int uvHeight = height / 2;
                int uvWidth = width / 2;

                // 复制 Y 平面
                for (int row = 0; row < height; row++)
                    Buffer.MemoryCopy(
                        Unsafe.AsPointer(ref y[row * width]),
                        dst + row * yPitch,
                        width, width);

                // 复制 UV 平面（交错 UV）
                byte* uvStart = dst + height * yPitch;
                for (int row = 0; row < uvHeight; row++)
                {
                    var srcU = new ReadOnlySpan<byte>(u, row * uvWidth, uvWidth);
                    var srcV = new ReadOnlySpan<byte>(v, row * uvWidth, uvWidth);
                    var dstUV = new Span<byte>(uvStart + row * uvPitch, uvWidth * 2);
                    for (int x = 0; x < uvWidth; x++)
                    {
                        dstUV[x * 2] = srcU[x];
                        dstUV[x * 2 + 1] = srcV[x];
                    }
                }
            }
            surf.UnlockRect();
            surf.Dispose();
            return tex;
        }

        public void Cleanup()
        {
            _isInitialized = false;
            _sharedSurface?.Dispose();
            _nv12TempSurface?.Dispose();
            _device?.Dispose();
            _d3d9?.Dispose();
        }
    }
}
