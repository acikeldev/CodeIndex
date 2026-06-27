using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using CodeIndex.Models;

namespace CodeIndex.Tests.Indexing;

public sealed class CodeIndexStoreTests
{
    private readonly IFileSystem _fs = Substitute.For<IFileSystem>();
    private readonly CodeIndexStore _store;
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    public CodeIndexStoreTests()
    {
        _store = new CodeIndexStore(_fs);
    }

    // ── initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void BeforeRebuild_GetProjects_ReturnsEmpty()
    {
        _store.GetProjects().Should().BeEmpty();
    }

    [Fact]
    public void BeforeRebuild_GetFiles_ReturnsEmpty()
    {
        _store.GetFiles().Should().BeEmpty();
    }

    [Fact]
    public void BeforeRebuild_SearchTypes_ReturnsEmpty()
    {
        _store.SearchTypes("Foo").Should().BeEmpty();
    }

    [Fact]
    public void BeforeRebuild_SearchMembers_ReturnsEmpty()
    {
        _store.SearchMembers("Get").Should().BeEmpty();
    }

    [Fact]
    public void BeforeRebuild_SearchFiles_ReturnsEmpty()
    {
        _store.SearchFiles("Service").Should().BeEmpty();
    }

    // ── Rebuild / GetProjects / GetFiles ──────────────────────────────────────

    [Fact]
    public void Rebuild_NoSolutionFiles_IndexIsEmpty()
    {
        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([]);

        _store.Rebuild(@"C:\Repo");

        _store.GetProjects().Should().BeEmpty();
        _store.GetFiles().Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_OneSolution_PopulatesProjects()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Foo.cs",
            source: "namespace App; public class Foo {}");

        _store.Rebuild(@"C:\Repo");

