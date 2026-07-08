using CodeIndex.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeIndex.Indexing;

/// <summary>
/// Background service that watches the repository for changes and triggers index rebuilds.
///
/// Two kinds of triggers:
///   1. <c>.git/HEAD</c> changed → branch switch detected → full rebuild (timestamps unreliable).
///   2. <c>*.cs / *.csproj / *.sln / *.slnx</c> changed → delta rebuild (only changed files re-parsed).
///
/// Rebuilds are serialized: if a new trigger fires while a rebuild is in progress the
/// in-progress rebuild is cancelled and a fresh one starts after the debounce window.
/// </summary>
public sealed class RepositoryWatcher : BackgroundService
{
    private static readonly TimeSpan DeltaDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FullDebounce = TimeSpan.FromMilliseconds(100);

    private readonly ICodeIndexStore _store;
    private readonly string _repoRoot;
    private readonly ILogger<RepositoryWatcher> _logger;

    // Pending rebuild state — touched only inside the _lock.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _pendingFull;
    private bool _pendingDelta;
    private DateTime _pendingAt = DateTime.MinValue;

    public RepositoryWatcher(
        ICodeIndexStore store,
        string repoRoot,
        ILogger<RepositoryWatcher> logger)
    {
        _store = store;
        _repoRoot = repoRoot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string gitHeadPath = Path.Combine(_repoRoot, ".git", "HEAD");
        string? gitDir = Path.GetDirectoryName(gitHeadPath);

        using FileSystemWatcher? headWatcher = Directory.Exists(gitDir)
            ? CreateHeadWatcher(gitDir)
            : null;

        using FileSystemWatcher sourceWatcher = CreateSourceWatcher(_repoRoot);

        if (headWatcher is null)
        {
            _logger.LogWarning("No .git directory found at {RepoRoot} — branch-switch detection disabled.", _repoRoot);
        }

        // Drain the trigger queue until the host shuts down.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(50, stoppingToken).ConfigureAwait(false);

            bool runFull;
            bool runDelta;

            await _lock.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                if (!_pendingFull && !_pendingDelta)
                {
                    continue;
                }

                // Respect the debounce window.
                TimeSpan debounce = _pendingFull ? FullDebounce : DeltaDebounce;
                if (DateTime.UtcNow - _pendingAt < debounce)
                {
                    continue;
                }

                runFull = _pendingFull;
                runDelta = _pendingDelta;
                _pendingFull = false;
                _pendingDelta = false;
            }
            finally
            {
                _lock.Release();
            }

            bool isFull = runFull;
            _logger.LogInformation(
                isFull ? "Branch switch detected — starting full rebuild." : "Source files changed — starting delta rebuild.");

            try
            {
                _store.Rebuild(_repoRoot, fullRebuild: isFull, cancellationToken: stoppingToken);
                _logger.LogInformation("{Kind} rebuild completed.", isFull ? "Full" : "Delta");
            }
            catch (OperationCanceledException)
            {
                // Host is shutting down — exit cleanly.
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Kind} rebuild failed.", isFull ? "Full" : "Delta");
            }
        }
    }

    private FileSystemWatcher CreateHeadWatcher(string gitDir)
    {
        FileSystemWatcher w = new(gitDir, "HEAD")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        w.Changed += (_, _) => Schedule(full: true);
        w.Created += (_, _) => Schedule(full: true);
        return w;
    }

    private FileSystemWatcher CreateSourceWatcher(string repoRoot)
    {
        FileSystemWatcher w = new(repoRoot)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };

        w.Changed += OnSourceChanged;
        w.Created += OnSourceChanged;
        w.Deleted += OnSourceChanged;
        w.Renamed += OnSourceChanged;
        return w;
    }

    private void OnSourceChanged(object sender, FileSystemEventArgs e)
    {
        string ext = Path.GetExtension(e.Name ?? string.Empty);
        if (!IsTrackedExtension(ext))
        {
            return;
        }

        // Ignore anything inside .git or bin/obj directories.
        string fullPath = e.FullPath;
        if (fullPath.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Schedule(full: false);
    }

    private static bool IsTrackedExtension(string ext) =>
        ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    private void Schedule(bool full)
    {
        _lock.Wait();
        try
        {
            if (full)
            {
                _pendingFull = true;
            }
            else if (!_pendingFull)
            {
                _pendingDelta = true;
            }

            _pendingAt = DateTime.UtcNow;
        }
        finally
        {
            _lock.Release();
        }
    }
}
