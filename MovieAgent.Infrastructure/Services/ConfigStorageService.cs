using System.IO;
using System.Text.Json;

namespace MovieAgent.Infrastructure.Services;

public interface IConfigStorageService
{
    Task<T?> GetConfigAsync<T>(string key);
    Task SetConfigAsync<T>(string key, T value);
    Task DeleteConfigAsync(string key);
}

public class ConfigStorageService : IConfigStorageService
{
    private readonly string _configDirectory;
    private readonly JsonSerializerOptions _options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ConfigStorageService(string configDirectory)
    {
        _configDirectory = configDirectory;
        Directory.CreateDirectory(configDirectory);
    }

    public async Task<T?> GetConfigAsync<T>(string key)
    {
        var filePath = GetFilePath(key);
        if (!File.Exists(filePath))
            return default;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<T>(json, _options);
        }
        catch
        {
            return default;
        }
    }

    public async Task SetConfigAsync<T>(string key, T value)
    {
        var filePath = GetFilePath(key);
        var json = JsonSerializer.Serialize(value, _options);
        await File.WriteAllTextAsync(filePath, json);
    }

    public Task DeleteConfigAsync(string key)
    {
        var filePath = GetFilePath(key);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }

    private string GetFilePath(string key)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeKey = key;
        foreach (var c in invalidChars)
        {
            safeKey = safeKey.Replace(c, '_');
        }
        return Path.Combine(_configDirectory, $"{safeKey}.json");
    }
}