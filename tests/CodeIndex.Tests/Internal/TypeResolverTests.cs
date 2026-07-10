using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// TypeResolver namespace-grouping / partial-merge / ambiguity rules. The store is stubbed with NSubstitute so
/// only FindTypes/TypeNames behaviour is exercised — no filesystem or real index involved.
/// </summary>
public class TypeResolverTests
{
    private static TypeInfo Type(
        string name,
        string? ns = null,
        List<string>? baseTypes = null,
        List<MemberInfo>? members = null,
        int startLine = 1,
        int lineCount = 10,
        string keyword = "class") =>
        new()
        {
            Name = name,
            Kind = SymbolKind.Class,
            TypeKeyword = keyword,
            Namespace = ns,
            BaseTypes = baseTypes,
            Members = members ?? [],
            StartLine = startLine,
            LineCount = lineCount,
        };

    private static SourceFileIndex File(string fileName, string project, string? ns, params TypeInfo[] types) =>
        new()
        {
            FileName = fileName,
            SourceFilePath = @"C:\repo\" + fileName,
            ProjectName = project,
            Namespace = ns,
            Types = [.. types],
        };

    private static MemberInfo Member(string name) =>
        new()
        {
            Name = name,
            Kind = SymbolKind.Method,
            ReturnType = "void",
            Signature = name + "()",
            StartLine = 1,
            LineCount = 1,
        };

    private static ICodeIndexStore StoreWith(List<(TypeInfo, SourceFileIndex)> matches, params string[] typeNames)
    {
        ICodeIndexStore store = Substitute.For<ICodeIndexStore>();
        store.FindTypes(Arg.Any<string>()).Returns(matches);
        store.TypeNames().Returns(typeNames);
        return store;
    }

    [Fact]
    public void Resolve_NoMatches_ReturnsNullWithNotFoundError()
    {
        ICodeIndexStore store = StoreWith([], "Widget", "Gadget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Nonexistent", null, null, out string? error);

        result.Should().BeNull();
        error.Should().Contain("Type 'Nonexistent'");
        error.Should().Contain("not found in index.");
        error.Should().Contain("Try search_symbol");
    }

    [Fact]
    public void Resolve_NoMatches_IncludesDidYouMeanSuggestion()
    {
        ICodeIndexStore store = StoreWith([], "Widget");

        TypeResolver.Resolve(store, "Widgat", null, null, out string? error);

        error.Should().Contain("Did you mean: Widget?");
    }

    [Fact]
    public void Resolve_NotFoundError_MentionsNamespaceAndProjectFilters()
    {
        // A match exists but is filtered out by the namespace filter -> not-found path.
        TypeInfo t = Type("Widget", ns: "App.Core");
        SourceFileIndex f = File("Widget.cs", "AppProj", "App.Core", t);
        ICodeIndexStore store = StoreWith([(t, f)], "Widget");

        TypeResolver.Resolve(store, "Widget", "OtherNs", "OtherProj", out string? error);

        error.Should().Contain("in namespace 'OtherNs'");
        error.Should().Contain("in project 'OtherProj'");
    }

    [Fact]
    public void Resolve_NamespaceFilter_SuffixMatchSelectsCorrectType()
    {
        TypeInfo dataType = Type("Widget", ns: "App.Data.Models");
        SourceFileIndex dataFile = File("WidgetData.cs", "DataProj", "App.Data.Models", dataType);
        TypeInfo uiType = Type("Widget", ns: "App.Ui");
        SourceFileIndex uiFile = File("WidgetUi.cs", "UiProj", "App.Ui", uiType);
        ICodeIndexStore store = StoreWith([(dataType, dataFile), (uiType, uiFile)], "Widget");

        // Suffix filter "Models" matches only "App.Data.Models".
        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", "Models", null, out string? error);

        error.Should().BeNull();
        result.Should().NotBeNull();
        result!.Namespace.Should().Be("App.Data.Models");
        result.Project.Should().Be("DataProj");
    }

    [Fact]
    public void Resolve_ProjectFilter_NarrowsMatchesCaseInsensitively()
    {
        TypeInfo a = Type("Widget", ns: "App");
        SourceFileIndex fa = File("A.cs", "ProjA", "App", a);
        TypeInfo b = Type("Widget", ns: "App");
        SourceFileIndex fb = File("B.cs", "ProjB", "App", b);
        ICodeIndexStore store = StoreWith([(a, fa), (b, fb)], "Widget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", null, "PROJB", out string? error);

        error.Should().BeNull();
        result.Should().NotBeNull();
        result!.DisplayFile.Should().Be("B.cs");
    }

    [Fact]
    public void Resolve_MultipleNamespaces_ReturnsAmbiguousError()
    {
        TypeInfo core = Type("Widget", ns: "App.Core");
        SourceFileIndex coreFile = File("Core.cs", "CoreProj", "App.Core", core);
        TypeInfo ui = Type("Widget", ns: "App.Ui");
        SourceFileIndex uiFile = File("Ui.cs", "UiProj", "App.Ui", ui);
        ICodeIndexStore store = StoreWith([(core, coreFile), (ui, uiFile)], "Widget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", null, null, out string? error);

        result.Should().BeNull();
        error.Should().StartWith("AMBIGUOUS: 2 types named 'Widget'");
        error.Should().Contain("App.Core");
        error.Should().Contain("App.Ui");
        error.Should().Contain("[Core.cs] (CoreProj)");
    }

