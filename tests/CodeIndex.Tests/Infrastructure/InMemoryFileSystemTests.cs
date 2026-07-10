using CodeIndex.Abstractions;

namespace CodeIndex.Tests.Infrastructure;

public sealed class InMemoryFileSystemTests
{
    private readonly InMemoryFileSystem _fs = new();

    // ── EnumerateFiles (glob-aware) ─────────────────────────────────────────────

    [Fact]
    public void EnumerateFiles_TopDirectoryOnly_ReturnsOnlyDirectChildren()
    {
        _fs.AddFile(@"C:\Repo\A.cs", "a");
        _fs.AddFile(@"C:\Repo\Sub\B.cs", "b");

        string[] hits = _fs.EnumerateFiles(@"C:\Repo", "*.cs", SearchOption.TopDirectoryOnly).ToArray();

        hits.Should().ContainSingle().Which.Should().Be(@"C:\Repo\A.cs");
    }

    [Fact]
    public void EnumerateFiles_AllDirectories_ReturnsNestedMatches()
    {
        _fs.AddFile(@"C:\Repo\A.cs", "a");
        _fs.AddFile(@"C:\Repo\Sub\B.cs", "b");
        _fs.AddFile(@"C:\Repo\Sub\C.txt", "c");

        string[] hits = _fs.EnumerateFiles(@"C:\Repo", "*.cs", SearchOption.AllDirectories).ToArray();

        hits.Should().BeEquivalentTo([@"C:\Repo\A.cs", @"C:\Repo\Sub\B.cs"]);
    }

    [Fact]
    public void EnumerateFiles_HonorsVersionedCacheGlob()
    {
        _fs.AddBinaryFile(@"C:\Repo\.cache\index.v4.cache", [1]);
        _fs.AddBinaryFile(@"C:\Repo\.cache\index.ts.v1.cache", [2]);
        _fs.AddBinaryFile(@"C:\Repo\.cache\index.cache", [3]);

        string[] hits = _fs.EnumerateFiles(@"C:\Repo\.cache", "index.v*.cache", SearchOption.TopDirectoryOnly).ToArray();

        hits.Should().ContainSingle().Which.Should().Be(@"C:\Repo\.cache\index.v4.cache");
    }

    [Fact]
    public void EnumerateFiles_DoesNotMatchSiblingPrefixDirectory()
    {
        _fs.AddFile(@"C:\Repo\A.cs", "a");
        _fs.AddFile(@"C:\RepoX\B.cs", "b");

        string[] hits = _fs.EnumerateFiles(@"C:\Repo", "*.cs", SearchOption.AllDirectories).ToArray();

        hits.Should().ContainSingle().Which.Should().Be(@"C:\Repo\A.cs");
    }

    // ── EnumerateDirectoryEntries ───────────────────────────────────────────────

