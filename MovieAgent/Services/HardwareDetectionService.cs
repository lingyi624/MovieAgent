using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace MovieAgent.Services
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
    public class HardwareDetectionService
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
                Console.WriteLine($"[Hardware] WMI查询失败: {ex.Message}");
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
                Console.WriteLine($"[Hardware] 获取所有显卡失败: {ex.Message}");
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
                Console.WriteLine("[Hardware] 无法获取显卡信息，使用软解");
                return DecoderType.Software;
            }

            Console.WriteLine($"[Hardware] 检测到显卡: {gpu}");

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
}