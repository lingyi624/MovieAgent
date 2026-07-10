using System.Diagnostics;
using System.IO;
using System.Text;
using LLama;
using LLama.Common;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Providers;

public class LlamaCppProvider : IChatProvider
{
    private readonly string _modelPath;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private bool _isAvailable;
    private string? _lastError;

    private const string DefaultModelPath = @"D:\ollama\llama-b9895-bin-win-cuda-12.4-x64\models\Qwen2.5-3B-Instruct-Q4_K_M.gguf";

    public string Name => "LlamaCpp";
    public string ProviderType => "LlamaCpp";
    public bool IsAvailable => _isAvailable;
    public string? LastError => _lastError;

    public event Action<string>? OnStreamDataReceived;

    public LlamaCppProvider(string modelPath = "")
    {
        _modelPath = string.IsNullOrEmpty(modelPath) ? DefaultModelPath : modelPath;
    }

    public async Task<bool> InitializeAsync()
    {
        _isAvailable = false;
        _lastError = null;

        try
        {
            if (!File.Exists(_modelPath))
            {
                _lastError = $"模型文件不存在: {_modelPath}";
                return false;
            }

            await Task.Run(() =>
            {
                try
                {
                    var parameters = new ModelParams(_modelPath)
                    {
                        ContextSize = 4096,
                        Seed = 1337,
                        GpuLayerCount = 999,
                        Threads = 8,
                    };

                    _model = LLamaWeights.LoadFromFile(parameters);
                    _context = _model.CreateContext(parameters);

                    _isAvailable = true;
                    Debug.WriteLine($"[LlamaCpp] 模型加载成功: {_modelPath}");
                }
                catch (Exception ex)
                {
                    _lastError = $"模型加载失败: {ex.Message}";
                    Debug.WriteLine($"[LlamaCpp] 模型加载失败: {ex.Message}");
                }
            });

            return _isAvailable;
        }
        catch (Exception ex)
        {
            _lastError = $"初始化失败: {ex.Message}";
            return false;
        }
    }

    public async Task<string> ChatAsync(string userMessage)
    {
        if (!_isAvailable || _context == null)
        {
            return "AI服务未就绪，请稍后重试";
        }

        try
        {
            var result = new StringBuilder();
            
            var prompt = $"<|im_start|>user\n{userMessage}<|im_end|>\n<|im_start|>assistant\n";

            var executor = new InteractiveExecutor(_context);

            var settings = new InferenceParams
            {
                Temperature = 0.7f,
                MaxTokens = 2048,
            };

            await foreach (var text in executor.InferAsync(prompt, settings))
            {
                if (text.Contains("<|im_end|>"))
                    break;
                
                result.Append(text);
                OnStreamDataReceived?.Invoke(text);
                await Task.Yield();
            }

            return result.ToString().Trim();
        }
        catch (Exception ex)
        {
            _lastError = $"聊天错误: {ex.Message}";
            return $"AI 响应出错: {ex.Message}";
        }
    }
}
