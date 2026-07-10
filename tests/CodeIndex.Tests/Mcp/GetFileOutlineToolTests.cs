using System.Text;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting coverage for <see cref="GetFileOutlineTool"/>: the full member-by-member outline,
/// the types-only summary (forced and size-triggered), enum/nested rendering, and the not-found path.
/// </summary>
public sealed class GetFileOutlineToolTests
{
    private const string Root = @"C:\repo";

    private static CodeIndexStore BuildStore(Action<InMemoryFileSystem> addFiles)
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
        fs.AddFile(@"C:\repo\P\P.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        addFiles(fs);
        CodeIndexStore store = new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void FullOutline_RendersTypesAndMembersWithLineMarkers()
    {
        CodeIndexStore store = BuildStore(fs => fs.AddFile(@"C:\repo\P\Widget.cs", """
            namespace P;
            public class Widget
            {
                private int _count;
                public event System.EventHandler? Changed;
                public Widget() { }
                public int Value { get; set; }
                public void Run() { }
            }
            """));

        string output = GetFileOutlineTool.GetFileOutline(store, "Widget.cs");

        output.Should().Contain("# Widget.cs (P)");
        output.Should().Contain("Namespace: P");
        output.Should().Contain("Source: ");
        output.Should().Contain("## class Widget");
        output.Should().Contain("Constructors:");
        output.Should().Contain("Properties:");
        output.Should().Contain("Fields:");
        output.Should().Contain("Events:");
        output.Should().Contain("Methods:");
        output.Should().Contain("Run");
        output.Should().MatchRegex(@"\[\d+\+\d+\]");
    }

    [Fact]
    public void EnumType_ShowsValuesLine()
    {
        CodeIndexStore store = BuildStore(fs => fs.AddFile(@"C:\repo\P\Colour.cs", """
            namespace P;
            public enum Colour { Red, Green, Blue }
            """));

        string output = GetFileOutlineTool.GetFileOutline(store, "Colour.cs");

        output.Should().Contain("enum Colour");
        output.Should().Contain("Values:");
        output.Should().Contain("Red");
    }

    [Fact]
    public void NestedType_UsesDeeperHeadingAndIndent()
    {
        CodeIndexStore store = BuildStore(fs => fs.AddFile(@"C:\repo\P\Outer.cs", """
            namespace P;
            public class Outer
            {
                public class Inner
                {
                    public void Ping() { }
                }
            }
            """));

        string output = GetFileOutlineTool.GetFileOutline(store, "Outer.cs");

        output.Should().Contain("## class Outer");
        output.Should().Contain("### class Inner");
    }

    [Fact]
    public void TypesOnlyFlag_ReturnsSummaryWithPerTypeCounts()
    {
        CodeIndexStore store = BuildStore(fs => fs.AddFile(@"C:\repo\P\Widget.cs", """
            namespace P;
            public class Widget
            {
                public Widget() { }
                public int Value { get; set; }
                public void Run() { }
            }
            """));

        string output = GetFileOutlineTool.GetFileOutline(store, "Widget.cs", typesOnly: true);

        output.Should().Contain("types-only summary (typesOnly=true)");
        output.Should().Contain("Call get_type_members(type=...)");
        output.Should().Contain("class Widget");
        output.Should().Contain("1 ctors");
        output.Should().Contain("1 props");
        output.Should().Contain("1 methods");
        // The forced summary must NOT emit the member-detail sections.
        output.Should().NotContain("Methods:");
    }

    [Fact]
    public void LargeFile_DegradesToTypesOnlySummary()
    {
        StringBuilder src = new();
        src.AppendLine("namespace P;");
        src.AppendLine("public class Giant");
        src.AppendLine("{");
        for (int i = 0; i < 3000; i++)
        {
            src.AppendLine($"    public void Method{i}() {{ }}");
        }

        src.AppendLine("}");

        CodeIndexStore store = BuildStore(fs => fs.AddFile(@"C:\repo\P\Giant.cs", src.ToString()));

        string output = GetFileOutlineTool.GetFileOutline(store, "Giant.cs");

        output.Should().Contain("types-only summary");
        output.Should().Contain("exceeds the response budget");
        output.Should().Contain("class Giant");
        output.Should().Contain("3000 methods");
        output.Length.Should().BeLessThan(48_000);
    }

    [Fact]
    public void UnknownFile_ReturnsNotFoundWithDidYouMean()
    {
        CodeIndexStore store = BuildStore(fs => fs.AddFile(@"C:\repo\P\Widget.cs", "namespace P; public class Widget { }"));

        string output = GetFileOutlineTool.GetFileOutline(store, "Widgets.cs");

        output.Should().Contain("File 'Widgets.cs' not found in index.");
        output.Should().Contain("Try list_files to browse indexed files");
        // Close name should trigger a suggestion of the real file.
        output.Should().Contain("Widget.cs");
    }
}
