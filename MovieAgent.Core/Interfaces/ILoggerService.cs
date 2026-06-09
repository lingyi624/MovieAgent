namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 日志服务接口 - 提供统一的日志记录功能
/// 使用 Serilog 实现，支持控制台和文件输出
/// </summary>
public interface ILoggerService
{
    /// <summary>调试日志</summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Debug(string message, params object[] args);

    /// <summary>信息日志</summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Information(string message, params object[] args);

    /// <summary>警告日志</summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Warning(string message, params object[] args);

    /// <summary>错误日志</summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Error(string message, params object[] args);

    /// <summary>错误日志（带异常）</summary>
    /// <param name="exception">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Error(Exception exception, string message, params object[] args);

    /// <summary>严重错误日志</summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Critical(string message, params object[] args);

    /// <summary>严重错误日志（带异常）</summary>
    /// <param name="exception">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Critical(Exception exception, string message, params object[] args);
}
