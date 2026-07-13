using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-contract tests for <see cref="PrepareChangeTool"/>: the pre-edit briefing assembles Definition/Call
/// sites/Implementors/Callers for a type and Definition/Call sites/Callers for a member, returns the ambiguity
/// list verbatim, and suggests on a miss.
/// </summary>
public sealed class PrepareChangeToolTests
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
    public void Type_AssemblesDefinitionCallSitesImplementorsCallers()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = PrepareChangeTool.PrepareChange(store, _fs, "IGreeter");

        output.Should().Contain("# prepare_change: interface IGreeter");
        output.Should().Contain("## Definition");
        output.Should().Contain("## Call sites");
        output.Should().Contain("## Implementors / overrides");
        output.Should().Contain("## Callers");
        output.Should().Contain("Greeter");   // implementor
    }

    [Fact]
    public void Member_AssemblesDefinitionCallSitesCallers()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = PrepareChangeTool.PrepareChange(store, _fs, "Greet");

        output.Should().Contain("# prepare_change:");
        output.Should().Contain("## Definition");
        output.Should().Contain("## Call sites");
        output.Should().Contain("## Callers");
        output.Should().NotContain("## Implementors / overrides");
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

        string output = PrepareChangeTool.PrepareChange(store, _fs, "Widget");

        output.Should().StartWith("AMBIGUOUS:");
    }

    [Fact]
    public void UnknownSymbol_ReturnsNotFoundWithSuggestion()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = PrepareChangeTool.PrepareChange(store, _fs, "Greetr");

        output.Should().Contain("No symbol found matching 'Greetr'.");
    }
}
