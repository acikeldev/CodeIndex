using CodeIndex.Abstractions;
using CodeIndex.Parsing;

namespace CodeIndex.Tests.Parsing;

public sealed class SolutionScannerTests
{
    private readonly IFileSystem _fs = Substitute.For<IFileSystem>();
    private readonly SolutionScanner _scanner;

    public SolutionScannerTests()
    {
        _scanner = new SolutionScanner(_fs);
    }

    [Fact]
    public void FindProjectFiles_EmptyRepo_ReturnsEmpty()
    {
        _fs.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
           .Returns([]);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnWithOneProject_ReturnsThatProject()
    {
        const string slnPath = @"C:\Repo\MyApp.sln";
        const string csprojPath = @"C:\Repo\src\MyApp\MyApp.csproj";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([slnPath]);
        _fs.ReadAllText(slnPath).Returns(BuildSlnContent(@"src\MyApp\MyApp.csproj"));
        _fs.FileExists(csprojPath).Returns(true);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().ContainSingle().Which.Should().Be(csprojPath);
    }

    [Fact]
    public void FindProjectFiles_SlnxWithOneProject_ReturnsThatProject()
    {
        const string slnxPath = @"C:\Repo\MyApp.slnx";
        const string csprojPath = @"C:\Repo\src\MyApp\MyApp.csproj";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([slnxPath]);
        _fs.ReadAllText(slnxPath).Returns(BuildSlnxContent(@"src/MyApp/MyApp.csproj"));
        _fs.FileExists(csprojPath).Returns(true);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().ContainSingle().Which.Should().Be(csprojPath);
    }

    [Fact]
    public void FindProjectFiles_MultipleSlns_DeduplicatesSharedProjects()
    {
        const string sln1 = @"C:\Repo\A.sln";
        const string sln2 = @"C:\Repo\B.sln";
        const string shared = @"C:\Repo\src\Shared\Shared.csproj";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([sln1, sln2]);
        _fs.ReadAllText(sln1).Returns(BuildSlnContent(@"src\Shared\Shared.csproj"));
        _fs.ReadAllText(sln2).Returns(BuildSlnContent(@"src\Shared\Shared.csproj"));
        _fs.FileExists(shared).Returns(true);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().ContainSingle().Which.Should().Be(shared);
    }

    [Fact]
    public void FindProjectFiles_ProjectFileNotOnDisk_IsExcluded()
    {
        const string slnPath = @"C:\Repo\MyApp.sln";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([slnPath]);
        _fs.ReadAllText(slnPath).Returns(BuildSlnContent(@"src\Ghost\Ghost.csproj"));
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_NonSolutionFiles_AreIgnored()
    {
        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([@"C:\Repo\README.txt", @"C:\Repo\notes.md", @"C:\Repo\MyApp.csproj"]);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnInsideBinDirectory_IsSkipped()
    {
        const string binSln = @"C:\Repo\bin\Release\MyApp.sln";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([binSln]);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnInsideVsDirectory_IsSkipped()
    {
        const string vsSln = @"C:\Repo\.vs\MyApp.sln";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([vsSln]);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnInsideGitDirectory_IsSkipped()
    {
        const string gitSln = @"C:\Repo\.git\refs\MyApp.sln";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([gitSln]);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnInsideObjDirectory_IsSkipped()
    {
        const string objSln = @"C:\Repo\obj\MyApp.sln";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([objSln]);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnInsideNodeModules_IsSkipped()
    {
        const string nmSln = @"C:\Repo\node_modules\tool\Tool.sln";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([nmSln]);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_ResultsAreSortedAlphabetically()
    {
        const string slnPath = @"C:\Repo\MyApp.sln";
        const string z = @"C:\Repo\Z\Z.csproj";
        const string a = @"C:\Repo\A\A.csproj";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([slnPath]);
        _fs.ReadAllText(slnPath).Returns(
            BuildSlnContent(@"Z\Z.csproj") + BuildSlnContent(@"A\A.csproj"));
        _fs.FileExists(z).Returns(true);
        _fs.FileExists(a).Returns(true);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().Equal(a, z);
    }

    [Fact]
    public void FindProjectFiles_SlnxNonCsprojPath_IsIgnored()
    {
        const string slnxPath = @"C:\Repo\MyApp.slnx";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([slnxPath]);
        _fs.ReadAllText(slnxPath).Returns(
            """
            <Solution>
              <Project Path="src/MyApp/MyApp.vbproj" />
            </Solution>
            """);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnxProjectWithoutPathAttribute_IsIgnored()
    {
        const string slnxPath = @"C:\Repo\MyApp.slnx";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([slnxPath]);
        _fs.ReadAllText(slnxPath).Returns(
            """
            <Solution>
              <Project Name="Orphan" />
            </Solution>
            """);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindProjectFiles_SlnWithBackslashPaths_ResolvesCorrectly()
    {
        const string slnPath = @"C:\Repo\MyApp.sln";
        const string expected = @"C:\Repo\src\MyApp\MyApp.csproj";

        _fs.EnumerateFiles(@"C:\Repo", "*.*", SearchOption.AllDirectories)
           .Returns([slnPath]);
        _fs.ReadAllText(slnPath).Returns(BuildSlnContent(@"src\MyApp\MyApp.csproj"));
        _fs.FileExists(expected).Returns(true);

        IReadOnlyList<string> result = _scanner.FindProjectFiles(@"C:\Repo");

        result.Should().ContainSingle().Which.Should().Be(expected);
    }

    private static string BuildSlnContent(string relativeCsprojPath) =>
        "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
        $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"MyApp\", \"{relativeCsprojPath}\", \"{{12345678-0000-0000-0000-000000000000}}\"\r\n" +
        "EndProject\r\n";

    private static string BuildSlnxContent(string relativeCsprojPath) =>
        $"""
         <Solution>
           <Project Path="{relativeCsprojPath}" />
         </Solution>
         """;
}
