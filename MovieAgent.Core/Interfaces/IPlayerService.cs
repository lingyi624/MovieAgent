namespace MovieAgent.Core.Interfaces;

public interface IPlayerService
{
    bool IsPlaying { get; }
    bool IsPaused { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    float Volume { get; }
    
    Task PlayAsync(string filePath);
    void Stop();
    void Pause();
    void Resume();
    void SetVolume(int volume);
    void Seek(int position);
    void Next();
    void Previous();
    void ToggleFullscreen();
    
    int AudioTrackCount { get; }
    int CurrentAudioTrack { get; }
    void SetAudioTrack(int trackIndex);
    
    int SpuTrackCount { get; }
    int CurrentSpuTrack { get; }
    void SetSpuTrack(int trackIndex);
}