using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Heuristic C# call hierarchy over Roslyn syntax trees (no semantic model — same name-based limitation as
/// find_references: an invocation of `X` matches any method named `X`, no overload/receiver-type resolution).
/// Its value over find_references: <b>callers</b> keeps only real INVOCATIONS (not declarations/comments/strings)
/// and reports the ENCLOSING member (the actual caller), and <b>callees</b> lists what a method invokes. Parses on
/// demand (parallel; generated files excluded; project-scopable). C# only — TS is deferred.
/// </summary>
internal static class CallHierarchy
{
    /// <summary>Who invokes <paramref name="method"/> — grouped by file, each hit labelled with its enclosing member.</summary>
    public static string Callers(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string method, string? project, int max, int perFileCap)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "call_hierarchy: provide a method name.";
        }

        List<SourceFileIndex> candidates = files
            .Where(f => f.Language == Language.CSharp)
            .Where(f => project is null || f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
            .ToList();

        FileReader reader = new(fileSystem);
        ConcurrentBag<CallerHit> bag = new();
        Parallel.ForEach(candidates, f =>
        {
            FileReader.ReadResult read = reader.ReadFileLines(f.SourceFilePath);
            if (!read.Success)
            {
                return;
            }
            string[] lines = read.Lines!;
            CompilationUnitSyntax root;
            try { root = CSharpSyntaxTree.ParseText(string.Join("\n", lines), path: f.SourceFilePath).GetCompilationUnitRoot(); }
            catch { return; }

            foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (InvokedName(inv) != method)
                {
                    continue;
                }
                int line0 = inv.GetLocation().GetLineSpan().StartLinePosition.Line;
                string snippet = line0 >= 0 && line0 < lines.Length ? Output.ClipLine(lines[line0].Trim(), max: 140) : inv.ToString();
                bag.Add(new CallerHit(f.ProjectName, f.SourceFilePath, f.FileName, line0 + 1, EnclosingMember(inv), snippet));
            }
        });

        return RenderCallers(method, bag, project, max, perFileCap);
    }

    /// <summary>What <paramref name="method"/> invokes — distinct callee names with counts, from the method's body.
    /// Only the file(s) DEFINING the method are parsed (cheap), located via the index.</summary>
    public static string Callees(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string method, string? project, int max)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "call_hierarchy: provide a method name.";
        }

        List<SourceFileIndex> definers = files
            .Where(f => f.Language == Language.CSharp)
            .Where(f => project is null || f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
            .Where(f => f.Types.Any(t => t.Members.Any(m => m.Name == method && m.Kind is Models.SymbolKind.Method)))
            .ToList();

        if (definers.Count == 0)
        {
            return $"call_hierarchy(callees): no C# method named '{method}' is indexed{Scope(project)}.";
        }

        FileReader reader = new(fileSystem);
        ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);
        ConcurrentBag<string> declFiles = new();
        Parallel.ForEach(definers, f =>
        {
            FileReader.ReadResult read = reader.ReadFileLines(f.SourceFilePath);
            if (!read.Success)
            {
                return;
            }
            CompilationUnitSyntax root;
            try { root = CSharpSyntaxTree.ParseText(string.Join("\n", read.Lines!), path: f.SourceFilePath).GetCompilationUnitRoot(); }
            catch { return; }

            bool found = false;
            foreach (MethodDeclarationSyntax decl in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == method))
            {
                found = true;
                foreach (InvocationExpressionSyntax inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string? callee = InvokedName(inv);
                    if (callee is not null && callee != method) // skip self-recursion noise in the summary
                    {
                        counts.AddOrUpdate(callee, 1, (_, v) => v + 1);
                    }
                }
            }
            if (found)
            {
                declFiles.Add($"{f.FileName} ({f.ProjectName})");
            }
        });

        StringBuilder sb = new();
        sb.AppendLine($"{method} calls ({counts.Count} distinct callees), defined in: {string.Join(", ", declFiles.Distinct().OrderBy(x => x, StringComparer.Ordinal))}");
        sb.AppendLine();
        if (counts.IsEmpty)
        {
            sb.AppendLine("  (no invocations found in the method body)");
            return sb.ToString();
        }
        foreach (KeyValuePair<string, int> kv in counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(max))
        {
            sb.AppendLine($"  {kv.Key} (×{kv.Value})");
        }
        if (counts.Count > max)
        {
            sb.AppendLine($"  … {counts.Count - max} more (raise max).");
        }
        return sb.ToString();
    }

    private readonly record struct CallerHit(string Project, string Path, string FileName, int Line, string Caller, string Snippet);

    private static string RenderCallers(string method, ConcurrentBag<CallerHit> bag, string? project, int max, int perFileCap)
    {
        int total = bag.Count;
        if (total == 0)
        {
            return $"call_hierarchy(callers): no invocations of '{method}' found{Scope(project)}. (Note: name-based + C#-only; a same-named method elsewhere won't disambiguate.)";
        }

        List<(string FileName, string Project, int Count, List<CallerHit> Hits)> byFile = bag
            .GroupBy(h => h.Path)
            .Select(g => (g.First().FileName, g.First().Project, Count: g.Count(), Hits: g.OrderBy(h => h.Line).ToList()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        StringBuilder sb = new();
        sb.AppendLine($"{method}: {total} call sites in {byFile.Count} files{Scope(project)}");
        sb.AppendLine();

        int emitted = 0, filesShown = 0;
        foreach ((string fileName, string proj, int count, List<CallerHit> hits) in byFile)
        {
            if (emitted >= max)
            {
                break;
            }
            filesShown++;
            sb.AppendLine($"== {fileName} ({proj}) — {count} ==");
            int perFile = 0;
            foreach (CallerHit h in hits)
            {
                if (emitted >= max || perFile >= perFileCap)
                {
                    break;
                }
                sb.AppendLine($"  {h.Line}| {h.Caller}: {h.Snippet}");
                emitted++;
                perFile++;
            }
        }

        if (filesShown < byFile.Count || emitted < total)
        {
            sb.AppendLine($"\n… showing {emitted} of {total} call sites across {filesShown}/{byFile.Count} files (raise max, or scope with project=).");
        }
        return sb.ToString();
    }

    // The simple name being invoked: X(), this.X(), a.b.X(), a?.X(), X<T>(), a.X<T>().
    private static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        _ => null,
    };

    // Nearest enclosing named member — the actual caller.
    private static string EnclosingMember(SyntaxNode node)
    {
        foreach (SyntaxNode a in node.Ancestors())
        {
            switch (a)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case LocalFunctionStatementSyntax lf: return lf.Identifier.Text;
                case ConstructorDeclarationSyntax c: return $"{c.Identifier.Text}(ctor)";
                case PropertyDeclarationSyntax p: return $"{p.Identifier.Text}(property)";
                case AccessorDeclarationSyntax acc when acc.Parent?.Parent is PropertyDeclarationSyntax pp: return $"{pp.Identifier.Text}.{acc.Keyword.Text}";
            }
        }
        return "(file-level)";
    }

    private static string Scope(string? project) => project is null ? string.Empty : $" in project '{project}'";
}
