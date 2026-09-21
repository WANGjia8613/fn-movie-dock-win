using System.Text.RegularExpressions;
using MovieDock.Core.Config;
using MovieDock.Core.Llm;

namespace MovieDock.Core.Subtitle;

/// <summary>字幕匹配结果。</summary>
public sealed class SubtitleResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public List<string> Files { get; init; } = new();
    public string EntryTitle { get; init; } = "";
}

/// <summary>
/// 字幕服务：下载完成后自动为视频匹配中文字幕。自 Python 版 app/subtitle/manager.py 移植。
/// 策略：
/// 1. 关键词候选：片名 → 片名+年份 → 配置里的 extra_keywords（大陆/台湾译名差异）
/// 2. 字幕条目打分：优先 简体/双语、ASS 特效格式、发布组/清晰度关键词命中
/// 3. 逐个候选尝试下载（前 3 个），任一成功即停
/// 4. 解压 → 挑最佳字幕文件 → 编码规整 → 按 {video}.zh.ass 改名放到视频同目录
/// </summary>
public sealed class SubtitleService(SubtitleConfig cfg, LlmClient? llm = null, string proxy = "")
{
    // 清晰度/来源关键词：用于和字幕条目标题比对
    private static readonly string[] HintTokens =
    {
        "2160p", "1080p", "720p", "uhd", "4k", "bluray", "blu-ray", "bdremux", "remux",
        "web-dl", "webrip", "hdr", "dovi", "dv", "x265", "x264", "hevc", "atmos", "truehd",
        "imax", "criterion", "sdr", "10bit",
    };

    // 大陆用户优先：简英双语 > 简体 > 繁英 > 繁体 > 纯英文
    private static readonly (Regex Pattern, double Bonus)[] LangBonus =
    {
        (new Regex(@"(简英|中英|简繁|chs&eng|chs_eng|zh&en|中英双语|简体&英文|简体中英)", RegexOptions.Compiled), 10.0),
        (new Regex(@"(简体|简中|chs|chi|中文|国语|简)", RegexOptions.Compiled), 7.0),
        (new Regex(@"(繁英|cht&eng|cht_eng|繁中|繁体|cht|big5|繁)", RegexOptions.Compiled), 3.0),
        (new Regex(@"(双语|bilingual)", RegexOptions.Compiled), 5.0),
        (new Regex(@"(英语|english|eng\b)", RegexOptions.Compiled), 1.0),
    };

