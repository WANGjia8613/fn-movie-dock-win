using MovieDock.Core.Config;
using MovieDock.Core.Models;
using MovieDock.Core.Organize;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 2 节（整理增强）与 review_checks 第 5 节（整理文件）。</summary>
public class OrganizerTests
{
    private static Organizer NewOrganizer(string root, string mode = "copy") =>
        new(new OrganizeConfig { Enabled = true, Mode = mode }, Path.Combine(root, "dl"));

    [Fact]
    public void Organize_SeriesPathAndName()
    {
        var td = TestSupport.NewTempDir();
        var src = Path.Combine(td, "raw", "Show.S01E02.2160p.WEB-DL.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllBytes(src, new byte[32]);

        var org = NewOrganizer(td);
        var final = org.OrganizeFile(src, "示例剧集", 2024, "2160p", new OrganizeOptions { Enabled = true, Mode = "copy" });

        // organize_series_path / organize_series_name / organize_series_nested
        Assert.Contains("Season 01", final);
        Assert.EndsWith(".mp4", final);
        Assert.Contains("S01E02", Path.GetFileName(final));
        // 路径分隔符归一成 / 再比对（模板里的 "/" 与 Path.Combine 的 "\" 会混用，对齐 Python 用例的 replace 写法）
        Assert.Contains("/Season 01/", final.Replace('\\', '/'));
    }

    [Fact]
    public void Organize_LibraryRootOverride()
    {
        var td = TestSupport.NewTempDir();
        var movie = Path.Combine(td, "raw", "WALL-E.2008.2160p.BDRemux.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(movie)!);
        File.WriteAllBytes(movie, new byte[32]);
        var lib = Path.Combine(td, "library");

        var org = NewOrganizer(td);
        var final = org.OrganizeFile(movie, "WALL-E", 2008, "2160p",
            new OrganizeOptions { Enabled = true, Mode = "copy", LibraryRoot = lib });

        var expectPrefix = Path.Combine(lib, "WALL-E (2008)");
        Assert.True(final.StartsWith(expectPrefix, StringComparison.OrdinalIgnoreCase), final);
    }

    [Fact]
    public void BuildPaths_PreviewKeepsExt()
    {
        var td = TestSupport.NewTempDir();
        var org = NewOrganizer(td);
        // 预览路径带扩展名（旧版硬写 .mkv 的回归）
        var (_, dest) = org.BuildPaths("沙丘2", 2024, "1080p", ext: ".mp4");
        Assert.Equal("沙丘2 (2024) - 1080p.mp4", Path.GetFileName(dest));
    }

    [Fact]
    public void Organize_MoviePath()
    {
        var td = TestSupport.NewTempDir();
        var src = Path.Combine(td, "raw", "file.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllBytes(src, new byte[10]);

        var org = NewOrganizer(td);
        var final = org.OrganizeFile(src, "沙丘2", 2024, "1080p", new OrganizeOptions { Enabled = true, Mode = "copy" });
        Assert.True(Path.GetFileName(final) == "沙丘2 (2024) - 1080p.mkv" && File.Exists(final), final);
    }

    [Fact]
    public void ApplyTemplate_UnknownYear()
    {
        var folder = NameParsing.ApplyTemplate("{title} ({year})", "测试", null, unknownYear: "未知年份");
        Assert.Equal("测试 (未知年份)", folder);
    }
}
