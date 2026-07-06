using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.Infrastructure.Services
{
    using MovieAgent.Core.Entities;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Windows.Shapes;

    public class UltimateMovieParser
    {
        // ============ 静态配置 ============

        // 网站水印/广告（最优先移除）
        private static readonly Regex[] SiteWatermarkPatterns = new[]
        {
        @"www\.[a-z0-9]+\.(com|co|cn|net|org|me|tv|xyz)",
        @"\[更多.*?访问.*?\]",
        @"【更多.*?访问.*?】",
        @"\(?YTS\.[A-Z]{2,3}\)?",
        @"阳光电影",
        @"电影天堂",
        @"66影视",
        @"btsj6\.com",
        @"domp4\.com",
        @"dygangs\.me",
        @"5266ys\.com",
        @"BTHDTV",
        @"PTHDTV",
        @"BT世界网",
        @"魅力社",
        @"BDYS",
        @"BTDX8",
        @"BATWEB",
        @"TAGWEB",
        @"HOMEWEB",
        @"NewWEB",
        @"MOMOWEB",
        @"GPTHD",
        @"SONYHD",
        @"PandaQT",
        @"QuickIO",
        @"ParkHD",
        @"NukeHD",
        @"DreamHD",
        @"GameHD",
        @"MiniHD",
        @"TrollUHD",
        @"GalaxyRG",
        @"Xiaomi",
        @"BBQDDQ\.COM",
        @"CTRLHD",
        @"CTRLWEB",
        @"CHD",
        @"WiKi",
        @"HDChina",
        @"CHDBits",
        @"MOMOHD",
        @"LHD",
        @"OPT",
        @"ALT",
        @"BATHD",
        @"SARTRE",
        @"DDR",
        @"DHTCLUB",
        @"FGT",
        @"RARBG",
        @"EVO",
        @"NTb",
        @"SPARKS",
        @"BOBO",
        @"SiNNERS",
        @"Grym",
        @"iKiW",
        @"PTer",
        @"usury",
        @"pignus",
        @"strife",
        @"terminal",
        @"peculate",
        @"regret",
        @"japhson",
        @"swte",
        @"alliance",
        @"hqndic",
        @"doraemon",
        @"haiyanghaiyang",
        @"softfeng",
        @"melite",
        @"mgb",
        @"rapidcows",
        @"grundig",
        @"mircrew",
        @"xeeder",
        @"accomplishedyak",
        @"nani",
        @"asmo",
        @"slot",
        @"sic",
        @"adweb",
        @"epsilon",
        @"collective",
        @"tabularia",
        @"phun",
        @"psyz",
        @"chiva",
        @"bpp0",
        @"opus",
        @"skyfire",
        @"jolan",
        @"tdotb",
        @"whiterhino",
        @"galaxyrg",
        @"iii",
        @"diaos",
        @"taobaobt",
        @"ffans",
        @"mp4ba",
        @"adans",
        @"uump4",
        @"hulujp",
        @"mkvhome",
        @"lxylab",
        @"butailing",
        @"lelvettv",
        @"chuck",
        @"softfeng",
        @"chd",
        @"hdchina",
        @"galaxy",
        @"troll",
        @"frame",
        @"hotweb",
        @"blacktv",
        @"seeweb",
        @"dream",
        @"nuke",
        @"park",
        @"quick",
        @"panda",
        @"game",
        @"mini",
        @"xiaomi",
        @"melite",
        @"haiyang",
        @"chdbits",
        @"wiki",
        @"yts",
        @"rarbg",
        @"evo",
        @"dimension",
        @"ntb",
        @"sparks",
        @"bobo",
        @"ctrlhd",
        @"felony",
        @"swtyblz",
        @"zr",
        @"playhd",
        @"hdchina",
        @"chd",
        @"wiki",
        @"cmct",
        @"beyond",
        @"hdsiky",
        @"e\.t\.hd",
        @"ethd",
        @"yify",
        @"psa",
        @"qxr",
        @"hqc",
        @"hq",
        @"hdtv",
        @"webdl",
        @"webrip",
        @"hotweb",
        @"blacktv",
        @"seeweb",
        @"seehd",
        @"nordic",
        @"rapidcows",
        @"grundig",
        @"mircrew",
        @"xeeder",
        @"accomplishedyak",
        @"nani",
        @"asmo",
        @"slot",
        @"sic",
        @"adweb",
        @"epsilon",
        @"collective",
        @"tabularia",
        @"phun",
        @"psyz",
        @"chiva",
        @"bpp0",
        @"opus",
        @"skyfire",
        @"jolan",
        @"tdotb",
        @"whiterhino",
        @"galaxyrg",
        @"iii",
        @"diaos",
        @"taobaobt",
        @"ffans",
        @"mp4ba",
        @"adans",
        @"uump4",
        @"hulujp",
        @"mkvhome",
        @"lxylab",
        @"butailing",
        @"lelvettv",
        @"chuck",
        @"softfeng",
        @"chd",
        @"hdchina",
        @"galaxy",
        @"troll",
        @"frame",
        @"dream",
        @"nuke",
        @"park",
        @"quick",
        @"panda",
        @"game",
        @"mini",
        @"xiaomi",
        @"melite",
        @"haiyang",
        @"chdbits",
        @"wiki",
        @"yts",
        @"rarbg",
        @"evo",
        @"dimension",
        @"ntb",
        @"sparks",
        @"bobo",
        @"ctrlhd",
        @"felony",
        @"swtyblz",
        @"zr",
        @"playhd",
        @"hdchina",
        @"chd",
        @"wiki",
        @"cmct",
        @"beyond",
        @"hdsiky",
        @"e\.t\.hd",
        @"ethd",
        @"yify",
        @"psa",
        @"qxr",
        @"hqc",
        @"hq",
        @"hdtv",
        @"webdl",
        @"webrip",
    }.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToArray();

        // 技术参数（用于移除）
        private static readonly Regex[] TechPatterns = new[]
        {
        @"\b(4K|2160p|1080p|720p|480p|360p|1080i|576p|1440p)\b",
        @"\b(HDR|HDR10|HDR10\+|DV|Dolby Vision|SDR|HLG|HDR10Plus|DoVi)\b",
        @"\b(HEVC|H\.?264|H\.?265|AVC|AV1|MPEG4|MPEG2|XviD|DivX|VP9|VC1|H264|H265)\b",
        @"\b(10bit|8bit|10-bit|8-bit|12bit|12-bit)\b",
        @"\b(BluRay|Blu-Ray|BDRip|BDRemux|WEB-DL|WEBRip|WEBRip|Remux|REMUX|HDTV|SATRip|DVDScr|DVDrip|WEB|BD|HD|UHD|HDRip|BRrip|DVDRip|DVDScr|R5|R6|TC|TS|CAM|WEBRip|WEBDL|HDTVRip|DVDRip)\b",
        @"\b(PROPER|REPACK|INTERNAL|REAL|RERIP|RETAiL|iNTERNAL|DUAL|MULTi|COMPLETE|UNCUT|EXTENDED|UNRATED|DIRECTOR.?S CUT|FINAL CUT|DC|EDITION|REMASTERED|REMASTER|RESTORATION|RESTORED|REDUX|IMAX|iMAX|THEATRICAL|Criterion|CRITERION|Limited|Collector|READNFO)\b",
        @"\b(DSNP|NF|AMZN|HMAX|ATVP|iT|GLHF|HBO|MAX|STZ|D\+|HULU|PEACOCK|PARAMOUNT|MA|CATCHPLAY|MUBI|HuluJP|Netflix|Disney|Amazon|Apple|Hami|iTunes|DSNP|NF|AMZN|HMAX|ATVP|HULU|PEACOCK|PARAMOUNT|MA|CATCHPLAY|MUBI)\b",
        @"\b(DDP?5\.1|Atmos|TrueHD|DTS-HD|DTS|MA|EAC3|AC3|AAC|MP3|FLAC|AAC2\.0|DDP|DD5\.1|2\.0|5\.1|7\.1|DD\+|DTS-HDMA|LPCM|PCM|AAC5\.1|AAC2\.0|DTS-HD|DTS-X|TrueHD\.7\.1|DTS-HD\.MA|DDP5\.1|DD5\.1|DTS5\.1|AC3|EAC3|AAC|FLAC|MP3|DTS|TrueHD|Atmos)\b",
        @"\b(2Audio|3Audio|MultiAudio|Multi-Sub|MultiSub|Subs?|Dual\s+Audio|国粤双语|国英双语|中英双字|中文字幕|国语中字|粤语中字|日语中字|韩语中字|英语中字|特效字幕|双语|CHS|ENG|CHT|MULTI|MANDARIN|CANTONESE|KOREAN|JAPANESE|FRENCH|GERMAN|ITALIAN|SPANISH|HINDI|RUSSIAN|ENGLISH|DUBBED|SUBBED|ENSUB|ENSUBBED|COMANCHE)\b",
        @"\b(60Fps|120Fps|60FPS|120FPS|60fps|120fps|50fps|30fps|25fps|24fps|23.976fps)\b",
        @"\b(S\d{2}E\d{2}|S\d{2}\s*E\d{2}|第\d+集|E\d{2}|EP?\d{2}|Episode\s*\d+|Season\s*\d+|Part\d+|EP\d{2,3})\b",
        @"\b(HQ|HD|FHD|UHD|SDR|HDR|DV|Dovi|REMUX|REPACK|PROPER|Rip|ENSUBBED|SUBBED|SUB|DUBBED|COMANCHE|iMAX|iMAX|MULTi|MULTI|MULTI\.|DDP5\.1|DTS-HD|TrueHD|Atmos|DTS-X|DTS-HDMA|LPCM|PCM|AAC5\.1|AAC2\.0|DD5\.1|DD\+|EAC3|AC3|AAC|FLAC|MP3|DTS|TrueHD|Atmos|DTS-HD|DTS-X|DTS-HDMA|LPCM|PCM|AAC5\.1|AAC2\.0|DD5\.1|DD\+|EAC3|AC3|AAC|FLAC|MP3|DTS|TrueHD|Atmos|DTS-HD|DTS-X|DTS-HDMA|LPCM|PCM|AAC5\.1|AAC2\.0|DD5\.1|DD\+|EAC3|AC3|AAC|FLAC|MP3|DTS|TrueHD|Atmos|DTS-HD|DTS-X|DTS-HDMA|LPCM|PCM|AAC5\.1|AAC2\.0|DD5\.1|DD\+|EAC3|AC3|AAC|FLAC|MP3|DTS|TrueHD|Atmos|DTS-HD|DTS-X|DTS-HDMA|LPCM|PCM|AAC5\.1|AAC2\.0|DD5\.1|DD\+|EAC3|AC3|AAC|FLAC|MP3|DTS|TrueHD|Atmos)\b",
    }.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToArray();

        // 年份正则
        private static readonly Regex YearRegex = new Regex(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
        private static readonly Regex YearInParenRegex = new Regex(@"\(?(19|20)\d{2}\)?", RegexOptions.Compiled);

        // 中文检测
        private static readonly Regex ChineseRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

        // 发布组（从末尾匹配）
        private static readonly Regex ReleaseGroupRegex = new Regex(
           @"[-_\s]+([A-Za-z0-9]+)(?>[-_\s]+(?:" +
           @"HD|RG|WEB|TV|HDTV|BD|UHD|BLURAY|WEB-DL|WEBRip|REMUX|REPACK|PROPER|INTERNAL|" +
           @"UNCUT|EXTENDED|UNRATED|DIRECTOR'?S? CUT|FINAL CUT|DC|EDITION|REMASTERED|REMASTER|" +
           @"RESTORATION|RESTORED|REDUX|IMAX|THEATRICAL|CRITERION|LIMITED|COLLECTOR|READNFO|" +
           @"DTS-HD|TRUEHD|ATMOS|DTS-X|DTS-HDMA|LPCM|PCM|AAC5\.1|AAC2\.0|DD5\.1|DD\+|EAC3|AC3|" +
           @"AAC|FLAC|MP3|DTS" +
           @"))+(?=\.[a-zA-Z0-9]+$|$)",
           RegexOptions.Compiled | RegexOptions.IgnoreCase
       );

        // 纯随机文件名检测（如 AFUK2470、220723111212094184）
        private static readonly Regex RandomFileNameRegex = new Regex(@"^[A-Z]{4}\d{4}$|^\d{15}$", RegexOptions.Compiled);
        private static readonly Regex NumberOnlyRegex = new Regex(@"^\d+$", RegexOptions.Compiled);

        /// <summary>
        /// 解析文件名
        /// </summary>
        public static Movie? ParseFileName(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            string fileName;
            try { fileName = System.IO.Path.GetFileNameWithoutExtension(filePath); }
            catch (ArgumentException) { return null; }

            if (string.IsNullOrWhiteSpace(fileName)) return null;

            // 特殊处理：如果是纯数字文件名，尝试从目录名获取信息
            string directoryName = System.IO.Path.GetDirectoryName(filePath) ?? "";
            string dirName = System.IO.Path.GetFileName(directoryName);

            var context = new ParseContext
            {
                OriginalFileName = fileName,
                WorkingTitle = fileName,
                DirectoryName = dirName,
                FilePath = filePath,
                FileSize = GetFileSizeSafe(filePath),
                IsRandomFileName = RandomFileNameRegex.IsMatch(fileName) || NumberOnlyRegex.IsMatch(fileName)
            };

            // 如果是随机文件名或纯数字，尝试从目录名提取
            if (context.IsRandomFileName && !string.IsNullOrEmpty(dirName) && dirName != ".")
            {
                context.WorkingTitle = dirName;
                context.OriginalFileName = dirName;
            }

            // 1. 提取年份
            ExtractYear(context);

            // 2. 移除网站水印
            RemoveSiteWatermarks(context);

            // 3. 提取发布组
            ExtractReleaseGroup(context);

            // 4. 提取技术参数
            ExtractTechnicalDetails(context);

            // 5. 提取括号中的标题
            ExtractBracketTitle(context);

            // 6. 提取中文标题
            ExtractChineseTitle(context);

            // 7. 如果还没有标题，从剩余文本提取
            if (string.IsNullOrWhiteSpace(context.Title))
            {
                context.Title = ExtractTitleFromRemaining(context);
            }

            // 8. 最终清理
            context.Title = FinalCleanTitle(context.Title);

            // 9. 如果标题为空或太短，尝试从原文件名提取
            if (string.IsNullOrWhiteSpace(context.Title) || context.Title.Length < 2)
            {
                context.Title = ExtractFallbackTitle(context.OriginalFileName);
            }

            // 10. 如果还是空，使用目录名
            if (string.IsNullOrWhiteSpace(context.Title) && !string.IsNullOrEmpty(context.DirectoryName) && context.DirectoryName != ".")
            {
                context.Title = ExtractFallbackTitle(context.DirectoryName);
            }

            if (string.IsNullOrWhiteSpace(context.Title))
                return null;
            //最新再次清理标题，确保最终结果干净
            context.Title = MovieTitleExtractor.ExtractTitle(context.Title);

            return new Movie
            {
                Title = context.Title,
                OriginalTitle = context.OriginalTitle,
                ReleaseYear = context.Year,
                Resolution = context.Resolution,
                VideoCodec = context.VideoCodec,
                AudioCodec = context.AudioCodec,
                ReleaseGroup = context.ReleaseGroup,
                IsTVSeries = context.IsTVSeries,
                SeasonNumber = context.SeasonNumber,
                EpisodeNumber = context.EpisodeNumber,
                FilePath = context.FilePath,
                FileSize = context.FileSize,
                IsWatched = false,
                IsFavorite = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        #region 解析步骤

        private static void ExtractYear(ParseContext context)
        {
            string source = context.WorkingTitle;

            // 优先从括号中提取
            var parenMatch = YearInParenRegex.Match(source);
            if (parenMatch.Success)
            {
                var yearStr = Regex.Match(parenMatch.Value, @"\d{4}").Value;
                if (!string.IsNullOrEmpty(yearStr))
                {
                    context.Year = int.Parse(yearStr);
                    context.WorkingTitle = context.WorkingTitle.Replace(parenMatch.Value, "");
                    return;
                }
            }

            // 普通年份匹配
            var match = YearRegex.Match(source);
            if (match.Success)
            {
                context.Year = int.Parse(match.Value);
                context.WorkingTitle = context.WorkingTitle.Replace(match.Value, "");
            }
        }

        private static void RemoveSiteWatermarks(ParseContext context)
        {
            foreach (var pattern in SiteWatermarkPatterns)
            {
                context.WorkingTitle = pattern.Replace(context.WorkingTitle, "");
            }
            context.WorkingTitle = Regex.Replace(context.WorkingTitle, @"\s+", " ");
        }

        private static void ExtractReleaseGroup(ParseContext context)
        {
            var match = ReleaseGroupRegex.Match(context.WorkingTitle);
            if (match.Success)
            {
                string group = match.Groups[1].Value;
                var excludeWords = new[] { "SAMPLE", "SAMPLE2", "SAMPLE3", "SAMPLE4", "SAMPLE5" };
                if (!excludeWords.Contains(group.ToUpperInvariant()))
                {
                    context.ReleaseGroup = group;
                    context.WorkingTitle = context.WorkingTitle.Replace(match.Value, "");
                }
            }
        }

        private static void ExtractTechnicalDetails(ParseContext context)
        {
            string source = context.OriginalFileName;

            // 提取分辨率
            var resMatch = new Regex(@"\b(4K|2160p|1080p|720p|480p|2160|1080|720|480)\b", RegexOptions.IgnoreCase).Match(source);
            if (resMatch.Success)
            {
                string val = resMatch.Value.ToUpperInvariant();
                if (val == "2160" || val == "2160P") context.Resolution = "2160P";
                else if (val == "1080" || val == "1080P") context.Resolution = "1080P";
                else if (val == "720" || val == "720P") context.Resolution = "720P";
                else if (val == "480" || val == "480P") context.Resolution = "480P";
                else if (val == "4K") context.Resolution = "4K";
            }

            // 提取视频编码
            var vidMatch = new Regex(@"\b(x265|HEVC|x264|AVC|AV1|H\.?264|H\.?265|MPEG4|MPEG2|XviD|DivX)\b", RegexOptions.IgnoreCase).Match(source);
            if (vidMatch.Success) context.VideoCodec = vidMatch.Value.ToUpperInvariant();

            // 提取音频编码
            var audMatch = new Regex(@"\b(DTS-HD|TrueHD|DTS|AC3|AAC|Atmos|EAC3|DDP?5\.1|DDP|DD5\.1|DTS-HDMA|LPCM|DTS-X)\b", RegexOptions.IgnoreCase).Match(source);
            if (audMatch.Success) context.AudioCodec = audMatch.Value.ToUpperInvariant();

            // 提取剧集信息
            var epMatch = Regex.Match(source, @"[Ss](\d+)[Ee](\d+)|第(\d+)集|EP?(\d{2,3})|E(\d{2,3})|\.E(\d{2,3})\.");
            if (epMatch.Success)
            {
                context.IsTVSeries = true;
                if (!string.IsNullOrEmpty(epMatch.Groups[1].Value))
                {
                    context.SeasonNumber = int.Parse(epMatch.Groups[1].Value);
                    context.EpisodeNumber = int.Parse(epMatch.Groups[2].Value);
                }
                else if (!string.IsNullOrEmpty(epMatch.Groups[3].Value))
                {
                    context.EpisodeNumber = int.Parse(epMatch.Groups[3].Value);
                }
                else if (!string.IsNullOrEmpty(epMatch.Groups[4].Value))
                {
                    context.EpisodeNumber = int.Parse(epMatch.Groups[4].Value);
                }
                else if (!string.IsNullOrEmpty(epMatch.Groups[5].Value))
                {
                    context.EpisodeNumber = int.Parse(epMatch.Groups[5].Value);
                }
                else if (!string.IsNullOrEmpty(epMatch.Groups[6].Value))
                {
                    context.EpisodeNumber = int.Parse(epMatch.Groups[6].Value);
                }
            }

            // 从WorkingTitle中移除所有技术参数
            foreach (var pattern in TechPatterns)
            {
                context.WorkingTitle = pattern.Replace(context.WorkingTitle, "");
            }
            context.WorkingTitle = Regex.Replace(context.WorkingTitle, @"\s+", " ");
        }

        private static void ExtractBracketTitle(ParseContext context)
        {
            var matches = Regex.Matches(context.WorkingTitle, @"[\[\(]([^\]\)]+)[\]\)]");
            foreach (Match match in matches)
            {
                var content = match.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(content)) continue;

                if (ChineseRegex.IsMatch(content))
                {
                    foreach (var pattern in TechPatterns)
                    {
                        content = pattern.Replace(content, "");
                    }
                    content = Regex.Replace(content, @"\s+", " ").Trim();
                    if (!string.IsNullOrEmpty(content))
                    {
                        context.Title = content;
                        context.WorkingTitle = context.WorkingTitle.Replace(match.Value, "");
                        return;
                    }
                }
                else if (string.IsNullOrEmpty(context.OriginalTitle) && content.Length > 2)
                {
                    context.OriginalTitle = CleanEnglishTitle(content);
                    context.WorkingTitle = context.WorkingTitle.Replace(match.Value, "");
                }
                else
                {
                    context.WorkingTitle = context.WorkingTitle.Replace(match.Value, "");
                }
            }
        }

        private static void ExtractChineseTitle(ParseContext context)
        {
            if (!string.IsNullOrEmpty(context.Title)) return;

            // 从剩余文本中提取中文部分
            var chineseMatches = ChineseRegex.Matches(context.WorkingTitle);
            if (chineseMatches.Count > 0)
            {
                var title = string.Join("", chineseMatches.Select(m => m.Value));
                // 检查是否有多段中文（可能是"标题.副标题"格式）
                if (chineseMatches.Count > 1)
                {
                    // 取最长的一段作为标题
                    var segments = context.WorkingTitle.Split(new[] { ' ', '.', '-', '_', '，', '、' }, StringSplitOptions.RemoveEmptyEntries);
                    var chineseSegment = segments.FirstOrDefault(s => ChineseRegex.IsMatch(s));
                    if (!string.IsNullOrEmpty(chineseSegment))
                    {
                        title = chineseSegment;
                    }
                }
                context.Title = title;
                // 从WorkingTitle中移除中文部分
                context.WorkingTitle = Regex.Replace(context.WorkingTitle, @"[\u4e00-\u9fff]+", "");
                context.WorkingTitle = Regex.Replace(context.WorkingTitle, @"\s+", " ");
            }
        }

        private static string ExtractTitleFromRemaining(ParseContext context)
        {
            string remaining = context.WorkingTitle;

            if (string.IsNullOrWhiteSpace(remaining))
                return string.Empty;

            // 如果剩余部分包含中文
            if (ChineseRegex.IsMatch(remaining))
            {
                var chineseParts = Regex.Matches(remaining, @"[\u4e00-\u9fff]+");
                if (chineseParts.Count > 0)
                {
                    return string.Join("", chineseParts.Select(m => m.Value));
                }
            }

            // 英文标题清理
            remaining = Regex.Replace(remaining, @"[\.\-_]", " ");
            remaining = Regex.Replace(remaining, @"\s+", " ").Trim();

            // 移除单独的字母（可能是乱码）
            remaining = Regex.Replace(remaining, @"\b[a-z]\b", " ", RegexOptions.IgnoreCase);
            remaining = Regex.Replace(remaining, @"\s+", " ").Trim();

            // 如果剩余内容有多个单词，格式化为TitleCase
            if (!string.IsNullOrEmpty(remaining))
            {
                var words = remaining.Split(' ');
                if (words.Length >= 2 || (words.Length == 1 && words[0].Length > 2))
                {
                    var textInfo = CultureInfo.InvariantCulture.TextInfo;
                    return textInfo.ToTitleCase(remaining.ToLowerInvariant());
                }
            }

            return remaining;
        }

        private static string CleanEnglishTitle(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            foreach (var pattern in TechPatterns)
            {
                input = pattern.Replace(input, "");
            }
            input = Regex.Replace(input, @"[\.\-_]", " ");
            input = Regex.Replace(input, @"\s+", " ").Trim();

            if (!string.IsNullOrEmpty(input))
            {
                var textInfo = CultureInfo.InvariantCulture.TextInfo;
                return textInfo.ToTitleCase(input.ToLowerInvariant());
            }
            return input;
        }

        private static string FinalCleanTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;

            title = title.Trim('.', '-', '_', ' ', '（', '）', '(', ')', '[', ']', '，', '、');

            if (Regex.IsMatch(title, @"^\d{4}\s*[-_\.]?\s*"))
            {
                title = Regex.Replace(title, @"^\d{4}\s*[-_\.]?\s*", "");
            }

            title = Regex.Replace(title, @"\([^)]*\)", "");
            title = Regex.Replace(title, @"\[[^\]]*\]", "");

            title = Regex.Replace(title, @"\s+", " ").Trim();

            return title;
        }

        private static string ExtractFallbackTitle(string original)
        {
            var cleaned = original;

            cleaned = YearRegex.Replace(cleaned, "");

            foreach (var pattern in TechPatterns)
            {
                cleaned = pattern.Replace(cleaned, "");
            }

            foreach (var pattern in SiteWatermarkPatterns)
            {
                cleaned = pattern.Replace(cleaned, "");
            }

            cleaned = Regex.Replace(cleaned, @"[\.\-_]", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            if (ChineseRegex.IsMatch(cleaned))
            {
                return cleaned;
            }
            else if (!string.IsNullOrEmpty(cleaned) && cleaned.Split(' ').Length >= 2)
            {
                var textInfo = CultureInfo.InvariantCulture.TextInfo;
                return textInfo.ToTitleCase(cleaned.ToLowerInvariant());
            }

            if (string.IsNullOrEmpty(cleaned) || cleaned.Length < 3)
            {
                cleaned = original;
                cleaned = Regex.Replace(cleaned, @"\.(mkv|mp4|avi|iso|m2ts|ts)$", "", RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, @"[\.\-_]", " ");
                cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            }

            return cleaned;
        }

        private static long GetFileSizeSafe(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                return fileInfo.Exists ? fileInfo.Length : 0;
            }
            catch { return 0; }
        }

        #endregion

        #region 上下文类

        private class ParseContext
        {
            public string OriginalFileName { get; set; } = string.Empty;
            public string WorkingTitle { get; set; } = string.Empty;
            public string DirectoryName { get; set; } = string.Empty;
            public string FilePath { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public bool IsRandomFileName { get; set; }

            public string? Title { get; set; }
            public string? OriginalTitle { get; set; }
            public int? Year { get; set; }
            public string? Resolution { get; set; }
            public string? VideoCodec { get; set; }
            public string? AudioCodec { get; set; }
            public string? ReleaseGroup { get; set; }
            public bool IsTVSeries { get; set; }
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
        }

        #endregion
    }

    public class MovieInfo
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? OriginalTitle { get; set; }
        public int? ReleaseYear { get; set; }
        public string? Resolution { get; set; }
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public string? ReleaseGroup { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public bool IsWatched { get; set; }
        public bool IsFavorite { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsTVSeries { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
    }
}
