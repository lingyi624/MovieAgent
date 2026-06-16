using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.FFmpegDecoder
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    // 硬件加速配置类
    public class HardwareAccelerationConfig
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 2;  // 配置版本，用于未来升级

        [JsonPropertyName("cached_at")]
        public DateTime CachedAt { get; set; }  // 缓存时间

        [JsonPropertyName("system_signature")]
        public string SystemSignature { get; set; } = "";  // 系统特征码，用于检测硬件变化

        [JsonPropertyName("available_hw_types")]
        public Dictionary<string, List<string>> AvailableCodecs { get; set; } = new();

        [JsonPropertyName("selected_hw_type")]
        public Dictionary<string, HwTypeInfo> SelectedTypes { get; set; } = new();

        [JsonPropertyName("fallback_to_software")]
        public bool FallbackToSoftware { get; set; } = true;
    }

    // 硬件类型信息
    public class HwTypeInfo
    {
        [JsonPropertyName("type")]
        public string TypeName { get; set; } = "";

        [JsonPropertyName("gpu_name")]
        public string GpuName { get; set; } = "";

        [JsonPropertyName("memory_mb")]
        public long MemoryMB { get; set; } = 0;

        [JsonPropertyName("verified_at")]
        public DateTime VerifiedAt { get; set; } = DateTime.Now;
    }
}
