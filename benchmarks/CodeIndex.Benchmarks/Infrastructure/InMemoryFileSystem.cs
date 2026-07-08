using CodeIndex.Abstractions;

namespace CodeIndex.Benchmarks.Infrastructure;

/// <summary>
/// A fully in-memory IFileSystem implementation. All "disk" state lives in
/// dictionaries so benchmarks measure pure compute, not kernel I/O.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _textFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _byteFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _timestamps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Write helpers (used in GlobalSetup) ──────────────────────────────────

    public void AddFile(string path, string content, DateTime? timestamp = null)
    {
        _textFiles[path] = content;
        _timestamps[path] = timestamp ?? DateTime.UtcNow;
        RegisterDirectories(path);
    }

    public void AddBinaryFile(string path, byte[] bytes)
    {
        _byteFiles[path] = bytes;
        RegisterDirectories(path);
    }

    // ── IFileSystem ───────────────────────────────────────────────────────────

    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
    {
        string ext = Path.GetExtension(pattern).TrimStart('*');
        IEnumerable<string> candidates = searchOption == SearchOption.AllDirectories
            ? _textFiles.Keys.Concat(_byteFiles.Keys)
            : _textFiles.Keys.Concat(_byteFiles.Keys)
                .Where(f => string.Equals(
                    Path.GetDirectoryName(f), path, StringComparison.OrdinalIgnoreCase));

        return candidates
            .Where(f => f.StartsWith(path, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(ext) || f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public bool FileExists(string path) =>
        _textFiles.ContainsKey(path) || _byteFiles.ContainsKey(path);

    public bool DirectoryExists(string path) =>
        _directories.Contains(path);

    public string ReadAllText(string path) =>
        _textFiles.TryGetValue(path, out string? text)
            ? text
            : throw new FileNotFoundException(path);

    public DateTime GetLastWriteTimeUtc(string path) =>
        _timestamps.TryGetValue(path, out DateTime ts) ? ts : DateTime.MinValue;

    public byte[] ReadAllBytes(string path) =>
        _byteFiles.TryGetValue(path, out byte[]? bytes)
            ? bytes
            : throw new FileNotFoundException(path);

    public void WriteAllBytes(string path, byte[] bytes)
    {
        _byteFiles[path] = bytes;
        RegisterDirectories(path);
    }

    public void EnsureDirectoryExists(string path) =>
        _directories.Add(path);

    // ── private ───────────────────────────────────────────────────────────────

    private void RegisterDirectories(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            _directories.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }
}
