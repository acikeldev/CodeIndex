using CodeIndex.Abstractions;
using CodeIndex.Indexing;

namespace CodeIndex.Tests.Indexing;

public sealed class EmptyCodeIndexStoreTests
{
    private readonly ICodeIndexStore _store = new EmptyCodeIndexStore();

    [Fact]
    public void Queries_ReturnEmptyOrNotFound()
    {
        _store.RepoRoot.Should().BeNull();
        _store.CacheDirectory.Should().BeEmpty();
        _store.LastBuild.Kind.Should().Be("none");
        _store.ProjectCount.Should().Be(0);
        _store.TsProjectCount.Should().Be(0);
        _store.SourceFileCount.Should().Be(0);
        _store.TypeCount.Should().Be(0);
        _store.MemberCount.Should().Be(0);
        _store.AllSourceFiles.Should().BeEmpty();

        _store.SearchSymbol("x", null, null).Should().BeEmpty();
        _store.GetFileOutline("x").Should().BeNull();
        _store.ResolveSourceFilePath("x").Should().BeNull();
        _store.IsIndexedPath("x").Should().BeFalse();
        _store.ListProjects().Should().BeEmpty();
        _store.GetProject("x").Should().BeNull();
        _store.FindTypes("x").Should().BeEmpty();
        _store.GetDerivedTypes("x").Should().BeEmpty();
        _store.ResolveBareName("f", "id").FileFound.Should().BeFalse();
        _store.GetRepoMap([], 1000, null).Should().BeEmpty();
        _store.SearchStructural("p", null, 10, 5).Should().BeEmpty();
        _store.CheckDanglingReferences("f").Should().BeEmpty();
        _store.GetCallHierarchy("m", "callers", null, 10, 5, "both").Should().BeEmpty();
        _store.GetProjectDependencyInfo("x").Should().BeNull();
        _store.ProjectDirsByName().Should().BeEmpty();
        _store.FileNames().Should().BeEmpty();
        _store.ProjectNames().Should().BeEmpty();
        _store.TypeNames().Should().BeEmpty();
        _store.SymbolNames(null).Should().BeEmpty();
    }

    [Fact]
    public async Task Mutations_AreNoOps()
    {
        _store.Build("root");
        _store.Rebuild("root", fullRebuild: false, CancellationToken.None);
        _store.LoadCachedSnapshot("root").Should().BeFalse();
        await _store.RefreshAsync("root", fullRebuild: false, CancellationToken.None);
        await _store.RefreshTypeScriptAsync("root", fullRebuild: false, CancellationToken.None);

        _store.ProjectCount.Should().Be(0);
    }
}
