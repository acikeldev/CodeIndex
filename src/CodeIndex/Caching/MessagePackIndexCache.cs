using System.Diagnostics.CodeAnalysis;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using MessagePack;
using MessagePack.Resolvers;

namespace CodeIndex.Caching;

/// <summary>
/// Persists the project index as a MessagePack binary file under
/// <c>&lt;repoRoot&gt;/.codeindex/cache.bin</c>.
/// Uses <see cref="ContractlessStandardResolver"/> so no attributes are needed on model types.
/// </summary>
public sealed class MessagePackIndexCache : ICodeIndexCache
{
    internal const string CacheDirectory = ".codeindex";
    internal const string CacheFileName = "cache.bin";

    private static readonly MessagePackSerializerOptions Options =
        ContractlessStandardResolver.Options.WithCompression(MessagePackCompression.Lz4BlockArray);

    private readonly string _cachePath;
    private readonly IFileSystem _fileSystem;

    public MessagePackIndexCache(string repoRoot, IFileSystem fileSystem)
    {
        _cachePath = Path.Combine(repoRoot, CacheDirectory, CacheFileName);
        _fileSystem = fileSystem;
    }

    /// <inheritdoc/>
    public void Save(IReadOnlyList<ProjectIndex> projects)
    {
        try
        {
            string cacheDir = Path.GetDirectoryName(_cachePath)!;
            _fileSystem.EnsureDirectoryExists(cacheDir);

            byte[] bytes = MessagePackSerializer.Serialize(projects, Options);
            _fileSystem.WriteAllBytes(_cachePath, bytes);
        }
        catch (Exception)
        {
            // Cache write failures are non-fatal: the index is still in memory.
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProjectIndex>? TryLoad()
    {
        if (!_fileSystem.FileExists(_cachePath))
        {
            return null;
        }

        try
        {
            byte[] bytes = _fileSystem.ReadAllBytes(_cachePath);
            return MessagePackSerializer.Deserialize<List<ProjectIndex>>(bytes, Options);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
