using System.Diagnostics.CodeAnalysis;
using CodeIndex.Abstractions;
using Microsoft.Extensions.Hosting;

namespace CodeIndex.Indexing;

/// <summary>
/// Keeps the in-memory index LIVE and OWNS the initial background revalidation, so the MCP host is never blocked
/// at startup. On start it kicks one background C# refresh (revalidating the preloaded cache); then, if TS indexing
/// is enabled, it kicks the initial TS/SCSS build (which never runs on the synchronous startup path — zero added
/// TTFB); then it drains debounced change events. One FileSystemWatcher classifies each event and routes it to the
/// C# and/or TS segment. A <c>.git/HEAD</c> change (branch switch) and source edits both schedule a rebuild; the
/// timestamp delta is correct across a checkout. A buffer overflow degrades to a delta on BOTH segments. The C# pump
/// awaits its rebuild; the TS pump is fire-and-forget with an in-flight guard, so a multi-second tree-sitter parse
/// never stalls C# rebuilds. Logs to stderr (stdout is reserved for MCP JSON-RPC). The FileSystemWatcher plumbing is
/// inherently OS-bound (it watches the real disk, not the IFileSystem abstraction); only the pure event-classification
/// decisions (<see cref="ShouldSchedule"/> / <see cref="ShouldScheduleTs"/>) are unit-tested.
/// </summary>
internal sealed class RepositoryWatcher : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly ICodeIndexStore _store;
    private readonly string _repoRoot;
    private readonly bool _indexTypeScript;

    private readonly object _lock = new();
    // C# pump state.
    private bool _pending;
    private DateTime _pendingAt = DateTime.MinValue;
    // TS pump state (separate debounce; _tsFullRebuild forces a full re-resolve after a tsconfig/package/branch change).
    private bool _tsPending;
    private DateTime _tsPendingAt = DateTime.MinValue;
    private bool _tsFullRebuild;
    private bool _tsInFlight;

    public RepositoryWatcher(ICodeIndexStore store, string repoRoot, bool indexTypeScript = true)
    {
        _store = store;
        _repoRoot = repoRoot;
        _indexTypeScript = indexTypeScript;
    }

    [ExcludeFromCodeCoverage] // real FileSystemWatcher + timing loop; the classification logic is tested directly
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string gitDir = Path.Combine(_repoRoot, ".git");

        // Only a real .git DIRECTORY hosts HEAD; a worktree/submodule .git FILE points elsewhere — the source
        // watcher still catches checkout writes there, so we simply skip the HEAD watcher in that case.
        using FileSystemWatcher? headWatcher = Directory.Exists(gitDir) ? CreateHeadWatcher(gitDir) : null;
        using FileSystemWatcher sourceWatcher = CreateSourceWatcher(_repoRoot);

        Console.Error.WriteLine(headWatcher is null
            ? $"[CodeIndex] Live watch on: no .git directory at {_repoRoot} — watching source files only."
            : $"[CodeIndex] Live watch on: .git/HEAD (branch switch) + *.cs/*.csproj/*.sln/*.slnx{(_indexTypeScript ? " + *.ts/*.tsx/*.scss/tsconfig/package.json" : string.Empty)} (-> debounced delta rebuild).");

        try
        {
            await _store.RefreshAsync(_repoRoot, fullRebuild: false, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndex] Initial background refresh failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Initial TS/SCSS build — POST-SERVE and off any startup-blocking path. Fire-and-forget with the in-flight
        // guard set so a racing event-driven fire can't double-run it. fullRebuild:false takes the warm-start path.
        if (_indexTypeScript)
        {
            lock (_lock)
            {
                _tsInFlight = true;
            }

            FireTs(fullRebuild: false, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool runCs;
            lock (_lock)
            {
                runCs = _pending && DateTime.UtcNow - _pendingAt >= Debounce;
                if (runCs)
                {
                    _pending = false;
                }
            }

            if (runCs)
            {
                try
                {
                    await _store.RefreshAsync(_repoRoot, fullRebuild: false, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CodeIndex] Live delta rebuild failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (_indexTypeScript)
            {
                MaybeFireTs(stoppingToken);
            }
        }
    }

    // Fire a TS rebuild without awaiting (so the C# pump keeps servicing events) and clear the in-flight flag when
    // it finishes. The caller sets _tsInFlight = true under _lock before calling.
    [ExcludeFromCodeCoverage]
    private void FireTs(bool fullRebuild, CancellationToken ct)
    {
        _ = _store.RefreshTypeScriptAsync(_repoRoot, fullRebuild, ct).ContinueWith(t =>
        {
            lock (_lock)
            {
                _tsInFlight = false;
            }

            if (t.IsFaulted)
            {
                Exception baseEx = t.Exception!.GetBaseException();
                Console.Error.WriteLine($"[CodeIndex] TS rebuild failed: {baseEx.GetType().Name}: {baseEx.Message}");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [ExcludeFromCodeCoverage]
    private void MaybeFireTs(CancellationToken ct)
    {
        bool full;
        lock (_lock)
        {
            if (_tsInFlight || !_tsPending || DateTime.UtcNow - _tsPendingAt < Debounce)
            {
                return;
            }

            _tsPending = false;
            full = _tsFullRebuild;
            _tsFullRebuild = false;
            _tsInFlight = true;
        }

        FireTs(full, ct);
    }

    [ExcludeFromCodeCoverage]
    private FileSystemWatcher CreateHeadWatcher(string gitDir)
    {
        FileSystemWatcher w = new(gitDir, "HEAD")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        // A branch switch: C# takes the delta path (git bumps mtimes); TS forces a full re-resolve, since a checkout
        // can change tsconfig/package.json boundaries + aliases that the timestamp delta alone wouldn't reconcile.
        w.Changed += (_, _) => ScheduleBranchSwitch();
        w.Created += (_, _) => ScheduleBranchSwitch();
        return w;
    }

    [ExcludeFromCodeCoverage]
    private FileSystemWatcher CreateSourceWatcher(string repoRoot)
    {
        FileSystemWatcher w = new(repoRoot)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024, // larger buffer: a big repo can burst many events (e.g. a build)
            EnableRaisingEvents = true,
        };
        w.Changed += OnSourceChanged;
        w.Created += OnSourceChanged;
        w.Deleted += OnSourceChanged;
        w.Renamed += OnSourceChanged;
        // On buffer overflow we don't know what changed -> schedule a DELTA on BOTH segments (each scan +
        // timestamp-diff reconciles exactly what changed). A full reparse is unnecessary and the delta is correct.
        w.Error += (_, _) =>
        {
            ScheduleCs();
            if (_indexTypeScript)
            {
                ScheduleTs(fullRebuild: false);
            }
        };
        return w;
    }

    [ExcludeFromCodeCoverage]
    private void OnSourceChanged(object sender, FileSystemEventArgs e)
    {
        string? oldName = (e as RenamedEventArgs)?.OldName;
        if (ShouldSchedule(e.ChangeType, e.Name, oldName, e.FullPath))
        {
            ScheduleCs();
        }

        if (_indexTypeScript && ShouldScheduleTs(e.ChangeType, e.Name, oldName, e.FullPath, out bool full))
        {
            ScheduleTs(full);
        }
    }

    /// <summary>Pure decision: does this filesystem event warrant a C# delta rebuild? Extracted for deterministic
    /// testing. Schedules when a tracked C# extension is on EITHER side of the event (so a rename INTO or OUT OF a
    /// .cs is caught), or when a non-Changed extension-less event looks like a directory op that could add/remove
    /// many source files. Excludes bin/obj/.git/node_modules/.vs/.codeindex paths.</summary>
    internal static bool ShouldSchedule(WatcherChangeTypes changeType, string? name, string? oldName, string fullPath)
    {
        if (IsExcludedPath(fullPath))
        {
            return false;
        }

        string newName = name ?? string.Empty;
        string old = oldName ?? string.Empty;

        bool tracked = IsTrackedExtension(Path.GetExtension(newName)) || IsTrackedExtension(Path.GetExtension(old));

        bool possibleDirectoryOp = changeType != WatcherChangeTypes.Changed
            && Path.GetExtension(newName).Length == 0
            && Path.GetExtension(old).Length == 0;

        return tracked || possibleDirectoryOp;
    }

    /// <summary>Pure decision for the TS/SCSS segment (testable truth table). Tracks .ts/.tsx/.mts/.cts/.scss on
    /// either side; a tsconfig.json / package.json change sets <paramref name="forceFull"/> (boundaries + aliases
    /// changed → full re-resolve); a directory op schedules a delta. package-lock.json and dist/.next paths are
    /// ignored.</summary>
    internal static bool ShouldScheduleTs(WatcherChangeTypes changeType, string? name, string? oldName, string fullPath, out bool forceFull)
    {
        forceFull = false;
        if (IsTsExcludedPath(fullPath))
        {
            return false;
        }

        string newName = name ?? string.Empty;
        string old = oldName ?? string.Empty;

        if (IsTsProjectConfig(Path.GetFileName(newName)) || IsTsProjectConfig(Path.GetFileName(old)))
        {
            forceFull = true;
            return true;
        }

        bool possibleDirectoryOp = changeType != WatcherChangeTypes.Changed
            && Path.GetExtension(newName).Length == 0
            && Path.GetExtension(old).Length == 0;
        if (possibleDirectoryOp)
        {
            return true; // a dir add/remove/rename could add or remove many TS files -> delta
        }

        return IsTrackedTsExtension(Path.GetExtension(newName)) || IsTrackedTsExtension(Path.GetExtension(old));
    }

    private static bool IsExcludedPath(string fullPath)
    {
        string p = fullPath.Replace('\\', '/');
        return p.Contains("/.git/", StringComparison.Ordinal) || p.Contains("/bin/", StringComparison.Ordinal) ||
               p.Contains("/obj/", StringComparison.Ordinal) || p.Contains("/node_modules/", StringComparison.Ordinal) ||
               p.Contains("/.vs/", StringComparison.Ordinal) || p.Contains("/.codeindex/", StringComparison.Ordinal);
    }

    // TS excludes the C# set PLUS the TS build-output dirs (mirrors TypeScriptWorkspaceScanner's prune set). Kept
    // separate from IsExcludedPath so C# behaviour (which does index a stray .cs under dist/) is unchanged.
    private static bool IsTsExcludedPath(string fullPath)
    {
        if (IsExcludedPath(fullPath))
        {
            return true;
        }

        string p = fullPath.Replace('\\', '/');
        return p.Contains("/dist/", StringComparison.Ordinal) || p.Contains("/.next/", StringComparison.Ordinal);
    }

    private static bool IsTrackedExtension(string ext) =>
        ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrackedTsExtension(string ext) =>
        ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".cts", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".scss", StringComparison.OrdinalIgnoreCase);

    private static bool IsTsProjectConfig(string fileName) =>
        fileName.Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase);

    [ExcludeFromCodeCoverage]
    private void ScheduleCs()
    {
        lock (_lock)
        {
            _pending = true;
            _pendingAt = DateTime.UtcNow;
        }
    }

    [ExcludeFromCodeCoverage]
    private void ScheduleTs(bool fullRebuild)
    {
        lock (_lock)
        {
            _tsPending = true;
            _tsPendingAt = DateTime.UtcNow;
            if (fullRebuild)
            {
                _tsFullRebuild = true;
            }
        }
    }

    [ExcludeFromCodeCoverage]
    private void ScheduleBranchSwitch()
    {
        ScheduleCs();
        if (_indexTypeScript)
        {
            ScheduleTs(fullRebuild: true);
        }
    }
}
