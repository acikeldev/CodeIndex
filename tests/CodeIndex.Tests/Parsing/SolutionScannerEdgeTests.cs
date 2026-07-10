using CodeIndex.Abstractions;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;
using NSubstitute.ExceptionExtensions;

namespace CodeIndex.Tests.Parsing;

/// <summary>Edge/branch coverage for <see cref="SolutionScanner"/> (defensive + rare paths).</summary>
public sealed class SolutionScannerEdgeTests
{
    [Fact]
    public void Scan_MalformedSlnx_IsSkipped()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\S.slnx", "<not-valid-xml");
        fs.AddFile(@"C:\repo\P\P.csproj", "<Project/>");
        fs.AddFile(@"C:\repo\P\A.cs", "class A { }");

        SolutionScanner.ScanResult r = new SolutionScanner(fs).Scan(@"C:\repo");

        r.Projects.Should().BeEmpty(); // slnx unreadable, no loose projects -> nothing registered
    }

    [Fact]
    public void Scan_NonCsprojSolutionEntry_Ignored()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\S.slnx", "<Solution><Project Path=\"P/P.vcxproj\" /></Solution>");
        fs.AddFile(@"C:\repo\P\P.vcxproj", "<Project/>");

        SolutionScanner.ScanResult r = new SolutionScanner(fs).Scan(@"C:\repo");

        r.Projects.Should().BeEmpty();
    }

    [Fact]
    public void Scan_DuplicateProjectReference_DedupedByDirectory()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\A.slnx", "<Solution><Project Path=\"P/P.csproj\" /></Solution>");
        fs.AddFile(@"C:\repo\B.slnx", "<Solution><Project Path=\"P/P.csproj\" /></Solution>");
        fs.AddFile(@"C:\repo\P\P.csproj", "<Project/>");
        fs.AddFile(@"C:\repo\P\A.cs", "class A { }");

        SolutionScanner.ScanResult r = new SolutionScanner(fs).Scan(@"C:\repo");

        r.Projects.Should().ContainSingle().Which.Name.Should().Be("P");
    }

    [Fact]
    public void Scan_InaccessibleDirectory_SkippedGracefully()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        fs.EnumerateDirectoryEntries(@"C:\repo").Throws(new UnauthorizedAccessException());

        SolutionScanner.ScanResult r = new SolutionScanner(fs).Scan(@"C:\repo");

        r.Projects.Should().BeEmpty();
        r.Timestamps.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ReparsePointDirectory_NotFollowed()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        fs.EnumerateDirectoryEntries(@"C:\repo").Returns(new[]
        {
            new FileSystemEntry(@"C:\repo\link", "link", IsDirectory: true, IsReparsePoint: true, LastWriteTimeUtcTicks: 0),
        });

        SolutionScanner.ScanResult r = new SolutionScanner(fs).Scan(@"C:\repo");

        r.Timestamps.Should().BeEmpty();
        fs.DidNotReceive().EnumerateDirectoryEntries(@"C:\repo\link");
    }

    [Fact]
    public void Scan_SlnReadThrows_Skipped()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        fs.EnumerateDirectoryEntries(@"C:\repo").Returns(new[]
        {
            new FileSystemEntry(@"C:\repo\S.sln", "S.sln", IsDirectory: false, IsReparsePoint: false, LastWriteTimeUtcTicks: 1),
        });
        fs.ReadAllText(@"C:\repo\S.sln").Throws(new IOException("locked"));

        SolutionScanner.ScanResult r = new SolutionScanner(fs).Scan(@"C:\repo");

        r.Projects.Should().BeEmpty();
    }

    [Fact]
    public void IsIndexableCsOnDisk_DesignerFile_RequiresSiblingDbml()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\M.designer.cs", "class M { }");
        SolutionScanner scanner = new(fs);

        scanner.IsIndexableCsOnDisk(@"C:\repo\M.designer.cs").Should().BeFalse();

        fs.AddFile(@"C:\repo\M.dbml", "<Database/>");
        scanner.IsIndexableCsOnDisk(@"C:\repo\M.designer.cs").Should().BeTrue();

        // a plain .cs is always indexable
        scanner.IsIndexableCsOnDisk(@"C:\repo\Plain.cs").Should().BeTrue();
    }
}
