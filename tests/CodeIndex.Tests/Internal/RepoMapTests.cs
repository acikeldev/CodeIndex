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
    public void SqrtDamping_RepetitionHelpsButSublinearly()
    {
        // Several referrer files each name TargetX once and TargetY nine times. Because both targets compete for
        // the SAME referrers' out-flow, their rank ratio reflects the edge-share ratio: sqrt(9):sqrt(1) = 3:1 under
        // sqrt damping, but 9:1 under linear weighting. So a ratio comfortably below the linear 9x — while still
        // above 1 (repetition does help) — pins the sqrt behaviour.
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = new();

        void Add(string name, string content)
        {
            string path = Path.Combine(ProjDir, name);
            fs.AddFile(path, content);
            SourceFileIndex? parsed = new SourceFileParser(fs).Parse(path, ProjectName);
            parsed.Should().NotBeNull();
            files.Add(parsed!);
        }

        Add("TargetX.cs", "namespace N;\npublic class TargetX { }");
        Add("TargetY.cs", "namespace N;\npublic class TargetY { }");
        for (int i = 0; i < 4; i++)
        {
            System.Text.StringBuilder body = new();
            body.Append($"namespace N;\npublic class Referrer{i} {{\n");
            body.Append("    private TargetX _x;\n");
            for (int j = 0; j < 9; j++)
            {
                body.Append($"    private TargetY _y{j};\n");
            }

            body.Append("}");
            Add($"Referrer{i}.cs", body.ToString());
        }

        RepoMap map = RepoMap.Build(files, fs);
        List<RepoMap.RankedSymbol> ranked = map.Rank([], null);
        double scoreX = ranked.First(r => r.Signature.Contains("TargetX", StringComparison.Ordinal)).Score;
        double scoreY = ranked.First(r => r.Signature.Contains("TargetY", StringComparison.Ordinal)).Score;

        double ratio = scoreY / scoreX;
        ratio.Should().BeGreaterThan(1.5, "9x repetition must still rank TargetY above TargetX");
        ratio.Should().BeLessThan(6.0, $"sqrt damping keeps the ratio near 3:1, well below the linear 9:1 (was {ratio:F2})");
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
