using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>Wave 2 CK1: search_symbol composite ranking, dedupe, generated-file classification, initials fallback.
/// The original real-disk temp-dir fixture is replaced by the shared in-memory file system; assertions unchanged.</summary>
public sealed class SearchSymbolRankingTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public SearchSymbolRankingTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
    }

    private void Add(string name, string content) => _fs.AddFile(Path.Combine(@"C:\repo\Proj", name), content);

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    // ---- GeneratedFileClassifier ----

    [Theory]
    [InlineData("Foo.designer.cs", true)]
    [InlineData("Foo.Designer.cs", true)]
    [InlineData("Widget.g.cs", true)]
    [InlineData("X.g.i.cs", true)]
    [InlineData("Bar.generated.cs", true)]
    [InlineData("Reference.cs", true)]
    [InlineData("AssemblyInfo.cs", true)]
    [InlineData("TemporaryGeneratedFile_ABC.cs", true)]
    [InlineData("PatientReference.cs", false)]
    [InlineData("Foo.cs", false)]
    [InlineData("Service.cs", false)]
    public void GeneratedFileClassifier_Default(string fileName, bool expected)
    {
        Assert.Equal(expected, GeneratedFileClassifier.IsGenerated(fileName));
        Assert.Equal(expected, GeneratedFileClassifier.IsGenerated("C:/repo/Proj/" + fileName)); // full path too
    }

    // ---- SymbolRanker.IsInitialsSubsequence ----

    [Theory]
    [InlineData("GetUsersByRole", "GUBR", true)]
    [InlineData("GetUsersByRole", "GUR", true)]
    [InlineData("GetUsersByRole", "GX", false)]
    [InlineData("GetUsersByRole", "G", false)]   // len < 2
    [InlineData("save", "sv", false)]            // no word boundary at 'v'
    public void IsInitialsSubsequence(string name, string query, bool expected)
    {
        Assert.Equal(expected, SymbolRanker.IsInitialsSubsequence(name, query));
    }

    // ---- Ranking (fixture) ----

    [Fact]
    public void CanonicalTypeDefinitionRanksFirst()
    {
        Add("WidgetInfo.cs", "namespace N; public class WidgetInfo { }");
        Add("Other.cs", "namespace N; public class Other { public int WidgetInfo { get; set; } }");
        Add("Reference.cs", "namespace N; public class Ref { public void WidgetInfo() { } }"); // generated
        CodeIndexStore store = Build();

        List<SymbolSearchResult> r = store.SearchSymbol("WidgetInfo", null, null);

        Assert.Equal("WidgetInfo", r[0].Name);
        Assert.Null(r[0].ParentType); // the type, not a member
    }

    [Fact]
    public void TypeBeforeMember_SameTier()
    {
        Add("A.cs", "namespace N; public class Widget { }");
        Add("B.cs", "namespace N; public class Holder { public int Widget { get; set; } }");
        CodeIndexStore store = Build();

        List<SymbolSearchResult> r = store.SearchSymbol("Widget", null, null);
        int typeIdx = r.FindIndex(x => x.Name == "Widget" && x.ParentType is null);
        int memberIdx = r.FindIndex(x => x.Name == "Widget" && x.ParentType is not null);
        Assert.True(typeIdx >= 0 && memberIdx >= 0);
        Assert.True(typeIdx < memberIdx);
    }

    [Fact]
    public void GeneratedDemotedButNotExcluded()
    {
        // Same name + same match tier (both exact `class Thing`); only the generated/hand-written axis differs.
        // Gen.g.cs is generated (*.g.cs) but IS indexed (the designer-without-dbml exclusion only hits *.Designer.cs).
        Add("Hand.cs", "namespace N; public class Thing { }");
        Add("Gen.g.cs", "namespace N; public class Thing { }");
        CodeIndexStore store = Build();

        List<SymbolSearchResult> r = store.SearchSymbol("Thing", null, null);
        int handIdx = r.FindIndex(x => x.Name == "Thing" && x.File == "Hand.cs");
        int genIdx = r.FindIndex(x => x.Name == "Thing" && x.File == "Gen.g.cs");
        Assert.True(handIdx >= 0, "hand-written present");
        Assert.True(genIdx >= 0, "generated STILL present (demote, not exclude)");
        Assert.True(handIdx < genIdx, "hand-written ranks before generated");
    }

    [Fact]
    public void SearchSymbol_IsDeterministic()
    {
        Add("A.cs", "namespace N; public class Zeta { } public class Alpha { }");
        Add("B.cs", "namespace N; public class Beta { }");
        CodeIndexStore store = Build();

        List<string> first = store.SearchSymbol("a", null, null).Select(x => x.Name + "@" + x.SourceFilePath).ToList();
        List<string> second = store.SearchSymbol("a", null, null).Select(x => x.Name + "@" + x.SourceFilePath).ToList();
        Assert.Equal(first, second);
    }

    [Fact]
    public void InitialsFallback_FindsAcronym_WhenNoSubstringHit()
    {
        Add("U.cs", "namespace N; public class UsersStore { public void GetUsersByRole() { } }");
        CodeIndexStore store = Build();

        // "GUBR" is not a substring of any name, but matches the initials of GetUsersByRole.
        List<SymbolSearchResult> r = store.SearchSymbol("GUBR", null, null);
        Assert.Contains(r, x => x.Name == "GetUsersByRole");
    }
}
