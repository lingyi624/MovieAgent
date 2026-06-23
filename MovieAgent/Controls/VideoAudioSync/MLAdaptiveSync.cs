using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.Controls.VideoAudioSync
{
    public class MLAdaptiveSync
    {
        private readonly List<VideoProfile> _trainedProfiles = new();
        private readonly Dictionary<string, double> _bestAdjustments = new();

        public double PredictSyncOffset(VideoProfile profile)
        {
            // 查找最相似的已知视频
            var similar = FindMostSimilar(profile);
            if (similar != null)
            {
                return _bestAdjustments[similar.Id];
            }

            // 基于规则预测
            return PredictBasedOnRules(profile);
        }

        private VideoProfile FindMostSimilar(VideoProfile profile)
        {
            double bestScore = 0;
            VideoProfile bestMatch = null;

            foreach (var trained in _trainedProfiles)
            {
                var score = CalculateSimilarity(profile, trained);
                if (score > bestScore && score > 0.8)
                {
                    bestScore = score;
                    bestMatch = trained;
                }
            }

            return bestMatch;
        }

        private double CalculateSimilarity(VideoProfile a, VideoProfile b)
        {
            double score = 0;

            // 编解码器相似
            if (a.Codec == b.Codec) score += 0.3;

            // 帧率相似
            var fpsDiff = Math.Abs(a.FrameRate - b.FrameRate) / a.FrameRate;
            score += (1 - fpsDiff) * 0.2;

            // 码率相似
            var bitrateDiff = Math.Abs(a.Bitrate - b.Bitrate) / a.Bitrate;
            score += (1 - bitrateDiff) * 0.2;

            // 分辨率相似
            var resolutionDiff = Math.Abs(a.Resolution - b.Resolution) / a.Resolution;
            score += (1 - resolutionDiff) * 0.3;

            return score;
        }
    }
}
