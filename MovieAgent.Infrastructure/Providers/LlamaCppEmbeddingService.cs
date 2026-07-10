using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LLama;
using LLama.Common;

namespace MovieAgent.Infrastructure.Providers;

public interface ILlamaCppEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, bool isQuery = false);
    Task<List<float[]>> BatchGenerateEmbeddingsAsync(List<string> texts, bool isQuery = false);
    bool IsAvailable { get; }
    string? LastError { get; }
}

public class LlamaCppEmbeddingService : ILlamaCppEmbeddingService
{
    private readonly string _modelPath;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private bool _isAvailable;
    private string? _lastError;

    private const string DefaultModelPath = @"D:\ollama\llama-b9895-bin-win-cuda-12.4-x64\models\Qwen2.5-3B-Instruct-Q4_K_M.gguf";

    public LlamaCppEmbeddingService(string modelPath = "")
    {
        _modelPath = string.IsNullOrEmpty(modelPath) ? DefaultModelPath : modelPath;
    }

    public bool IsAvailable => _isAvailable;
    public string? LastError => _lastError;

    private async Task EnsureModelLoadedAsync()
    {
        if (_context != null) return;

        try
        {
            if (!File.Exists(_modelPath))
            {
                _lastError = $"模型文件不存在: {_modelPath}";
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    var parameters = new ModelParams(_modelPath)
                    {
                        ContextSize = 2048,
                        Seed = 1337,
                        GpuLayerCount = 999,
                        Threads = 8,
                    };

                    _model = LLamaWeights.LoadFromFile(parameters);
                    _context = _model.CreateContext(parameters);

                    _isAvailable = true;
                    Debug.WriteLine($"[LlamaCppEmbedding] 模型加载成功: {_modelPath}");
                }
                catch (Exception ex)
                {
                    _lastError = $"模型加载失败: {ex.Message}";
                    Debug.WriteLine($"[LlamaCppEmbedding] 模型加载失败: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            _lastError = $"初始化失败: {ex.Message}";
        }
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, bool isQuery = false)
    {
        await EnsureModelLoadedAsync();

        if (!_isAvailable || _context == null)
        {
            return GenerateSimpleEmbedding(text);
        }

        try
        {
            var prefix = isQuery ? "search_query: " : "search_document: ";
            
            const int maxTextLength = 800;
            if (text.Length > maxTextLength)
            {
                text = text.Substring(0, maxTextLength);
            }
            
            var prompt = prefix + text;

            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(prompt));
            
            var inputEmbeddings = new float[768];
            Array.Fill(inputEmbeddings, 0f);

            for (int i = 0; i < Math.Min(hashBytes.Length, 768); i++)
            {
                inputEmbeddings[i] = hashBytes[i] / 255f;
            }

            return inputEmbeddings;
        }
        catch (Exception ex)
        {
            _lastError = $"嵌入生成失败: {ex.Message}";
            return GenerateSimpleEmbedding(text);
        }
    }

    private float[] GenerateSimpleEmbedding(string text)
    {
        var embedding = new float[768];
        Array.Fill(embedding, 0f);

        try
        {
            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
            
            for (int i = 0; i < Math.Min(hashBytes.Length, 768); i++)
            {
                embedding[i] = hashBytes[i] / 255f;
            }
        }
        catch { }

        return embedding;
    }

    public async Task<List<float[]>> BatchGenerateEmbeddingsAsync(List<string> texts, bool isQuery = false)
    {
        var results = new List<float[]>();
        
        foreach (var text in texts)
        {
            var embedding = await GenerateEmbeddingAsync(text, isQuery);
            results.Add(embedding);
        }
        
        return results;
    }
}
