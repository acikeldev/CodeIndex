using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// repo_map analyzer: PageRank over the symbol-mention graph ranks referenced symbols above unreferenced ones,
/// excludes generated files, honors the token budget + project filter, is deterministic, personalizes on focus,
/// and emits a graceful message for an empty index. These are direct tests of <see cref="RepoMap"/> — the
/// company's store/tool-level tests land with the store wave.
/// </summary>
public class RepoMapTests
{
    private const string ProjectName = "Proj";
    private static readonly string ProjDir = @"C:\repo\Proj";

    // CoreService is referenced by three consumers → high in-degree → high PageRank. LonelyWidget is referenced
    // by nobody. GeneratedThing lives in a generated file and must be excluded entirely.
    private static (InMemoryFileSystem Fs, List<SourceFileIndex> Files) BuildFixture()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = new();

        void Add(string name, string content)
        {
            string path = Path.Combine(ProjDir, name);
            fs.AddFile(path, content);
            SourceFileIndex? parsed = new SourceFileParser(fs).Parse(path, ProjectName);
            parsed.Should().NotBeNull($"fixture file {name} should parse");
            files.Add(parsed!);
        }

        Add("Core.cs", "namespace N;\npublic interface ICoreService { }\npublic class CoreService { public void Execute() { } }");
        Add("ConsumerA.cs", "namespace N;\npublic class ConsumerA { private readonly CoreService _svc = new CoreService(); public void RunA() { _svc.Execute(); } }");
        Add("ConsumerB.cs", "namespace N;\npublic class ConsumerB { private readonly CoreService _svc = new CoreService(); public void RunB() { _svc.Execute(); } }");
        Add("ConsumerC.cs", "namespace N;\npublic class ConsumerC { private readonly CoreService _svc = new CoreService(); public void RunC() { _svc.Execute(); } }");
        Add("Lonely.cs", "namespace N;\npublic class LonelyWidget { public void Idle() { } }");
        Add("Gen.g.cs", "namespace N;\npublic class GeneratedThing { public void Gen() { } }");

        return (fs, files);
    }

    private static string RenderMap(IReadOnlyList<string> focus, int tokenBudget, string? project)
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();
        RepoMap map = RepoMap.Build(files, fs);
        List<RepoMap.RankedSymbol> ranked = map.Rank(focus, project);
        return RepoMap.Render(ranked, tokenBudget);
    }

    [Fact]
    public void RanksReferencedSymbolAboveUnreferenced()
    {
        string map = RenderMap([], tokenBudget: 4000, project: null);

        map.Should().Contain("CoreService");
        map.Should().Contain("LonelyWidget");
        map.IndexOf("CoreService", StringComparison.Ordinal)
            .Should().BeLessThan(map.IndexOf("LonelyWidget", StringComparison.Ordinal),
                "the heavily-referenced CoreService should rank above the unreferenced LonelyWidget");
    }

    [Fact]
    public void ExcludesGeneratedFiles()
    {
        string map = RenderMap([], tokenBudget: 8000, project: null);
        map.Should().NotContain("GeneratedThing");
    }

    [Fact]
    public void HonorsTokenBudget_Truncates()
    {
        string small = RenderMap([], tokenBudget: 60, project: null);
        string large = RenderMap([], tokenBudget: 8000, project: null);

        small.Length.Should().BeLessThan(large.Length, "a tiny budget must produce less output");
        small.Should().Contain("showing top"); // truncation footer
        small.Should().Contain("CoreService"); // highest-ranked still shown first
    }

    [Fact]
    public void Deterministic_SameInputSameOutput()
    {
        RenderMap([], 4000, null).Should().Be(RenderMap([], 4000, null));
    }

    [Fact]
    public void ProjectFilter_LimitsOutput()
    {
        RenderMap([], tokenBudget: 4000, project: ProjectName).Should().Contain("CoreService");

        // A bogus project yields no symbols → graceful empty message.
        string none = RenderMap([], tokenBudget: 4000, project: "NoSuchProject");
        none.Should().NotContain("CoreService");
    }

    [Fact]
    public void Focus_PersonalizesRanking()
    {
        string global = RenderMap([], tokenBudget: 8000, project: null);
        string focused = RenderMap(["LonelyWidget"], tokenBudget: 8000, project: null);

        // Focusing on LonelyWidget must not lower its rank vs. the global map (personalized teleport favours it).
        int globalPos = global.IndexOf("LonelyWidget", StringComparison.Ordinal);
        int focusedPos = focused.IndexOf("LonelyWidget", StringComparison.Ordinal);
        focusedPos.Should().BeLessThanOrEqualTo(globalPos, "focusing on LonelyWidget should not push it down the ranking");
    }

    [Fact]
    public void EmptyIndex_GracefulMessage()
    {
        InMemoryFileSystem fs = new();
        RepoMap map = RepoMap.Build([], fs);
        string rendered = RepoMap.Render(map.Rank([], null), 2000);
        rendered.Should().Contain("no hand-written symbols");
    }
}
