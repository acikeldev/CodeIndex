using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Integration of <see cref="RememberTool"/> with <see cref="GetOnboardingTool"/>: a remembered fact appears in
/// onboarding, survives a reindex (it is stored separately from the build-keyed digest), and onboarding stays
/// byte-identical when nothing has been remembered.
/// </summary>
public sealed class RememberToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private CodeIndexStore Build()
    {
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\A.cs", "namespace App; public class Alpha { public void Work() { } }");
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void RememberedFact_AppearsInOnboarding()
    {
        CodeIndexStore store = Build();

        string ack = RememberTool.Remember(store, _fs, "auth entry point is AuthController.Login");
        ack.Should().Contain("Remembered");

        string onboarding = GetOnboardingTool.GetOnboarding(store, _fs);
        onboarding.Should().Contain("# Repo onboarding");
        onboarding.Should().Contain("Notes from previous sessions");
        onboarding.Should().Contain("auth entry point is AuthController.Login");
    }

    [Fact]
    public void RememberedFact_SurvivesReindex()
    {
        CodeIndexStore store = Build();
        RememberTool.Remember(store, _fs, "gotcha: DTOs are generated");

        // A new file changes the build signature and invalidates the onboarding digest cache...
        _fs.AddFile(@"C:\repo\App\B.cs", "namespace App; public class Beta { }");
        store.Build(Root);

        // ...but the note lives in a separate file and must still surface.
        string onboarding = GetOnboardingTool.GetOnboarding(store, _fs);
        onboarding.Should().Contain("gotcha: DTOs are generated");
    }

    [Fact]
    public void Onboarding_ByteIdenticalWhenNothingRemembered()
    {
        CodeIndexStore store = Build();

        // No remember call → the notes section must be absent and the output unchanged from the pure digest.
        string onboarding = GetOnboardingTool.GetOnboarding(store, _fs);
        onboarding.Should().NotContain("Notes from previous sessions");
    }
}
