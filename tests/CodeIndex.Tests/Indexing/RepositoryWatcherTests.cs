using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using CodeIndex.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeIndex.Tests.Indexing;

/// <summary>
/// Integration tests for <see cref="RepositoryWatcher"/>.
///
/// These tests use a real temporary directory and real FileSystemWatcher so they
/// exercise the actual OS notification path. Each test gets an isolated temp dir
/// that is deleted on teardown.
///
/// Timing: FileSystemWatcher events are OS-delivered asynchronously. Tests wait
/// up to 3 seconds for the expected call to arrive before failing.
/// </summary>
public sealed class RepositoryWatcherTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _gitDir;

    public RepositoryWatcherTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"codeindex-test-{Guid.NewGuid():N}");
        _gitDir = Path.Combine(_repoRoot, ".git");
        Directory.CreateDirectory(_gitDir);
        // Minimal HEAD so the watcher can start.
        File.WriteAllText(Path.Combine(_gitDir, "HEAD"), "ref: refs/heads/main");
    }

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ICodeIndexStore MakeStore(
        Action<string, bool, CancellationToken>? onRebuild = null)
    {
        ICodeIndexStore store = Substitute.For<ICodeIndexStore>();
        store.When(s => s.Rebuild(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()))
             .Do(ci => onRebuild?.Invoke(
                 ci.ArgAt<string>(0),
                 ci.ArgAt<bool>(1),
                 ci.ArgAt<CancellationToken>(2)));
        return store;
    }

    private RepositoryWatcher MakeWatcher(ICodeIndexStore store) =>
        new(store, _repoRoot, NullLogger<RepositoryWatcher>.Instance);

    private static async Task WaitForCallAsync(
        ICodeIndexStore store,
        bool? expectFull = null,
        int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var calls = store.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ICodeIndexStore.Rebuild))
                .ToList();

            if (expectFull is null && calls.Count > 0)
                return;

            if (expectFull is bool full &&
                calls.Any(c => (bool)c.GetArguments()[1]! == full))
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Timed out waiting for Rebuild(fullRebuild={expectFull?.ToString() ?? "any"}) to be called.");
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CsFileCreated_TriggersDeltaRebuild()
    {
        ICodeIndexStore store = MakeStore();
        using RepositoryWatcher watcher = MakeWatcher(store);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        _ = watcher.StartAsync(cts.Token);
        await Task.Delay(200); // allow FileSystemWatcher to initialise

        File.WriteAllText(Path.Combine(_repoRoot, "Foo.cs"), "class Foo {}");

        await WaitForCallAsync(store, expectFull: false);

        store.Received().Rebuild(_repoRoot, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CsprojFileChanged_TriggersDeltaRebuild()
    {
        string projPath = Path.Combine(_repoRoot, "App.csproj");
        File.WriteAllText(projPath, "<Project/>");

        ICodeIndexStore store = MakeStore();
        using RepositoryWatcher watcher = MakeWatcher(store);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        _ = watcher.StartAsync(cts.Token);
        await Task.Delay(200); // allow FileSystemWatcher to initialise

        // Modify an existing .csproj
        File.WriteAllText(projPath, "<Project Sdk=\"Microsoft.NET.Sdk\"/>");

        await WaitForCallAsync(store, expectFull: false);

        store.Received().Rebuild(_repoRoot, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GitHeadChanged_TriggersFullRebuild()
    {
        ICodeIndexStore store = MakeStore();
        using RepositoryWatcher watcher = MakeWatcher(store);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        _ = watcher.StartAsync(cts.Token);
        await Task.Delay(200); // allow FileSystemWatcher to initialise

        // Simulate a branch switch by rewriting HEAD.
        File.WriteAllText(Path.Combine(_gitDir, "HEAD"), "ref: refs/heads/feature/new-branch");

        await WaitForCallAsync(store, expectFull: true);

        store.Received().Rebuild(_repoRoot, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTrackedExtension_DoesNotTriggerRebuild()
    {
        ICodeIndexStore store = MakeStore();
        using RepositoryWatcher watcher = MakeWatcher(store);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        _ = watcher.StartAsync(cts.Token);

        // .txt is not a tracked extension — should not trigger a rebuild.
        File.WriteAllText(Path.Combine(_repoRoot, "notes.txt"), "hello");

        // Wait briefly to confirm no rebuild fires.
        await Task.Delay(800);

        store.DidNotReceive().Rebuild(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileInBinDirectory_DoesNotTriggerRebuild()
    {
        string binDir = Path.Combine(_repoRoot, "App", "bin", "Debug");
        Directory.CreateDirectory(binDir);

        ICodeIndexStore store = MakeStore();
        using RepositoryWatcher watcher = MakeWatcher(store);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        _ = watcher.StartAsync(cts.Token);

        File.WriteAllText(Path.Combine(binDir, "App.cs"), "// generated");

        await Task.Delay(800);

        store.DidNotReceive().Rebuild(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_CancelsInFlightRebuild()
    {
        CancellationToken capturedToken = default;
        TaskCompletionSource rebuildStarted = new();

        ICodeIndexStore store = MakeStore((_, _, ct) =>
        {
            capturedToken = ct;
            rebuildStarted.TrySetResult();
            // Block until the token is cancelled (simulates a long parse).
            // WaitHandle.WaitOne unblocks as soon as the token fires.
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        });

        using RepositoryWatcher watcher = MakeWatcher(store);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        _ = watcher.StartAsync(cts.Token);
        await Task.Delay(200); // allow FileSystemWatcher to initialise

        File.WriteAllText(Path.Combine(_repoRoot, "Foo.cs"), "class Foo {}");

        // Wait until Rebuild() has actually started (i.e. debounce passed and loop fired).
        await rebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // Stop the watcher — this cancels the stoppingToken, which cancels the rebuild token.
        await watcher.StopAsync(CancellationToken.None);

        capturedToken.IsCancellationRequested.Should().BeTrue();
    }
}
