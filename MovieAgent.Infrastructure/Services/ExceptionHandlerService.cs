using MovieAgent.Core.Interfaces;
using System.Diagnostics;

namespace MovieAgent.Infrastructure.Services;

public interface IExceptionHandlerService
{
    Task HandleExceptionAsync(Exception ex, string context = "");
    Task HandleExceptionAsync(Exception ex, string context, params object[] args);
    void HandleSyncException(Exception ex, string context = "");
}

public class ExceptionHandlerService : IExceptionHandlerService
{
    private readonly ILoggerService _logger;

    public ExceptionHandlerService(ILoggerService logger)
    {
        _logger = logger;
    }

    public async Task HandleExceptionAsync(Exception ex, string context = "")
    {
        await HandleExceptionInternalAsync(ex, context, Array.Empty<object>());
    }

    public async Task HandleExceptionAsync(Exception ex, string context, params object[] args)
    {
        await HandleExceptionInternalAsync(ex, context, args);
    }

    public void HandleSyncException(Exception ex, string context = "")
    {
        HandleExceptionInternal(ex, context, Array.Empty<object>());
    }

    private async Task HandleExceptionInternalAsync(Exception ex, string context, object[] args)
    {
        await Task.Run(() => HandleExceptionInternal(ex, context, args));
    }

    private void HandleExceptionInternal(Exception ex, string context, object[] args)
    {
        try
        {
            var fullContext = string.Format(context, args);
            
            _logger.Error(ex, $"Exception occurred in '{fullContext}'");

            Debug.WriteLine($"[Exception] Context: {fullContext}");
            Debug.WriteLine($"[Exception] Message: {ex.Message}");
            Debug.WriteLine($"[Exception] StackTrace: {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                _logger.Error(ex.InnerException, $"Inner exception in '{fullContext}'");
                Debug.WriteLine($"[Exception] Inner Message: {ex.InnerException.Message}");
                Debug.WriteLine($"[Exception] Inner StackTrace: {ex.InnerException.StackTrace}");
            }
        }
        catch (Exception handlerEx)
        {
            Debug.WriteLine($"[Exception Handler Error] {handlerEx.Message}");
        }
    }
}