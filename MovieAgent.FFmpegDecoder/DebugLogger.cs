using System;
using System.Collections.Generic;
using System.Text;
using System;
using System.IO;
using System.Runtime.CompilerServices;
namespace MovieAgent.FFmpegDecoder
{ 
        /// <summary>
        /// 调试日志类 - 用于记录跨进程调试信息
        /// </summary>
        public static class DebugLogger
        {
            private static string _logDirectory;
            private static readonly object _lock = new object();
            private static string _currentLogPath;

        /// <summary>
        /// 初始化日志（可选，不调用则使用默认临时目录）
        /// </summary>
        /// <param name="logDirectory">日志目录，默认使用系统临时目录</param>
        public static void Initialize(string logDirectory = null)
        {
            _logDirectory = logDirectory ?? Path.GetTempPath();
            _logDirectory = Path.Combine(_logDirectory, "logs");
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
            var prefix = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var pid = Environment.ProcessId;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _currentLogPath = Path.Combine(_logDirectory, $"{prefix}_{pid}_{timestamp}.log");
           
            WriteLine($"=== 日志初始化 ===");
            WriteLine($"进程: {prefix}, PID: {pid}");
            WriteLine($"启动时间: {DateTime.Now}");

        }

        /// <summary>
        /// 写入一行日志
        /// </summary>
        public static void WriteLine(string message)
            {
                if (string.IsNullOrEmpty(_currentLogPath))
                {
                    Initialize();
                }

                lock (_lock)
                {
                    try
                    {
                        var logEntry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                        File.AppendAllText(_currentLogPath, logEntry + Environment.NewLine);

                        // 同时输出到控制台（如果存在）
                        Console.WriteLine(logEntry);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"日志写入失败: {ex.Message}");
                    }
                }
            }

            /// <summary>
            /// 写入带调用者信息的日志
            /// </summary>
            public static void WriteLineWithCaller(
                string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string filePath = "",
                [CallerLineNumber] int lineNumber = 0)
            {
                var callerInfo = $"{Path.GetFileName(filePath)}.{memberName}:{lineNumber}";
                WriteLine($"[{callerInfo}] {message}");
            }

            /// <summary>
            /// 写入参数列表
            /// </summary>
            public static void WriteArgs(string[] args)
            {
                WriteLine($"参数个数: {args.Length}");
                for (int i = 0; i < args.Length; i++)
                {
                    WriteLine($"  args[{i}] = \"{args[i]}\"");
                }
            }

            /// <summary>
            /// 写入异常信息
            /// </summary>
            public static void WriteException(Exception ex, string context = "")
            {
                WriteLine($"异常: {context}");
                WriteLine($"  消息: {ex.Message}");
                WriteLine($"  堆栈: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    WriteLine($"  内部异常: {ex.InnerException.Message}");
                }
            }

            /// <summary>
            /// 获取当前日志文件路径
            /// </summary>
            public static string GetLogPath() => _currentLogPath;

            /// <summary>
            /// 写入分隔线
            /// </summary>
            public static void WriteSeparator(char ch = '=', int count = 50)
            {
                WriteLine(new string(ch, count));
            }
        }
    
}
