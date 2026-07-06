using System;
using System.Collections.Generic;
using System.Text;

namespace MovieAgent.Infrastructure.Services
{
    using System.IO;
    using System.Text.RegularExpressions;

    public static class MovieTitleExtractor
    {
        private static readonly Regex _cleanupRegex;

        // 静态构造函数，一次性编译所有规则，提高性能
        static MovieTitleExtractor()
        {
            // 将所有的过滤规则拼接在一起（注意用 | 分隔）
            string pattern = string.Join("|", new[]
            {
            // 1. 扩展名（.mkv, .mp4, .avi等）
            @"\.[^.]+$",

            // 2. 分辨率与来源
            @"[\s\._-]+(?:1080p|2160p|4K|BD|WEB|WEB-DL|WEBRip|HDTV|HD|REMUX|REPACK|PROPER|UNCENSORED|UNRATED|DIRECTOR'?S\s*CUT|EXTENDED)",

            // 3. 视频编码
            @"[\s\._-]+(?:X264|X265|HEVC|AVC|H264|H265|MPEG|DIVX|XVID)",

            // 4. 音频格式（含杜比、DTS等）
            @"[\s\._-]+(?:DTS|AC3|TrueHD|AAC|FLAC|DD[P]?|DDP|MA|HDMA|ATMOS|MPEG\s*AUDIO|LPCM|SURCODE)",

            // 5. 声道数量
            @"[\s\._-]+(?:[257]\.1|2\.0|6\.1|MONO)",

            // 6. 中文常见后缀（必须优先于英文组匹配，防止误删中文片名）
            @"(?:HD|BD|国英|国粤|国语|中字|双语|特效|音轨|字幕|未分级|高码版|无水印|修复版|导演剪辑版|蓝光|重编码)",
            // 注意：由于中文片名通常没有空格，上面这行不加 [\s\._-] 前缀，直接在原文中剔除这些中文字眼

            // 7. 制作组 / 发布组织（从你给的清单里提取的高频词）
            @"[\s\._-]+(?:CtrlTV|NOGRP|CMRG|Smurf|Flux|Tepes|Iamable|Vxt|Aoc|BBQDDQ|Bits|HDS|HDWING|BEAST|Rovers|Nogrp|Gasmask|Depth|Xebec|Sp3ll|Handjob|Phobos|Sbr|Doden|Yop|Mt|Cinefile|Amiable|Turehd|DiY|Ttg|Hd4u|Jpn|2Yz|Hdh|Riprg|Ma5|Apex|Cm|Byndr|Dvsux|Rumour|Mauveskunkofstereotypedaptitude|Rightnow|Snoopy|Edith|Ade|Sbr|Phobos|Doden|Yop|Hdwing|Hdh|Ctrltv|Flux|Xebec|Tepes|Vxt|Aoc|Ctrltv|Hdma|Dvsux|Rumour|Mauveskunkofstereotypedaptitude|Lelvetv|Pmtp|Ntg|Iqy|Wanna|Scare|Moon|Putao|Rough|Orbs|Handjob|Bitme|Hdbits)",

            // 8. 剧集标识（第X集、EXX、SXXEXX）
            @"[\s\._-]*(?:第\d+集|E\d+|S\d+E\d+)(?:修正)?",

            // 9. 年份（四位数字，但只匹配前面带空格的，避免误伤《12 Strong》这种数字片名）
            @"[\s\._-]+(?:19|20)\d{2}",

            // 10. 常见注释括号及其内容
            @"[\s\._-]*[\[\(].*?[\]\)]",
            
            // 11. 特殊杂项（Sample, Trailer, V2, V1等）
            @"[\s\._-]+(?:Sample|Trailer|Featurette|Documentary|V\d+|FEST|THEATER|RE-RELEASE)",
            
            // 12. 处理像 "10017@" 或 "10008@" 这样的数字+特殊符号
            @"[\s\._-]+\d+[@＠]",
            @"[\s\._-]*\d{2}(?=$|\s|_)",   // 匹配末尾的两位数字（如 01、02...）
            
        });

            // 编译正则（不区分大小写，忽略注释空格）
            _cleanupRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        public static string ExtractTitle(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return fileName;

            // 1. 先移除扩展名（如果包含）
            string name = System.IO.Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(name)) name = fileName;

            // 2. 执行全量正则替换（去除所有干扰项）
            name = _cleanupRegex.Replace(name, " ");

            // 3. 清理多余的分隔符（点、下划线、短横、空格），统一替换为空格
            name = Regex.Replace(name, @"[\s\._-]+", " ");

            // 4. 去掉首尾空格，并合并内部连续空格
            name = Regex.Replace(name.Trim(), @"\s+", " ");

            // 5. 如果经过处理变成了空字符串或纯数字/符号，回退原文件名（去掉扩展名）
            if (string.IsNullOrWhiteSpace(name) || Regex.IsMatch(name, @"^[\d\W]+$"))
            {
                return System.IO.Path.GetFileNameWithoutExtension(fileName) ?? fileName;
            }

            return name;
        }
    }
}
