using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class SimpleLogger : ILoggerService
{
    public void Debug(string message, params object[] args)
    {
        Console.WriteLine($"[DEBUG] {string.Format(message, args)}");
    }

    public void Information(string message, params object[] args)
    {
        Console.WriteLine($"[INFO] {string.Format(message, args)}");
    }

    public void Warning(string message, params object[] args)
    {
        Console.WriteLine($"[WARN] {string.Format(message, args)}");
    }

    public void Error(string message, params object[] args)
    {
        Console.WriteLine($"[ERROR] {string.Format(message, args)}");
    }

    public void Error(Exception exception, string message, params object[] args)
    {
        Console.WriteLine($"[ERROR] {string.Format(message, args)} - {exception}");
    }

    public void Critical(string message, params object[] args)
    {
        Console.WriteLine($"[CRITICAL] {string.Format(message, args)}");
    }

    public void Critical(Exception exception, string message, params object[] args)
    {
        Console.WriteLine($"[CRITICAL] {string.Format(message, args)} - {exception}");
    }
}