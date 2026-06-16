using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Xml.Linq;
using static MovieAgent.FFmpegDecoder.FFmpegDecoderEngine;
 

namespace MovieAgent.FFmpegDecoder
{
    class Program
    {
        // 关闭所有 ffmpeg.exe 进程
        private static void KillAllFFmpegProcesses()
        {
            var processes = Process.GetProcessesByName("MovieAgent.FFmpegDecoder");
            foreach (var process in processes)
            {
                try
                {
                    // 先尝试友好关闭
                    process.StandardInput.Write("q");
                    process.WaitForExit(2000);

                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit();
                    }
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"Failed to kill FFmpeg process: {ex.Message}");
                }
            }
        }
        static async Task Main(string[] args)
        { 
            DebugLogger.Initialize(AppContext.BaseDirectory);
           // KillAllFFmpegProcesses();

            if (args.Length == 0)
            {
                DebugLogger.WriteLine("Usage: MovieAgent.FFmpegDecoder --pipe-name <pipe-name>");
                Environment.Exit(1);
                return;
            }
            string pipeName = string.Empty;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--pipe-name" && i + 1 < args.Length)
                {
                    pipeName = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrEmpty(pipeName))
            {
                DebugLogger.WriteLine("Error: Pipe name not specified");
                Environment.Exit(1);
                return;
            }
            //获取显卡信息
            
            FFmpegDecoderEngine decoder = new FFmpegDecoderEngine(DecodeMode.Auto);  
            DebugLogger.WriteLine($"[Initialize FFmpegDecoder Main DecodeMode:] {decoder.CurrentDecodeMode}");
            var ipcServer = new DecoderIpcServer(pipeName, decoder);

            try
            {
                await ipcServer.RunAsync();
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"Initialize Decoder error: {ex.Message}");
                Environment.Exit(1);
            }
        }
    }
}