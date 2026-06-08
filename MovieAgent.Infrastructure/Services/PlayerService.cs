using System.Diagnostics;
using System.IO;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class PlayerService : IPlayerService
{
    private Process? _process;
    private readonly string _userPlayerPath;
    private string? _vlcPath;

    public bool IsPlaying => _process != null && !_process.HasExited;
    public bool IsPaused => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public TimeSpan Position => TimeSpan.Zero;
    public float Volume => 0;
    
    public int AudioTrackCount => 0;
    public int CurrentAudioTrack => -1;
    public int SpuTrackCount => 0;
    public int CurrentSpuTrack => -1;

    public PlayerService(string userPlayerPath)
    {
        _userPlayerPath = userPlayerPath;
        _vlcPath = FindVlcPlayer();
    }

    private string? FindVlcPlayer()
    {
        var possiblePaths = new[]
        {
            @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
            Environment.GetEnvironmentVariable("VLC_PATH")
        };
        
        foreach (var path in possiblePaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }
        return null;
    }

    public async Task PlayAsync(string filePath)
    {
        Stop();
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Movie file not found: {filePath}");

        if (!string.IsNullOrWhiteSpace(_vlcPath))
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = _vlcPath,
                Arguments = $"\"{filePath}\" --fullscreen",
                UseShellExecute = true
            });
            Debug.WriteLine("[Player] Playing via VLC executable");
        }
        else if (!string.IsNullOrWhiteSpace(_userPlayerPath) && File.Exists(_userPlayerPath))
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = _userPlayerPath,
                Arguments = $"\"{filePath}\"",
                UseShellExecute = true
            });
            Debug.WriteLine("[Player] Playing via user specified player");
        }
        else
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
            Debug.WriteLine("[Player] Playing via system default");
        }
        await Task.CompletedTask;
    }

    public void Stop()
    {
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(); } catch { }
            _process.Dispose();
        }
        _process = null;
    }

    public void Pause() { }
    public void Resume() { }
    public void SetVolume(int volume) { }
    public void Seek(int position) { }
    public void Next() { }
    public void Previous() { }
    public void ToggleFullscreen() { }
    public void SetAudioTrack(int trackIndex) { }
    public void SetSpuTrack(int trackIndex) { }
}