        _store.GetProjects().Should().ContainSingle().Which.Name.Should().Be("App");
        _store.GetFiles().Should().ContainSingle().Which.FileName.Should().Be("Foo.cs");
    }

    [Fact]
    public void Rebuild_SecondRebuild_ReplacesIndex()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Foo.cs",
            source: "namespace App; public class Foo {}");

        _store.Rebuild(@"C:\Repo");
        _store.Rebuild(@"C:\Repo");

        _store.GetProjects().Should().ContainSingle();
    }

    // ── delta rebuild ─────────────────────────────────────────────────────────

    [Fact]
    public void Rebuild_UnchangedFile_NotReparsedOnSecondRebuild()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\Foo.cs";

        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: proj,
            csFile: file,
            source: "namespace App; public class Foo {}",
            time: T0);

        _store.Rebuild(@"C:\Repo");
        _store.Rebuild(@"C:\Repo");

        // ReadAllText should be called exactly once (first rebuild only)
        _fs.Received(1).ReadAllText(file);
    }

    [Fact]
    public void Rebuild_ChangedFile_ReparsedOnSecondRebuild()
    {
        const string proj = @"C:\Repo\App\App.csproj";
        const string file = @"C:\Repo\App\Foo.cs";

        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: proj,
            csFile: file,
            source: "namespace App; public class Foo {}",
            time: T0);

        _store.Rebuild(@"C:\Repo");

        // Simulate file change: bump the timestamp
        _fs.GetLastWriteTimeUtc(file).Returns(T1);
        _fs.ReadAllText(file).Returns("namespace App; public class FooV2 {}");

        _store.Rebuild(@"C:\Repo");

        _store.GetFiles().Should().ContainSingle()
            .Which.Types.Should().ContainSingle()
            .Which.Name.Should().Be("FooV2");
    }

    // ── SearchTypes ───────────────────────────────────────────────────────────

    [Fact]
    public void SearchTypes_MatchesBySubstring()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\UserService.cs",
            source: "namespace App; public class UserService {}");

        _store.Rebuild(@"C:\Repo");

        _store.SearchTypes("Service").Should().ContainSingle()
            .Which.Name.Should().Be("UserService");
    }

    [Fact]
    public void SearchTypes_IsCaseInsensitive()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\UserService.cs",
            source: "namespace App; public class UserService {}");

        _store.Rebuild(@"C:\Repo");

        _store.SearchTypes("userservice").Should().ContainSingle();
        _store.SearchTypes("USERSERVICE").Should().ContainSingle();
    }

    [Fact]
    public void SearchTypes_FiltersByKind()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Types.cs",
            source: """
                namespace App;
                public class MyClass {}
                public interface IMyInterface {}
                """);

        _store.Rebuild(@"C:\Repo");

        _store.SearchTypes("My", SymbolKind.Interface)
            .Should().ContainSingle().Which.Name.Should().Be("IMyInterface");
    }

    [Fact]
    public void SearchTypes_NoMatch_ReturnsEmpty()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Foo.cs",
            source: "namespace App; public class Foo {}");

        _store.Rebuild(@"C:\Repo");

        _store.SearchTypes("XyzNoMatch").Should().BeEmpty();
    }

    [Fact]
    public void SearchTypes_KindMismatch_ReturnsEmpty()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Foo.cs",
            source: "namespace App; public class Foo {}");

        _store.Rebuild(@"C:\Repo");

        _store.SearchTypes("Foo", SymbolKind.Interface).Should().BeEmpty();
    }

    // ── SearchMembers ─────────────────────────────────────────────────────────

    [Fact]
    public void SearchMembers_MatchesBySubstring()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Svc.cs",
            source: """
                namespace App;
                public class Svc
                {
                    public string GetUser() { return "x"; }
                    public void SaveUser() {}
                }
                """);

        _store.Rebuild(@"C:\Repo");

        _store.SearchMembers("GetUser").Should().ContainSingle()
            .Which.Name.Should().Be("GetUser");
    }

    [Fact]
    public void SearchMembers_IsCaseInsensitive()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Svc.cs",
            source: """
                namespace App;
                public class Svc { public void GetUser() {} }
                """);

        _store.Rebuild(@"C:\Repo");

        _store.SearchMembers("getuser").Should().ContainSingle();
    }

    [Fact]
    public void SearchMembers_FiltersByKind()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Svc.cs",
            source: """
                namespace App;
                public class Svc
                {
                    public int Count { get; set; }
                    public void Count() {}
                }
                """);

        _store.Rebuild(@"C:\Repo");

        _store.SearchMembers("Count", SymbolKind.Property)
            .Should().ContainSingle().Which.Kind.Should().Be(SymbolKind.Property);
    }

    [Fact]
    public void SearchMembers_NoMatch_ReturnsEmpty()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Svc.cs",
            source: "namespace App; public class Svc { public void Run() {} }");

        _store.Rebuild(@"C:\Repo");

        _store.SearchMembers("XyzNoMatch").Should().BeEmpty();
    }

    // ── SearchFiles ───────────────────────────────────────────────────────────

    [Fact]
    public void SearchFiles_MatchesByPathFragment()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Services\UserService.cs",
            source: "namespace App.Services; public class UserService {}");

        _store.Rebuild(@"C:\Repo");

        _store.SearchFiles("Services").Should().ContainSingle()
            .Which.FileName.Should().Be("UserService.cs");
    }

    [Fact]
    public void SearchFiles_IsCaseInsensitive()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\UserService.cs",
            source: "namespace App; public class UserService {}");

        _store.Rebuild(@"C:\Repo");

        _store.SearchFiles("userservice").Should().ContainSingle();
        _store.SearchFiles("USERSERVICE").Should().ContainSingle();
    }

    [Fact]
    public void SearchFiles_NoMatch_ReturnsEmpty()
    {
        SetupOneProjectRepo(@"C:\Repo",
            sln: @"C:\Repo\App.sln",
            proj: @"C:\Repo\App\App.csproj",
            csFile: @"C:\Repo\App\Foo.cs",
            source: "namespace App; public class Foo {}");

        _store.Rebuild(@"C:\Repo");

        _store.SearchFiles("XyzNoMatch").Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetupOneProjectRepo(
        string repoRoot,
        string sln,
        string proj,
        string csFile,
        string source,
        DateTime? time = null)
    {
        DateTime fileTime = time ?? T0;
        string slnContent =
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            $"Project(\"{{FAE04EC0}}\") = \"App\", \"{Path.GetRelativePath(Path.GetDirectoryName(sln)!, proj).Replace('\\', '/')}\", \"{{00000000}}\"\r\n" +
            "EndProject\r\n";

        _fs.EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories)
           .Returns([sln]);
        _fs.ReadAllText(sln).Returns(slnContent);
        _fs.FileExists(proj).Returns(true);

        string projDir = Path.GetDirectoryName(proj)!;
        _fs.DirectoryExists(projDir).Returns(true);
        _fs.EnumerateFiles(projDir, "*.cs", SearchOption.AllDirectories)
           .Returns([csFile]);
        _fs.FileExists(csFile).Returns(true);
        _fs.ReadAllText(csFile).Returns(source);
        _fs.GetLastWriteTimeUtc(csFile).Returns(fileTime);
    }
}
