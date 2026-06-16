using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;

namespace MovieAgent.FFmpegDecoder
{

    internal static class FFmpegHelper
    {
        public static unsafe string av_strerror(int error)
        {
            var bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
            var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
            DebugLogger.WriteLine(message);
            return message;
        }

        public static int ThrowExceptionIfError(this int error, string v)
        {
            DebugLogger.WriteLine(v);
            if (error < 0) throw new ApplicationException(av_strerror(error));
            return error;
        }
    }
}
