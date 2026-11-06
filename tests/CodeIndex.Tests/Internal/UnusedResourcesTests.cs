using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// search_structural unused-resx / unused-xaml-key: resource keys referenced nowhere in C#/XAML. Uses count from
/// any non-generated C#/XAML file (typed accessor, string, markup extension, Dynamic/StaticResource); the
/// generated Designer.cs and an x:Key's own definitions do not; runtime-built keys are listed apart.
/// </summary>
public sealed class UnusedResourcesTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private static string Resx(params string[] keys) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n"
        + string.Concat(keys.Select(k => $"  <data name=\"{k}\" xml:space=\"preserve\"><value>{k}</value></data>\n"))
        + "</root>\n";

    private CodeIndexStore Build()
    {
        _fs.AddFile(Root + @"\App.slnx", "<Solution>\n  <Project Path=\"Ui/Ui.csproj\" />\n</Solution>\n");
        _fs.AddFile(Root + @"\Ui\Ui.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");

        _fs.AddFile(Root + @"\Ui\Strings.resx", Resx("Title", "Header", "Raw", "Dead", "Perm_Edit", "$this.Text"));
        _fs.AddFile(Root + @"\Ui\Strings.en.resx", Resx("Title", "OnlyInEnglish"));
        // Generated accessor mentions every key — must not count as a use.
        _fs.AddFile(Root + @"\Ui\Strings.Designer.cs",
            "namespace App;\ninternal class Strings { internal static string Title => \"\"; internal static string Dead => \"\"; internal static string Header => \"\"; }\n");
        // WinForms form resx: designer-applied keys, never referenced by name.
        _fs.AddFile(Root + @"\Ui\Form1.resx", Resx("FormOnlyKey"));
        _fs.AddFile(Root + @"\Ui\Form1.cs", "namespace App;\npublic class Form1 { }\n");

        _fs.AddFile(Root + @"\Ui\Use.cs", """
            namespace App;
            class Use
            {
                string A() => Strings.Title;
                string B() => Lookup("Raw");
                string C(string id) => Lookup("Perm_" + id);
                string Lookup(string k) => k;
            }
            """);

        const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
        _fs.AddFile(Root + @"\Ui\Themes\Light.xaml", $"""
            <ResourceDictionary {Ns}>
                <Color x:Key="Base">#FFF</Color>
                <SolidColorBrush x:Key="Accent" Color="{"{"}StaticResource Base{"}"}"/>
                <SolidColorBrush x:Key="Unused" Color="Red"/>
            </ResourceDictionary>
            """);
        _fs.AddFile(Root + @"\Ui\Themes\Dark.xaml", $"""
            <ResourceDictionary {Ns}>
                <Color x:Key="Base">#000</Color>
                <SolidColorBrush x:Key="Accent" Color="{"{"}StaticResource Base{"}"}"/>
                <SolidColorBrush x:Key="Unused" Color="Blue"/>
            </ResourceDictionary>
            """);
        _fs.AddFile(Root + @"\Ui\Main.xaml", $"""
            <Window x:Class="App.Main" {Ns} xmlns:local="clr-namespace:App">
                <TextBlock Text="{"{"}local:Tr Header{"}"}" Foreground="{"{"}DynamicResource Accent{"}"}"/>
            </Window>
            """);

        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Resx_ReportsOnlyUnreferencedKeys_SkippingCultureFormAndDesignerNoise()
    {
        CodeIndexStore store = Build();

        string res = StructuralSearchTool.SearchStructural(store, "unused-resx", max: 50, perFileMax: 50);

        res.Should().Contain("unused-resx: 1 unused of 5 distinct .resx keys in 1 files (+1 possibly built at runtime");
        res.Should().Contain("| Dead");
        res.Should().Contain("Perm_Edit  ← \"Perm_…\" in Use.cs");
        res.Should().NotContain("| Title").And.NotContain("| Header").And.NotContain("| Raw");
        res.Should().NotContain("OnlyInEnglish").And.NotContain("FormOnlyKey").And.NotContain("$this");
    }

    [Fact]
    public void XamlKey_DefinitionsAreNotUses_ButReferencesFromOtherKeysAre()
    {
        CodeIndexStore store = Build();

        string res = StructuralSearchTool.SearchStructural(store, "unused-xaml-key", max: 50, perFileMax: 50);

        res.Should().Contain("1 unused of 3 distinct XAML x:Key keys in 2 files");
        res.Should().Contain("Light.xaml — 1 unused");
        res.Should().Contain("Dark.xaml — 1 unused");
        res.Should().Contain("| Unused");
        res.Should().NotContain("| Accent").And.NotContain("| Base");
    }

    [Fact]
    public void ProjectScope_AndNoKeys_Message()
    {
        CodeIndexStore store = Build();

        StructuralSearchTool.SearchStructural(store, "unused-resx", project: "Nope").Should().Contain("no .resx keys found in project 'Nope'");
    }

    [Fact]
    public void Truncation_PerFileAndOverall()
    {
        CodeIndexStore store = Build();

        StructuralSearchTool.SearchStructural(store, "unused-xaml-key", max: 50, perFileMax: 0).Should().Contain("1 more in this file");
    }

    [Fact]
    public void UnknownPattern_ListsResourcePatterns()
    {
        CodeIndexStore store = Build();

        string res = StructuralSearchTool.SearchStructural(store, "nope");

        res.Should().Contain("Resource patterns");
        res.Should().Contain("unused-resx");
        res.Should().Contain("unused-xaml-key");
    }

    [Theory]
    [InlineData("Perm_Edit2", new[] { "Perm_Edit", "Perm_" })]
    [InlineData("A_B_C", new[] { "A_B_", "A_" })]
    [InlineData("Plain", new string[0])]
    public void Prefixes_AreRuntimeBuildCandidates(string key, string[] expected)
    {
        UnusedResources.Prefixes(key).Should().Equal(expected);
    }

    [Theory]
    [InlineData(@"C:\r\Strings.resx", false)]
    [InlineData(@"C:\r\Strings.en.resx", true)]
    [InlineData(@"C:\r\Strings.pt-BR.resx", true)]
    [InlineData(@"C:\r\My.Strings.resx", false)]
    public void IsCultureSpecific(string path, bool expected)
    {
        UnusedResources.IsCultureSpecific(path).Should().Be(expected);
    }
}
