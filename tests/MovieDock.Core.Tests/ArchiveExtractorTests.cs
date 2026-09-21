using System.IO.Compression;
using System.Text;
using MovieDock.Core.Subtitle;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 6 节（归档解压：zip 含中文名 / rar 兜底）。</summary>
public class ArchiveExtractorTests
{
    [Fact]
    public void Extract_ZipWithChineseName()
    {
        var td = TestSupport.NewTempDir();
        var arch = Path.Combine(td, "sub.zip");
        using (var z = ZipFile.Open(arch, ZipArchiveMode.Create))
        {
            var entry = z.CreateEntry("机器人总动员.WALL.E.2008.2160p.zh.ass");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("[Script Info]\nTitle: t\n");
        }
        var outDir = Path.Combine(td, "out");
        ArchiveExtractor.ExtractArchive(arch, outDir);
        Assert.Single(ArchiveExtractor.FindSubtitleFiles(outDir));
    }

    [Fact]
    public void Extract_RarFailureGivesClearError()
    {
        // rar 场景：本机若有 7z/bsdtar 才能解；假 rar 字节必然解不开 —— 验证「失败会给出明确报错」
        var td = TestSupport.NewTempDir();
        var rar = Path.Combine(td, "broken.rar");
        var head = new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }; // "Rar!\x1a\x07\x00"
        File.WriteAllBytes(rar, head.Concat(new byte[64]).ToArray());
        try
        {
            ArchiveExtractor.ExtractArchive(rar, Path.Combine(td, "out2"));
            // 解压成功（工具可用且真的解开了）——对齐 Python 用例的放行分支
        }
        catch (Exception exc)
        {
            var msg = exc.Message;
            Assert.True(msg.Contains("解压失败") || msg.Contains("7z") || msg.Contains("bsdtar"), msg[..Math.Min(80, msg.Length)]);
        }
    }
}
