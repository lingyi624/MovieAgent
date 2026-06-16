using System.IO;

namespace MovieAgent.Infrastructure.Services;

public interface IBackupService
{
    Task CreateBackupAsync(string backupPath = null);
    Task<List<string>> GetBackupHistoryAsync();
    Task RestoreFromBackupAsync(string backupFilePath);
    Task CleanupOldBackupsAsync(int keepDays = 7);
}

public class BackupService : IBackupService
{
    private readonly string _databasePath;
    private readonly string _vectorDbPath;
    private readonly string _backupDirectory;

    public BackupService(string databasePath, string vectorDbPath, string backupDirectory)
    {
        _databasePath = databasePath;
        _vectorDbPath = vectorDbPath;
        _backupDirectory = backupDirectory;
        Directory.CreateDirectory(backupDirectory);
    }

    public async Task CreateBackupAsync(string backupPath = null)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupDir = backupPath ?? Path.Combine(_backupDirectory, timestamp);
        
        Directory.CreateDirectory(backupDir);

        if (File.Exists(_databasePath))
        {
            var dbBackupPath = Path.Combine(backupDir, "movies.db");
            File.Copy(_databasePath, dbBackupPath, overwrite: true);
        }

        if (Directory.Exists(_vectorDbPath))
        {
            var vectorBackupPath = Path.Combine(backupDir, "lancedb");
            CopyDirectory(_vectorDbPath, vectorBackupPath);
        }

        await Task.CompletedTask;
    }

    public Task<List<string>> GetBackupHistoryAsync()
    {
        var backups = new List<string>();
        
        if (Directory.Exists(_backupDirectory))
        {
            var directories = Directory.GetDirectories(_backupDirectory);
            foreach (var dir in directories)
            {
                var name = Path.GetFileName(dir);
                if (DateTime.TryParseExact(name, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var date))
                {
                    backups.Add(dir);
                }
            }
        }

        backups.Sort((a, b) => b.CompareTo(a));
        return Task.FromResult(backups);
    }

    public async Task RestoreFromBackupAsync(string backupFilePath)
    {
        if (!Directory.Exists(backupFilePath))
            throw new DirectoryNotFoundException($"备份目录不存在: {backupFilePath}");

        var dbBackupPath = Path.Combine(backupFilePath, "movies.db");
        var vectorBackupPath = Path.Combine(backupFilePath, "lancedb");

        if (File.Exists(dbBackupPath))
        {
            File.Copy(dbBackupPath, _databasePath, overwrite: true);
        }

        if (Directory.Exists(vectorBackupPath))
        {
            if (Directory.Exists(_vectorDbPath))
            {
                Directory.Delete(_vectorDbPath, recursive: true);
            }
            CopyDirectory(vectorBackupPath, _vectorDbPath);
        }

        await Task.CompletedTask;
    }

    public async Task CleanupOldBackupsAsync(int keepDays = 7)
    {
        if (!Directory.Exists(_backupDirectory))
            return;

        var cutoffDate = DateTime.Now.AddDays(-keepDays);
        var backups = await GetBackupHistoryAsync();

        foreach (var backup in backups)
        {
            var name = Path.GetFileName(backup);
            if (DateTime.TryParseExact(name, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var date))
            {
                if (date < cutoffDate)
                {
                    Directory.Delete(backup, recursive: true);
                }
            }
        }

        await Task.CompletedTask;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }
}