    [Fact]
    public void Resolve_AmbiguousWithGlobalNamespace_LabelsGlobal()
    {
        // Type namespace null and file namespace null -> empty group key -> "(global namespace)" label.
        TypeInfo global = Type("Widget", ns: null);
        SourceFileIndex globalFile = File("Global.cs", "GlobalProj", null, global);
        TypeInfo scoped = Type("Widget", ns: "App");
        SourceFileIndex scopedFile = File("Scoped.cs", "ScopedProj", "App", scoped);
        ICodeIndexStore store = StoreWith([(global, globalFile), (scoped, scopedFile)], "Widget");

        TypeResolver.Resolve(store, "Widget", null, null, out string? error);

        error.Should().Contain("(global namespace)");
    }

    [Fact]
    public void Resolve_SingleNamespace_MergesPartialParts()
    {
        MemberInfo m1 = Member("Alpha");
        MemberInfo m2 = Member("Beta");
        TypeInfo part1 = Type("Widget", ns: "App", baseTypes: ["BaseA"], members: [m1], startLine: 5);
        SourceFileIndex file1 = File("Widget.part1.cs", "AppProj", "App", part1);
        TypeInfo part2 = Type("Widget", ns: "App", baseTypes: ["IThing"], members: [m2], startLine: 20);
        SourceFileIndex file2 = File("Widget.part2.cs", "AppProj", "App", part2);
        ICodeIndexStore store = StoreWith([(part1, file1), (part2, file2)], "Widget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", null, null, out string? error);

        error.Should().BeNull();
        result.Should().NotBeNull();
        result!.PartCount.Should().Be(2);
        result.Members.Select(m => m.Name).Should().BeEquivalentTo(["Alpha", "Beta"]);
        result.BaseTypesDisplay.Should().Be("BaseA, IThing");
    }

    [Fact]
    public void Resolve_PrimarySelection_PrefersPartWithBaseTypesThenLowestStartLine()
    {
        // part-without-bases starts earlier, but the part WITH bases must win as primary.
        TypeInfo bare = Type("Widget", ns: "App", baseTypes: null, startLine: 1, keyword: "class", lineCount: 3);
        SourceFileIndex bareFile = File("Widget.bare.cs", "AppProj", "App", bare);
        TypeInfo withBase = Type("Widget", ns: "App", baseTypes: ["BaseA"], startLine: 50, keyword: "sealed class", lineCount: 99);
        SourceFileIndex baseFile = File("Widget.impl.cs", "AppProj", "App", withBase);
        ICodeIndexStore store = StoreWith([(bare, bareFile), (withBase, baseFile)], "Widget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", null, null, out string? error);

        error.Should().BeNull();
        result!.DisplayFile.Should().Be("Widget.impl.cs");
        result.StartLine.Should().Be(50);
        result.TypeKeyword.Should().Be("sealed class");
        result.LineCount.Should().Be(99);
    }

    [Fact]
    public void Resolve_SingleMatch_NoBaseTypes_BaseTypesDisplayNull()
    {
        TypeInfo t = Type("Widget", ns: "App", baseTypes: null);
        SourceFileIndex f = File("Widget.cs", "AppProj", "App", t);
        ICodeIndexStore store = StoreWith([(t, f)], "Widget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", null, null, out string? error);

        error.Should().BeNull();
        result!.BaseTypesDisplay.Should().BeNull();
        result.PartCount.Should().Be(1);
    }

    [Fact]
    public void Resolve_TypeNamespaceNull_FallsBackToFileNamespace()
    {
        // Type.Namespace null -> group key + resolved Namespace come from the file's namespace.
        TypeInfo t = Type("Widget", ns: null);
        SourceFileIndex f = File("Widget.cs", "AppProj", "App.FromFile", t);
        ICodeIndexStore store = StoreWith([(t, f)], "Widget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", null, null, out string? error);

        error.Should().BeNull();
        result!.Namespace.Should().Be("App.FromFile");
    }

    [Fact]
    public void Resolve_MergedBases_AreDistinct()
    {
        TypeInfo part1 = Type("Widget", ns: "App", baseTypes: ["BaseA", "IShared"], startLine: 1);
        SourceFileIndex file1 = File("Widget.1.cs", "AppProj", "App", part1);
        TypeInfo part2 = Type("Widget", ns: "App", baseTypes: ["IShared", "IExtra"], startLine: 20);
        SourceFileIndex file2 = File("Widget.2.cs", "AppProj", "App", part2);
        ICodeIndexStore store = StoreWith([(part1, file1), (part2, file2)], "Widget");

        TypeResolver.ResolvedType? result = TypeResolver.Resolve(store, "Widget", null, null, out string? error);

        error.Should().BeNull();
        result!.BaseTypesDisplay.Should().Be("BaseA, IShared, IExtra");
    }
}
