namespace CodeIndex.Models;

/// <summary>
/// Forward (<paramref name="References"/>) and reverse (<paramref name="Dependents"/>) project references
/// for <c>get_project_dependencies</c>, from the precomputed exact-name dependency graph.
/// </summary>
public sealed record ProjectDependencyInfo(
    string Name,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Dependents);
