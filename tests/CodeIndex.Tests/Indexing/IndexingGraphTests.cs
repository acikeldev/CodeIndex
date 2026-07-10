using CodeIndex.Indexing;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

public sealed class IndexingGraphTests
{
    private static Dictionary<string, ProjectIndex> Projects(params ProjectIndex[] p) =>
        p.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    // ── ProjectDependencyGraph ────────────────────────────────────────────────

    [Fact]
    public void DependencyGraph_ExactNameMatch_ForwardAndReverse()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\r\App\App.csproj", "<Project><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>");
        fs.AddFile(@"C:\r\Core\Core.csproj", "<Project/>");
        Dictionary<string, ProjectIndex> projects = Projects(
            new ProjectIndex { Name = "App", ProjectDirPath = @"C:\r\App" },
            new ProjectIndex { Name = "Core", ProjectDirPath = @"C:\r\Core" });

        ProjectDependencyGraph g = ProjectDependencyGraph.Build(projects, fs);

        g.GetReferences("App").Should().ContainSingle().Which.Should().Be("Core");
        g.GetDependents("Core").Should().ContainSingle().Which.Should().Be("App");
        g.GetReferences("Core").Should().BeEmpty();
        g.GetDependents("App").Should().BeEmpty();
    }

    [Fact]
    public void DependencyGraph_NoSubstringFalsePositive()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\r\Web\Web.csproj", "<Project><ProjectReference Include=\"..\\Core.Services\\Core.Services.csproj\" /></Project>");
        fs.AddFile(@"C:\r\Core.Services\Core.Services.csproj", "<Project/>");
        fs.AddFile(@"C:\r\Core\Core.csproj", "<Project/>");
        Dictionary<string, ProjectIndex> projects = Projects(
            new ProjectIndex { Name = "Web", ProjectDirPath = @"C:\r\Web" },
            new ProjectIndex { Name = "Core.Services", ProjectDirPath = @"C:\r\Core.Services" },
            new ProjectIndex { Name = "Core", ProjectDirPath = @"C:\r\Core" });

        ProjectDependencyGraph g = ProjectDependencyGraph.Build(projects, fs);

        g.GetDependents("Core.Services").Should().ContainSingle().Which.Should().Be("Web");
        g.GetDependents("Core").Should().BeEmpty(); // exact-name: "Core" is NOT a dependent via "Core.Services"
    }

    [Fact]
    public void DependencyGraph_MissingCsproj_And_UnknownProject_YieldEmpty()
    {
        InMemoryFileSystem fs = new();
        Dictionary<string, ProjectIndex> projects = Projects(
            new ProjectIndex { Name = "Ghost", ProjectDirPath = @"C:\r\Ghost" });

        ProjectDependencyGraph g = ProjectDependencyGraph.Build(projects, fs);

        g.GetReferences("Ghost").Should().BeEmpty();
        g.GetReferences("Unknown").Should().BeEmpty();
        g.GetDependents("Unknown").Should().BeEmpty();
    }

    // ── SnapshotBuilder + IndexSnapshot ───────────────────────────────────────

    [Fact]
    public void SnapshotBuilder_BuildsSnapshotWithCountsAndLookups()
    {
        InMemoryFileSystem fs = new();
        SnapshotBuilder b = new();
        SourceFileIndex f = new()
        {
            FileName = "A.cs",
            SourceFilePath = @"C:\r\P\A.cs",
            ProjectName = "P",
            Types =
            [
                new TypeInfo
                {
                    Name = "A", Kind = SymbolKind.Class, TypeKeyword = "class", StartLine = 1, LineCount = 3,
                    Members = [new MemberInfo { Name = "M", Kind = SymbolKind.Method, ReturnType = "void", Signature = "void M()", StartLine = 2, LineCount = 1 }],
                },
            ],
        };
        b.AddFile(f);

        IndexSnapshot snap = b.Build(new Dictionary<string, long> { [@"C:\r\P\A.cs"] = 1L }, fs);

        snap.SourceFileCount.Should().Be(1);
        snap.TypeCount.Should().Be(1);
        snap.MemberCount.Should().Be(1);
        snap.SourceFilesByName["A.cs"].Should().ContainSingle();
        snap.SourceFilesByPath.Should().ContainKey(@"C:\r\P\A.cs");
    }

    [Fact]
    public void SnapshotBuilder_AddProjects_DedupsByName_And_StoresRelativeForwardSlashedPaths()
    {
        InMemoryFileSystem fs = new();
        SnapshotBuilder b = new();
        SolutionScanner.ScanResult scan = new(
            [new SolutionScanner.ProjectInfo("P", @"C:\r\P", [@"C:\r\P\Sub\A.cs"])],
            new Dictionary<string, long>());
        b.AddProjects(scan);
        b.AddProjects([new ProjectIndex { Name = "P", ProjectDirPath = @"C:\r\P" }]); // same name -> skipped

        IndexSnapshot snap = b.Build(new Dictionary<string, long>(), fs);

        snap.Projects.Should().ContainKey("P");
        snap.Projects["P"].SourceFiles.Should().ContainSingle().Which.Should().Be("Sub/A.cs");
    }

    [Fact]
    public void Empty_IsEmpty_AndLazyGraphsMaterializeWithoutError()
    {
        IndexSnapshot.Empty.SourceFileCount.Should().Be(0);
        IndexSnapshot.Empty.ProjectCount.Should().Be(0);
        IndexSnapshot.Empty.DependencyGraph.GetReferences("x").Should().BeEmpty();
        IndexSnapshot.Empty.MentionGraph.Should().NotBeNull();
    }

    [Fact]
    public void Snapshot_InheritedGraph_IsReusedAcrossRepublish()
    {
        InMemoryFileSystem fs = new();
        IndexSnapshot s1 = new SnapshotBuilder().Build(new Dictionary<string, long>(), fs);
        Lazy<ProjectDependencyGraph> inherited = s1.DependencyGraphLazy;

        IndexSnapshot s2 = new SnapshotBuilder().Build(new Dictionary<string, long>(), fs, inherited);

        s2.DependencyGraphLazy.Should().BeSameAs(inherited);
    }
}
