namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 主题服务接口 - 管理应用主题（暗色/亮色模式）
/// 设置会持久化到系统环境变量
/// </summary>
public interface IThemeService
{
    /// <summary>当前主题模式</summary>
    ThemeMode CurrentTheme { get; }

    /// <summary>主题变化事件</summary>
    event Action<ThemeMode>? ThemeChanged;

    /// <summary>
    /// 设置主题
    /// </summary>
    /// <param name="theme">目标主题模式</param>
    Task SetThemeAsync(ThemeMode theme);

    /// <summary>加载保存的主题设置</summary>
    Task LoadThemeAsync();
}

/// <summary>
/// 主题模式枚举
/// </summary>
public enum ThemeMode
{
    /// <summary>暗色模式</summary>
    Dark,
    /// <summary>亮色模式</summary>
    Light,
    /// <summary>跟随系统</summary>
    System
}
