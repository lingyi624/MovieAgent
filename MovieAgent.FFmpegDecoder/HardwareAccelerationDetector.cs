using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MovieAgent.FFmpegDecoder
{
    public class HardwareAccelerationDetector
    {
        private HwAccelConfigManager _configManager;
        private Dictionary<AVCodecID, List<AVHWDeviceType>> _runtimeCache = new();

        public HardwareAccelerationDetector(string appName = "MovieAgentPlayer")
        {
            _configManager = new HwAccelConfigManager(appName);

            // 检查硬件是否变化
            if (_configManager.IsHardwareChanged())
            {
                DebugLogger.WriteLine("[Detector] Hardware changed, will re-detect all");
                _configManager.ClearCache();
            }
        }

        public async Task<List<AVHWDeviceType>> DetectHardwareTypesAsync(AVCodecID codecId, bool forceRedetect = false)
        {
            // 先检查运行时缓存
            if (_runtimeCache.TryGetValue(codecId, out var cached))
                return cached;

            // 从配置文件读取
            if (!forceRedetect)
            {
                var cachedTypes = _configManager.GetCachedTypes(codecId);
                if (cachedTypes.Count > 0)
                {
                    DebugLogger.WriteLine($"[Detector] Using cached result for {codecId}");
                    _runtimeCache[codecId] = cachedTypes;
                    return cachedTypes;
                }
            }

            // 重新检测（在后台线程执行）
            //var result = await Task.Run(() => {
            //    DebugLogger.WriteLine($"[Detector] Detecting hardware for {codecId}...");
            //     return PerformFullDetection(codecId); 
            //});
            var result= PerformFullDetection(codecId); 
            // 保存结果
            _runtimeCache[codecId] = result;
            _configManager.SaveDetectionResult(codecId, result);

            return result;
        }

        private List<AVHWDeviceType> PerformFullDetection(AVCodecID codecId)
        {
            var supported = new List<AVHWDeviceType>();
            var orderedTypes = GetPlatformSpecificHwTypes(); // 你之前实现的排序逻辑

            foreach (var hwType in orderedTypes)
            {
                if (TryInitHwDecoder(codecId, hwType))
                {
                    supported.Add(hwType);
                    DebugLogger.WriteLine($"[Detector] ✓ {ffmpeg.av_hwdevice_get_type_name(hwType)} supported");
                }
            }

            return supported;
        }
        private unsafe bool TryInitHwDecoder(AVCodecID codecId, AVHWDeviceType hwType)
        {
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(codecId);
            if (decoder == null) return false;

            AVCodecContext* codecCtx = ffmpeg.avcodec_alloc_context3(decoder);
            AVBufferRef* hwDeviceCtx = null;

            try
            {
                // 1. 尝试创建硬件设备上下文 (例如, 创建 cuda/dxva2 设备)
                if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, hwType, null, null, 0) < 0)
                    return false;

                codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);

                // 2. 打开解码器 (这一步会真正验证能否配合硬件工作)
                if (ffmpeg.avcodec_open2(codecCtx, decoder, null) < 0)
                    return false;

                DebugLogger.WriteLine($"[FFmpeg] Hardware acceleration confirmed: {ffmpeg.av_hwdevice_get_type_name(hwType)}");
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"Init failed: {ex.Message}");
                return false;
            }
            finally
            {
                // 清理资源
                if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
                if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);
            }
        }
        private List<AVHWDeviceType> GetPlatformSpecificHwTypes()
        {
            var types = new List<AVHWDeviceType>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows 优先级顺序
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);  // 推荐优先，性能好
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2);    // 兼容老系统
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA);     // NVIDIA显卡
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_AMF);      // AMD显卡
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV);      // Intel核显
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA);  // Win11+优化
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI);    // Intel/AMD Linux主流
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA);     // NVIDIA
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU);    // 老NVIDIA方案
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_DRM);      // 嵌入式/DRM
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN);   // 通用但成熟度一般
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX);  // macOS 唯一主流方案
            }

            // 通用但通常不作为首选
            types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC);  // Android专用
            types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_OHCODEC);     // 鸿蒙专用
            types.Add(AVHWDeviceType.AV_HWDEVICE_TYPE_OPENCL);      // 极少用于纯解码

            return types;
        }
        public async Task<AVHWDeviceType?> GetBestHardwareTypeAsync(AVCodecID codecId)
        {
            // 优先从配置读取最佳选择
            var bestCached = _configManager.GetBestCachedType(codecId);
            if (bestCached.HasValue)
            {
                // 验证缓存的是否仍然可用
                if (TryInitHwDecoder(codecId, bestCached.Value))
                    return bestCached.Value;
            }

            // 重新检测
            var allTypes = await DetectHardwareTypesAsync(codecId, forceRedetect: true);
            return allTypes.FirstOrDefault();
        }
    }
}
