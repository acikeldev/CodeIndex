using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Tests for <see cref="GetOnboardingTool"/>: the digest content, the cache-MISS path (generate + persist), the
/// cache-HIT path (return the persisted digest without regenerating), build-signature invalidation, and a
/// malformed cache falling back to regenerate.
/// </summary>
public sealed class GetOnboardingToolTests
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

    private static string CachePath(CodeIndexStore store) => Path.Combine(store.CacheDirectory, GetOnboardingTool.CacheFileName);

    [Fact]
    public void CacheMiss_GeneratesAndPersists()
    {
        CodeIndexStore store = Build();

        string output = GetOnboardingTool.GetOnboarding(store, _fs);

        output.Should().Contain("# Repo onboarding");
        output.Should().Contain("## Projects");
        output.Should().Contain("## Most important symbols");
        output.Should().Contain("App");
        _fs.FileExists(CachePath(store)).Should().BeTrue();   // persisted for next time
    }

    [Fact]
    public void CacheHit_ReturnsPersistedDigestWithoutRegenerating()
    {
        CodeIndexStore store = Build();
        GetOnboardingTool.GetOnboarding(store, _fs);   // warm the cache

        // Overwrite the cached body (keeping the matching signature) with a sentinel; a HIT must return it verbatim.
        string signature = GetOnboardingTool.Signature(store);
        _fs.AddFile(CachePath(store), signature + "\nSENTINEL-CACHED-BODY");

        string output = GetOnboardingTool.GetOnboarding(store, _fs);

        output.Should().Be("SENTINEL-CACHED-BODY");
    }

    [Fact]
    public void SignatureChange_InvalidatesCacheAndRegenerates()
    {
        CodeIndexStore store = Build();
        GetOnboardingTool.GetOnboarding(store, _fs);
        string oldSignature = GetOnboardingTool.Signature(store);
        _fs.AddFile(CachePath(store), oldSignature + "\nSTALE-SENTINEL");

        // A new file changes the build signature, so the cached digest no longer matches.
        _fs.AddFile(@"C:\repo\App\B.cs", "namespace App; public class Beta { }");
        store.Build(Root);

        string output = GetOnboardingTool.GetOnboarding(store, _fs);

        output.Should().NotContain("STALE-SENTINEL");   // miss on the new signature -> regenerated
        output.Should().Contain("# Repo onboarding");
    }

    [Fact]
    public void MalformedCache_FallsBackToRegenerate()
    {
        CodeIndexStore store = Build();
        _fs.AddFile(CachePath(store), "no-newline-so-no-signature-line");

        string output = GetOnboardingTool.GetOnboarding(store, _fs);

        output.Should().Contain("# Repo onboarding");
    }

    [Fact]
    public void CacheIoFailure_IsSwallowedAndDigestStillReturned()
    {
        CodeIndexStore store = Build();   // real index over _fs; only the CACHE filesystem throws
        IFileSystem throwing = Substitute.For<IFileSystem>();
        throwing.FileExists(Arg.Any<string>()).Returns(true);
        throwing.ReadAllText(Arg.Any<string>()).Returns(_ => throw new IOException("read boom"));
        throwing.When(f => f.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>())).Do(_ => throw new IOException("write boom"));

        string output = GetOnboardingTool.GetOnboarding(store, throwing);

        output.Should().Contain("# Repo onboarding");   // both the load and save failures are swallowed
    }
}
