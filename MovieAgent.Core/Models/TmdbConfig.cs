using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace MovieAgent.Core.Models
{
 
    public class TmdbConfig
    {
        public string ApiKey { get; set; }
    }

    public class AIConfig
    {
        public string ModelUrl { get; set; }
        public string ModelName { get; set; }
        public string EmbeddingEndpoint { get; set; }
        public string EmbeddingModel { get; set; }
        public int EmbeddingDimension { get; set; }
        public string Provider { get; set; }
        public string DeepSeekUrl { get; set; }
        public string ApiKey { get; set; }
        public bool IsDefault { get; set; }
    }

    public class LoggingConfig
    {
        public LogLevelConfig LogLevel { get; set; }
    }

    public class LogLevelConfig
    {
        public string Default { get; set; }
        public string Microsoft { get; set; }
    }

    public class RootConfig
    {
        public TmdbConfig TMDB { get; set; }
        public AIConfig AI { get; set; }
        public LoggingConfig Logging { get; set; }
        // 注意属性名要与 JSON 键一致，这里额外处理 "Subtitle:ApiKey" 和 "Player:Path"
        public string SubtitleApiKey { get; set; }  // 映射 "Subtitle:ApiKey"
        public string PlayerPath { get; set; }      // 映射 "Player:Path"
    }
}
