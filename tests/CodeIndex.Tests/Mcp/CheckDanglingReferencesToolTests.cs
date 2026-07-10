using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting coverage for the <c>check_dangling_references</c> tool: the not-indexed message
/// (with did-you-mean), the C#-only refusal, and the TS report listing both a possibly-MISSING import
/// (a used PascalCase symbol defined in another indexed TS file) and an UNUSED import.
/// </summary>
public sealed class CheckDanglingReferencesToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    public CheckDanglingReferencesToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\A.cs", "namespace App; public class A { public void M() { } }");
    }

    private CodeIndexStore NewStore()
    {
        return new CodeIndexStore(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
    }

    [Fact]
    public void UnknownFile_ReturnsNotIndexedWithDidYouMean()
    {
        CodeIndexStore store = NewStore();
        store.Build(Root);

        string output = CheckDanglingReferencesTool.CheckDanglingReferences(store, "Nope.ts");

        output.Should().Contain("check_dangling_references: file 'Nope.ts' not indexed.");
    }

    [Fact]
    public void CSharpFile_ReturnsTypeScriptOnlyMessage()
    {
        CodeIndexStore store = NewStore();
        store.Build(Root);

        string output = CheckDanglingReferencesTool.CheckDanglingReferences(store, "A.cs");

        output.Should().Be(
            "check_dangling_references: TypeScript/TSX only (for C# bare-name resolution use resolve_bare_name).");
    }

    [Fact]
    public async Task TsFile_ReportsMissingAndUnusedImports()
    {
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\web\Widget.ts", "export class Widget { }\n");
        _fs.AddFile(@"C:\repo\web\Consumer.ts",
            "import { Unused } from './other';\n"
            + "export class Consumer {\n"
            + "  make(): void {\n"
            + "    const w = new Widget();\n"
            + "  }\n"
            + "}\n");

        CodeIndexStore store = NewStore();
        store.Build(Root);
        await store.RefreshTypeScriptAsync(Root, fullRebuild: true, CancellationToken.None);

        string output = CheckDanglingReferencesTool.CheckDanglingReferences(store, "Consumer.ts");

        output.Should().Contain("# Reference check: Consumer.ts");
        // Widget is used but not imported, and IS defined in another indexed TS file → possibly MISSING.
        output.Should().Contain("Possibly MISSING imports (1)");
        output.Should().Contain("Widget");
        output.Should().Contain("defined in Widget.ts");
        // Unused is imported but never referenced → UNUSED.
        output.Should().Contain("UNUSED imports (1)");
        output.Should().Contain("Unused  from './other'");
        output.Should().Contain("Heuristic:");
    }

    [Fact]
    public async Task TsFile_WithNoIssues_ReportsNone()
    {
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\web\Clean.ts",
            "export class Clean {\n"
            + "  run(): number {\n"
            + "    return 1;\n"
            + "  }\n"
            + "}\n");

        CodeIndexStore store = NewStore();
        store.Build(Root);
        await store.RefreshTypeScriptAsync(Root, fullRebuild: true, CancellationToken.None);

        string output = CheckDanglingReferencesTool.CheckDanglingReferences(store, "Clean.ts");

        output.Should().Contain("Possibly MISSING imports (0)");
        output.Should().Contain("UNUSED imports (0)");
        output.Should().Contain("(none)");
    }
}
