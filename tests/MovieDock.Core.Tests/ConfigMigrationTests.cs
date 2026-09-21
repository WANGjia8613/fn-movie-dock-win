using MovieDock.Core.Config;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 12 节（老配置自动迁移：0.2.x 没有 builtin，升级后必须补上）。</summary>
public class ConfigMigrationTests
{
    /// <summary>写一份 0.2.x 老配置（无 builtin 源），返回 (store, 配置路径)。</summary>
    private static (ConfigStore Store, string Path) WriteOldConfig()
    {
        var dir = TestSupport.NewTempDir("moviedock-migrate-");
        var path = Path.Combine(dir, "config.yaml");
        File.WriteAllText(path, $$"""
            paths:
              download_root: {{Json(Path.Combine(dir, "dl"))}}
              state_dir: {{Json(Path.Combine(dir, "data"))}}
            search:
              providers:
                - type: demo
                  enabled: false
                  name: 演示数据
                - type: llm
                  enabled: false
                  name: 大模型检索
                - type: qbittorrent
                  enabled: true
                  name: qBittorrent 搜索
                  url: http://127.0.0.1:8085
                  options:
                    username: admin
                    password: ""
                    plugins: yts,bt4g
                - type: custom_api
                  enabled: false
                  name: 自定义索引
            """);
        return (new ConfigStore(_ => null, path), path);
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void Migrate_BuiltinInjected()
    {
        var (store, _) = WriteOldConfig();
        var types = store.Load().Search.Providers.Select(p => p.Type).ToList();
        Assert.Contains("builtin", types);
    }

    [Fact]
    public void Migrate_BuiltinEnabled()
    {
        var (store, _) = WriteOldConfig();
        var migrated = store.Load();
        Assert.True(migrated.Search.Providers.First(p => p.Type == "builtin").Enabled);
    }

    [Fact]
    public void Migrate_BuiltinFirst()
    {
        var (store, _) = WriteOldConfig();
        var types = store.Load().Search.Providers.Select(p => p.Type).ToList();
        Assert.Equal("builtin", types[0]);
    }

    [Fact]
    public void Migrate_KeepsOldProviders()
    {
        var (store, _) = WriteOldConfig();
        var types = store.Load().Search.Providers.Select(p => p.Type).ToList();
        Assert.Contains("qbittorrent", types);
    }

    [Fact]
    public void Migrate_Persisted()
    {
        var (store, path) = WriteOldConfig();
        _ = store.Load();
        // 迁移结果应落盘一份（对齐 Python load_config 的 save_app_config 回写）
        var back = File.ReadAllText(path);
        Assert.Contains("builtin", back);
    }
}
