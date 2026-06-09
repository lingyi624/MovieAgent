using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Services;

namespace MovieAgent.Controls;

public class VideoRenderer : System.Windows.Controls.Image
{
    private static readonly ILoggerService _logger = new LoggerService();
    
    private WriteableBitmap? _writeableBitmap;
    private int _lastWidth;
    private int _lastHeight;
    private readonly object _lockObj = new();
    private byte[]? _pendingFrame;
    private int _pendingWidth;
    private int _pendingHeight;
    private Timer? _renderTimer;
    private bool _isRendering;
    
    private string? _currentSubtitle;
    private readonly object _subtitleLock = new();

    public VideoRenderer()
    {
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.Both;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Visibility = Visibility.Visible;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.Debug("[VideoRenderer] 控件已加载");
        _renderTimer = new Timer(RenderPendingFrame, null, 33, 33);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer?.Dispose();
        _renderTimer = null;
    }

    public void UpdateFrame(byte[] frameData, int width, int height)
    {
        if (frameData == null || frameData.Length == 0 || width <= 0 || height <= 0)
            return;

        lock (_lockObj)
        {
            _pendingFrame = new byte[frameData.Length];
            Buffer.BlockCopy(frameData, 0, _pendingFrame, 0, frameData.Length);
            _pendingWidth = width;
            _pendingHeight = height;
        }
    }

    public void SetSubtitle(string? subtitle)
    {
        lock (_subtitleLock)
        {
            _currentSubtitle = subtitle;
        }
    }

    private void RenderPendingFrame(object? state)
    {
        if (_isRendering) return;
        
        byte[]? frameData;
        int width, height;

        lock (_lockObj)
        {
            if (_pendingFrame == null) return;
            frameData = _pendingFrame;
            width = _pendingWidth;
            height = _pendingHeight;
            _pendingFrame = null;
        }

        _isRendering = true;
        try
        {
            if (!CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateFrameInternal(frameData!, width, height));
            }
            else
            {
                UpdateFrameInternal(frameData!, width, height);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[VideoRenderer] RenderPendingFrame 失败: {ex.Message}");
        }
        finally
        {
            _isRendering = false;
        }
    }

    private void UpdateFrameInternal(byte[] frameData, int width, int height)
    {
        try
        {
            // 如果尺寸变了，重新创建 WriteableBitmap
            if (_writeableBitmap == null || _lastWidth != width || _lastHeight != height)
            {
                _writeableBitmap = new WriteableBitmap(
                    width,
                    height,
                    96, 96,
                    PixelFormats.Bgr24,
                    null);

                _lastWidth = width;
                _lastHeight = height;
                Source = _writeableBitmap;
                _logger.Debug($"[VideoRenderer] 创建 WriteableBitmap: {width}x{height}");
            }

            int stride = width * 3; // BGR24

            _writeableBitmap.Lock();

            try
            {
                IntPtr pBackBuffer = _writeableBitmap.BackBuffer;

                // 直接逐行复制
                for (int y = 0; y < height; y++)
                {
                    int srcOffset = y * stride;
                    IntPtr dstLine = pBackBuffer + y * _writeableBitmap.BackBufferStride;

                    if (srcOffset + stride <= frameData.Length)
                    {
                        Marshal.Copy(frameData, srcOffset, dstLine, stride);
                    }
                }

                _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                _writeableBitmap.Unlock();
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[VideoRenderer] UpdateFrameInternal 失败: {ex.Message}");
            try { _writeableBitmap?.Unlock(); } catch { }
        }
    }

    public void Clear()
    {
        try
        {
            lock (_lockObj)
            {
                _pendingFrame = null;
            }
            
            lock (_subtitleLock)
            {
                _currentSubtitle = null;
            }

            if (!CheckAccess())
            {
                Dispatcher.Invoke(() =>
                {
                    Source = null;
                    _writeableBitmap = null;
                    _lastWidth = 0;
                    _lastHeight = 0;
                });
            }
            else
            {
                Source = null;
                _writeableBitmap = null;
                _lastWidth = 0;
                _lastHeight = 0;
            }
        }
        catch { }
    }

    public void ClearSubtitle()
    {
        lock (_subtitleLock)
        {
            _currentSubtitle = null;
        }
    }
}
