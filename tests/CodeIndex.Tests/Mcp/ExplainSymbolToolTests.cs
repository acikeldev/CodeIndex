using System.Text;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-contract tests for <see cref="ExplainSymbolTool"/>: the one-call dossier resolves a type or a member and
/// assembles Members/Inheritance/Source/References (type) or Source/References/Callers (member) sections, hands back
/// the ambiguity list verbatim, suggests on a miss, caps inline source, and truncates under the response budget.
/// </summary>
public sealed class ExplainSymbolToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private void Seed()
    {
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\IGreeter.cs", "namespace Core;\npublic interface IGreeter { string Greet(string name); }\n");
        _fs.AddFile(@"C:\repo\Core\Greeter.cs",
            "namespace Core;\npublic class Greeter : IGreeter\n{\n    public string Greet(string name) => $\"Hi {name}\";\n}\n");
        _fs.AddFile(@"C:\repo\Core\Consumer.cs",
            "namespace Core;\npublic class Consumer\n{\n    private readonly Greeter _greeter = new();\n    public void Run() { string s = _greeter.Greet(\"x\"); }\n}\n");
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Type_AssemblesMembersInheritanceSourceReferences()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greeter");

        output.Should().Contain("# explain_symbol: class Greeter");
        output.Should().Contain("## Members");
        output.Should().Contain("## Inheritance");
        output.Should().Contain("## Source");
        output.Should().Contain("## References");
        output.Should().Contain("IGreeter");   // inheritance
        output.Should().Contain("Consumer");   // reference site
    }

    [Fact]
    public void Member_AssemblesSourceReferencesCallers()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greet");

        output.Should().Contain("# explain_symbol:");
        output.Should().Contain("## Source");
        output.Should().Contain("## References");
        output.Should().Contain("## Callers");
    }

    [Fact]
    public void AmbiguousType_ReturnsDisambiguationVerbatim()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"A/A.csproj\" />\n  <Project Path=\"B/B.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\A\A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\B\B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\A\Widget.cs", "namespace Alpha;\npublic class Widget { }\n");
        _fs.AddFile(@"C:\repo\B\Widget.cs", "namespace Beta;\npublic class Widget { }\n");
        CodeIndexStore store = Build();

        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "Widget");

        output.Should().StartWith("AMBIGUOUS:");
        output.Should().Contain("Alpha");
        output.Should().Contain("Beta");
    }

    [Fact]
    public void CaseInsensitiveQuery_StillFindsReferences()
    {
        Seed();
        CodeIndexStore store = Build();

        // Lowercase query resolves the type case-insensitively; References must search the resolved proper-case
        // name (the reference matcher is case-SENSITIVE) or it reports zero references for a used type.
        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "greeter");

        output.Should().Contain("## References");
        output.Should().Contain("Consumer");
    }

    [Fact]
    public void UnknownSymbol_ReturnsNotFoundWithSuggestion()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greetr");

        output.Should().Contain("No symbol found matching 'Greetr'.");
    }

    [Fact]
    public void StaleIndexDeletedFile_SourceSectionShowsUnavailable()
    {
        Seed();
        CodeIndexStore store = Build();
        // The file is deleted after indexing; the Source section must not present the error sentinel as code.
        _fs.DeleteFile(@"C:\repo\Core\Greeter.cs");

        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greeter");

        output.Should().Contain("source unavailable");
    }

    [Fact]
    public void LongSource_IsCappedWithGetMoreHint()
    {
        // A type whose body spans well over the inline-source cap (160 lines) so RenderSource truncates it.
        StringBuilder body = new();
        body.AppendLine("namespace Core;");
        body.AppendLine("public class Big");
        body.AppendLine("{");
        for (int i = 0; i < 200; i++)
        {
            body.AppendLine($"    private int _field{i};");
        }

        body.AppendLine("}");
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Big.cs", body.ToString());
        CodeIndexStore store = Build();

        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "Big");

        output.Should().Contain("more line(s) — get_symbol_source(");
    }

    [Fact]
    public void OversizeType_TruncatesSectionsUnderBudget()
    {
        // Thousands of members make the first section alone exceed the response budget, so later sections are
        // replaced by the budget note rather than dropped silently.
        StringBuilder body = new();
        body.AppendLine("namespace Core;");
        body.AppendLine("public class Huge");
        body.AppendLine("{");
        for (int i = 0; i < 3000; i++)
        {
            body.AppendLine($"    public int Property{i} {{ get; set; }}");
        }

        body.AppendLine("}");
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Huge.cs", body.ToString());
        CodeIndexStore store = Build();

        string output = ExplainSymbolTool.ExplainSymbol(store, _fs, "Huge");

        output.Should().Contain("Response budget reached");
    }

    [Fact]
    public void Concise_Type_OmitsSourceBodyKeepsStructureAndPointer()
    {
        Seed();
        CodeIndexStore store = Build();

        string detailed = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greeter");
        string concise = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greeter", verbosity: "concise");

        // The method body's string literal lives ONLY in the rendered source, not in Members/References.
        detailed.Should().Contain("Hi {name}");
        concise.Should().NotContain("Hi {name}");

        // Structure survives, the tier is tagged, and the exact re-fetch call is offered.
        concise.Should().Contain("[concise]");
        concise.Should().Contain("## Members");
        concise.Should().Contain("## References");
        concise.Should().Contain("get_symbol_source(");
        concise.Length.Should().BeLessThan(detailed.Length, "omitting the body must shrink the response");
    }

    [Fact]
    public void Concise_Member_OmitsBodyButKeepsReferencesAndCallers()
    {
        // Multi-line body with an interior token that never appears on the signature line (which find_references
        // echoes) nor at the call site — so it can only reach the response via the rendered Source body.
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Worker.cs",
            "namespace Core;\npublic class Worker\n{\n    public int Compute(int n)\n    {\n        int uniqueBodyMarker = n * 2;\n        return uniqueBodyMarker;\n    }\n}\n");
        _fs.AddFile(@"C:\repo\Core\Caller.cs",
            "namespace Core;\npublic class Caller\n{\n    public void Go() { int x = new Worker().Compute(3); }\n}\n");
        CodeIndexStore store = Build();

        string detailed = ExplainSymbolTool.ExplainSymbol(store, _fs, "Compute");
        string concise = ExplainSymbolTool.ExplainSymbol(store, _fs, "Compute", verbosity: "concise");

        detailed.Should().Contain("uniqueBodyMarker");    // body rendered in the Source section
        concise.Should().NotContain("uniqueBodyMarker");  // body omitted in concise
        concise.Should().Contain("[concise]");
        concise.Should().Contain("## References");
        concise.Should().Contain("## Callers");
        concise.Should().Contain("get_symbol_source(");
        concise.Length.Should().BeLessThan(detailed.Length);
    }

    [Fact]
    public void Verbosity_DefaultsToDetailed_AndUnknownValueIsDetailed()
    {
        Seed();
        CodeIndexStore store = Build();

        string dflt = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greeter");
        string bogus = ExplainSymbolTool.ExplainSymbol(store, _fs, "Greeter", verbosity: "verbose");

        // Anything that isn't "concise" is the detailed tier → byte-identical to the default (no behaviour change).
        bogus.Should().Be(dflt);
        dflt.Should().NotContain("[concise]");
        dflt.Should().Contain("Hi {name}");
    }
}
