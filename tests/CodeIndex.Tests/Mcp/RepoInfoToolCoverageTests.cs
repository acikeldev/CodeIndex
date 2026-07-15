using CodeIndex.Abstractions;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Branch coverage for <see cref="RepoInfoTool"/> over a mocked store: the age-formatting tiers (s/m/h/d,
/// clamped, unknown), the none-build and TS-breakdown paths, and the project= found/empty/not-found branches.
/// </summary>
public sealed class RepoInfoToolCoverageTests
{
    private static ICodeIndexCache MakeCache()
    {
        ICodeIndexCache cache = Substitute.For<ICodeIndexCache>();
        cache.CurrentSchemaVersion.Returns(7);
        cache.GetCachePath(Arg.Any<string>()).Returns("C:\\cache\\index.bin");
        return cache;
    }

    private static ICodeIndexStore MakeStore(BuildInfo build, int tsProjectCount = 0)
    {
        ICodeIndexStore store = Substitute.For<ICodeIndexStore>();
        store.RepoRoot.Returns("C:\\repo");
        store.CacheDirectory.Returns("C:\\cache");
        store.LastBuild.Returns(build);
        store.ProjectCount.Returns(2);
        store.TsProjectCount.Returns(tsProjectCount);
        store.SourceFileCount.Returns(5);
        store.TypeCount.Returns(9);
        store.MemberCount.Returns(20);
        store.ListProjects().Returns(new List<ProjectIndex>());
        return store;
    }

    [Fact]
    public void NoneBuild_ReportsNotBuilt()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.MinValue, "none", 0, 0, 0)), MakeCache());

        output.Should().Contain("Last build: none yet (index not built).");
        output.Should().Contain("2 projects");
        output.Should().NotContain("TS/SCSS");
        output.Should().Contain("No projects indexed.");
    }

    [Fact]
    public void TsProjects_ShowsCombinedProjectsText()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.UtcNow, "full", 100, 3, 0), tsProjectCount: 4), MakeCache());

        output.Should().Contain("6 projects (2 C#, 4 TS/SCSS)");
    }

    [Fact]
    public void FutureBuildTime_ClampsAgeToZeroSeconds()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.UtcNow.AddMinutes(5), "full", 10, 1, 0)), MakeCache());

        output.Should().Contain("full — 0s ago");
    }

    [Fact]
    public void RecentBuild_FormatsAgeInSeconds()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.UtcNow.AddSeconds(-10), "delta", 5, 1, 0)), MakeCache());

        output.Should().MatchRegex(@"delta — \d+s ago");
    }

    [Fact]
    public void MinutesOldBuild_FormatsAgeInMinutes()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.UtcNow.AddMinutes(-10), "full", 5, 1, 0)), MakeCache());

        output.Should().Contain("full — 10m ago");
    }

    [Fact]
    public void HoursOldBuild_FormatsAgeInHours()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.UtcNow.AddHours(-5), "full", 5, 1, 0)), MakeCache());

        output.Should().Contain("full — 5h ago");
    }

    [Fact]
    public void DaysOldBuild_FormatsAgeInDays()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.UtcNow.AddDays(-5), "cache", 5, 0, 0)), MakeCache());

        output.Should().Contain("cache — 5d ago");
    }

    [Fact]
    public void MinValueBuildTime_ReportsUnknownAge()
    {
        string output = RepoInfoTool.RepoInfo(MakeStore(new BuildInfo(DateTime.MinValue, "full", 5, 0, 0)), MakeCache());

        output.Should().Contain("full — unknown");
    }

    [Fact]
    public void Project_EmptyProject_ReportsNoSourceFiles()
    {
        ICodeIndexStore store = MakeStore(new BuildInfo(DateTime.UtcNow, "full", 1, 0, 0));
        store.GetProject("Empty").Returns(new ProjectIndex { Name = "Empty", ProjectDirPath = "C:\\repo\\Empty", SourceFiles = [] });

        string output = RepoInfoTool.RepoInfo(store, MakeCache(), project: "Empty");

        output.Should().Be("Project 'Empty' has no source files.");
    }

    [Fact]
    public void Project_NotFound_ReturnsMessage()
    {
        ICodeIndexStore store = MakeStore(new BuildInfo(DateTime.UtcNow, "full", 1, 0, 0));
        store.GetProject("Missing").Returns((ProjectIndex?)null);

        string output = RepoInfoTool.RepoInfo(store, MakeCache(), project: "Missing");

        output.Should().Be("Project 'Missing' not found.");
    }

    [Fact]
    public void Overview_WithProjects_ListsThem()
    {
        ICodeIndexStore store = MakeStore(new BuildInfo(DateTime.UtcNow, "full", 1, 0, 0));
        store.ListProjects().Returns(new List<ProjectIndex>
        {
            new() { Name = "App", ProjectDirPath = "C:\\repo\\App", SourceFiles = ["a.cs", "b.cs"] },
        });

        string output = RepoInfoTool.RepoInfo(store, MakeCache());

        output.Should().Contain("Projects:");
        output.Should().Contain("App (2 files)");
    }
}
