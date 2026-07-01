 
using MovieAgent.Controls;
 
using MovieAgent.FFmpegDecoder;
using NAudio.CoreAudioApi;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace MovieAgent.D3D11Window
{
    public partial class VideoWindow : Window
    {
        //[DllImport("user32.dll")]
        //private static extern int GetSystemMetrics(int nIndex);
        //[DllImport("user32.dll")]
        //private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        //    int X, int Y, int cx, int cy, uint uFlags);
        //private const int SM_CXSCREEN = 0;
        //private const int SM_CYSCREEN = 1;
        //private const uint SWP_FRAMECHANGED = 0x0020;
        //private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public VideoWindow()
        {
            InitializeComponent(); // 由 XAML 自动生成 
          
            this.Loaded += OnWindowLoaded; // 延迟初始化DXGI

        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var source = (HwndSource)PresentationSource.FromVisual(this);
            int w = (int)ActualWidth;
            int h = (int)ActualHeight;
         }

        
     
        private IntPtr hWnd;

        public unsafe nint Handle => (nint)hWnd;

        public SizeI ClientSize { get; internal set; }

        //private void Window_Loaded(object sender, RoutedEventArgs e)
        //{
        //    //int width = GetSystemMetrics(SM_CXSCREEN);
        //    //int height = GetSystemMetrics(SM_CYSCREEN);
        //    //IntPtr hwnd = new WindowInteropHelper(this).Handle;
        //    //SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, width, height, SWP_FRAMECHANGED);

        //    //this.Left = 0;
        //    //this.Top = 0;
        //    //this.Width = width;
        //    //this.Height = height;
        //    //this.UpdateLayout();

        //    // 触发渲染器初始化
        //  _ = VideoView.Handle;

        //    DebugLogger.WriteLine($"VideoWindow 全屏后实际大小: {this.ActualWidth}x{this.ActualHeight}");
        //} 

        public ID3D11Device? GetDevice()
        {
            return VideoView.GetDevice();
        }
        public void SetScaleMode(VideoScaleMode _videoScaleMode= VideoScaleMode.Zoom)
        {
            VideoView.SetScaleMode(_videoScaleMode); 
        }
        protected override void OnClosed(EventArgs e)
        {
           // VideoView.Dispose();
            base.OnClosed(e);
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}