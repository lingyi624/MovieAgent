using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.Controls.VideoAudioSync
{
    public class VideoAnalyzer
    {
        public VideoProfile Analyze(IVideoPlayer player)
        {
            var profile = new VideoProfile();

            // 1. 分析帧率稳定性
            profile.FrameRateStability = AnalyzeFrameRateStability(player);

            // 2. 分析音频稳定性
            profile.AudioStability = AnalyzeAudioStability(player);

            // 3. 分析音视频相关性
            profile.AVCorrelation = AnalyzeAVCorrelation(player);

            // 4. 检测是否 VFR (可变帧率)
            profile.IsVFR = DetectVFR(player);

            // 5. 分析编码模式
            profile.EncodingPattern = AnalyzeEncodingPattern(player);

            return profile;
        }

        private double AnalyzeFrameRateStability(IVideoPlayer player)
        {
            var ptsHistory = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                ptsHistory.Add(player.GetVideoPTS());
                Thread.Sleep(100);
            }

            // 计算标准差
            var mean = ptsHistory.Average();
            var variance = ptsHistory.Average(p => Math.Pow(p - mean, 2));
            var stddev = Math.Sqrt(variance);

            // 标准差越小越稳定
            return Math.Max(0, 1 - stddev / 0.1);
        }
    }
}
