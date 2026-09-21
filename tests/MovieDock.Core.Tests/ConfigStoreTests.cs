using MovieDock.Core.Config;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 review_checks 第 1/6 节（环境变量 / 默认配置不污染 / 持久化）与 unit_checks 第 8 节（配置往返）。</summary>
public class ConfigStoreTests
{
    private static ConfigStore NewStore(string filePath, Dictionary<string, string?>? env = null) =>
        new(key => env is not null && env.TryGetValue(key, out var v) ? v : null, filePath);

    [Fact]
    public void Env_ApiKeyApplied()
    {
        var td = TestSupport.NewTempDir();
        var env = new Dictionary<string, string?> { ["LLM_API_KEY"] = "sk-test-key-should-not-leak-to-default" };
        var cfg = NewStore(Path.Combine(td, "config.yaml"), env).Load();
        Assert.Equal("sk-test-key-should-not-leak-to-default", cfg.Llm.ApiKey);
    }

    [Fact]
    public void Env_DefaultNotPolluted()
    {
        // 先用带环境变量的 store 加载一次，再用干净环境加载 —— 默认值不得被上一次污染
        var td = TestSupport.NewTempDir();
        var env = new Dictionary<string, string?> { ["LLM_API_KEY"] = "sk-leak-check" };
        _ = NewStore(Path.Combine(td, "a.yaml"), env).Load();
        var cfg2 = NewStore(Path.Combine(td, "b.yaml")).Load();
        Assert.Equal("", cfg2.Llm.ApiKey);
    }

    [Fact]
    public void Env_DownloadRootApplied()
    {
        var td = TestSupport.NewTempDir();
        var root = Path.Combine(td, "downloads");
        var env = new Dictionary<string, string?> { ["DOWNLOAD_ROOT"] = root };
        var cfg = NewStore(Path.Combine(td, "config.yaml"), env).Load();
        Assert.Equal(System.IO.Path.GetFullPath(root), cfg.DownloadRoot());
    }

    [Fact]
    public void Default_HasCustomApiProvider()
    {
        var td = TestSupport.NewTempDir();
        var cfg = NewStore(Path.Combine(td, "config.yaml")).Load();
        Assert.Contains(cfg.Search.Providers, p => p.Type == "custom_api");
    }

    [Fact]
    public void Persist_Llm()
    {
        var td = TestSupport.NewTempDir();
        var path = Path.Combine(td, "config.yaml");
        var store = NewStore(path);
        var cfg = store.Load();
        cfg.Llm.ApiKey = "sk-persist";
        cfg.Llm.BaseUrl = "https://example.com/v1";
        store.Save(cfg);
        var cfg3 = NewStore(path).Load();
        Assert.True(cfg3.Llm.ApiKey == "sk-persist" && cfg3.Llm.BaseUrl == "https://example.com/v1",
            $"{cfg3.Llm.ApiKey}/{cfg3.Llm.BaseUrl}");
    }

    [Fact]
    public void Persist_Organize()
    {
        var td = TestSupport.NewTempDir();
        var path = Path.Combine(td, "config.yaml");
        var store = NewStore(path);
        var cfg = store.Load();
        cfg.Organize.MovieDirTemplate = "{title}_{year}";
        store.Save(cfg);
        var cfg3 = NewStore(path).Load();
        Assert.Equal("{title}_{year}", cfg3.Organize.MovieDirTemplate);
    }

    [Fact]
    public void Persist_ProviderEnabled()
    {
        var td = TestSupport.NewTempDir();
        var path = Path.Combine(td, "config.yaml");
        var store = NewStore(path);
        var cfg = store.Load();
        foreach (var p in cfg.Search.Providers)
            if (p.Type == "demo") p.Enabled = false;
        store.Save(cfg);
        var cfg3 = NewStore(path).Load();
        var demo = cfg3.Search.Providers.First(p => p.Type == "demo");
        Assert.False(demo.Enabled);
    }

    [Fact]
    public void Config_OptionsRoundtrip()
    {
        var td = TestSupport.NewTempDir();
        var path = Path.Combine(td, "config.yaml");
        var store = NewStore(path);
        var cfg = store.Load();
        var custom = cfg.Search.Providers.First(p => p.Type == "custom_api");
        custom.Options = new Dictionary<string, object> { ["foo"] = "bar" };
        store.Save(cfg);
        var reload = NewStore(path).Load();
        var custom2 = reload.Search.Providers.First(p => p.Type == "custom_api");
        Assert.True(custom2.Options.TryGetValue("foo", out var v) && v?.ToString() == "bar",
            string.Join(",", custom2.Options.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Fact]
    public void Config_LibraryRootRoundtrip()
    {
        var td = TestSupport.NewTempDir();
        var path = Path.Combine(td, "config.yaml");
        var store = NewStore(path);
        var cfg = store.Load();
        cfg.Organize.LibraryRoot = "/vol2/1000/movie";
        store.Save(cfg);
        var reload = NewStore(path).Load();
        Assert.Equal("/vol2/1000/movie", reload.Organize.LibraryRoot);
    }

    [Fact]
    public void Config_SubtitleHintRoundtrip()
    {
        var td = TestSupport.NewTempDir();
        var path = Path.Combine(td, "config.yaml");
        var store = NewStore(path);
        var cfg = store.Load();
        cfg.Subtitle.MatchHint = "4K适配HDR";
        store.Save(cfg);
        var reload = NewStore(path).Load();
        Assert.Equal("4K适配HDR", reload.Subtitle.MatchHint);
    }
}
