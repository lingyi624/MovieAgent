namespace MovieAgent.Core.Interfaces;

public interface ISpeechService
{
    Task InitializeAsync();
    
    bool IsAvailable { get; }
    
    Task<string> RecognizeSpeechAsync(byte[] audioData, string language = "zh");
    
    Task<string> RecognizeFromMicrophoneAsync(int durationMs = 5000);
    
    Task SpeakAsync(string text, string language = "zh");
    
    void StartListening(Action<string> onResult);
    
    void StopListening();
}