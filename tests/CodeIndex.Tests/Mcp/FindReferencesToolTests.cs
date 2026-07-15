using System.Text.RegularExpressions;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-level coverage of the find_references MCP tool: grouped-by-file rendering, the file-frequency summary,
/// word-boundary matching, comment skipping, the project filter, context windows, truncation footer, and the
/// not-found message. The tool reads file contents through the injected file system, so tests drive it with an
/// in-memory file system.
/// </summary>
public sealed class FindReferencesToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    public FindReferencesToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\A.cs", """
            namespace App;
            public class Widget
            {
                public void Frobnicate() { }
                public void Run() { Frobnicate(); }
            }
            """);
        _fs.AddFile(@"C:\repo\App\B.cs", """
            namespace App;
            public class Consumer
            {
                // Widget mentioned only in a comment here
                public void Use()
                {
                    Widget w = new Widget();
                    int WidgetId = 5;
                    w.Frobnicate();
                }
            }
            """);

        // Reference-facet fixture: 'Sprocket' occurs in production code (2 code lines), a test file, a generated
        // file, and once inside a string literal on its own line. 'SprocketUser'/'SprocketTests'/'SprocketGen'
        // do NOT match \bSprocket\b (word boundary), so the counts are exactly 2 prod / 1 test / 1 generated / 1 string.
        _fs.AddFile(@"C:\repo\App\Sprocket.cs", """
            namespace App;
            public class Sprocket
            {
                public void Spin() { }
            }
            public class SprocketUser
            {
                public void Use()
                {
                    Sprocket s = new Sprocket();
                    string label = "Sprocket";
                }
            }
            """);
        _fs.AddFile(@"C:\repo\App\SprocketTests.cs", """
            namespace App.Tests;
            public class SprocketTests
            {
                public void T() { Sprocket s = new Sprocket(); }
            }
            """);
        _fs.AddFile(@"C:\repo\App\SprocketGen.g.cs", """
            namespace App;
            public partial class SprocketGen { private Sprocket _s; }
            """);
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void FindReferences_GroupsByFile_WithSummaryAndHeaders()
    {
        CodeIndexStore store = Build();

        string result = FindReferencesTool.FindReferences(store, _fs, "Frobnicate");

        // Declaration + call in A.cs, one call in B.cs = 3 refs across 2 files.
        result.Should().Contain("Frobnicate: 3 refs in 2 files");
        result.Should().Contain("== App/A.cs ==");
        result.Should().Contain("== App/B.cs ==");
        result.Should().Contain("Frobnicate");
    }

    [Fact]
    public void FindReferences_WordBoundary_ExcludesSubstringsAndComments()
    {
        CodeIndexStore store = Build();

        string result = FindReferencesTool.FindReferences(store, _fs, "Widget");

        // A.cs class declaration (1) + B.cs `new Widget()` line (1). The comment line and `WidgetId` are excluded.
        result.Should().Contain("Widget: 2 refs in 2 files");
        result.Should().NotContain("WidgetId");
        result.Should().NotContain("mentioned only in a comment");
    }

    [Fact]
    public void FindReferences_ReturnsNotFoundMessage_WhenNoMatches()
    {
        CodeIndexStore store = Build();

        string result = FindReferencesTool.FindReferences(store, _fs, "Nonexistent");

        result.Should().Be("No references found for 'Nonexistent'.");
    }

    [Fact]
    public void FindReferences_ProjectFilter_NarrowsScope()
    {
        CodeIndexStore store = Build();

        FindReferencesTool.FindReferences(store, _fs, "Frobnicate", project: "App")
            .Should().Contain("Frobnicate: 3 refs");
        FindReferencesTool.FindReferences(store, _fs, "Frobnicate", project: "Nope")
            .Should().Be("No references found for 'Frobnicate'.");
    }

    [Fact]
    public void FindReferences_ContextLines_EmitsMarkedWindow()
    {
        CodeIndexStore store = Build();

        string result = FindReferencesTool.FindReferences(store, _fs, "Frobnicate", contextLines: 1);

        // The context renderer marks the matched line with a leading '>'.
        result.Should().Contain(">");
    }

    [Fact]
    public void FindReferences_TruncationFooter_WhenMaxExceeded()
    {
        CodeIndexStore store = Build();

        string result = FindReferencesTool.FindReferences(store, _fs, "Frobnicate", max: 1);

        result.Should().Contain("Frobnicate: 3 refs in 2 files");
        result.Should().Contain("Showing 1 sampled line(s)");
    }

    [Theory]
    [InlineData("    public void Frobnicate() { }", true)]
    [InlineData("    // Frobnicate is only in a comment", false)]
    [InlineData("    int FrobnicateId = 5;", false)]
    [InlineData("    int other = 5;", false)]
    public void IsCodeReference_AppliesBoundaryAndCommentRules(string line, bool expected)
    {
        Regex boundary = FindReferencesTool.GetWordBoundaryRegex("Frobnicate");

        FindReferencesTool.IsCodeReference(line, "Frobnicate", boundary).Should().Be(expected);
    }

    [Fact]
    public void GetWordBoundaryRegex_MatchesWholeWordOnly()
    {
        Regex boundary = FindReferencesTool.GetWordBoundaryRegex("User");

        boundary.IsMatch("User user").Should().BeTrue();
        boundary.IsMatch("UserId x").Should().BeFalse();
    }

    [Fact]
    public void FindReferences_UsageFacet_SplitsProdTestGenerated_AndExcludesStringOnly()
    {
        CodeIndexStore store = Build();

        string result = FindReferencesTool.FindReferences(store, _fs, "Sprocket");

        // Headline counts EVERY matched line (5) across the 3 files — unaffected by the facet split.
        result.Should().Contain("Sprocket: 5 refs in 3 files");
        // Facet: 2 prod + 1 test + 1 generated = 4 real; the lone string-literal line is excluded (heuristic).
        result.Should().Contain("Usage: prod 2, test 1, generated 1 — 4 real refs (plus 1 string/comment-only, excluded — heuristic)");
    }

    [Theory]
    [InlineData("        Sprocket s = new Sprocket();", false)]   // real code occurrences
    [InlineData("        string label = \"Sprocket\";", true)]    // only inside a string literal
    [InlineData("        DoThing(); // note about Sprocket", true)] // only in a trailing comment
    [InlineData("        var u = \"http://Sprocket/path\";", true)] // '//' inside a string must NOT read as a comment
    [InlineData("        Sprocket s; // and Sprocket again", false)] // one real occurrence keeps it a code ref
    public void IsStringOrCommentOnlyReference_ClassifiesMatchContext(string line, bool expected)
    {
        Regex boundary = FindReferencesTool.GetWordBoundaryRegex("Sprocket");

        FindReferencesTool.IsStringOrCommentOnlyReference(line, boundary).Should().Be(expected);
    }
}
