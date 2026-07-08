using CodeIndex.Models;

namespace CodeIndex.Abstractions;

/// <summary>
/// Read/write interface for the in-memory code index.
/// </summary>
public interface ICodeIndexStore
{
    /// <summary>
    /// Rebuilds the index for all projects discovered under <paramref name="repoRoot"/>.
    /// Only files whose <see cref="DateTime"/> on disk differs from the cached value are re-parsed.
    /// Pass <paramref name="fullRebuild"/> = <c>true</c> to discard the cache and re-parse every file
    /// (required after a branch switch where timestamps are unreliable).
    /// </summary>
    void Rebuild(string repoRoot, bool fullRebuild = false, CancellationToken cancellationToken = default);

    /// <summary>Returns every indexed project.</summary>
    IReadOnlyList<ProjectIndex> GetProjects();

    /// <summary>Returns every indexed source file across all projects.</summary>
    IReadOnlyList<SourceFileIndex> GetFiles();

    /// <summary>
    /// Returns all types whose <see cref="TypeInfo.Name"/> contains <paramref name="name"/>
    /// (case-insensitive), optionally limited to one <paramref name="kind"/>.
    /// </summary>
    IReadOnlyList<TypeInfo> SearchTypes(string name, SymbolKind? kind = null);

    /// <summary>
    /// Returns all members whose <see cref="MemberInfo.Name"/> contains <paramref name="name"/>
    /// (case-insensitive), optionally limited to one <paramref name="kind"/>.
    /// </summary>
    IReadOnlyList<MemberInfo> SearchMembers(string name, SymbolKind? kind = null);

    /// <summary>
    /// Returns all source files whose <see cref="SourceFileIndex.FullPath"/> contains
    /// <paramref name="pathFragment"/> (case-insensitive).
    /// </summary>
    IReadOnlyList<SourceFileIndex> SearchFiles(string pathFragment);
}
