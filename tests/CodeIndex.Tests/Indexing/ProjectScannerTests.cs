using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using CodeIndex.Models;

namespace CodeIndex.Tests.Indexing;

public sealed class ProjectScannerTests
{
    private readonly IFileSystem _fs = Substitute.For<IFileSystem>();
    private readonly ProjectScanner _scanner;
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    public ProjectScannerTests()
    {
        _scanner = new ProjectScanner(_fs);
    }

    [Fact]
    public void Scan_ProjectNameDerivedFromCsprojFilename()
    {
        SetupEmptyProject(@"C:\Repo\MyApp\MyApp.csproj");

        ProjectIndex result = _scanner.Scan(@"C:\Repo\MyApp\MyApp.csproj", NoCached);

        result.Name.Should().Be("MyApp");
        result.ProjectFilePath.Should().Be(@"C:\Repo\MyApp\MyApp.csproj");
        result.Directory.Should().Be(@"C:\Repo\MyApp");
    }

    [Fact]
    public void Scan_ParsesValidCsFile()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\Foo.cs";

        SetupProject(proj, [file], [(file, "namespace App; public class Foo {}", T0)]);

        ProjectIndex result = _scanner.Scan(proj, NoCached);

        result.SourceFiles.Should().ContainSingle()
            .Which.FileName.Should().Be("Foo.cs");
    }

    [Fact]
    public void Scan_SkipsDesignerFiles()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\Form.Designer.cs";

        _fs.DirectoryExists(@"C:\Repo\App").Returns(true);
        _fs.EnumerateFiles(@"C:\Repo\App", "*.cs", SearchOption.AllDirectories)
           .Returns([file]);
        _fs.FileExists(file).Returns(true);
        _fs.GetLastWriteTimeUtc(file).Returns(T0);

        ProjectIndex result = _scanner.Scan(proj, NoCached);

        result.SourceFiles.Should().BeEmpty();
    }

    [Fact]
    public void Scan_SkipsFilesInBinDirectory()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\bin\Debug\Foo.cs";

        _fs.DirectoryExists(@"C:\Repo\App").Returns(true);
        _fs.EnumerateFiles(@"C:\Repo\App", "*.cs", SearchOption.AllDirectories)
           .Returns([file]);

        ProjectIndex result = _scanner.Scan(proj, NoCached);

        result.SourceFiles.Should().BeEmpty();
    }

    [Fact]
    public void Scan_SkipsFilesInObjDirectory()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\obj\Release\net10.0\Foo.cs";

        _fs.DirectoryExists(@"C:\Repo\App").Returns(true);
        _fs.EnumerateFiles(@"C:\Repo\App", "*.cs", SearchOption.AllDirectories)
           .Returns([file]);

        ProjectIndex result = _scanner.Scan(proj, NoCached);

        result.SourceFiles.Should().BeEmpty();
    }

    [Fact]
    public void Scan_NonExistentDirectory_ReturnsEmptyProject()
    {
        const string proj = @"C:\Repo\App\App.csproj";

        _fs.DirectoryExists(@"C:\Repo\App").Returns(false);

        ProjectIndex result = _scanner.Scan(proj, NoCached);

        result.SourceFiles.Should().BeEmpty();
    }

    [Fact]
    public void Scan_UnchangedCachedFile_ReusedWithoutReParsing()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\Foo.cs";

        _fs.DirectoryExists(@"C:\Repo\App").Returns(true);
        _fs.EnumerateFiles(@"C:\Repo\App", "*.cs", SearchOption.AllDirectories)
           .Returns([file]);
        _fs.GetLastWriteTimeUtc(file).Returns(T0);

        SourceFileIndex cachedEntry = MakeCachedEntry(file, T0);
        Dictionary<string, SourceFileIndex> cache = new(StringComparer.OrdinalIgnoreCase)
        {
            [file] = cachedEntry,
        };

        ProjectIndex result = _scanner.Scan(proj, cache);

        result.SourceFiles.Should().ContainSingle()
            .Which.Should().BeSameAs(cachedEntry);
        _fs.DidNotReceive().ReadAllText(Arg.Any<string>());
    }

    [Fact]
    public void Scan_ChangedFile_ReParses()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\Foo.cs";

        _fs.DirectoryExists(@"C:\Repo\App").Returns(true);
        _fs.EnumerateFiles(@"C:\Repo\App", "*.cs", SearchOption.AllDirectories)
           .Returns([file]);
        _fs.GetLastWriteTimeUtc(file).Returns(T1);
        _fs.FileExists(file).Returns(true);
        _fs.ReadAllText(file).Returns("namespace App; public class Foo {}");

        SourceFileIndex staleEntry = MakeCachedEntry(file, T0);
        Dictionary<string, SourceFileIndex> cache = new(StringComparer.OrdinalIgnoreCase)
        {
            [file] = staleEntry,
        };

        ProjectIndex result = _scanner.Scan(proj, cache);

        result.SourceFiles.Should().ContainSingle()
            .Which.IndexedAtUtc.Should().Be(T1);
    }

    [Fact]
    public void Scan_NewFileNotInCache_IsParsed()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\New.cs";

        SetupProject(proj, [file], [(file, "namespace App; public class New {}", T0)]);

        ProjectIndex result = _scanner.Scan(proj, NoCached);

        result.SourceFiles.Should().ContainSingle()
            .Which.FileName.Should().Be("New.cs");
    }

    [Fact]
    public void Scan_MultipleFiles_AllParsed()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        string[] files =
        [
            @"C:\Repo\App\A.cs",
            @"C:\Repo\App\B.cs",
            @"C:\Repo\App\C.cs",
        ];

        SetupProject(proj, files,
        [
            (files[0], "namespace App; public class A {}", T0),
            (files[1], "namespace App; public class B {}", T0),
            (files[2], "namespace App; public class C {}", T0),
        ]);

        ProjectIndex result = _scanner.Scan(proj, NoCached);

        result.SourceFiles.Should().HaveCount(3);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, SourceFileIndex> NoCached =
        new Dictionary<string, SourceFileIndex>(StringComparer.OrdinalIgnoreCase);

    private void SetupEmptyProject(string projPath)
    {
        string dir = Path.GetDirectoryName(projPath)!;
        _fs.DirectoryExists(dir).Returns(true);
        _fs.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Returns([]);
    }

    private void SetupProject(
        string projPath,
        IEnumerable<string> files,
        IEnumerable<(string path, string source, DateTime time)> fileData)
    {
        string dir = Path.GetDirectoryName(projPath)!;
        _fs.DirectoryExists(dir).Returns(true);
        _fs.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Returns([.. files]);

        foreach ((string path, string source, DateTime time) in fileData)
        {
            _fs.FileExists(path).Returns(true);
            _fs.ReadAllText(path).Returns(source);
            _fs.GetLastWriteTimeUtc(path).Returns(time);
        }
    }

    private static SourceFileIndex MakeCachedEntry(string path, DateTime time) =>
        new()
        {
            FileName = Path.GetFileName(path),
            FullPath = path,
            Namespace = "App",
            IndexedAtUtc = time,
        };
}
