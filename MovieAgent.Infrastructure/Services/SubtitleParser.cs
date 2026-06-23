using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class SubtitleItem
{
    public int Index { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public List<string> Lines { get; set; } = new();
    
    public string Text => string.Join("\n", Lines);
    
    public bool IsActive(TimeSpan currentTime)
    {
        return currentTime >= StartTime && currentTime <= EndTime;
    }
}

public static class SubtitleParser
{
    private static ILoggerService? _logger;
    private static readonly object _loggerLock = new object();
    
    private static ILoggerService Logger
    {
        get
        {
            if (_logger == null)
            {
                lock (_loggerLock)
                {
                    if (_logger == null)
                    {
                        try
                        {
                            _logger = new LoggerService();
                        }
                        catch
                        {
                            _logger = new SimpleLogger();
                        }
                    }
                }
            }
            return _logger;
        }
    }
    
    private static readonly Regex SrtTimeRegex = new Regex(
        @"(\d{2}):(\d{2}):(\d{2}),(\d{3}) --> (\d{2}):(\d{2}):(\d{2}),(\d{3})",
        RegexOptions.Compiled);

    public static List<SubtitleItem> ParseSrt(string filePath, string encoding = "UTF-8")
    {
        var subtitles = new List<SubtitleItem>();
        
        if (!File.Exists(filePath))
            return subtitles;

        try
        {
            var encodingObj = GetEncoding(encoding);
            var lines = File.ReadAllLines(filePath, encodingObj);
            
            SubtitleItem? currentItem = null;
            int lineIndex = 0;
            
            while (lineIndex < lines.Length)
            {
                var line = lines[lineIndex].Trim();
                
                if (string.IsNullOrWhiteSpace(line))
                {
                    lineIndex++;
                    continue;
                }
                
                if (int.TryParse(line, out var index))
                {
                    if (currentItem != null && currentItem.Lines.Count > 0)
                    {
                        subtitles.Add(currentItem);
                    }
                    
                    currentItem = new SubtitleItem { Index = index };
                    lineIndex++;
                }
                else if (currentItem != null && SrtTimeRegex.IsMatch(line))
                {
                    var match = SrtTimeRegex.Match(line);
                    if (match.Success)
                    {
                        currentItem.StartTime = ParseTimeSpan(
                            int.Parse(match.Groups[1].Value),
                            int.Parse(match.Groups[2].Value),
                            int.Parse(match.Groups[3].Value),
                            int.Parse(match.Groups[4].Value));
                        
                        currentItem.EndTime = ParseTimeSpan(
                            int.Parse(match.Groups[5].Value),
                            int.Parse(match.Groups[6].Value),
                            int.Parse(match.Groups[7].Value),
                            int.Parse(match.Groups[8].Value));
                    }
                    lineIndex++;
                }
                else if (currentItem != null)
                {
                    currentItem.Lines.Add(line);
                    lineIndex++;
                }
                else
                {
                    lineIndex++;
                }
            }
            
            if (currentItem != null && currentItem.Lines.Count > 0)
            {
                subtitles.Add(currentItem);
            }
            
            return subtitles.OrderBy(s => s.StartTime).ToList();
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subtitle] ParseSrt error: {ex.Message}");
            return subtitles;
        }
    }

    public static List<SubtitleItem> ParseAss(string filePath, string encoding = "UTF-8")
    {
        var subtitles = new List<SubtitleItem>();
        
        if (!File.Exists(filePath))
            return subtitles;

        try
        {
            var encodingObj = GetEncoding(encoding);
            var content = File.ReadAllText(filePath, encodingObj);
            
            var eventsSection = content.Split("[Events]").LastOrDefault()?.Split("[/Events]").FirstOrDefault();
            if (string.IsNullOrEmpty(eventsSection))
                return subtitles;

            var lines = eventsSection.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool isDialogueSection = false;
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                if (trimmedLine.StartsWith("Format:"))
                {
                    isDialogueSection = true;
                    continue;
                }
                
                if (isDialogueSection && trimmedLine.StartsWith("Dialogue:"))
                {
                    var parts = trimmedLine.Substring(9).Split(',');
                    if (parts.Length >= 10)
                    {
                        try
                        {
                            var item = new SubtitleItem
                            {
                                StartTime = ParseAssTime(parts[1]),
                                EndTime = ParseAssTime(parts[2]),
                                Lines = new List<string> { DecodeAssText(string.Join(",", parts.Skip(9))) }
                            };
                            subtitles.Add(item);
                        }
                        catch { }
                    }
                }
            }
            
            return subtitles.OrderBy(s => s.StartTime).ToList();
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subtitle] ParseAss error: {ex.Message}");
            return subtitles;
        }
    }

    public static List<SubtitleItem> Parse(string filePath, string encoding = "UTF-8")
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        
        return ext switch
        {
            ".srt" => ParseSrt(filePath, encoding),
            ".ass" => ParseAss(filePath, encoding),
            ".ssa" => ParseAss(filePath, encoding),
            _ => ParseSrt(filePath, encoding)
        };
    }

    private static TimeSpan ParseTimeSpan(int hours, int minutes, int seconds, int milliseconds)
    {
        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }

    private static TimeSpan ParseAssTime(string timeStr)
    {
        var parts = timeStr.Split(':');
        if (parts.Length == 3)
        {
            var hhmm = parts[0].Split('.');
            int hours = int.Parse(hhmm[0]);
            int minutes = int.Parse(parts[1]);
            var secondsParts = parts[2].Split('.');
            int seconds = int.Parse(secondsParts[0]);
            int centiseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1]) : 0;
            
            return new TimeSpan(0, hours, minutes, seconds, centiseconds * 10);
        }
        return TimeSpan.Zero;
    }

    private static string DecodeAssText(string text)
    {
        return text
            .Replace("\\N", "\n")
            .Replace("\\n", "\n")
            .Replace("\\H", "")
            .Replace("\\h", "")
            .Replace("\\Q", "")
            .Replace("\\q", "");
    }

    private static Encoding GetEncoding(string encodingName)
    {
        try
        {
            return Encoding.GetEncoding(encodingName);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}

// ==================== 简单日志类 - 使用公共的 SimpleLogger ====================