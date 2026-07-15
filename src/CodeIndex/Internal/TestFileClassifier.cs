namespace CodeIndex.Internal;

/// <summary>
/// Classifies test source by path/name so reference output can report a REAL production-usage count separate from
/// test usages. High-precision, repo-agnostic heuristics — NOT authoritative. A file is test code when ANY holds:
/// (a) the project's last name segment ends with Tests (UnitTests/IntegrationTests/…) or equals Test — the .NET
/// convention that test code lives in a *.Tests project; (b) the filename ends with Tests.cs (chosen over Test.cs
/// to avoid false hits like Latest.cs); (c) a directory segment BELOW the project root is test/tests. The dir check
/// is scoped under the project dir on purpose — a repo checked out under an ancestor named "test" must not mark
/// every file as test.
/// </summary>
internal static class TestFileClassifier
{
    private static readonly string[] FileSuffixes = ["Tests.cs"];
    private static readonly string[] DirSegments = ["test", "tests"];

    public static bool IsTest(string sourceFilePath, string? projectName, string? projectDir = null)
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

        // Only inspect segments BELOW the project directory — never ambient/ancestor dirs. Without a known project
        // dir we skip this axis entirely rather than scan the absolute path (which could match a machine folder).
        if (projectDir is not null && sourceFilePath.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
        {
            string relative = sourceFilePath[projectDir.Length..];
            foreach (string segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string dir in DirSegments)
                {
                    if (segment.Equals(dir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
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

        // EndsWith("Tests") catches Tests/UnitTests/IntegrationTests/FunctionalTests/E2ETests; Equals("Test")
        // catches the singular. Bare EndsWith("Test") is avoided so a name like "Greatest" isn't a false positive.
        return lastSegment.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || lastSegment.Equals("Test", StringComparison.OrdinalIgnoreCase);
    }
}
