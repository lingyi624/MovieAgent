using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class CompositePlayerService : IPlayerService
{
    private readonly string _externalPlayerPath;
    private bool _isPlaying;

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public TimeSpan Position => TimeSpan.Zero;
    public float Volume => 0;

    public int AudioTrackCount => 0;
    public int CurrentAudioTrack => -1;
    public int SpuTrackCount => 0;
    public int CurrentSpuTrack => -1;

    public event EventHandler<byte[]>? FrameUpdated
    {
        add { }
        remove { }
    }

    public CompositePlayerService(string externalPlayerPath)
    {
        _externalPlayerPath = externalPlayerPath;
    }

    public async Task PlayAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Movie file not found: {filePath}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                if (TryInvokeWpfMethod("PlayMovie", filePath))
                {
                    _isPlaying = true;
                    Debug.WriteLine($"[CompositePlayer] Playing via WPF FFmpeg.AutoGen: {filePath}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CompositePlayer] WPF player failed: {ex.Message}, falling back to external player");
            }
        }

        await PlayExternal(filePath);
    }

    private bool TryInvokeWpfMethod(string methodName, string? arg = null)
    {
        try
        {
            var app = Application.Current;
            if (app == null) 
            {
                Debug.WriteLine($"[CompositePlayer] Application.Current is null");
                return false;
            }

            var window = app.MainWindow;
            if (window == null) 
            {
                Debug.WriteLine($"[CompositePlayer] MainWindow is null");
                return false;
            }

            // 检查窗口是否已关闭
            if (!window.IsLoaded)
            {
                Debug.WriteLine($"[CompositePlayer] MainWindow is not loaded");
                return false;
            }

            var parameterTypes = arg != null 
                ? new[] { typeof(string) } 
                : Type.EmptyTypes;
            
            var method = window.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase,
                null, parameterTypes, null);
            
            if (method == null) 
            {
                Debug.WriteLine($"[CompositePlayer] Method not found: {methodName}");
                return false;
            }

            object[]? parameters = arg != null ? new object[] { arg } : null;

            if (window.Dispatcher.CheckAccess())
            {
                method.Invoke(window, parameters);
            }
            else
            {
                window.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        method.Invoke(window, parameters);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CompositePlayer] Dispatcher invoke failed: {ex.Message}");
                    }
                });
            }
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // 窗口已关闭或 Dispatcher 已终止
            Debug.WriteLine($"[CompositePlayer] InvalidOperationException in {methodName}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompositePlayer] WPF method invoke failed: {methodName} - {ex.Message}");
            return false;
        }
    }

    private async Task PlayExternal(string filePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_externalPlayerPath) && File.Exists(_externalPlayerPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _externalPlayerPath,
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = true
                });
                Debug.WriteLine("[CompositePlayer] Playing via user specified player");
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
                Debug.WriteLine("[CompositePlayer] Playing via system default player");
            }
            _isPlaying = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompositePlayer] External play failed: {ex.Message}");
            throw;
        }

        await Task.CompletedTask;
    }

    public void Stop()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryInvokeWpfMethod("StopPlayback");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompositePlayer] Stop failed: {ex.Message}");
        }
        _isPlaying = false;
    }

    public void Pause()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryInvokeWpfMethod("PausePlayback");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompositePlayer] Pause failed: {ex.Message}");
        }
    }

    public void Resume()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryInvokeWpfMethod("ResumePlayback");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompositePlayer] Resume failed: {ex.Message}");
        }
    }

    public void SetVolume(int volume)
    {
    }

    public void Seek(int position)
    {
    }

    public void Next()
    {
    }

    public void Previous()
    {
    }

    public void ToggleFullscreen()
    {
    }

    public void SetAudioTrack(int trackIndex)
    {
    }

    public void SetSpuTrack(int trackIndex)
    {
    }
}
