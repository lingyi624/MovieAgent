using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.Direct3D9;

public unsafe class D3D9DecoderInitializer : IDisposable
{
    private IntPtr _deviceManagerPtr;
    private AVBufferRef* _hwDeviceContext;

    public AVBufferRef* HardwareDeviceContext => _hwDeviceContext;

    public void Initialize(IDirect3DDevice9Ex device)
    {
        if (device == null) throw new ArgumentNullException(nameof(device));

        // 1. 创建设备管理器
        uint resetToken;
        int hr = NativeMethods.DXVA2CreateDirect3DDeviceManager9(out resetToken, out _deviceManagerPtr);
        if (hr < 0)
            throw new InvalidOperationException($"DXVA2CreateDirect3DDeviceManager9 失败: 0x{hr:X8}");

        // 2. 通过 vtable 调用 ResetDevice（避免自定义 COM 接口）
        hr = VTableHelper.ResetDevice(_deviceManagerPtr, device.NativePointer, resetToken);
        if (hr < 0)
        {
            Marshal.Release(_deviceManagerPtr);
            throw new InvalidOperationException($"ResetDevice 失败: 0x{hr:X8}");
        }

        // 3. 分配硬件上下文（DXVA2 枚举值 = 3）
        _hwDeviceContext = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2);
        if (_hwDeviceContext == null) throw new Exception("av_hwdevice_ctx_alloc 失败");

        // 4. 填充 AVDXVA2DeviceContext 的唯一字段 devmgr
        var hwCtx = (AVHWDeviceContext*)_hwDeviceContext->data;
        void** dxva2Ctx = (void**)hwCtx->hwctx;   // hwctx 指向的是 AVDXVA2DeviceContext
        *dxva2Ctx = (void*)_deviceManagerPtr;

        // 5. 初始化
        int ret = ffmpeg.av_hwdevice_ctx_init(_hwDeviceContext);
        if (ret < 0)
        {
             AVBufferRef* ctx = _hwDeviceContext;
            ffmpeg.av_buffer_unref(&ctx);
            _hwDeviceContext = null;   // ctx 现在为 null，原始指针已被释放
            _hwDeviceContext = null;
            throw new Exception($"av_hwdevice_ctx_init 失败: {ret}");
        }
    }

    public void Dispose()
    {
        // 1. 释放 FFmpeg 硬件上下文
        //if (_hwDeviceContext != null)
        //{
        //    AVBufferRef* ctx = _hwDeviceContext;
        //    ffmpeg.av_buffer_unref(&ctx);
        //    _hwDeviceContext = null;
        //}

        // 2. 释放设备管理器
        //if (_deviceManagerPtr != IntPtr.Zero)
        //{
        //    Marshal.Release(_deviceManagerPtr);
        //    _deviceManagerPtr = IntPtr.Zero;
        //}
    }

    // ---------- 底层帮助函数 ----------
    private static class NativeMethods
    {
        [DllImport("dxva2.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int DXVA2CreateDirect3DDeviceManager9(
            out uint resetToken,
            out IntPtr ppDeviceManager);
    }

    private static class VTableHelper
    {
        // IDirect3DDeviceManager9 的 ResetDevice 在 vtable 中的索引为 3
        private static readonly int VTableOffset = IntPtr.Size * 3;

        //public static unsafe int ResetDevice(IntPtr pDeviceManager, IntPtr pDevice, uint index)
        //{
        //    IntPtr vtable = Marshal.ReadIntPtr(pDeviceManager);
        //    IntPtr funcPtr = Marshal.ReadIntPtr(vtable + VTableOffset);
        //    var func = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)funcPtr;
        //    return func(pDeviceManager, pDevice, index);
        //}
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ResetDeviceDelegate(IntPtr pDeviceManager, IntPtr pDevice, uint resetToken);

        public static unsafe int ResetDevice(IntPtr pDeviceManager, IntPtr pDevice, uint index)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pDeviceManager);
            IntPtr funcPtr = Marshal.ReadIntPtr(vtable + 0x18); // 64位系统使用 0x18
            var func = Marshal.GetDelegateForFunctionPointer<ResetDeviceDelegate>(funcPtr);
            return func(pDeviceManager, pDevice, index);
        }
    }
}