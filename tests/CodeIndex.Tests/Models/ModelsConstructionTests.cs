using CodeIndex.Indexing;
using CodeIndex.Models;

namespace CodeIndex.Tests.Models;

/// <summary>Constructs every model / DTO so their property surfaces are exercised (data-shape coverage).</summary>
public sealed class ModelsConstructionTests
{
    [Fact]
    public void SymbolSearchResult_ExposesAllFields()
    {
        SymbolSearchResult r = new()
        {
            Name = "GetUser",
            Kind = "Method",
            Project = "App",
            File = "UserService.cs",
            SourceFilePath = @"C:\Repo\UserService.cs",
            StartLine = 5,
            LineCount = 3,
            Namespace = "App.Services",
            Signature = "public string GetUser()",
            ParentType = "UserService",
        };

        r.Name.Should().Be("GetUser");
        r.Kind.Should().Be("Method");
        r.Project.Should().Be("App");
        r.File.Should().Be("UserService.cs");
        r.SourceFilePath.Should().Be(@"C:\Repo\UserService.cs");
        r.StartLine.Should().Be(5);
        r.LineCount.Should().Be(3);
        r.Namespace.Should().Be("App.Services");
        r.Signature.Should().Be("public string GetUser()");
        r.ParentType.Should().Be("UserService");
    }

    [Fact]
    public void TsProjectInfo_ExposesNameAndDir()
    {
        TsProjectInfo p = new() { Name = "web", ProjectDirPath = @"C:\Repo\web" };

        p.Name.Should().Be("web");
        p.ProjectDirPath.Should().Be(@"C:\Repo\web");
    }

    [Fact]
    public void AliasMap_EmptyAndPopulated()
    {
        AliasMap.Empty.Paths.Should().BeEmpty();
        AliasMap.Empty.Packages.Should().BeEmpty();

        AliasMap m = new()
        {
            Paths = new Dictionary<string, List<string>> { ["@app/*"] = ["src/*"] },
            Packages = new Dictionary<string, string> { ["@app/core"] = @"C:\Repo\core" },
        };

        m.Paths.Should().ContainKey("@app/*");
        m.Packages.Should().ContainKey("@app/core");
    }

    [Fact]
    public void TsSegment_EmptyAndPopulated()
    {
        TsSegment.Empty.Files.Should().BeEmpty();
        TsSegment.Empty.Projects.Should().BeEmpty();
        TsSegment.Empty.Timestamps.Should().BeEmpty();
        TsSegment.Empty.Aliases.Should().BeSameAs(AliasMap.Empty);

        SourceFileIndex f = new()
        {
            FileName = "a.ts",
            SourceFilePath = @"C:\Repo\a.ts",
            ProjectName = "web",
            Language = Language.TypeScript,
        };
        TsSegment seg = new(
            [f],
            new Dictionary<string, long> { [@"C:\Repo\a.ts"] = 1L },
            [new TsProjectInfo { Name = "web", ProjectDirPath = @"C:\Repo\web" }],
            AliasMap.Empty);

        seg.Files.Should().ContainSingle();
        seg.Projects.Should().ContainSingle();
        seg.Timestamps.Should().ContainKey(@"C:\Repo\a.ts");
        seg.Aliases.Should().BeSameAs(AliasMap.Empty);
    }

    [Fact]
    public void RecordDtos_Construct()
    {
        BuildInfo b = new(DateTime.UnixEpoch, "delta", 12, 3, 1);
        b.Kind.Should().Be("delta");
        b.Reparsed.Should().Be(3);
        b.Removed.Should().Be(1);
        b.DurationMs.Should().Be(12);

        ProjectDependencyInfo d = new("App", ["Core"], ["Web"]);
        d.Name.Should().Be("App");
        d.References.Should().ContainSingle().Which.Should().Be("Core");
        d.Dependents.Should().ContainSingle().Which.Should().Be("Web");

        BareNameCandidate c = new("App.Data.User", "App.Data", @"C:\Repo\User.cs", "using App.Data;");
        c.FullyQualified.Should().Be("App.Data.User");
        c.Namespace.Should().Be("App.Data");
        c.SourceFilePath.Should().Be(@"C:\Repo\User.cs");
        c.Via.Should().Be("using App.Data;");

        BareNameResolution res = new(true, "App", null, [c], []);
        res.FileFound.Should().BeTrue();
        res.FileNamespace.Should().Be("App");
        res.InScope.Should().ContainSingle();
        res.OutOfScope.Should().BeEmpty();
    }

    [Fact]
    public void MemberInfo_ExposesFields()
    {
        MemberInfo m = new()
        {
            Name = "Count",
            Kind = SymbolKind.Property,
            ReturnType = "int",
            Signature = "public int Count { get; }",
            StartLine = 3,
            LineCount = 1,
        };

        m.Name.Should().Be("Count");
        m.Kind.Should().Be(SymbolKind.Property);
        m.ReturnType.Should().Be("int");
        m.Signature.Should().Be("public int Count { get; }");
        m.StartLine.Should().Be(3);
        m.LineCount.Should().Be(1);
    }
}
