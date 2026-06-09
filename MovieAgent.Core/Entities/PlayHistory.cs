using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieAgent.Core.Entities;

/// <summary>
/// 播放历史记录 - 记录用户观看电影的历史
/// </summary>
public class PlayHistory
{
    /// <summary>播放记录唯一标识符（自增主键）</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>关联的电影ID</summary>
    public int MovieId { get; set; }

    /// <summary>关联的电影实体（导航属性）</summary>
    [ForeignKey(nameof(MovieId))]
    public Movie? Movie { get; set; }

    /// <summary>播放时间（UTC）</summary>
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;

    /// <summary>播放进度（秒）- 表示上次停止的位置</summary>
    public int? Progress { get; set; }

    /// <summary>电影总时长（秒）</summary>
    public int? Duration { get; set; }

    /// <summary>是否已看完（播放进度超过90%视为看完）</summary>
    public bool Completed { get; set; }

    /// <summary>播放设备信息</summary>
    public string? Device { get; set; }
}
