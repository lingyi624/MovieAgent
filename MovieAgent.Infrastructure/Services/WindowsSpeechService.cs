using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class WindowsSpeechService : ISpeechService
{
    private SpeechRecognitionEngine? _recognizer;
    private SpeechSynthesizer? _synthesizer;
    private bool _isListening;
    private Action<string>? _onResultCallback;
    private List<string> _recognizedTexts = new();
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            if (_isAvailable && _recognizer != null)
                return;

            var culture = new CultureInfo("zh-CN");
            var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers().ToList();
            
            if (!installedRecognizers.Any(r => r.Culture.Equals(culture)))
            {
                culture = new CultureInfo("en-US");
                if (!installedRecognizers.Any(r => r.Culture.Equals(culture)))
                {
                    _isAvailable = false;
                    return;
                }
            }

            _recognizer = new SpeechRecognitionEngine(culture);
            _recognizer.SetInputToDefaultAudioDevice();

            var grammarBuilder = new GrammarBuilder();
            grammarBuilder.AppendWildcard();
            var grammar = new Grammar(grammarBuilder);
            _recognizer.LoadGrammar(grammar);

            _recognizer.SpeechRecognized += OnSpeechRecognized;
            _recognizer.SpeechRecognitionRejected += OnSpeechRejected;

            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();

            _isAvailable = true;
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            System.Diagnostics.Debug.WriteLine($"Speech service init failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Speech rejected");
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (!_isListening || e.Result == null || string.IsNullOrWhiteSpace(e.Result.Text))
            return;

        var text = e.Result.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            _recognizedTexts.Add(text);
            _onResultCallback?.Invoke(text);
        }
    }

    public Task<string> RecognizeSpeechAsync(byte[] audioData, string language = "zh")
    {
        return Task.FromResult("暂不支持音频文件识别");
    }

    public async Task<string> RecognizeFromMicrophoneAsync(int durationMs = 5000)
    {
        _recognizedTexts.Clear();

        if (!_isAvailable || _recognizer == null)
            return "";

        return await Task.Run(() =>
        {
            var tcs = new TaskCompletionSource<string>();
            var wasAlreadyListening = _isListening;

            if (!wasAlreadyListening)
            {
                _isListening = true;
                _onResultCallback = null;
                
                try
                {
                    _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Start recognition failed: {ex.Message}");
                    return "";
                }
            }

            Task.Delay(durationMs).ContinueWith(_ =>
            {
                if (!wasAlreadyListening)
                {
                    try
                    {
                        _recognizer?.RecognizeAsyncStop();
                    }
                    catch { }
                    _isListening = false;
                }
                
                tcs.SetResult(string.Join(" ", _recognizedTexts));
            });

            return tcs.Task.Result;
        });
    }

    public Task<string> ListenAsync()
    {
        return RecognizeFromMicrophoneAsync(5000);
    }

    public async Task SpeakAsync(string text, string language = "zh")
    {
        try
        {
            if (_synthesizer == null)
            {
                _synthesizer = new SpeechSynthesizer();
                _synthesizer.SetOutputToDefaultAudioDevice();
            }

            await Task.Run(() =>
            {
                _synthesizer?.Speak(text);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Speak failed: {ex.Message}");
        }
    }

    public void StartListening(Action<string> onResult)
    {
        if (!_isAvailable || _recognizer == null)
            return;

        if (_isListening)
            return;

        _onResultCallback = onResult;
        _isListening = true;
        _recognizedTexts.Clear();

        try
        {
            _recognizer.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Start listening failed: {ex.Message}");
            _isListening = false;
        }
    }

    public void StopListening()
    {
        if (!_isListening)
            return;

        try
        {
            _recognizer?.RecognizeAsyncStop();
        }
        catch { }
        _isListening = false;
        _onResultCallback = null;
    }

    public void Dispose()
    {
        StopListening();
        _recognizer?.Dispose();
        _synthesizer?.Dispose();
    }
}