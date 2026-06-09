namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 键盘快捷键服务接口 - 管理全局键盘快捷键
/// </summary>
public interface IKeyboardShortcutService
{
    /// <summary>
    /// 注册快捷键
    /// </summary>
    /// <param name="shortcut">快捷键类型</param>
    /// <param name="handler">处理函数</param>
    void RegisterShortcut(KeyboardShortcut shortcut, Action handler);

    /// <summary>
    /// 取消注册快捷键
    /// </summary>
    /// <param name="shortcut">快捷键类型</param>
    void UnregisterShortcut(KeyboardShortcut shortcut);

    /// <summary>
    /// 处理键盘事件
    /// </summary>
    /// <param name="e">KeyEventArgs 事件参数</param>
    void HandleKeyDown(object e);
}

/// <summary>
/// 快捷键类型枚举
/// </summary>
public enum KeyboardShortcut
{
    // 导航快捷键
    /// <summary>首页</summary>
    Home,
    /// <summary>仪表盘</summary>
    Dashboard,
    /// <summary>电影库</summary>
    Movies,
    /// <summary>语义搜索</summary>
    Search,
    /// <summary>AI对话</summary>
    Chat,
    /// <summary>观影报告</summary>
    Report,
    /// <summary>设置</summary>
    Settings,
    
    // 播放控制
    /// <summary>播放/暂停</summary>
    PlayPause,
    /// <summary>停止</summary>
    Stop,
    /// <summary>增加音量</summary>
    VolumeUp,
    /// <summary>降低音量</summary>
    VolumeDown,
    /// <summary>静音</summary>
    Mute,
    
    // 全局操作
    /// <summary>聚焦搜索框</summary>
    SearchFocus,
    /// <summary>刷新</summary>
    Refresh,
    /// <summary>切换主题</summary>
    ToggleTheme,
    
    /// <summary>退出</summary>
    Exit
}
