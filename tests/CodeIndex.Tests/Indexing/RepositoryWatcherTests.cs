using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using Microsoft.Extensions.Hosting;

namespace CodeIndex.Tests.Indexing;

/// <summary>
/// Truth tables for the watcher's pure event-classification decisions. The FileSystemWatcher plumbing itself is
/// OS-bound and excluded from coverage; these lock the routing logic (which events warrant a C#/TS rebuild).
/// </summary>
public sealed class RepositoryWatcherTests
{
    private const string Root = @"C:\repo\";

    [Fact]
    public void Constructs_AsHostedBackgroundService()
    {
        RepositoryWatcher watcher = new(Substitute.For<ICodeIndexStore>(), @"C:\repo", indexTypeScript: true);

        watcher.Should().BeAssignableTo<BackgroundService>();
        watcher.Should().BeAssignableTo<IHostedService>();
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Changed, "A.cs", true)]
    [InlineData(WatcherChangeTypes.Created, "App.csproj", true)]
    [InlineData(WatcherChangeTypes.Changed, "Sln.sln", true)]
    [InlineData(WatcherChangeTypes.Changed, "Sln.slnx", true)]
    [InlineData(WatcherChangeTypes.Changed, "notes.txt", false)]
    [InlineData(WatcherChangeTypes.Changed, "readme.md", false)]
    public void ShouldSchedule_TracksCSharpExtensions(WatcherChangeTypes change, string name, bool expected)
    {
        RepositoryWatcher.ShouldSchedule(change, name, null, Root + name).Should().Be(expected);
    }

    [Fact]
    public void ShouldSchedule_RenameOntoTrackedExtension_IsCaught()
    {
        // rename notes.txt -> A.cs : tracked extension on the NEW side.
        RepositoryWatcher.ShouldSchedule(WatcherChangeTypes.Renamed, "A.cs", "notes.txt", Root + "A.cs").Should().BeTrue();
        // rename A.cs -> notes.txt : tracked extension on the OLD side.
        RepositoryWatcher.ShouldSchedule(WatcherChangeTypes.Renamed, "notes.txt", "A.cs", Root + "notes.txt").Should().BeTrue();
    }

    [Fact]
    public void ShouldSchedule_DirectoryOp_SchedulesButPlainChangedDoesNot()
    {
        RepositoryWatcher.ShouldSchedule(WatcherChangeTypes.Created, "NewFolder", null, Root + "NewFolder").Should().BeTrue();
        // an extension-less CHANGED event is not a dir add/remove -> ignored
        RepositoryWatcher.ShouldSchedule(WatcherChangeTypes.Changed, "NewFolder", null, Root + "NewFolder").Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\repo\bin\A.cs")]
    [InlineData(@"C:\repo\obj\A.cs")]
    [InlineData(@"C:\repo\.git\A.cs")]
    [InlineData(@"C:\repo\node_modules\pkg\a.cs")]
    [InlineData(@"C:\repo\.vs\A.cs")]
    [InlineData(@"C:\repo\.codeindex\A.cs")]
    public void ShouldSchedule_ExcludedPaths_AreIgnored(string fullPath)
    {
        RepositoryWatcher.ShouldSchedule(WatcherChangeTypes.Changed, "A.cs", null, fullPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("a.ts")]
    [InlineData("a.tsx")]
    [InlineData("a.mts")]
    [InlineData("a.cts")]
    [InlineData("styles.scss")]
    public void ShouldScheduleTs_TracksTsExtensions_NoForceFull(string name)
    {
        RepositoryWatcher.ShouldScheduleTs(WatcherChangeTypes.Changed, name, null, Root + name, out bool full).Should().BeTrue();
        full.Should().BeFalse();
    }

    [Theory]
    [InlineData("tsconfig.json")]
    [InlineData("package.json")]
    public void ShouldScheduleTs_ProjectConfig_ForcesFullRebuild(string name)
    {
        RepositoryWatcher.ShouldScheduleTs(WatcherChangeTypes.Changed, name, null, Root + name, out bool full).Should().BeTrue();
        full.Should().BeTrue();
    }

    [Fact]
    public void ShouldScheduleTs_DirectoryOp_SchedulesDelta()
    {
        RepositoryWatcher.ShouldScheduleTs(WatcherChangeTypes.Created, "components", null, Root + "components", out bool full).Should().BeTrue();
        full.Should().BeFalse();
    }

    [Theory]
    [InlineData("a.cs")]              // C# file — not a TS concern
    [InlineData("package-lock.json")] // not tsconfig/package.json, not a TS extension
    [InlineData("readme.md")]
    public void ShouldScheduleTs_NonTsFiles_AreIgnored(string name)
    {
        RepositoryWatcher.ShouldScheduleTs(WatcherChangeTypes.Changed, name, null, Root + name, out bool full).Should().BeFalse();
        full.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\repo\node_modules\pkg\a.ts")]
    [InlineData(@"C:\repo\dist\a.ts")]
    [InlineData(@"C:\repo\.next\a.ts")]
    [InlineData(@"C:\repo\bin\a.ts")]
    public void ShouldScheduleTs_ExcludedPaths_AreIgnored(string fullPath)
    {
        RepositoryWatcher.ShouldScheduleTs(WatcherChangeTypes.Changed, "a.ts", null, fullPath, out bool full).Should().BeFalse();
        full.Should().BeFalse();
    }
}
