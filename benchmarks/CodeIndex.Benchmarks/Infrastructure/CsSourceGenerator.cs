using System.Text;

namespace CodeIndex.Benchmarks.Infrastructure;

/// <summary>
/// Generates realistic-looking C# source files for benchmark input.
///
/// Distribution is modelled on a real mid-size .NET codebase:
///   - ~70 % of files have 1 class, ~20 % have 2-4, ~10 % are large (5-10 types)
///   - Methods average 4 per type, properties average 3 per type
///   - Hierarchy: ~30 % of classes inherit one base type or implement one interface
///
/// The generator is deterministic given the same seed so benchmark runs
/// are reproducible.
/// </summary>
public static class CsSourceGenerator
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a synthetic solution file (.sln format) that references
    /// <paramref name="projectPaths"/>.
    /// </summary>
    public static string GenerateSln(IEnumerable<string> projectPaths, string slnDir)
    {
        StringBuilder sb = new();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        int i = 0;
        foreach (string projPath in projectPaths)
        {
            string rel = Path.GetRelativePath(slnDir, projPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(projPath);
            sb.AppendLine(
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = " +
                $"\"{name}\", \"{rel}\", \"{{{Guid(i++)}}}\"\r\nEndProject");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Generates <paramref name="fileCount"/> .cs source files and registers
    /// them (plus a matching .sln and .csproj) in <paramref name="fs"/>.
    ///
    /// Returns the absolute path to the .sln file.
    /// </summary>
    public static string Populate(
        InMemoryFileSystem fs,
        string repoRoot,
        int fileCount,
        int seed = 42)
    {
        Random rng = new(seed);

        string projDir = Path.Combine(repoRoot, "App");
        string projPath = Path.Combine(projDir, "App.csproj");
        fs.AddFile(projPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        for (int i = 0; i < fileCount; i++)
        {
            string filePath = Path.Combine(projDir, $"File{i:D4}.cs");
            string source = GenerateCsFile(rng, $"File{i:D4}", i);
            fs.AddFile(filePath, source);
        }

        string slnPath = Path.Combine(repoRoot, "App.sln");
        fs.AddFile(slnPath, GenerateSln([projPath], repoRoot));

        return slnPath;
    }

    /// <summary>
    /// Generates a single realistic C# source file.
    /// </summary>
    public static string GenerateCsFile(Random rng, string fileId, int index)
    {
        int typeCount = TypeCount(rng);
        StringBuilder sb = new();
        sb.AppendLine($"namespace BenchApp.Module{index % 20};");
        sb.AppendLine();

        for (int t = 0; t < typeCount; t++)
        {
            AppendType(sb, rng, fileId, t);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Internal generators ───────────────────────────────────────────────────

    private static int TypeCount(Random rng)
    {
        double roll = rng.NextDouble();
        if (roll < 0.70) return 1;
        if (roll < 0.90) return rng.Next(2, 5);
        return rng.Next(5, 11);
    }

    private static void AppendType(StringBuilder sb, Random rng, string fileId, int typeIndex)
    {
        string typeKind = rng.NextDouble() switch
        {
            < 0.60 => "class",
            < 0.75 => "sealed class",
            < 0.85 => "interface",
            < 0.92 => "abstract class",
            _ => "record",
        };

        string typeName = $"{fileId}Type{typeIndex}";
        bool hasBase = typeKind is "class" or "sealed class" && rng.NextDouble() < 0.30;
        string baseClause = hasBase ? $" : Base{typeName}" : string.Empty;

        string modifier = typeKind.StartsWith("interface", StringComparison.Ordinal)
            ? "public"
            : PickModifier(rng);

        sb.AppendLine($"{modifier} {typeKind} {typeName}{baseClause}");
        sb.AppendLine("{");

        int propCount = rng.Next(0, 6);
        int methodCount = rng.Next(0, 9);

        for (int p = 0; p < propCount; p++)
        {
            AppendProperty(sb, rng, typeName, p);
        }

        for (int m = 0; m < methodCount; m++)
        {
            AppendMethod(sb, rng, typeName, m);
        }

        sb.AppendLine("}");
    }

    private static void AppendProperty(StringBuilder sb, Random rng, string typeName, int idx)
    {
        string[] types = ["string", "int", "bool", "DateTime", "IReadOnlyList<string>", "double"];
        string propType = types[rng.Next(types.Length)];
        sb.AppendLine($"    public {propType} Prop{idx} {{ get; set; }}");
    }

    private static void AppendMethod(StringBuilder sb, Random rng, string typeName, int idx)
    {
        string[] returnTypes = ["void", "string", "int", "bool", "Task", "IEnumerable<string>"];
        string[] paramTypes = ["string", "int", "bool", "CancellationToken"];
        string ret = returnTypes[rng.Next(returnTypes.Length)];
        bool hasParam = rng.NextDouble() < 0.60;
        string param = hasParam
            ? $"{paramTypes[rng.Next(paramTypes.Length)]} p{idx}"
            : string.Empty;
        string asyncMod = ret == "Task" ? "async " : string.Empty;

        sb.AppendLine($"    public {asyncMod}{ret} Method{idx}({param})");
        sb.AppendLine("    {");
        sb.AppendLine(ret == "void" || ret == "Task" ? "    }" : $"        return default!;\r\n    }}");
    }

    private static string PickModifier(Random rng) => rng.NextDouble() switch
    {
        < 0.70 => "public",
        < 0.85 => "internal",
        _ => "public sealed",
    };

    private static string Guid(int seed) =>
        $"{seed:X8}-0000-0000-0000-{seed:X12}".PadRight(36, '0')[..36];
}