    private static readonly Dictionary<string, double> FmtBonus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ASS"] = 6.0, ["SSA"] = 4.0, ["SRT"] = 3.0, ["SUP"] = 2.0, ["SUB"] = 1.0,
    };

    private static readonly Dictionary<string, int> SubFileRank = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ass"] = 4, [".ssa"] = 3, [".srt"] = 2, [".sup"] = 1, [".sub"] = 0, [".idx"] = 0, [".vtt"] = 2,
    };

    private static readonly Regex ReleaseGroupRegex =
        new(@"(?:^|[.\-_])([A-Za-z][A-Za-z0-9]{1,11})$", RegexOptions.Compiled);
    private static readonly Regex CjkRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex YearRegex = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex DotUnderscoreRegex = new(@"[._]", RegexOptions.Compiled);
    private static readonly Regex QualityTokenRegex =
        new(@"(?<![a-z0-9])(2160p|1080p|720p|bluray|blu-ray|bdremux|remux|web-dl|webrip|hdr|dovi|x265|x264|hevc|atmos|truehd|10bit|uhd|4k)(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SplitRegex = new(@"[.\-_ ]+", RegexOptions.Compiled);
    private static readonly Regex FullYearRegex = new(@"^(19|20)\d{2}$", RegexOptions.Compiled);

    public SubtitleConfig Config { get; } = cfg;
    public string Proxy { get; } = (proxy ?? "").Trim();

    /// <summary>取文件名里第一个画质/来源标记之前的部分，作为片名候选。</summary>
    private static string TitlePrefix(string stem)
    {
        var parts = SplitRegex.Split(stem);
        var keep = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            var low = part.ToLowerInvariant();
            if (HintTokens.Contains(low) || FullYearRegex.IsMatch(part)) break;
            keep.Add(part);
            if (keep.Count >= 5) break;
        }
        return string.Join(" ", keep).Trim();
    }

    /// <summary>从视频文件名提取片源特征（清晰度/发布组），用于字幕条目比对。</summary>
    public static List<string> TokensFromVideo(string video)
    {
        var stem = Path.GetFileNameWithoutExtension(video);
        var low = stem.ToLowerInvariant();
        var tokens = new List<string>();
        foreach (var t in HintTokens)
        {
            if (Regex.IsMatch(low, $@"(?<![a-z0-9]){Regex.Escape(t)}(?![a-z0-9])"))
                tokens.Add(t);
        }
        var group = ReleaseGroupRegex.Match(stem);
        if (group.Success)
        {
            var token = group.Groups[1].Value.ToLowerInvariant();
            if (!tokens.Contains(token) && token is not ("by" or "repack" or "proper"))
                tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>字幕条目打分：语言 > 格式 > 片源特征命中。</summary>
    public double ScoreEntry(SubHdEntry entry, List<string> tokens, bool preferBilingual, bool preferSimplified = true)
    {
        var title = entry.Title ?? "";
        var langText = $"{title} {entry.Lang ?? ""}";
        double score = 0;

        var simplified = Regex.IsMatch(langText, @"(简体|简中|简英|简繁|chs|\bzh\b)", RegexOptions.IgnoreCase);
        var traditional = Regex.IsMatch(langText, @"(繁体|繁中|繁英|cht|big5)", RegexOptions.IgnoreCase);

        foreach (var (pattern, bonus) in LangBonus)
        {
            if (pattern.IsMatch(langText))
            {
                score += bonus * (preferBilingual && bonus >= 8.0 ? 1.2 : 1.0);
                break;
            }
        }
        // 大陆用户优先简体：简繁同时出现（简繁双语）也算简体优先
        if (preferSimplified)
        {
            if (simplified) score += 6.0;
            else if (traditional) score -= 4.0;
        }
        if (FmtBonus.TryGetValue(entry.Fmt ?? "", out var fmtScore))
            score += fmtScore;

        var low = title.ToLowerInvariant();
        var matchedYear = YearRegex.IsMatch(title);
        foreach (var token in tokens)
        {
            if (token.Length > 0 && low.Contains(token)) score += 3.0;
            else if (token.Length > 0 && (entry.Lang ?? "").ToLowerInvariant().Contains(token)) score += 1.0;
        }
        if (low.Contains("1080p") && !low.Contains("2160p") && !low.Contains("uhd"))
            score -= 1.5;
        if (entry.Year.Length > 0 && matchedYear && title.Contains(entry.Year))
            score += 0.5;
        // 标题过短/只有片名的条目信息不足，略降权
        if (title.Length < 8) score -= 1.0;
        return score;
    }

    private static bool TokenHit(SubHdEntry entry, List<string> tokens)
    {
        var text = $"{entry.Title} {entry.Lang ?? ""}".ToLowerInvariant();
        return tokens.Any(t => t.Length > 0 && text.Contains(t));
    }

    /// <summary>挑最佳字幕文件：格式优先级 + 文件名语言标记。</summary>
    internal static double ScoreSubFile(string path)
    {
        SubFileRank.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out var rank);
        var score = rank * 1.0;
        var name = Path.GetFileName(path);
        var low = name.ToLowerInvariant();
        if (Regex.IsMatch(low, @"(简英|中英|双语|简体|chs|chi|zh)")) score += 3.0;
        else if (Regex.IsMatch(low, @"(繁|cht)")) score += 1.0;
        if (Regex.IsMatch(low, @"(eng|english)") && !Regex.IsMatch(low, @"(简英|中英|双语)"))
            score -= 2.0;
        return score;
    }

    // ---------- 对外 ----------
    public async Task<SubtitleResult> FetchForVideoAsync(
        string video, string title = "", int? year = null, string quality = "")
    {
        var keywords = KeywordCandidates(video, title, year);
        if (Config.LlmTranslate && NeedChinese(keywords))
        {
            var zh = await LlmChineseTitlesAsync(title.Length > 0 ? title : Path.GetFileNameWithoutExtension(video), year);
            if (zh.Count > 0)
                keywords = zh.Concat(keywords).ToList();
        }
        // 阻塞流程放线程池，避免卡住调用方
        return await Task.Run(() => FetchSync(video, title, year, quality, keywords));
    }

    private static bool NeedChinese(List<string> keywords) =>
        keywords.All(k => !CjkRegex.IsMatch(k ?? ""));

    /// <summary>SubHD 对英文名匹配很差，用已配置的大模型补中文译名。失败不影响主流程。</summary>
    private async Task<List<string>> LlmChineseTitlesAsync(string name, int? year)
    {
        if (llm is null || string.IsNullOrEmpty(llm.Config.ApiKey))
            return new List<string>();
        var hint = year is int y ? $"{name} {y}" : name;
        var system =
            "你是影视名称助手。给出这部电影的简体中文名（如有大陆译名、台湾译名、常见别名都要）。"
            + "只输出 JSON 数组，例如 [\"机器人总动员\"]，不要其它说明。最多 3 个，按常用度排序。";
        string content;
        try
        {
            content = await llm.ChatAsync(new List<Dictionary<string, string>>
            {
                new() { ["role"] = "system", ["content"] = system },
                new() { ["role"] = "user", ["content"] = hint },
            }, 0);
        }
        catch (Exception) { return new List<string>(); }

        var outList = new List<string>();
        if (SourceParser.TryParseJsonBlock(content) is { } doc &&
            doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var text = ((item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : item.ToString()) ?? "").Trim();
                if (text.Length > 0 && CjkRegex.IsMatch(text))
                    outList.Add(text);
            }
        }
        return outList.Take(3).ToList();
    }

    // ---------- 关键词候选 ----------
    public List<string> KeywordCandidates(string video, string title, int? year)
    {
        var cands = new List<string>();
        var hint = (Config.MatchHint ?? "").Trim();
        if (hint.Length > 0) cands.Add(hint);

        var baseTitle = (title ?? "").Trim();
        if (baseTitle.Length > 0)
        {
            cands.Add(baseTitle);
            if (year is int y)
                cands.Add($"{baseTitle} {y}");
        }
        // 从文件名里抠英文名（去掉年份/清晰度/来源等）
        var stem = Path.GetFileNameWithoutExtension(video);
        var english = DotUnderscoreRegex.Replace(stem, " ");
        english = QualityTokenRegex.Replace(english, " ");
        english = Regex.Replace(english, @"\b(19|20)\d{2}\b", " ");
        english = Regex.Replace(english, @"\s+", " ").Trim(' ', '-', '_', '.');
        if (english.Length > 0)
        {
            cands.Add(english);
            var parts = english.Split(' ');
            if (parts.Length > 0)
                cands.Add(string.Join(" ", parts.Take(3)));
        }
        var prefix = TitlePrefix(stem);
        if (prefix.Length > 0)
        {
            cands.Add(prefix);
            cands.Add(prefix.Replace("-", " "));
        }
        foreach (var kw in Config.ExtraKeywords)
        {
            var k = (kw ?? "").Trim();
            if (k.Length > 0) cands.Add(k);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var c in cands)
        {
            if (c.Length >= 2 && seen.Add(c.ToLowerInvariant()))
                outList.Add(c);
        }
        return outList;
    }

    // ---------- 主流程 ----------
    private SubtitleResult FetchSync(string video, string title, int? year, string quality, List<string>? keywords)
    {
        if (!Config.Enabled)
            return new SubtitleResult { Ok = false, Message = "字幕功能未启用" };
        if (!File.Exists(video))
            return new SubtitleResult { Ok = false, Message = $"视频文件不存在：{video}" };

        var tokens = TokensFromVideo(video);
        if (!string.IsNullOrEmpty(quality))
            tokens.AddRange(HintTokens.Where(t => quality.ToLowerInvariant().Contains(t)));
        var client = new SubHdClient(Math.Max(10, Config.TimeoutSeconds), Proxy);
        var errors = new List<string>();

        foreach (var keyword in keywords ?? KeywordCandidates(video, title, year))
        {
            List<SubHdEntry> entries;
            try
            {
                entries = client.ListByKeywordAsync(keyword, year, Math.Max(1, Config.MaxMovies)).GetAwaiter().GetResult();
            }
            catch (Exception exc)
            {
                errors.Add($"{keyword}: {exc.Message}");
                continue;
            }
            if (entries.Count == 0)
            {
                errors.Add($"{keyword}: 无字幕条目");
                continue;
            }

            var ranked = entries
                .OrderByDescending(e => ScoreEntry(e, tokens, Config.PreferBilingual, Config.PreferSimplified))
                .Take(3)
                .ToList();
            var keywordIsCn = CjkRegex.IsMatch(keyword);
            var skipped = 0;
            foreach (var entry in ranked)
            {
                var score = ScoreEntry(entry, tokens, Config.PreferBilingual, Config.PreferSimplified);
                // 英文关键词经常会搜到同系列短片/别名（如用 WALL-E 搜到《电焊工波力》），
                // 此时要求条目里能找到片源特征（2160p/DVT…），否则宁可不配也不配错。
                if (!keywordIsCn && !TokenHit(entry, tokens)) { skipped++; continue; }
                if (!keywordIsCn && score < 2.0) { skipped++; continue; }
                SubtitleResult result;
                try
                {
                    result = TryEntry(client, entry, video);
                }
                catch (SubHdException exc)
                {
                    errors.Add($"{Truncate(entry.Title, 30)}: {exc.Message}");
                    continue;
                }
                if (result.Ok)
                    return result;
                errors.Add($"{Truncate(entry.Title, 30)}: {result.Message}");
            }
            if (skipped > 0)
            {
                errors.Add($"{keyword}: {skipped} 条候选与片源信息不匹配已跳过（可能是同系列短片或别名）");
            }
        }
        var tail = errors.Count > 0 ? string.Join("；", errors.TakeLast(3)) : "无可用候选";
        return new SubtitleResult { Ok = false, Message = $"未找到可用中文字幕（{tail}）" };
    }

    private SubtitleResult TryEntry(SubHdClient client, SubHdEntry entry, string video)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"moviedock-sub-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var archive = client.FetchEntryAsync(entry.Sid, tmpDir).GetAwaiter().GetResult();
            var extractDir = Path.Combine(tmpDir, "out");
            try
            {
                ArchiveExtractor.ExtractArchive(archive, extractDir, Config.ExtractTools);
            }
            catch (InvalidOperationException exc)
            {
                return new SubtitleResult { Ok = false, Message = $"解压失败：{exc.Message}" };
            }
            var files = ArchiveExtractor.FindSubtitleFiles(extractDir);
            if (files.Count == 0)
                return new SubtitleResult { Ok = false, Message = "压缩包里没有字幕文件" };

            var best = files.OrderByDescending(ScoreSubFile).First();
            var enc = ArchiveExtractor.NormalizeTextEncoding(best);
            var name = (Config.NameTemplate ?? "{video}.zh").Replace("{video}", Path.GetFileNameWithoutExtension(video));
            var target = Path.Combine(Path.GetDirectoryName(video)!, name + Path.GetExtension(best).ToLowerInvariant());
            File.Copy(best, target, overwrite: true);

            var note = $"{Truncate(entry.Title, 40)}（{Path.GetFileName(best)}，{files.Count} 个字幕文件";
            note += enc.Length > 0 && enc != "utf-8" ? $"，源编码 {enc}）" : "）";
            return new SubtitleResult { Ok = true, Message = note, Files = new List<string> { target }, EntryTitle = entry.Title };
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch (Exception) { /* 清理失败无所谓 */ }
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
