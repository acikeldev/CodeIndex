using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Tests for <see cref="GetTaskContextTool"/>: high-confidence anchoring (task terms match real types → focused
/// ranking + dossier); the low-confidence gate (no match, or only stopword/short terms → plain orientation, never
/// a confident guess); and reuse of the onboarding cache (MISS generates + persists, HIT returns the persisted
/// digest).
/// </summary>
public sealed class GetTaskContextToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private CodeIndexStore Build()
    {
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\PaymentProcessor.cs",
            "namespace App;\npublic class PaymentProcessor { public void Charge(decimal amount) { } }");
        _fs.AddFile(@"C:\repo\App\Checkout.cs",
            "namespace App;\npublic class Checkout { private readonly PaymentProcessor _p = new(); }");
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    private static string OnboardingCachePath(CodeIndexStore store) => Path.Combine(store.CacheDirectory, GetOnboardingTool.CacheFileName);

    [Fact]
    public void HighConfidence_AnchorsAndBuildsFocusedContext()
    {
        CodeIndexStore store = Build();

        string output = GetTaskContextTool.GetTaskContext(store, _fs, "fix a bug in the payment processor charge flow");

        output.Should().Contain("# Task context for:");
        output.Should().Contain("Anchored on: PaymentProcessor");
        output.Should().Contain("## Task-relevant symbols");
        output.Should().Contain("## Anchor dossier: PaymentProcessor");
        output.Should().Contain("## Repo at a glance");
    }

    [Fact]
    public void LowConfidence_NoMatch_ReturnsPlainOrientation()
    {
        CodeIndexStore store = Build();

        string output = GetTaskContextTool.GetTaskContext(store, _fs, "investigate quuxbar frobnication zzzznope");

        output.Should().Contain("# Task context (low confidence)");
        output.Should().Contain("Couldn't anchor");
        output.Should().Contain("# Repo onboarding");   // the plain-orientation fallback
    }

    [Fact]
    public void LowConfidence_OnlyStopwordsAndShortTerms_ReturnsPlainOrientation()
    {
        CodeIndexStore store = Build();

        string output = GetTaskContextTool.GetTaskContext(store, _fs, "make the code do it");

        output.Should().Contain("# Task context (low confidence)");
    }

    [Fact]
    public void CacheMiss_GeneratesOnboardingBase()
    {
        CodeIndexStore store = Build();

        string output = GetTaskContextTool.GetTaskContext(store, _fs, "quuxbar zzzznope");   // low-conf -> returns onboarding

        output.Should().Contain("# Repo onboarding");
        _fs.FileExists(OnboardingCachePath(store)).Should().BeTrue();   // onboarding persisted on the miss path
    }

    [Fact]
    public void CacheHit_ReusesPersistedOnboarding()
    {
        CodeIndexStore store = Build();
        GetTaskContextTool.GetTaskContext(store, _fs, "quuxbar");   // warm the onboarding cache

        // Overwrite the onboarding cache body (keeping its signature) with a sentinel; the primer must reuse it.
        string signature = GetOnboardingTool.Signature(store);
        _fs.AddFile(OnboardingCachePath(store), signature + "\nONBOARDING-CACHE-SENTINEL");

        string output = GetTaskContextTool.GetTaskContext(store, _fs, "quuxbar");

        output.Should().Contain("ONBOARDING-CACHE-SENTINEL");   // cache HIT: reused the persisted onboarding
    }

    [Fact]
    public void ManyMatchingTypes_CapsAnchorsAtMax()
    {
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        System.Text.StringBuilder src = new();
        src.AppendLine("namespace App;");
        for (int i = 0; i < 8; i++)
        {
            src.AppendLine($"public class WidgetKind{i} {{ }}");
        }

        _fs.AddFile(@"C:\repo\App\Widgets.cs", src.ToString());
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);

        string output = GetTaskContextTool.GetTaskContext(store, _fs, "refactor the widget rendering");

        string anchorLine = output.Split('\n').First(l => l.StartsWith("Anchored on:", StringComparison.Ordinal));
        System.Text.RegularExpressions.Regex.Matches(anchorLine, "WidgetKind").Count.Should().Be(6);   // capped
    }
}
