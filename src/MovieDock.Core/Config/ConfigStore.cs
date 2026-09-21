using System.Collections;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MovieDock.Core.Config;

/// <summary>
/// 配置加载/保存。YAML 结构与 Python 版（fn-movie-dock 0.4.1）兼容：
/// 默认值深合并文件值，环境变量优先，MOVIE_DOCK_CONFIG 指定路径。
/// 老配置（0.2.x 无 builtin）加载时自动补上内置索引源并默认启用（用户踩过：升级后搜索依旧是空的）。
/// </summary>
public sealed class ConfigStore
{
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieDock", "config.yaml");

    private readonly string _filePath;
    private readonly Func<string, string?> _env;

    public ConfigStore(Func<string, string?>? env = null, string? filePath = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        _filePath = filePath ?? _env("MOVIE_DOCK_CONFIG") ?? DefaultFilePath;
    }

    public string FilePath => _filePath;

    public AppConfig Load()
    {
        var data = MergeDefaults(ReadYamlFile());
        ApplyEnvOverrides(data);
        var cfg = DeserializeAppConfig(SerializeDict(data));
        if (EnsureProviderTypes(cfg, migrateBuiltin: true) && File.Exists(_filePath))
        {
            // 老配置自动补上新源后落盘一份，避免“升了版本但功能没生效”（对齐 Python load_config）
            try { Save(cfg); }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }
        }
        return cfg;
    }

    /// <summary>把内存配置完整写回配置文件（保留文件里未知的顶层键）。</summary>
    public string Save(AppConfig cfg)
    {
        var existing = ReadYamlFile();
        existing["llm"] = ToDict(cfg.Llm);
        existing["paths"] = ToDict(cfg.Paths);
        existing["network"] = ToDict(cfg.Network);
        existing["organize"] = ToDict(cfg.Organize);
        existing["search"] = ToDict(cfg.Search);
        existing["downloader"] = ToDict(cfg.Downloader);
        existing["subtitle"] = ToDict(cfg.Subtitle);
        existing["server"] = ToDict(cfg.Server);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, SerializeDict(existing));
        return _filePath;
    }

    private Dictionary<string, object> ReadYamlFile()
    {
        if (!File.Exists(_filePath)) return new();
        var text = File.ReadAllText(_filePath);
        if (string.IsNullOrWhiteSpace(text)) return new();
        var deserializer = new DeserializerBuilder().Build();
        var obj = deserializer.Deserialize(text) ?? new Dictionary<string, object>();
        return NormalizeDict(obj);
    }

    private static Dictionary<string, object> MergeDefaults(Dictionary<string, object> fileData)
    {
        return (Dictionary<string, object>)MergeValue(BuildDefaultData(), fileData);
    }

    /// <summary>与 Python 版 _DEFAULT_DATA 对齐（桌面模式：download_root 留空走系统默认目录）。</summary>
    private static Dictionary<string, object> BuildDefaultData() => new()
    {
        ["server"] = new Dictionary<string, object> { ["host"] = "0.0.0.0", ["port"] = 8090 },
        ["llm"] = new Dictionary<string, object>
        {
            ["base_url"] = "https://api.openai.com/v1",
            ["api_key"] = "",
            ["model"] = "gpt-4o-mini",
            ["timeout_seconds"] = 60,
        },
        ["paths"] = new Dictionary<string, object> { ["download_root"] = "", ["state_dir"] = "" },
        ["network"] = new Dictionary<string, object> { ["proxy"] = "", ["timeout_seconds"] = 20 },
        ["search"] = new Dictionary<string, object>
        {
            ["providers"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "builtin", ["enabled"] = true, ["name"] = "内置索引",
                    ["options"] = new Dictionary<string, object> { ["sources"] = "tpb,yts,dmhy", ["limit"] = "60" },
                },
                new Dictionary<string, object>
                {
                    ["type"] = "qbittorrent", ["enabled"] = false, ["name"] = "qBittorrent 搜索",
                    ["url"] = "http://127.0.0.1:8085",
                    ["options"] = new Dictionary<string, object>
                    {
                        ["username"] = "admin", ["password"] = "", ["plugins"] = "", ["limit"] = "100",
                    },
                },
                new Dictionary<string, object> { ["type"] = "demo", ["enabled"] = false, ["name"] = "演示数据" },
                new Dictionary<string, object> { ["type"] = "llm", ["enabled"] = false, ["name"] = "大模型检索" },
                new Dictionary<string, object>
                {
                    ["type"] = "custom_api", ["enabled"] = false, ["name"] = "自定义索引",
                    ["url"] = "", ["method"] = "GET", ["headers"] = new Dictionary<string, object>(),
                },
            },
            ["sort_by_score"] = true,
        },
        ["organize"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["movie_dir_template"] = "{title} ({year})",
            ["file_name_template"] = "{title} ({year}) - {quality}",
            ["series_dir_template"] = "{title} ({year})/Season {season}",
            ["series_file_template"] = "{title} ({year}) - S{season}E{episode} - {quality}",
            ["mode"] = "move",
            ["unknown_year"] = "未知年份",
            ["movies_subdir"] = "movies",
            ["library_root"] = "",
        },
        ["downloader"] = new Dictionary<string, object>
        {
            ["engine"] = "aria2",
            ["aria2"] = new Dictionary<string, object>
            {
                ["rpc_url"] = "http://127.0.0.1:6800/jsonrpc",
                ["rpc_secret"] = "",
                ["auto_start"] = false,
                ["binary"] = "",
                ["port"] = 6800,
            },
            ["max_concurrent"] = 3,
            ["category_dir"] = "incoming",
            ["per_task_dir"] = true,
            ["extra_options"] = new Dictionary<string, object>(),
            ["bt_trackers"] = "",
        },
        ["subtitle"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["provider"] = "subhd",
            ["extra_keywords"] = new List<object>(),
            ["match_hint"] = "",
            ["prefer_bilingual"] = true,
            ["prefer_simplified"] = true,
            ["name_template"] = "{video}.zh",
            ["extract_tools"] = new List<object> { "7z", "7zz", "bsdtar", "unrar", "unar" },
            ["timeout_seconds"] = 30,
            ["llm_translate"] = true,
            ["max_movies"] = 2,
            ["soft_fail"] = true,
        },
    };

    private void ApplyEnvOverrides(Dictionary<string, object> data)
    {
        var overrides = new (string EnvKey, string[] Path)[]
        {
            ("LLM_BASE_URL", new[] { "llm", "base_url" }),
            ("LLM_API_KEY", new[] { "llm", "api_key" }),
            ("LLM_MODEL", new[] { "llm", "model" }),
            ("DOWNLOAD_ROOT", new[] { "paths", "download_root" }),
            ("STATE_DIR", new[] { "paths", "state_dir" }),
            ("ARIA2_RPC_URL", new[] { "downloader", "aria2", "rpc_url" }),
            ("ARIA2_RPC_SECRET", new[] { "downloader", "aria2", "rpc_secret" }),
            ("SERVER_PORT", new[] { "server", "port" }),
        };
        foreach (var (envKey, pathKeys) in overrides)
        {
            var raw = _env(envKey);
            if (string.IsNullOrEmpty(raw)) continue;
            IDictionary cursor = data;
            for (var i = 0; i < pathKeys.Length - 1; i++)
            {
                var key = pathKeys[i];
                if (cursor.Contains(key) && cursor[key] is IDictionary) cursor = (IDictionary)cursor[key]!;
                else
                {
                    var created = new Dictionary<string, object>();
                    cursor[key] = created;
                    cursor = created;
                }
            }
            var leaf = pathKeys[^1];
            object value;
            if (leaf == "port")
            {
                if (!int.TryParse(raw, out var port)) continue;
                value = port;
            }
            else
            {
                value = raw;
            }
            cursor[leaf] = value;
        }
    }

    /// <summary>
    /// 保证内置源与三类基础源都存在；返回是否有改动。
    /// migrateBuiltin=true 时：若配置里根本没有 builtin（0.2.x 老配置），
    /// 则补上并默认启用 —— 否则升级后搜索依旧是空的（用户实际踩过）。
    /// </summary>
    public static bool EnsureProviderTypes(AppConfig cfg, bool migrateBuiltin = true)
    {
        var changed = false;
        var existing = cfg.Search.Providers.Select(p => p.Type).ToHashSet();
        var merged = new List<ProviderConfig>(cfg.Search.Providers);

        var required = new[]
        {
            new ProviderConfig { Type = "demo", Enabled = false, Name = "演示数据" },
            new ProviderConfig { Type = "llm", Enabled = false, Name = "大模型检索" },
            new ProviderConfig { Type = "custom_api", Enabled = false, Name = "自定义索引", Url = "", Method = "GET", Headers = new() },
        };
        foreach (var item in required)
        {
            if (existing.Contains(item.Type)) continue;
            merged.Add(item);
            existing.Add(item.Type);
            changed = true;
        }

        if (migrateBuiltin && !existing.Contains("builtin"))
        {
            merged.Insert(0, new ProviderConfig
            {
                Type = "builtin",
                Enabled = true,
                Name = "内置索引",
                Options = new Dictionary<string, object>
                {
                    ["sources"] = "tpb,yts,dmhy",
                    ["limit"] = "60",
                },
            });
            changed = true;
        }

        if (changed)
            cfg.Search.Providers = merged;
        return changed;
    }

    private static AppConfig DeserializeAppConfig(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<AppConfig>(new StringReader(yaml)) ?? new AppConfig();
    }

    private static string SerializeDict(Dictionary<string, object> data)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();
        return serializer.Serialize(data);
    }

    private static Dictionary<string, object> ToDict(object section)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(section);
        var deserializer = new DeserializerBuilder().Build();
        return NormalizeDict(deserializer.Deserialize(yaml) ?? new Dictionary<string, object>());
    }

    private static object MergeValue(object baseValue, object overrideValue)
    {
        if (baseValue is IDictionary b && overrideValue is IDictionary o)
        {
            var merged = NormalizeDict(b);
            foreach (var (key, value) in NormalizeDict(o))
                merged[key] = merged.TryGetValue(key, out var bv) && bv is IDictionary && value is IDictionary
                    ? MergeValue(bv, value)
                    : value;
            return merged;
        }
        return overrideValue;
    }

    private static Dictionary<string, object> NormalizeDict(object o)
    {
        var result = new Dictionary<string, object>();
        if (o is IDictionary d)
            foreach (DictionaryEntry e in d)
                result[Convert.ToString(e.Key) ?? ""] = e.Value ?? "";
        return result;
    }
}
