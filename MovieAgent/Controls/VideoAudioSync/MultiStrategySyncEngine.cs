using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.Controls.VideoAudioSync
{
    public class MultiStrategySyncEngine
    {
        private readonly List<ISyncStrategy> _strategies = new();
        private ISyncStrategy _activeStrategy;
        private readonly SyncMetrics _metrics = new();

        public MultiStrategySyncEngine()
        {
            // 注册所有策略
            _strategies.Add(new AudioMasterStrategy());
            _strategies.Add(new VideoMasterStrategy());
            _strategies.Add(new DynamicAdjustStrategy());
            _strategies.Add(new FrameDropStrategy());
            _strategies.Add(new SpeedAdjustStrategy());
            _strategies.Add(new HybridStrategy());
        }

        public SyncResult Synchronize(double videoTime, double audioTime, VideoContext context)
        {
            var diff = videoTime - audioTime;

            // 收集所有策略的建议
            var suggestions = new List<StrategySuggestion>();
            foreach (var strategy in _strategies)
            {
                var suggestion = strategy.Analyze(videoTime, audioTime, context);
                suggestion.Confidence = CalculateConfidence(strategy, suggestion, context);
                suggestions.Add(suggestion);
            }

            // 选择最佳策略
            var best = suggestions.OrderByDescending(s => s.Confidence).First();

            // 如果最佳策略置信度太低，使用混合策略
            if (best.Confidence < 0.6)
            {
                return HybridAdjustment(suggestions);
            }

            // 执行最佳策略
            return best.Result;
        }

        private double CalculateConfidence(ISyncStrategy strategy, StrategySuggestion suggestion, VideoContext context)
        {
            double confidence = 0;

            // 历史成功率
            confidence += strategy.HistoricalSuccessRate * 0.3;

            // 当前误差
            var error = Math.Abs(suggestion.Result.Adjustment);
            confidence += (1 - Math.Min(error / 1.0, 1)) * 0.3;

            // 趋势分析
            confidence += AnalyzeTrend(strategy, context) * 0.2;

            // 策略适用性
            confidence += strategy.ApplicabilityScore(context) * 0.2;

            return Math.Clamp(confidence, 0, 1);
        }
    }

    // 策略接口
    public interface ISyncStrategy
    {
        StrategySuggestion Analyze(double videoTime, double audioTime, VideoContext context);
        double HistoricalSuccessRate { get; }
        double ApplicabilityScore(VideoContext context);
    }

    // 音频主时钟策略
    public class AudioMasterStrategy : ISyncStrategy
    {
        public double HistoricalSuccessRate { get; private set; } = 0.8;

        public StrategySuggestion Analyze(double videoTime, double audioTime, VideoContext context)
        {
            var diff = videoTime - audioTime;
            var adjustment = -diff * 0.5; // 平滑调整

            return new StrategySuggestion
            {
                StrategyType = SyncStrategyType.AudioMaster,
                Result = new SyncResult { Adjustment = adjustment, Method = "AudioMaster" },
                Confidence = 0.8 - Math.Abs(diff) * 0.1
            };
        }

        public double ApplicabilityScore(VideoContext context)
        {
            // 音频稳定的视频适合此策略
            return context.AudioStability ? 0.9 : 0.5;
        }
    }

    // 动态调整策略（AI 风格）
    public class DynamicAdjustStrategy : ISyncStrategy
    {
        private readonly Queue<double> _diffHistory = new();
        private readonly int _historySize = 10;
        private double _trend = 0;

        public double HistoricalSuccessRate { get; private set; } = 0.7;

        public StrategySuggestion Analyze(double videoTime, double audioTime, VideoContext context)
        {
            var diff = videoTime - audioTime;
            _diffHistory.Enqueue(diff);
            if (_diffHistory.Count > _historySize) _diffHistory.Dequeue();

            // 计算趋势
            _trend = CalculateTrend();

            // 根据趋势调整
            double adjustment;
            if (Math.Abs(_trend) > 0.01)
            {
                // 有趋势，提前预测
                adjustment = -diff - _trend * 0.3;
            }
            else
            {
                // 稳定状态，平滑调整
                adjustment = -diff * 0.3;
            }

            // 限制最大调整
            adjustment = Math.Clamp(adjustment, -0.5, 0.5);

            return new StrategySuggestion
            {
                StrategyType = SyncStrategyType.DynamicAdjust,
                Result = new SyncResult { Adjustment = adjustment, Method = "Dynamic" },
                Confidence = 0.7 - Math.Abs(_trend) * 0.2
            };
        }

        private double CalculateTrend()
        {
            if (_diffHistory.Count < 3) return 0;

            var list = _diffHistory.ToList();
            var sum = 0.0;
            var sumX = 0.0;

            for (int i = 0; i < list.Count; i++)
            {
                sum += list[i];
                sumX += list[i] * i;
            }

            var n = list.Count;
            var mean = sum / n;
            var meanX = (n - 1) / 2.0;

            var slope = (sumX - n * meanX * mean) / (n * (n * n - 1) / 12.0);
            return slope;
        }

        public double ApplicabilityScore(VideoContext context)
        {
            // 适用于变化的视频
            return context.Variability > 0.3 ? 0.8 : 0.6;
        }
    }

    // 混合策略
    public class HybridStrategy : ISyncStrategy
    {
        private readonly List<ISyncStrategy> _subStrategies = new();

        public double HistoricalSuccessRate { get; private set; } = 0.9;

        public HybridStrategy()
        {
            _subStrategies.Add(new AudioMasterStrategy());
            _subStrategies.Add(new DynamicAdjustStrategy());
        }

        public StrategySuggestion Analyze(double videoTime, double audioTime, VideoContext context)
        {
            var suggestions = _subStrategies.Select(s =>
                s.Analyze(videoTime, audioTime, context)).ToList();

            // 加权平均
            var totalWeight = suggestions.Sum(s => s.Confidence);
            var weightedAdjustment = suggestions.Sum(s => s.Result.Adjustment * s.Confidence) / totalWeight;

            return new StrategySuggestion
            {
                StrategyType = SyncStrategyType.Hybrid,
                Result = new SyncResult { Adjustment = weightedAdjustment, Method = "Hybrid" },
                Confidence = suggestions.Average(s => s.Confidence) * 0.9
            };
        }

        public double ApplicabilityScore(VideoContext context)
        {
            return 0.8; // 通用性高
        }
    }
}
