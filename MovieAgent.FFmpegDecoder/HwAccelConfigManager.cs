using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.FFmpegDecoder
{
    using FFmpeg.AutoGen;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;

    public class HwAccelConfigManager
    {
        private readonly string _configPath;
        private HardwareAccelerationConfig _config;
        private readonly object _lock = new object();

        public HwAccelConfigManager(string appName = "MovieAgentPlayer")
        {
            // 配置文件路径: %AppData%\MyFFmpegApp\hwaccel.json (Windows)
            // 或 ~/.config/MyFFmpegApp/hwaccel.json (Linux)
            //var configDir = Path.Combine(
            //    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            //    appName
            //);
            var configDir = Path.Combine(
                AppContext.BaseDirectory,
                appName
            );

            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            _configPath = Path.Combine(configDir, "hwaccel.json");
            LoadConfig();
        }

        // 加载配置
        private void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<HardwareAccelerationConfig>(json);

                    // 检查配置是否过期（例如超过30天）
                    if (_config != null && (DateTime.Now - _config.CachedAt).TotalDays > 30)
                    {
                        DebugLogger.WriteLine("[Config] Cache expired, will re-detect");
                        _config = null;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[Config] Load failed: {ex.Message}");
                    _config = null;
                }
            }

            if (_config == null)
            {
                _config = new HardwareAccelerationConfig
                {
                    CachedAt = DateTime.Now,
                    SystemSignature = ComputeSystemSignature()
                };
            }
        }

        // 保存配置
        public void SaveConfig()
        {
            lock (_lock)
            {
                try
                {
                    _config.CachedAt = DateTime.Now;
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(_config, options);
                    File.WriteAllText(_configPath, json);
                    DebugLogger.WriteLine($"[Config] Saved to {_configPath}");
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[Config] Save failed: {ex.Message}");
                }
            }
        }

        // 计算系统特征码（检测硬件变化）
        private string ComputeSystemSignature()
        {
            try
            {
                var sb = new StringBuilder();

                // 获取 CPU 信息
                sb.Append(Environment.ProcessorCount);

                // 获取显卡信息（简单版本）
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // 使用 Windows Management Instrumentation
                    // 简化版，实际使用需要添加 System.Management 引用
                    sb.Append("|Win");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    sb.Append("|Linux");
                    // 可以读取 /proc/driver/nvidia/version 等
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    sb.Append("|Mac");
                }

                // 计算哈希
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return Convert.ToBase64String(hash);
            }
            catch
            {
                return Guid.NewGuid().ToString(); // 失败时返回随机值，强制重新检测
            }
        }

        // 检查硬件是否发生变化
        public bool IsHardwareChanged()
        {
            var newSignature = ComputeSystemSignature();
            return _config.SystemSignature != newSignature;
        }

        // 获取缓存的硬件类型（用于特定编码）
        public List<AVHWDeviceType> GetCachedTypes(AVCodecID codecId)
        {
            var codecName = ffmpeg.avcodec_get_name(codecId);

            if (_config.AvailableCodecs.TryGetValue(codecName, out var typeNames))
            {
                var types = new List<AVHWDeviceType>();
                foreach (var name in typeNames)
                {
                    if (Enum.TryParse<AVHWDeviceType>($"AV_HWDEVICE_TYPE_{name}", out var type))
                        types.Add(type);
                    else if (Enum.TryParse<AVHWDeviceType>(name, out var type2))
                        types.Add(type2);
                }
                return types;
            }

            return new List<AVHWDeviceType>();
        }

        // 保存检测结果
        public void SaveDetectionResult(AVCodecID codecId, List<AVHWDeviceType> supportedTypes,
                                         Dictionary<AVHWDeviceType, string> gpuInfo = null)
        {
            var codecName = ffmpeg.avcodec_get_name(codecId);
            var typeNames = supportedTypes.Select(t => t.ToString().Replace("AV_HWDEVICE_TYPE_", "")).ToList();

            _config.AvailableCodecs[codecName] = typeNames;

            // 保存最佳选择
            if (supportedTypes.Count > 0)
            {
                var best = supportedTypes.First();
                var info = new HwTypeInfo
                {
                    TypeName = best.ToString(),
                    VerifiedAt = DateTime.Now
                };

                if (gpuInfo != null && gpuInfo.TryGetValue(best, out var gpuName))
                {
                    info.GpuName = gpuName;
                }

                _config.SelectedTypes[codecName] = info;
            }

            _config.SystemSignature = ComputeSystemSignature();
            SaveConfig();
        }

        // 获取最佳硬件类型（从缓存）
        public AVHWDeviceType? GetBestCachedType(AVCodecID codecId)
        {
            var codecName = ffmpeg.avcodec_get_name(codecId);

            if (_config.SelectedTypes.TryGetValue(codecName, out var info))
            {
                if (Enum.TryParse<AVHWDeviceType>(info.TypeName, out var type))
                    return type;
            }

            return null;
        }

        // 清除所有缓存（强制重新检测）
        public void ClearCache()
        {
            _config.AvailableCodecs.Clear();
            _config.SelectedTypes.Clear();
            _config.CachedAt = DateTime.Now;
            _config.SystemSignature = ComputeSystemSignature();
            SaveConfig();
            DebugLogger.WriteLine("[Config] Cache cleared");
        }
    }
}
