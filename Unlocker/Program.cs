using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FileUnlocker
{
    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeleteFile(string lpFileName);

        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint OPEN_EXISTING = 3;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        static void Main(string[] args)
        {
            string filePath = @"D:\study\.net core\MovieAgent\MovieAgent.Core\obj\Debug\net10.0\MovieAgent.Core.dll";
            
            Console.WriteLine($"Attempting to unlock: {filePath}");

            // Try to open with exclusive access to identify locks
            IntPtr handle = CreateFile(
                filePath,
                GENERIC_WRITE,
                0, // No sharing
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle != IntPtr.Zero && handle != (IntPtr)(-1))
            {
                Console.WriteLine("File is not locked, deleting...");
                CloseHandle(handle);
                if (DeleteFile(filePath))
                {
                    Console.WriteLine("File deleted successfully");
                }
                else
                {
                    Console.WriteLine($"Failed to delete, error: {Marshal.GetLastWin32Error()}");
                }
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                Console.WriteLine($"File is locked, error: {error}");
                Console.WriteLine("Try closing Visual Studio or any running debugger and retry");
            }
        }
    }
}
