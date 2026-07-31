using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Services;

public class FileNameIndexTests
{
    static FileNameIndex BuildSample()
    {
        // 模擬 C:\Users\dev\Docs\report.pdf 等層級（FRN 任意取值，父鏈成立即可）
        var index = new FileNameIndex('c');
        index.Set(10, 0, "Users", isDir: true);
        index.Set(20, 10, "dev", isDir: true);
        index.Set(30, 20, "Docs", isDir: true);
        index.Set(40, 30, "report.pdf", isDir: false);
        index.Set(41, 30, "Report Final.docx", isDir: false);
        index.Set(50, 20, "report", isDir: true);
        index.Set(60, 10, "readme.md", isDir: false);
        return index;
    }

    [Fact]
    public void ResolvePath_WalksParentChainToVolumeRoot()
    {
        var index = BuildSample();
        index.ResolvePath(40).Should().Be(@"C:\Users\dev\Docs\report.pdf");
        index.ResolvePath(10).Should().Be(@"C:\Users");
    }

    [Fact]
    public void Search_MatchesCaseInsensitiveSubstring()
    {
        var index = BuildSample();
        var hits = new List<FileHit>();
        index.SearchInto("REPORT", hits, 10);
        hits.Should().HaveCount(3);
        hits.Select(h => h.Name).Should().Contain(["report.pdf", "Report Final.docx", "report"]);
    }

    [Fact]
    public void Search_ExactAndPrefixRankAboveSubstring()
    {
        var index = BuildSample();
        var hits = new List<FileHit>();
        index.SearchInto("report", hits, 10);
        hits = [.. hits.OrderByDescending(h => h.Score)];
        // 完整命中 "report"（目錄）得分最高；"Report Final.docx" 前綴優於無前綴
        hits[0].Name.Should().Be("report");
        hits[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Search_MultiTokenRequiresAllTokens()
    {
        var index = BuildSample();
        var hits = new List<FileHit>();
        index.SearchInto("report final", hits, 10);
        hits.Should().ContainSingle().Which.Name.Should().Be("Report Final.docx");
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var index = BuildSample();
        var hits = new List<FileHit>();
        index.SearchInto("e", hits, 2);
        hits.Should().HaveCount(2);
    }

    [Fact]
    public void Remove_DeletedEntryNoLongerFound()
    {
        var index = BuildSample();
        index.Remove(40);
        var hits = new List<FileHit>();
        index.SearchInto("report.pdf", hits, 10);
        hits.Should().BeEmpty();
        index.Count.Should().Be(6);
    }

    [Fact]
    public void Set_RenameUpdatesNameAndParent()
    {
        var index = BuildSample();
        // 模擬 USN RENAME_NEW_NAME：同 FRN 換名字換父目錄（移動 + 改名）
        index.Set(40, 20, "summary.pdf", isDir: false);
        index.ResolvePath(40).Should().Be(@"C:\Users\dev\summary.pdf");
        var hits = new List<FileHit>();
        index.SearchInto("report.pdf", hits, 10);
        hits.Should().BeEmpty();
    }

    [Fact]
    public void ResolvePath_OrphanParentFallsBackToRoot()
    {
        var index = new FileNameIndex('d');
        index.Set(99, 12345, "lonely.txt", isDir: false); // 父 FRN 不在索引中（頂層項的父即卷根）
        index.ResolvePath(99).Should().Be(@"D:\lonely.txt");
    }

    [Fact]
    public void Search_EmptyQueryReturnsNothing()
    {
        var index = BuildSample();
        var hits = new List<FileHit>();
        index.SearchInto("   ", hits, 10);
        hits.Should().BeEmpty();
    }

    [Fact]
    public void ConcurrentReadWrite_DoesNotThrow()
    {
        var index = BuildSample();
        var stop = false;
        var writer = Task.Run(() =>
        {
            for (ulong i = 100; i < 3000; i++) { index.Set(i, 30, $"file{i}.txt", false); if (i % 7 == 0) index.Remove(i - 3); }
            stop = true;
        });
        var reader = Task.Run(() =>
        {
            while (!stop) { var hits = new List<FileHit>(); index.SearchInto("file", hits, 20); _ = index.ResolvePath(40); }
        });
        var act = () => Task.WaitAll(writer, reader);
        act.Should().NotThrow();
        index.Count.Should().BeGreaterThan(2000);
    }
}
