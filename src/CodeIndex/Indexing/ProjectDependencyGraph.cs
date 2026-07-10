using System.Text.RegularExpressions;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Indexing;

/// <summary>
/// Forward + reverse project-reference graph, built ONCE from the snapshot's projects instead of re-reading every
/// .csproj on each get_project_dependencies call. Dependents are matched by EXACT project name (via
/// Path.GetFileNameWithoutExtension of each ProjectReference Include), so "MyApp.Core" is not reported as a
/// dependent of everything referencing "MyApp.Core.Services". Derived from the never-serialized snapshot
/// (ProjectIndex.ProjectDirPath), so it needs no cache-schema change.
/// </summary>
internal sealed partial class ProjectDependencyGraph
{
    [GeneratedRegex(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectRefPattern();

    private static readonly IReadOnlyList<string> Empty = [];

    private readonly Dictionary<string, IReadOnlyList<string>> _references;
    private readonly Dictionary<string, IReadOnlyList<string>> _dependents;

    private ProjectDependencyGraph(
        Dictionary<string, IReadOnlyList<string>> references,
        Dictionary<string, IReadOnlyList<string>> dependents)
    {
        _references = references;
        _dependents = dependents;
    }

    public IReadOnlyList<string> GetReferences(string project) => _references.GetValueOrDefault(project, Empty);

    public IReadOnlyList<string> GetDependents(string project) => _dependents.GetValueOrDefault(project, Empty);

    public static ProjectDependencyGraph Build(IReadOnlyDictionary<string, ProjectIndex> projects, IFileSystem fileSystem)
    {
        Dictionary<string, IReadOnlyList<string>> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ProjectIndex> kv in projects)
        {
            references[kv.Key] = ParseReferences(kv.Value, fileSystem);
        }

        // Reverse map by EXACT referenced-project name.
        Dictionary<string, SortedSet<string>> dependentsSet = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IReadOnlyList<string>> kv in references)
        {
            foreach (string referenced in kv.Value)
            {
                if (!dependentsSet.TryGetValue(referenced, out SortedSet<string>? set))
                {
                    set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    dependentsSet[referenced] = set;
                }

                set.Add(kv.Key);
            }
        }

        Dictionary<string, IReadOnlyList<string>> dependents = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, SortedSet<string>> kv in dependentsSet)
        {
            dependents[kv.Key] = [.. kv.Value];
        }

        return new ProjectDependencyGraph(references, dependents);
    }

    private static IReadOnlyList<string> ParseReferences(ProjectIndex project, IFileSystem fileSystem)
    {
        string? csproj = FindCsproj(project, fileSystem);
        if (csproj is null)
        {
            return Empty;
        }

        string content;
        try
        {
            content = fileSystem.ReadAllText(csproj);
        }
        catch (Exception)
        {
            return Empty;
        }

        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ProjectRefPattern().Matches(content))
        {
            names.Add(Path.GetFileNameWithoutExtension(m.Groups[1].Value));
        }

        return [.. names];
    }

    private static string? FindCsproj(ProjectIndex project, IFileSystem fileSystem)
    {
        try
        {
            // ProjectIndex.Name is the csproj filename without extension (SolutionScanner), so try the direct path first.
            string direct = Path.Combine(project.ProjectDirPath, project.Name + ".csproj");
            if (fileSystem.FileExists(direct))
            {
                return direct;
            }

            return fileSystem.EnumerateFiles(project.ProjectDirPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
