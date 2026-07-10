using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>Phase 2: the C#+TS snapshot merge through the real store — both languages queryable, TS discoverable as
/// projects, resolve_bare_name scoped to C#, and the load-bearing regression: a C# delta must NOT purge the TS
/// segment (the delta baseline is the C#-only snapshot, not the merged one).</summary>
public sealed class TsMergeStoreTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly string _root = @"C:\repo";
    private readonly string _aCs = @"C:\repo\Proj\A.cs";

    public TsMergeStoreTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(_aCs, "namespace N; public class CsThing { public void M() { } }");

        _fs.AddFile(@"C:\repo\web\package.json", """{ "name": "myapp-web" }""");
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\web\Store.ts", "export class TsStore { getById(id: number): void { } }");
        _fs.AddFile(@"C:\repo\web\Styles.scss", ".toolbar { color: red; }");
    }

    private CodeIndexStore NewStore() =>
        new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);

    private async Task<CodeIndexStore> BuildBoth()
    {
        CodeIndexStore store = NewStore();
        store.Build(_root);
        await store.RefreshTypeScriptAsync(_root, fullRebuild: true, CancellationToken.None);
        return store;
    }

    private static bool Has(CodeIndexStore s, string name) => s.SearchSymbol(name, null, null).Any(r => r.Name == name);

    [Fact]
    public async Task ColdBuild_ThenTs_SearchSeesBothLanguages()
    {
        CodeIndexStore store = NewStore();
        store.Build(_root);
        Has(store, "CsThing").Should().BeTrue();
        Has(store, "TsStore").Should().BeFalse(); // TS not built yet

        await store.RefreshTypeScriptAsync(_root, fullRebuild: true, CancellationToken.None);
        Has(store, "TsStore").Should().BeTrue();
        Has(store, "toolbar").Should().BeTrue();   // SCSS selector
        Has(store, "CsThing").Should().BeTrue();   // C# still present after the merge
    }

    [Fact]
    public async Task CsDelta_KeepsTsSymbols()
    {
        // THE load-bearing regression: after both segments are live, a C# delta rebuild (reparsing A.cs) must keep
        // the TS segment. If the delta baseline were the merged snapshot, every TS file would look "removed".
        CodeIndexStore store = await BuildBoth();
        Has(store, "TsStore").Should().BeTrue();

        _fs.WriteAllText(_aCs, "namespace N; public class CsThing { public void M() { } public void M2() { } }");
        _fs.SetLastWriteTimeUtc(_aCs, DateTime.UtcNow.AddSeconds(5)); // force the delta to see a change
        store.Rebuild(_root, fullRebuild: false, CancellationToken.None);

        Has(store, "CsThing").Should().BeTrue();
        Has(store, "TsStore").Should().BeTrue();  // <-- must survive the C# rebuild
        Has(store, "toolbar").Should().BeTrue();
    }

    [Fact]
    public async Task TsBuild_DoesNotChangeCsTypePresence()
    {
        CodeIndexStore store = NewStore();
        store.Build(_root);
        int csTypeCount = store.TypeCount;
        store.FindTypes("CsThing").Should().ContainSingle();

        await store.RefreshTypeScriptAsync(_root, fullRebuild: true, CancellationToken.None);

        store.FindTypes("CsThing").Should().ContainSingle();       // C# type set unchanged
        store.TypeCount.Should().BeGreaterThan(csTypeCount);        // TS types added on top
    }

    [Fact]
    public async Task ResolveBareName_IgnoresTsTypes_ButResolvesCs()
    {
        CodeIndexStore store = await BuildBoth();

        // A TS-only type name must not surface as a candidate (resolve_bare_name is C#-semantic).
        BareNameResolution ts = store.ResolveBareName("A.cs", "TsStore");
        ts.FileFound.Should().BeTrue();
        ts.InScope.Should().BeEmpty();
        ts.OutOfScope.Should().BeEmpty();

        // The C# path still resolves the file's own-namespace type.
        BareNameResolution cs = store.ResolveBareName("A.cs", "CsThing");
        cs.InScope.Should().Contain(c => c.FullyQualified == "N.CsThing");
    }

    [Fact]
    public async Task ListProjects_UnionsTsProjects()
    {
        CodeIndexStore store = await BuildBoth();
        List<string> names = store.ListProjects().Select(p => p.Name).ToList();
        names.Should().Contain("Proj");
        names.Should().Contain("myapp-web");
    }

    [Fact]
    public async Task GetProjectDependencyInfo_TsProject_DegradesNotNull()
    {
        CodeIndexStore store = await BuildBoth();

        ProjectDependencyInfo? ts = store.GetProjectDependencyInfo("myapp-web");
        ts.Should().NotBeNull();
        ts!.References.Should().BeEmpty();
        ts.Dependents.Should().BeEmpty();

        store.GetProjectDependencyInfo("Proj").Should().NotBeNull(); // C# still works
        store.GetProjectDependencyInfo("NoSuchProject").Should().BeNull();
    }

    [Fact]
    public async Task GetProject_TsProject_ReturnsFiles()
    {
        CodeIndexStore store = await BuildBoth();
        ProjectIndex? web = store.GetProject("myapp-web");
        web.Should().NotBeNull();
        web!.SourceFiles.Should().Contain(f => f.EndsWith("Store.ts", StringComparison.OrdinalIgnoreCase));
    }
}
