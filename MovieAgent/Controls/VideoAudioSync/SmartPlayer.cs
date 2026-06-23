using MovieAgent.Components.Pages;
using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.Controls.VideoAudioSync
{
    public class SmartPlayer
    {
        private readonly MultiStrategySyncEngine _syncEngine = new();
        private readonly VideoAnalyzer _analyzer = new();
        private VideoProfile _currentProfile;
        private double _lastAdjustment = 0;
        private readonly int _adjustmentHistorySize = 20;
        private readonly Queue<double> _adjustmentHistory = new();

        public async Task PlayAsync(string videoPath)
        {
            // 1. 分析视频
            _currentProfile = _analyzer.Analyze(Player);

            // 2. 启动播放
            Player.Play(videoPath);

            // 3. 启动自适应同步
            await StartAdaptiveSync();
        }

        private async Task StartAdaptiveSync()
        {
            while (Player.IsPlaying)
            {
                var videoTime = Player.GetVideoTime();
                var audioTime = Player.GetAudioTime();

                // 获取同步结果
                var context = new VideoContext
                {
                    VideoProfile = _currentProfile,
                    AudioStability = CheckAudioStability(),
                    Variability = CalculateVariability()
                };

                var result = _syncEngine.Synchronize(videoTime, audioTime, context);

                // 应用调整
                if (Math.Abs(result.Adjustment) > 0.02) // 2ms 阈值
                {
                    ApplyAdjustment(result);
                    _lastAdjustment = result.Adjustment;
                    _adjustmentHistory.Enqueue(result.Adjustment);
                    if (_adjustmentHistory.Count > _adjustmentHistorySize)
                        _adjustmentHistory.Dequeue();
                }

                // 学习调整
                if (_adjustmentHistory.Count >= _adjustmentHistorySize)
                {
                    LearnFromHistory();
                }

                await Task.Delay(50); // 20fps 检查
            }
        }

        private void ApplyAdjustment(SyncResult result)
        {
            switch (result.Method)
            {
                case "AudioMaster":
                    Player.SetAudioDelay(result.Adjustment);
                    break;
                case "VideoMaster":
                    Player.SetVideoSpeed(1 + result.Adjustment / 10);
                    break;
                case "Dynamic":
                    Player.SetAudioDelay(result.Adjustment * 0.5);
                    break;
                case "Hybrid":
                    Player.SetAudioDelay(result.Adjustment);
                    Player.SetVideoSpeed(1 - result.Adjustment / 20);
                    break;
                default:
                    Player.SetAudioDelay(result.Adjustment);
                    break;
            }
        }

        private void LearnFromHistory()
        {
            // 学习最佳的调整模式
            var avgAdjustment = _adjustmentHistory.Average();
            var stdAdjustment = Math.Sqrt(_adjustmentHistory.Average(a => Math.Pow(a - avgAdjustment, 2)));

            if (stdAdjustment < 0.01)
            {
                // 调整稳定，降低检查频率
                // 可以优化性能
            }
        }
    }
}
