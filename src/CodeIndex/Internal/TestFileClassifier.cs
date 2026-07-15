namespace CodeIndex.Internal;

/// <summary>
/// Classifies test source by path/name so reference output can report a REAL production-usage count separate from
/// test usages. High-precision, repo-agnostic heuristics — NOT authoritative. A file is test code when ANY holds:
/// (a) the project's last name segment is Tests/Test (the .NET convention — test code lives in a *.Tests project);
/// (b) the filename ends with Tests.cs (chosen over Test.cs to avoid false hits like Latest.cs); (c) a path segment
/// is test/tests.
/// </summary>
internal static class TestFileClassifier
{
    private static readonly string[] ProjectSuffixes = ["Tests", "Test"];
    private static readonly string[] FileSuffixes = ["Tests.cs"];
    private static readonly string[] DirSegments = ["test", "tests"];

    public static bool IsTest(string sourceFilePath, string? projectName)
    {
        if (IsTestProject(projectName))
        {
            return true;
        }

        string name = Path.GetFileName(sourceFilePath);
        foreach (string suffix in FileSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Split on both separators so the heuristic is OS-agnostic (indexed paths may use either).
        string[] segments = sourceFilePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            foreach (string dir in DirSegments)
            {
                if (segment.Equals(dir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTestProject(string? projectName)
    {
        if (string.IsNullOrEmpty(projectName))
        {
            return false;
        }

        int dot = projectName.LastIndexOf('.');
        string lastSegment = dot >= 0 ? projectName[(dot + 1)..] : projectName;
        foreach (string suffix in ProjectSuffixes)
        {
            if (lastSegment.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
