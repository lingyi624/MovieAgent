using FFmpeg.AutoGen;
using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices; 
 
namespace MovieAgent.FFmpegDecoder
{
    /// <summary>
    /// 显卡信息
    /// </summary>
    public class GPUInfo
    {
        public string Name { get; set; } = "";
        public ulong AdapterRAM { get; set; }
        public string DriverVersion { get; set; } = "";
        public string VideoProcessor { get; set; } = "";
        public bool IsPrimary { get; set; }

        /// <summary>
        /// 显存大小(GB)
        /// </summary>
        public double AdapterRAMGB => Math.Round(AdapterRAM / 1024.0 / 1024 / 1024, 2);

        /// <summary>
        /// 显卡类型
        /// </summary>
        public GPUType Type
        {
            get
            {
                if (Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return GPUType.NVIDIA;
                if (Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    return GPUType.Intel;
                if (Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                    return GPUType.AMD;
                return GPUType.Unknown;
            }
        }

        public override string ToString()
        {
            return $"[{Type}] {Name}, 显存: {AdapterRAMGB}GB, 驱动: {DriverVersion}";
        }
    }

    public enum GPUType
    {
        Unknown,
        NVIDIA,
        Intel,
        AMD
    }

    /// <summary>
    /// 硬件检测服务
    /// </summary>
    public class HardwareDetectionHelper
    {
        /// <summary>
        /// 获取主显卡信息
        /// </summary>
        public static GPUInfo GetPrimaryGPU()
        {
            try
            {
                // WMI 查询所有视频控制器
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    var gpus = searcher.Get().Cast<ManagementObject>().ToList().Where(d => d["CurrentHorizontalResolution"] != null); 
                    // Availability = 3 表示 运行/全功率状态
                    var primaryGpu = gpus.FirstOrDefault(g => (ushort?)g["Availability"] == 3);

                    // 或选择性能更高的显卡（根据 AdapterRAM 比较）
                    var bestGpu = gpus.OrderByDescending(g => (Convert.ToUInt64(g["AdapterRAM"] ?? 0))).First();
                    primaryGpu ??= bestGpu; 
                    //// 优先选择当前正在使用的显卡（有分辨率信息的）
                    //var primaryGpu = gpus.FirstOrDefault(v => v["CurrentHorizontalResolution"] != null);

                    // 如果没有主显卡，则取第一个
                    // primaryGpu ??= gpus.FirstOrDefault();

                    // if (primaryGpu == null) return null;

                    return new GPUInfo
                    {
                        Name = primaryGpu["Name"]?.ToString() ?? "Unknown",
                        AdapterRAM = Convert.ToUInt64(primaryGpu["AdapterRAM"] ?? 0),
                        DriverVersion = primaryGpu["DriverVersion"]?.ToString() ?? "",
                        VideoProcessor = primaryGpu["VideoProcessor"]?.ToString() ?? "",
                        IsPrimary = true
                    };
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[Hardware] WMI查询失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取所有显卡信息
        /// </summary>
        public static List<GPUInfo> GetAllGPUs()
        {
            var result = new List<GPUInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    foreach (ManagementObject gpu in searcher.Get())
                    {
                        result.Add(new GPUInfo
                        {
                            Name = gpu["Name"]?.ToString() ?? "Unknown",
                            AdapterRAM = (ulong)(gpu["AdapterRAM"] ?? 0),
                            DriverVersion = gpu["DriverVersion"]?.ToString() ?? "",
                            VideoProcessor = gpu["VideoProcessor"]?.ToString() ?? "",
                            IsPrimary = gpu["CurrentHorizontalResolution"] != null
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[Hardware] 获取所有显卡失败: {ex.Message}");
            }

            return result;
        }
        /// <summary>
        /// 获取当前使用的显卡信息
        /// </summary>
        /// <returns></returns>
        public static string GetUseGpuInfo()
        {
            var gpu = GetPrimaryGPU();
            if (gpu == null)
                return "无法获取显卡信息";
            return gpu.ToString();
        }   
        /// <summary>
        /// 获取推荐的解码器类型
        /// </summary>
        public static DecoderType GetRecommendedDecoder()
        {
            var gpu = GetPrimaryGPU();

            if (gpu == null)
            {
                DebugLogger.WriteLine("[Hardware] 无法获取显卡信息，使用软解");
                return DecoderType.Software;
            }

            DebugLogger.WriteLine($"[Hardware] 检测到显卡: {gpu}");

            // 根据显卡类型推荐解码器
            switch (gpu.Type)
            {
                case GPUType.NVIDIA:
                    // 检查驱动版本是否支持CUDA
                    if (IsNvidiaDriverCompatible(gpu.DriverVersion))
                        return DecoderType.CUDA;
                    return DecoderType.D3D11VA;  // 降级到通用硬解

                case GPUType.Intel:
                    return DecoderType.QSV;

                case GPUType.AMD:
                    return DecoderType.D3D11VA;

                default:
                    return DecoderType.D3D11VA;  // 通用Windows硬解
            }
        }

        private static bool IsNvidiaDriverCompatible(string driverVersion)
        {
            // NVIDIA驱动版本需要 >= 470 才支持较好的CUDA解码
            if (string.IsNullOrEmpty(driverVersion)) return false;

            // 驱动版本格式通常是 31.0.15.xxxx 或 470.xx
            var parts = driverVersion.Split('.');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var major))
            {
                return major >= 470;
            }
            return false;
        }
    }

    /// <summary>
    /// 解码器类型
    /// </summary>
    public enum DecoderType
    {
        Software,   // 软解
        D3D11VA,    // Windows D3D11VA
        DXVA2,      // Windows DXVA2
        CUDA,       // NVIDIA CUDA
        QSV,        // Intel QuickSync
        AMF         // AMD AMF
    }



public unsafe class HardwareDecoder
    {
        private AVCodecContext* _codecContext;
        private AVHWDeviceContext* _hwDeviceContext;
        private AVBufferRef* _hwDeviceRef;
        private AVBufferRef* _hwFramesRef;

        /// <summary>
        /// 初始化硬件解码器（自动获取当前活动显示设备）
        /// </summary>
        public   int InitializeHardwareDecoder(int deviceType = (int)AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
        {
            int ret;

            // 1. 获取当前活动显示设备（Windows 示例）
            string devicePath = GetActiveDisplayDevicePath();

            // 2. 根据设备类型创建硬件设备上下文
            AVHWDeviceType hwType = (AVHWDeviceType)deviceType;

            fixed (AVBufferRef** deviceRefPtr = &_hwDeviceRef)
            {
                // 选择硬件设备类型（CUDA、DXVA2、D3D11VA、VAAPI 等）
                ret = ffmpeg.av_hwdevice_ctx_create(deviceRefPtr, hwType, devicePath, null, 0);
                ret.ThrowExceptionIfError("创建硬件设备上下文失败");
            }

            // 3. 从设备引用获取设备上下文
            _hwDeviceContext = (AVHWDeviceContext*)_hwDeviceRef->data;

            // 4. 查找对应的解码器（例如 H.264）
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (decoder == null)
                throw new InvalidOperationException("未找到解码器");

            // 5. 创建解码器上下文
            _codecContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (_codecContext == null)
                throw new OutOfMemoryException("无法分配解码器上下文");

            // 6. 设置硬件设备帧池
            _hwFramesRef = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceRef);
            if (_hwFramesRef == null)
                throw new OutOfMemoryException("无法分配硬件帧上下文");

            AVHWFramesContext* hwFramesCtx = (AVHWFramesContext*)_hwFramesRef->data;
            hwFramesCtx->format = AVPixelFormat.AV_PIX_FMT_CUDA;  // 使用硬件像素格式
            hwFramesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12; // 软件回退格式
            hwFramesCtx->width = 1920;   // 根据实际视频设置
            hwFramesCtx->height = 1080;  // 根据实际视频设置
            hwFramesCtx->initial_pool_size = 32; // 帧池大小

            ret = ffmpeg.av_hwframe_ctx_init(_hwFramesRef);
            ret.ThrowExceptionIfError("初始化硬件帧池失败");

            // 7. 将硬件帧池附加到解码器上下文
            _codecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesRef);
            if (_codecContext->hw_frames_ctx == null)
                throw new OutOfMemoryException("无法引用硬件帧上下文");

            // 8. 打开解码器
            ret = ffmpeg.avcodec_open2(_codecContext, decoder, null);
            ret.ThrowExceptionIfError("打开解码器失败");

            return ret;
        }

        /// <summary>
        /// 获取当前活动显示设备路径（Windows 示例）
        /// </summary>
        private string GetActiveDisplayDevicePath()
        {
            // Windows 示例：使用默认 CUDA 设备
            // 实际实现中可调用 DXGI 或 NVAPI 获取当前活动 GPU

            // 方式1：自动检测（传 null 或空字符串让 FFmpeg 自动选择）
            return HardwareDetectionHelper.GetRecommendedDecoder().ToString();

            // 方式2：指定特定设备路径（Windows NVIDIA）
            // return "/dev/dri/renderD128";  // Linux
            // return "GPU-0";                // Windows D3D11
        }
    }
}