using CodeIndex.Abstractions;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests.Mcp;

public sealed class IndexStatsToolCoverageTests
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
        return store;
    }

    [Fact]
    public void IndexStats_WithNoneBuild_ReportsNotBuilt()
    {
        BuildInfo build = new BuildInfo(DateTime.MinValue, "none", 0, 0, 0);
        ICodeIndexStore store = MakeStore(build);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().Contain("Last build: none yet (index not built).");
        output.Should().Contain("2 projects");
        output.Should().NotContain("TS/SCSS");
    }

    [Fact]
    public void IndexStats_WithTsProjects_ShowsCombinedProjectsText()
    {
        BuildInfo build = new BuildInfo(DateTime.UtcNow, "full", 100, 3, 0);
        ICodeIndexStore store = MakeStore(build, tsProjectCount: 4);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().Contain("6 projects (2 C#, 4 TS/SCSS)");
    }

    [Fact]
    public void IndexStats_WithFutureBuildTime_ClampsAgeToZeroSeconds()
    {
        BuildInfo build = new BuildInfo(DateTime.UtcNow.AddMinutes(5), "full", 10, 1, 0);
        ICodeIndexStore store = MakeStore(build);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().Contain("full — 0s ago");
    }

    [Fact]
    public void IndexStats_WithRecentBuild_FormatsAgeInSeconds()
    {
        BuildInfo build = new BuildInfo(DateTime.UtcNow.AddSeconds(-10), "delta", 5, 1, 0);
        ICodeIndexStore store = MakeStore(build);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().MatchRegex(@"delta — \d+s ago");
    }

    [Fact]
    public void IndexStats_WithMinutesOldBuild_FormatsAgeInMinutes()
    {
        BuildInfo build = new BuildInfo(DateTime.UtcNow.AddMinutes(-10), "full", 5, 1, 0);
        ICodeIndexStore store = MakeStore(build);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().Contain("full — 10m ago");
    }

    [Fact]
    public void IndexStats_WithHoursOldBuild_FormatsAgeInHours()
    {
        BuildInfo build = new BuildInfo(DateTime.UtcNow.AddHours(-5), "full", 5, 1, 0);
        ICodeIndexStore store = MakeStore(build);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().Contain("full — 5h ago");
    }

    [Fact]
    public void IndexStats_WithDaysOldBuild_FormatsAgeInDays()
    {
        BuildInfo build = new BuildInfo(DateTime.UtcNow.AddDays(-5), "cache", 5, 0, 0);
        ICodeIndexStore store = MakeStore(build);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().Contain("cache — 5d ago");
    }

    [Fact]
    public void IndexStats_WithMinValueBuildTime_ReportsUnknownAge()
    {
        BuildInfo build = new BuildInfo(DateTime.MinValue, "full", 5, 0, 0);
        ICodeIndexStore store = MakeStore(build);

        string output = IndexStatsTool.IndexStats(store, MakeCache());

        output.Should().Contain("full — unknown");
    }
}
