using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Abstractions;

namespace CodeIndex.Benchmarks.Infrastructure;

/// <summary>
/// A fully in-memory, thread-safe <see cref="IFileSystem"/> for deterministic benchmarks. All "disk" state
/// lives in concurrent dictionaries so scanners, parsers, caches, and the store run without touching a real
/// disk — benchmarks measure pure compute, not kernel I/O. Writes and moves stamp a last-write time (so the
/// stale-cache reclaim check behaves), globs are honored in <see cref="EnumerateFiles"/>, and the backing store
/// is concurrent so the parallel indexing paths can hammer it.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<string, string> _textFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _byteFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _timestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Root the "user" cache-dir seam resolves against (see <c>CodeIndexConfig.ResolveCacheDirectory</c>),
    /// overridable so that branch is exercisable in-memory rather than against the real profile folder.
    /// </summary>
    public string UserProfileRoot { get; set; } = @"C:\Users\Test\AppData\Local";

    // ── setup helpers (not part of IFileSystem) ─────────────────────────────────

    public void AddFile(string path, string content, DateTime? timestamp = null)
    {
        _textFiles[path] = content;
        _byteFiles.TryRemove(path, out _);
        _timestamps[path] = timestamp ?? DateTime.UtcNow;
        RegisterDirectories(path);
    }

    public void AddBinaryFile(string path, byte[] bytes, DateTime? timestamp = null)
    {
        _byteFiles[path] = bytes;
        _textFiles.TryRemove(path, out _);
        _timestamps[path] = timestamp ?? DateTime.UtcNow;
        RegisterDirectories(path);
    }

    public void WriteAllText(string path, string content) => AddFile(path, content);

    /// <summary>Test seam: drive mtime deltas deterministically (branch switch / stale-cache aging).</summary>
    public void SetLastWriteTimeUtc(string path, DateTime timestamp) => _timestamps[path] = timestamp;

    // ── IFileSystem: original 8 ─────────────────────────────────────────────────

    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
    {
        Regex glob = GlobToRegex(pattern);
        foreach (string file in AllPaths())
        {
            if (IsUnder(file, path, searchOption) && glob.IsMatch(Path.GetFileName(file)))
            {
                yield return file;
            }
        }
    }

    public bool FileExists(string path) => _textFiles.ContainsKey(path) || _byteFiles.ContainsKey(path);

    public bool DirectoryExists(string path) => _directories.ContainsKey(path);

    public string ReadAllText(string path)
    {
        if (_textFiles.TryGetValue(path, out string? text))
        {
            return text;
        }

        if (_byteFiles.TryGetValue(path, out byte[]? bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        throw new FileNotFoundException("File not found in the in-memory file system.", path);
    }

    public DateTime GetLastWriteTimeUtc(string path) =>
        _timestamps.TryGetValue(path, out DateTime ts) ? ts : DateTime.MinValue;

    public byte[] ReadAllBytes(string path)
    {
        if (_byteFiles.TryGetValue(path, out byte[]? bytes))
        {
            return bytes;
        }

        if (_textFiles.TryGetValue(path, out string? text))
        {
            return Encoding.UTF8.GetBytes(text);
        }

        throw new FileNotFoundException("File not found in the in-memory file system.", path);
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        _byteFiles[path] = bytes;
        _textFiles.TryRemove(path, out _);
        _timestamps[path] = DateTime.UtcNow;
        RegisterDirectories(path);
    }

    public void EnsureDirectoryExists(string path) => RegisterDirectoryChain(path);

    // ── IFileSystem: added 4 ────────────────────────────────────────────────────

    public IEnumerable<FileSystemEntry> EnumerateDirectoryEntries(string directory)
    {
        foreach (string file in AllPaths())
        {
            if (string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase))
            {
                _timestamps.TryGetValue(file, out DateTime ts);
                yield return new FileSystemEntry(file, Path.GetFileName(file), false, false, ts.Ticks);
            }
        }

        foreach (string dir in _directories.Keys)
        {
            if (string.Equals(Path.GetDirectoryName(dir), directory, StringComparison.OrdinalIgnoreCase))
            {
                yield return new FileSystemEntry(dir, Path.GetFileName(dir), true, false, 0L);
            }
        }
    }

    public string[] ReadAllLines(string path)
    {
        List<string> lines = new();
        using StringReader reader = new(ReadAllText(path));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    public void DeleteFile(string path)
    {
        _textFiles.TryRemove(path, out _);
        _byteFiles.TryRemove(path, out _);
        _timestamps.TryRemove(path, out _);
    }

    public void MoveFile(string sourcePath, string destPath, bool overwrite)
    {
        if (!FileExists(sourcePath))
        {
            throw new FileNotFoundException("Source file not found in the in-memory file system.", sourcePath);
        }

        if (FileExists(destPath) && !overwrite)
        {
            throw new IOException($"Destination already exists: {destPath}");
        }

        if (_byteFiles.TryRemove(sourcePath, out byte[]? bytes))
        {
            _byteFiles[destPath] = bytes;
            _textFiles.TryRemove(destPath, out _);
        }

        if (_textFiles.TryRemove(sourcePath, out string? text))
        {
            _textFiles[destPath] = text;
            _byteFiles.TryRemove(destPath, out _);
        }

        _timestamps.TryRemove(sourcePath, out _);
        _timestamps[destPath] = DateTime.UtcNow;
        RegisterDirectories(destPath);
    }

    // ── private ─────────────────────────────────────────────────────────────────

    private IEnumerable<string> AllPaths() =>
        _textFiles.Keys.Concat(_byteFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsUnder(string file, string root, SearchOption searchOption)
    {
        string? dir = Path.GetDirectoryName(file);
        if (dir is null)
        {
            return false;
        }

        if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return searchOption == SearchOption.AllDirectories &&
               dir.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static Regex GlobToRegex(string pattern)
    {
        string escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private void RegisterDirectories(string filePath) => RegisterDirectoryChain(Path.GetDirectoryName(filePath));

    private void RegisterDirectoryChain(string? directory)
    {
        while (!string.IsNullOrEmpty(directory))
        {
            _directories.TryAdd(directory, 0);
            directory = Path.GetDirectoryName(directory);
        }
    }
}
