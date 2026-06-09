namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 国际化服务接口 - 管理应用语言切换
/// 支持中文和英文
/// </summary>
public interface ILocalizationService
{
    /// <summary>当前语言</summary>
    Language CurrentLanguage { get; }

    /// <summary>语言变化事件</summary>
    event Action<Language>? LanguageChanged;

    /// <summary>
    /// 设置语言
    /// </summary>
    /// <param name="language">目标语言</param>
    Task SetLanguageAsync(Language language);

    /// <summary>加载保存的语言设置</summary>
    Task LoadLanguageAsync();

    /// <summary>
    /// 翻译文本
    /// </summary>
    /// <param name="key">翻译键</param>
    /// <returns>翻译后的文本</returns>
    string Translate(string key);

    /// <summary>
    /// 翻译文本（带参数）
    /// </summary>
    /// <param name="key">翻译键</param>
    /// <param name="args">格式化参数</param>
    /// <returns>翻译后的文本</returns>
    string Translate(string key, params object[] args);
}

/// <summary>
/// 支持的语言枚举
/// </summary>
public enum Language
{
    /// <summary>中文</summary>
    Chinese,
    /// <summary>英文</summary>
    English
}
