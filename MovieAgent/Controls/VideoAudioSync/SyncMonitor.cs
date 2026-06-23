using System;
using System.Collections.Generic;
using System.Text;
using static MovieAgent.Controls.VideoAudioSync.AdaptiveSyncManager;

namespace MovieAgent.Controls.VideoAudioSync
{
    public class SyncMonitor
    {
        private readonly Dictionary<string, double> _strategySuccess = new();

        public void RecordResult(StrategySuggestion suggestion, bool success)
        {
            var key = suggestion.StrategyType.ToString();
            if (!_strategySuccess.ContainsKey(key))
            {
                _strategySuccess[key] = 0.5;
            }

            // 更新成功率
            var rate = _strategySuccess[key];
            rate = rate * 0.9 + (success ? 0.1 : -0.1);
            _strategySuccess[key] = Math.Clamp(rate, 0, 1);
        }

        public SyncStrategyType GetBestStrategy()
        {
            return _strategySuccess.OrderByDescending(kv => kv.Value)
                                  .First().Key.ToString();
        }
    }
}
