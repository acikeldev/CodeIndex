using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

/// <summary>
/// XAML indexing: the x:Class partial (x:Name fields typed by element), identifier-valued attributes as hooks,
/// and how that feeds receiver resolution (a control field's method call binds) and callers (a handler wired only
/// in markup has the XAML as its caller).
/// </summary>
public sealed class XamlParserTests
{
    private const string Root = @"C:\repo";

    private const string SaleXaml = """
        <Window x:Class="App.Sale"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:pos="clr-namespace:App.Controls"
                Loaded="Window_Loaded" WindowState="Maximized">
            <Grid>
                <Grid.RowDefinitions><RowDefinition/></Grid.RowDefinitions>
                <pos:ScalePanel x:Name="scalePanel"/>
                <Button Name="saveButton" Click="btnSave_Click" Content="Save" Orientation="Horizontal"/>
                <!-- btnSave_Click saves the receipt -->
                <ListBox>
                    <ListBox.ItemTemplate>
                        <DataTemplate><TextBlock x:Name="inTemplate"/></DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </Grid>
        </Window>
        """;

    [Fact]
    public void Parse_XClass_FieldsTypedByElement_SkipsTemplatesAndRoot()
    {
        SourceFileIndex f = XamlParser.Parse(Root + @"\Sale.xaml", "Proj", SaleXaml);

        f.Language.Should().Be(Language.Xaml);
        f.Namespace.Should().Be("App");
        CodeIndex.Models.TypeInfo t = f.Types.Should().ContainSingle().Subject;
        t.Name.Should().Be("Sale");
        t.Namespace.Should().Be("App");
        t.BaseTypes.Should().Equal("Window");
        t.Members.Select(m => (m.Name, m.ReturnType)).Should().Equal(
            ("scalePanel", "App.Controls.ScalePanel"),
            ("saveButton", "Button"));
        t.Members.Single(m => m.Name == "saveButton").StartLine.Should().Be(9);
    }

    [Fact]
    public void Parse_Hooks_IdentifierValuedAttributes_ExceptKnownNonEvents()
    {
        SourceFileIndex f = XamlParser.Parse(Root + @"\Sale.xaml", "Proj", SaleXaml);

        f.XamlHooks!.Select(h => (h.Element, h.Attribute, h.Member)).Should().Equal(
            ("Window", "Loaded", "Window_Loaded"),
            ("Button", "Click", "btnSave_Click"));
    }

    [Fact]
    public void Parse_ResourceDictionary_OrMalformed_IsIndexedEmpty()
    {
        SourceFileIndex dict = XamlParser.Parse(Root + @"\Theme.xaml", "Proj",
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><SolidColorBrush/></ResourceDictionary>");
        dict.Language.Should().Be(Language.Xaml);
        dict.Types.Should().BeEmpty();
        dict.XamlHooks.Should().BeNull();

        SourceFileIndex broken = XamlParser.Parse(Root + @"\Broken.xaml", "Proj", "<Window x:Class=\"A.B\"");
        broken.Types.Should().BeEmpty();
    }

    // ── End to end: scanner → parser → call graph / references ────────────────────────────────────────────

    private readonly InMemoryFileSystem _fs = new();

    private CodeIndexStore BuildWpf()
    {
        _fs.AddFile(Root + @"\App.slnx", "<Solution>\n  <Project Path=\"Ui/Ui.csproj\" />\n</Solution>\n");
        _fs.AddFile(Root + @"\Ui\Ui.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(Root + @"\Ui\Sale.xaml", SaleXaml);
        _fs.AddFile(Root + @"\Ui\Sale.xaml.cs", """
            namespace App;
            public partial class Sale : Window
            {
                private void Window_Loaded(object s, object e) { scalePanel.Reset(); }
                private void btnSave_Click(object s, object e) { Save(); }
                private void Save() { }
            }
            """);
        _fs.AddFile(Root + @"\Ui\ScalePanel.cs", """
            namespace App.Controls;
            public class ScalePanel { public void Reset() { } }
            public class Printer { public void Reset() { } }
            """);
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Store_IndexesXamlNextToCodeBehind()
    {
        CodeIndexStore store = BuildWpf();

        store.AllSourceFiles.Should().Contain(f => f.FileName == "Sale.xaml" && f.Language == Language.Xaml);
    }

    [Fact]
    public void XNameField_TypesTheReceiver()
    {
        CodeIndexStore store = BuildWpf();

        string trace = TraceCallsTool.TraceCalls(store, "Window_Loaded", direction: "callees", depth: 2);

        trace.Should().Contain("ScalePanel.Reset  [ScalePanel.cs:");
        trace.Should().NotContain("Printer.Reset");
        trace.Should().NotContain("? receiver");
    }

    [Fact]
    public void HandlerWiredInXaml_HasTheMarkupAsCaller()
    {
        CodeIndexStore store = BuildWpf();

        string callers = CallHierarchyTool.CallHierarchy(store, "Sale.btnSave_Click", "callers", "csharp");
        callers.Should().Contain("== Sale.xaml (Ui) — 1 ==");
        callers.Should().Contain("9| (XAML) Button.Click:");

        string upstream = TraceCallsTool.TraceCalls(store, "Save", direction: "callers", depth: 3);
        upstream.Should().Contain("Sale.btnSave_Click");
        upstream.Should().Contain("Sale.xaml:9  <Button Click=\"btnSave_Click\">");
    }

    [Fact]
    public void EnumValuedAttribute_IsNotACaller()
    {
        CodeIndexStore store = BuildWpf();

        // "Maximized" is an attribute value but no method on Sale — never reported.
        CallHierarchyTool.CallHierarchy(store, "Maximized", "callers", "csharp").Should().Contain("no invocations");
    }

    [Fact]
    public void FindReferences_XamlHitsCountAsCode_NotStringLiterals()
    {
        CodeIndexStore store = BuildWpf();

        string refs = FindReferencesTool.FindReferences(store, _fs, "btnSave_Click");

        refs.Should().Contain("Sale.xaml");
        // The Click attribute counts as code; only the <!-- --> line is set aside.
        refs.Should().Contain("2 real refs");
        refs.Should().Contain("plus 1 string/comment-only");
    }

    [Fact]
    public void CheckDangling_RejectsXaml()
    {
        CodeIndexStore store = BuildWpf();

        store.CheckDanglingReferences("Sale.xaml").Should().Contain("TypeScript/TSX only");
    }
}
