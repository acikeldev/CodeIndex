using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>
/// Targeted coverage for <see cref="CodeIndexStore"/> edge branches: the cache-preload catch, the
/// full-rebuild / delta-full / delta-remove / delta-change+add publish paths, the TS warm-start and TS
/// delta copy-forward paths, the substring file-lookup fallbacks (outline / bare-name / dangling),
/// the "could not read" TS branch, the TS-project enumeration helpers, and every kind-filter arm.
/// Each test builds its own store over a fresh in-memory file system (xUnit new-instance-per-test).
/// </summary>
public sealed class CodeIndexStoreCoverageTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private readonly string _aCs = @"C:\repo\Proj\A.cs";
    private readonly string _bCs = @"C:\repo\Proj\B.cs";
    private readonly string _storeTs = @"C:\repo\web\Store.ts";

    public CodeIndexStoreCoverageTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");

        _fs.AddFile(_aCs, """
            using System;

            namespace N;

            public interface IThing
            {
                void Do();
            }

            public enum Color
            {
                Red,
                Green
            }

            public class CsThing
            {
                public int MyProp { get; set; }

                public string MyField;

                public event EventHandler MyEvent;

                public CsThing()
                {
                }

                public void GetUserRole()
                {
                }
            }
            """);
        _fs.AddFile(_bCs, "namespace N; public class BThing { }");

        // TS/SCSS project "myapp-web".
        _fs.AddFile(@"C:\repo\web\package.json", """{ "name": "myapp-web" }""");
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(_storeTs, "export class TsStore { getById(id: number): void { this.load(id); } load(id: number): void { } }");
        _fs.AddFile(@"C:\repo\web\Styles.scss", ".toolbar { color: red; }");

        // A TS project whose name COLLIDES with the C# project "Proj" (UnifiedProjects must skip it).
        _fs.AddFile(@"C:\repo\dup\package.json", """{ "name": "Proj" }""");
        _fs.AddFile(@"C:\repo\dup\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\dup\Dup.ts", "export class DupThing { }");
    }

    private CodeIndexStore NewStore() =>
        new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);

    private CodeIndexStore BuildCSharp()
    {
        CodeIndexStore store = NewStore();
        store.Build(Root);
        return store;
    }

    private async Task<CodeIndexStore> BuildBoth()
    {
        CodeIndexStore store = BuildCSharp();
        await store.RefreshTypeScriptAsync(Root, fullRebuild: true, CancellationToken.None);
        return store;
    }

    private static bool Has(CodeIndexStore s, string name) =>
        s.SearchSymbol(name, null, null).Any(r => r.Name == name);

    // ── LoadCachedSnapshot catch (191-194) ──────────────────────────────────────

    [Fact]
    public void LoadCachedSnapshot_WhenCacheLoadThrows_ReturnsFalse()
    {
        ICodeIndexCache throwingCache = Substitute.For<ICodeIndexCache>();
        throwingCache.Load(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("boom"));

        CodeIndexStore store = new(_fs, throwingCache, new TsIndexCache(_fs), CodeIndexConfig.Default);

        bool loaded = store.LoadCachedSnapshot(Root);

        loaded.Should().BeFalse();
    }

    // ── full rebuild branch (240-242) ───────────────────────────────────────────

    [Fact]
    public void Rebuild_FullRebuild_RepublishesFullSnapshot()
    {
        CodeIndexStore store = BuildCSharp();

        store.Rebuild(Root, fullRebuild: true, CancellationToken.None);

        store.LastBuild.Kind.Should().Be("full");
        Has(store, "CsThing").Should().BeTrue();
        Has(store, "BThing").Should().BeTrue();
    }

    // ── delta base = loaded cache with empty timestamps → IsFullRebuild (274-276) ─

    [Fact]
    public void Rebuild_ColdWithEmptyTimestampCache_FallsBackToFullBuild()
    {
        // A non-null cache whose FileTimestamps is empty makes ComputeDelta report IsFullRebuild,
        // driving the delta-base → PublishFull fallback (the else/cache branch, not the live-prev branch).
        ICodeIndexCache emptyCache = Substitute.For<ICodeIndexCache>();
        emptyCache.CurrentSchemaVersion.Returns(IndexCache.SchemaVersion);
        emptyCache.Load(Arg.Any<string>()).Returns(new CacheData
        {
            Projects = [],
            SourceFiles = [],
            FileTimestamps = new Dictionary<string, long>(),
            SchemaVersion = IndexCache.SchemaVersion,
        });

        CodeIndexStore store = new(_fs, emptyCache, new TsIndexCache(_fs), CodeIndexConfig.Default);

        store.Rebuild(Root, fullRebuild: false, CancellationToken.None);

        store.LastBuild.Kind.Should().Be("full");
        Has(store, "CsThing").Should().BeTrue();
    }

    // ── delta REMOVE path (310, 331-335) ────────────────────────────────────────

    [Fact]
    public void Rebuild_Delta_RemovesDeletedFile()
    {
        CodeIndexStore store = BuildCSharp();
        Has(store, "BThing").Should().BeTrue();

        _fs.DeleteFile(_bCs);
        store.Rebuild(Root, fullRebuild: false, CancellationToken.None);

        store.LastBuild.Kind.Should().Be("delta");
        store.LastBuild.Removed.Should().Be(1);
        Has(store, "BThing").Should().BeFalse();
        Has(store, "CsThing").Should().BeTrue();
    }

    // ── delta CHANGE + ADD path (285-308) ───────────────────────────────────────

    [Fact]
    public void Rebuild_Delta_ReparsesChangedAndAddedFiles()
    {
        CodeIndexStore store = BuildCSharp();

        // change A.cs (new type) and add C.cs — both must appear via the reparse loop.
        _fs.AddFile(_aCs, "namespace N; public class CsThing { } public class AddedByEdit { }");
        _fs.SetLastWriteTimeUtc(_aCs, DateTime.UtcNow.AddSeconds(30));
        _fs.AddFile(@"C:\repo\Proj\C.cs", "namespace N; public class CThing { }");

        store.Rebuild(Root, fullRebuild: false, CancellationToken.None);

        store.LastBuild.Kind.Should().Be("delta");
        store.LastBuild.Reparsed.Should().BeGreaterThan(0);
        Has(store, "AddedByEdit").Should().BeTrue();
        Has(store, "CThing").Should().BeTrue();
        Has(store, "BThing").Should().BeTrue(); // untouched survivor
    }

    // ── TS warm start from cache (415-422) ──────────────────────────────────────

    [Fact]
    public async Task RefreshTypeScript_WarmStart_LoadsCachedSegment()
    {
        // First store builds + saves the TS cache to the shared cache dir.
        CodeIndexStore first = await BuildBoth();
        Has(first, "TsStore").Should().BeTrue();

        // A SECOND store over the same fs + cache dir with fullRebuild:false must warm-load the cached
        // TS segment (nothing changed → early return after the warm publish), so TS symbols are queryable.
        CodeIndexStore second = NewStore();
        await second.RefreshTypeScriptAsync(Root, fullRebuild: false, CancellationToken.None);

        Has(second, "TsStore").Should().BeTrue();
        second.TsProjectCount.Should().BeGreaterThan(0);
    }

    // ── TS delta copy-forward + reparse changed (446-458, 482-490) ───────────────

    [Fact]
    public async Task RefreshTypeScript_Delta_ReparsesChangedTsFile()
    {
        CodeIndexStore store = await BuildBoth();
        Has(store, "TsStore").Should().BeTrue();

        _fs.AddFile(_storeTs, "export class TsStore { getById(id: number): void { } newlyAddedTsMethod(): void { } }");
        _fs.SetLastWriteTimeUtc(_storeTs, DateTime.UtcNow.AddSeconds(30));

        await store.RefreshTypeScriptAsync(Root, fullRebuild: false, CancellationToken.None);

        Has(store, "newlyAddedTsMethod").Should().BeTrue();
        // SCSS survivor carried forward untouched.
        Has(store, "toolbar").Should().BeTrue();
    }

    // ── GetFileOutline substring fallback (620) ─────────────────────────────────

    [Fact]
    public void GetFileOutline_SubstringMatch_ResolvesFile()
    {
        CodeIndexStore store = BuildCSharp();

        // "A" is a substring of "A.cs" but not an exact SourceFilesByName key.
        SourceFileIndex? outline = store.GetFileOutline("A");

        outline.Should().NotBeNull();
        outline!.FileName.Should().Be("A.cs");
    }

    // ── ResolveBareName substring file fallback (758) ───────────────────────────

    [Fact]
    public void ResolveBareName_SubstringFileMatch_FindsFile()
    {
        CodeIndexStore store = BuildCSharp();

        BareNameResolution r = store.ResolveBareName("A", "CsThing");

        r.FileFound.Should().BeTrue();
        r.InScope.Should().Contain(c => c.FullyQualified == "N.CsThing");
    }

    // ── CheckDanglingReferences substring file fallback (852) ───────────────────

    [Fact]
    public async Task CheckDanglingReferences_SubstringMatch_ProducesReport()
    {
        CodeIndexStore store = await BuildBoth();

        // "Store" is a substring of "Store.ts" but not an exact key.
        string report = store.CheckDanglingReferences("Store");

        report.Should().Contain("Reference check");
        report.Should().Contain("Store.ts");
    }

    // ── CheckDanglingReferences "could not read" (868-869) ──────────────────────

    [Fact]
    public async Task CheckDanglingReferences_WhenFileUnreadable_ReportsCouldNotRead()
    {
        CodeIndexStore store = await BuildBoth();

        // The file stays in the (already published) index, but the underlying bytes vanish, so
        // TsReferenceCheck.Analyze's ReadAllText throws FileNotFoundException (an IOException) → null.
        _fs.DeleteFile(_storeTs);

        string report = store.CheckDanglingReferences("Store.ts");

        report.Should().Contain("could not read");
    }

    // ── GetCallHierarchy TypeScript callees dispatch (955) ──────────────────────

    [Fact]
    public async Task GetCallHierarchy_TypeScriptCallees_Dispatches()
    {
        CodeIndexStore store = await BuildBoth();

        string res = store.GetCallHierarchy("getById", "callees", null, 40, 5, "typescript");

        res.Should().NotBeNullOrWhiteSpace();
    }

    // ── ProjectNames yields TS projects (981-983) ───────────────────────────────

    [Fact]
    public async Task ProjectNames_IncludesTsProjects()
    {
        CodeIndexStore store = await BuildBoth();

        List<string> names = store.ProjectNames().ToList();

        names.Should().Contain("Proj");        // C#
        names.Should().Contain("myapp-web");   // TS
    }

    // ── SymbolNames projectFilter skip branch (1014-1015) ───────────────────────

    [Fact]
    public async Task SymbolNames_WithProjectFilter_SkipsOtherProjects()
    {
        CodeIndexStore store = await BuildBoth();

        List<string> names = store.SymbolNames("Proj").ToList();

        names.Should().Contain("CsThing");       // in project Proj
        names.Should().NotContain("TsStore");    // in myapp-web → skipped by the filter continue
    }

    // ── ProjectDirsByName includes TS project dirs (1062-1064) ──────────────────

    [Fact]
    public async Task ProjectDirsByName_IncludesTsProjectDirs()
    {
        CodeIndexStore store = await BuildBoth();

        IReadOnlyDictionary<string, string> dirs = store.ProjectDirsByName();

        dirs.Should().ContainKey("Proj");        // C#
        dirs.Should().ContainKey("myapp-web");   // TS added via TryAdd
    }

    // ── UnifiedProjects: TS name collides with C# project (676-677) ─────────────

    [Fact]
    public async Task ListProjects_TsNameCollidesWithCsProject_CsWins()
    {
        CodeIndexStore store = await BuildBoth();

        List<ProjectIndex> projects = store.ListProjects();

        // Exactly one "Proj" — the C# one wins over the same-named TS project (the continue at 676-677).
        List<ProjectIndex> projs = projects.Where(p => p.Name == "Proj").ToList();
        projs.Should().ContainSingle();
        projs[0].SourceFiles.Should().Contain(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
    }

    // ── FindType first-match + none (718-721) ───────────────────────────────────

    [Fact]
    public void FindType_ReturnsFirstMatch_OrNull()
    {
        CodeIndexStore store = BuildCSharp();

        (TypeInfo Type, SourceFileIndex File)? hit = store.FindType("CsThing");
        hit.Should().NotBeNull();
        hit!.Value.Type.Name.Should().Be("CsThing");

        store.FindType("NoSuchType").Should().BeNull();
    }

    // ── SearchSymbol initials fallback (acronym query) ──────────────────────────

    [Fact]
    public void SearchSymbol_AcronymQuery_FallsBackToInitials()
    {
        CodeIndexStore store = BuildCSharp();

        // "GUR" is not a substring of any name, but matches GetUserRole by camelCase initials.
        List<SymbolSearchResult> results = store.SearchSymbol("GUR", null, null);

        results.Should().Contain(r => r.Name == "GetUserRole");
    }

    // ── MatchesKindFilter arms (1082-1088) ──────────────────────────────────────

    [Fact]
    public void SearchSymbol_KindFilters_MatchEachMemberKind()
    {
        CodeIndexStore store = BuildCSharp();

        store.SearchSymbol("MyProp", "property", null)
            .Should().Contain(r => r.Name == "MyProp" && r.Kind == "Property");
        store.SearchSymbol("MyField", "field", null)
            .Should().Contain(r => r.Name == "MyField" && r.Kind == "Field");
        store.SearchSymbol("MyEvent", "event", null)
            .Should().Contain(r => r.Name == "MyEvent" && r.Kind == "Event");
        store.SearchSymbol("CsThing", "constructor", null)
            .Should().Contain(r => r.Kind == "Constructor");
        store.SearchSymbol("Color", "enum", null)
            .Should().Contain(r => r.Name == "Color" && r.Kind == "Enum");
        store.SearchSymbol("IThing", "interface", null)
            .Should().Contain(r => r.Name == "IThing" && r.Kind == "Interface");
    }
}
