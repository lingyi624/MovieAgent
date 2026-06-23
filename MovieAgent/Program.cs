using System;
using System.IO;
using System.Windows;

namespace MovieAgent
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // 这是应用程序的第一个入口点，必须首先写入日志
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
                File.WriteAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 应用程序入口点启动\r\n");
                Console.WriteLine("[Program] 入口点启动");
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(@"C:\temp\movieagent_entry_error.log", $"[{DateTime.Now}] 入口点日志写入失败: {ex.Message}\r\n"); } catch { }
                return -1;
            }

            try
            {
                Console.WriteLine("[Program] 创建 App 实例...");
                var app = new App();
                
                // 添加异常处理
                app.DispatcherUnhandledException += (sender, e) => 
                {
                    // 忽略 Blazor WebView 的事件追踪错误，让应用程序继续运行
                    if (e.Exception?.InnerException?.Message?.Contains("Event") == true && 
                        e.Exception?.InnerException?.Message?.Contains("already tracked") == true)
                    {
                        LogException("Blazor事件追踪警告(已忽略)", e.Exception);
                        e.Handled = true;
                        return;
                    }
                    LogException("Dispatcher异常", e.Exception);
                    e.Handled = true;
                };
                
                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                {
                    var ex = e.ExceptionObject as Exception;
                    // 忽略 Blazor WebView 的事件追踪错误
                    if (ex?.InnerException?.Message?.Contains("Event") == true && 
                        ex?.InnerException?.Message?.Contains("already tracked") == true)
                    {
                        LogException("Blazor事件追踪警告(已忽略)", ex);
                        return;
                    }
                    LogException("未处理的异常", ex);
                };
                
                Console.WriteLine("[Program] 调用 app.Run()...");
                app.Run();
                Console.WriteLine("[Program] app.Run() 完成");
                return 0;
            }
            catch (Exception ex)
            {
                LogException("应用程序运行异常", ex);
                return -1;
            }
        }
        
        private static void LogException(string title, Exception? ex)
        {
            if (ex == null) return;
            
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
                string innerExMsg = "";
                Exception? inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    innerExMsg += $"\r\n内部异常 {depth + 1}: {inner.Message}\r\n{inner.StackTrace}";
                    inner = inner.InnerException;
                    depth++;
                }
                
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {title}: {ex.Message}\r\n堆栈: {ex.StackTrace}{innerExMsg}\r\n");
                Console.WriteLine($"{title}: {ex.Message}\r\n堆栈: {ex.StackTrace}{innerExMsg}");
            }
            catch { }
        }
    }
}