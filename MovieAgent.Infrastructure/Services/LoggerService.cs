using MovieAgent.Core.Interfaces;
using Serilog;
using System.IO;

namespace MovieAgent.Infrastructure.Services;

public class LoggerService : ILoggerService
{
    private readonly ILogger _logger;

    public LoggerService()
    {
        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "movieagent-.log");
        
        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();
    }

    public void Debug(string message, params object[] args)
    {
        _logger.Debug(message, args);
    }

    public void Information(string message, params object[] args)
    {
        _logger.Information(message, args);
    }

    public void Warning(string message, params object[] args)
    {
        _logger.Warning(message, args);
    }

    public void Error(string message, params object[] args)
    {
        _logger.Error(message, args);
    }

    public void Error(Exception exception, string message, params object[] args)
    {
        _logger.Error(exception, message, args);
    }

    public void Critical(string message, params object[] args)
    {
        _logger.Fatal(message, args);
    }

    public void Critical(Exception exception, string message, params object[] args)
    {
        _logger.Fatal(exception, message, args);
    }
}