    [Fact]
    public void EnumerateDirectoryEntries_ReturnsDirectFilesAndDirectories()
    {
        _fs.AddFile(@"C:\Repo\A.cs", "a", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        _fs.AddFile(@"C:\Repo\Sub\B.cs", "b");

        List<FileSystemEntry> entries = _fs.EnumerateDirectoryEntries(@"C:\Repo").ToList();

        entries.Should().ContainSingle(e => !e.IsDirectory)
               .Which.Name.Should().Be("A.cs");
        entries.Should().ContainSingle(e => e.IsDirectory)
               .Which.Name.Should().Be("Sub");
    }

    [Fact]
    public void EnumerateDirectoryEntries_CarriesLastWriteTicks()
    {
        DateTime stamp = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        _fs.AddFile(@"C:\Repo\A.cs", "a", stamp);

        FileSystemEntry file = _fs.EnumerateDirectoryEntries(@"C:\Repo").Single(e => !e.IsDirectory);

        file.LastWriteTimeUtcTicks.Should().Be(stamp.Ticks);
        file.IsReparsePoint.Should().BeFalse();
    }

    [Fact]
    public void EnumerateDirectoryEntries_MissingDirectory_ReturnsEmpty()
    {
        _fs.EnumerateDirectoryEntries(@"C:\Nope").Should().BeEmpty();
    }

    // ── ReadAllLines ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a\r\nb\r\nc", 3)]
    [InlineData("a\nb\nc\n", 3)]      // trailing newline does NOT add an empty line
    [InlineData("a\r\n\r\nb", 3)]     // blank line preserved
    [InlineData("", 0)]
    public void ReadAllLines_MatchesFileReadAllLinesSemantics(string content, int expectedCount)
    {
        _fs.AddFile(@"C:\Repo\f.txt", content);

        _fs.ReadAllLines(@"C:\Repo\f.txt").Should().HaveCount(expectedCount);
    }

    // ── WriteAllBytes / timestamps ──────────────────────────────────────────────

    [Fact]
    public void WriteAllBytes_StampsLastWriteTime()
    {
        _fs.WriteAllBytes(@"C:\Repo\.cache\index.v4.cache", [1, 2, 3]);

        _fs.GetLastWriteTimeUtc(@"C:\Repo\.cache\index.v4.cache")
           .Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        _fs.ReadAllBytes(@"C:\Repo\.cache\index.v4.cache").Should().Equal(1, 2, 3);
    }

    // ── MoveFile (atomic replace) ───────────────────────────────────────────────

    [Fact]
    public void MoveFile_Overwrite_ReplacesDestinationAndRemovesSource()
    {
        _fs.AddBinaryFile(@"C:\Repo\dst.cache", [9]);
        _fs.AddBinaryFile(@"C:\Repo\src.tmp", [1, 2]);

        _fs.MoveFile(@"C:\Repo\src.tmp", @"C:\Repo\dst.cache", overwrite: true);

        _fs.FileExists(@"C:\Repo\src.tmp").Should().BeFalse();
        _fs.ReadAllBytes(@"C:\Repo\dst.cache").Should().Equal(1, 2);
    }

    [Fact]
    public void MoveFile_NoOverwrite_ExistingDestination_Throws()
    {
        _fs.AddBinaryFile(@"C:\Repo\dst.cache", [9]);
        _fs.AddBinaryFile(@"C:\Repo\src.tmp", [1]);

        _fs.Invoking(f => f.MoveFile(@"C:\Repo\src.tmp", @"C:\Repo\dst.cache", overwrite: false))
           .Should().Throw<IOException>();
    }

    [Fact]
    public void MoveFile_MissingSource_Throws()
    {
        _fs.Invoking(f => f.MoveFile(@"C:\Repo\none.tmp", @"C:\Repo\dst.cache", overwrite: true))
           .Should().Throw<FileNotFoundException>();
    }

    // ── DeleteFile ──────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteFile_RemovesFileAndTimestamp()
    {
        _fs.AddFile(@"C:\Repo\A.cs", "a");

        _fs.DeleteFile(@"C:\Repo\A.cs");

        _fs.FileExists(@"C:\Repo\A.cs").Should().BeFalse();
        _fs.GetLastWriteTimeUtc(@"C:\Repo\A.cs").Should().Be(DateTime.MinValue);
    }

    // ── SetLastWriteTimeUtc seam ────────────────────────────────────────────────

    [Fact]
    public void SetLastWriteTimeUtc_OverridesTimestamp()
    {
        _fs.AddFile(@"C:\Repo\A.cs", "a");
        DateTime aged = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _fs.SetLastWriteTimeUtc(@"C:\Repo\A.cs", aged);

        _fs.GetLastWriteTimeUtc(@"C:\Repo\A.cs").Should().Be(aged);
    }

    // ── ReadAllText / ReadAllBytes cross-form ───────────────────────────────────

    [Fact]
    public void ReadAllText_MissingFile_Throws()
    {
        _fs.Invoking(f => f.ReadAllText(@"C:\Repo\missing.cs"))
           .Should().Throw<FileNotFoundException>();
    }
}
