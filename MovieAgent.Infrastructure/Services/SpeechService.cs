using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class SpeechService : ISpeechService
{
    public bool IsAvailable { get; private set; }

    public Task InitializeAsync()
    {
        IsAvailable = false;
        return Task.CompletedTask;
    }

    public Task<string> RecognizeSpeechAsync(byte[] audioData, string language = "zh")
    {
        return Task.FromResult("语音识别功能暂不可用");
    }

    public Task<string> RecognizeFromMicrophoneAsync(int durationMs = 5000)
    {
        return Task.FromResult("语音识别功能暂不可用");
    }

    public Task SpeakAsync(string text, string language = "zh")
    {
        return Task.CompletedTask;
    }

    public void StartListening(Action<string> onResult)
    {
    }

    public void StopListening()
    {
    }
}