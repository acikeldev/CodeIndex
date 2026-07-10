using System.Collections.Concurrent;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using TreeSitter;
using TSLanguage = TreeSitter.Language;

namespace CodeIndex.Internal;

/// <summary>
/// Heuristic TypeScript/TSX call hierarchy via tree-sitter — the TS twin of <see cref="CallHierarchy"/>. Name-based
/// (no type resolution): callers keeps only real call-expressions of the method, each labelled with its enclosing
/// member (tracked by a downward walk, since tree-sitter nodes don't expose parents); callees lists what a method
/// invokes. Parses on demand (parallel; generated excluded; project-scopable). Mirrors the C# output shape.
/// </summary>
internal static class TsCallHierarchy
{
    private static readonly Lazy<TSLanguage> TsLang = new(() => new TSLanguage("tree-sitter-typescript", "tree_sitter_typescript"));
    private static readonly Lazy<TSLanguage> TsxLang = new(() => new TSLanguage("tree-sitter-tsx", "tree_sitter_tsx"));

    public static string Callers(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string method, string? project, int max, int perFileCap)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "call_hierarchy(TS): provide a method name.";
        }

        List<SourceFileIndex> candidates = Candidates(files, project);
        ConcurrentBag<CallerHit> bag = new();
        Parallel.ForEach(candidates, f =>
        {
            string source;
            try { source = fileSystem.ReadAllText(f.SourceFilePath); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            TSLanguage lang = f.SourceFilePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? TsxLang.Value : TsLang.Value;
            using Parser parser = new(lang);
            using Tree? tree = parser.Parse(source);
            if (tree is null)
            {
                return;
            }
            // Walk WHILE the tree is alive — a tree-sitter Node points into the tree's native memory, so it must
            // never escape this scope (that was a use-after-free that crashed the server under concurrent calls).
            WalkCallers(tree.RootNode, method, "(module-level)", f, lines, bag);
        });

        return RenderCallers(method, bag, project, max, perFileCap);
    }

    public static string Callees(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string method, string? project, int max)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "call_hierarchy(TS): provide a method name.";
        }

        List<SourceFileIndex> definers = Candidates(files, project)
            .Where(f => f.Types.Any(t => t.Members.Any(m => m.Name == method) || t.Name == method))
            .ToList();
        if (definers.Count == 0)
        {
            return $"call_hierarchy(TS callees): no TS method named '{method}' is indexed{Scope(project)}.";
        }

        ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);
        ConcurrentBag<string> declFiles = new();
        Parallel.ForEach(definers, f =>
        {
            string source;
            try { source = fileSystem.ReadAllText(f.SourceFilePath); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            TSLanguage lang = f.SourceFilePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? TsxLang.Value : TsLang.Value;
            using Parser parser = new(lang);
            using Tree? tree = parser.Parse(source);
            if (tree is null)
            {
                return;
            }
            bool found = false;
            foreach (Node decl in FindDefinitions(tree.RootNode, method)) // consumed within the tree's lifetime
            {
                found = true;
                foreach (Node call in Descendants(decl).Where(n => n.Type == "call_expression"))
                {
                    string? callee = InvokedName(call);
                    if (callee is not null && callee != method)
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

    // Downward walk carrying the enclosing named member — avoids needing node.Parent (which tree-sitter doesn't expose).
    private static void WalkCallers(Node node, string method, string enclosing, SourceFileIndex f, string[] lines, ConcurrentBag<CallerHit> bag)
    {
        string childEnclosing = enclosing;
        switch (node.Type)
        {
            case "method_definition":
            case "function_declaration":
            case "generator_function_declaration":
                childEnclosing = NameOf(node) ?? enclosing;
                break;
            case "variable_declarator":
                // `const Foo = () => {…}` / `= function() {…}` — the body's calls belong to Foo.
                if (Field(node, "value") is { } v && v.Type is "arrow_function" or "function" or "function_expression")
                {
                    childEnclosing = NameOf(node) ?? enclosing;
                }
                break;
        }

        if (node.Type == "call_expression" && InvokedName(node) == method)
        {
            int line0 = (int)node.StartPosition.Row;
            string snippet = line0 >= 0 && line0 < lines.Length ? Output.ClipLine(lines[line0].Trim(), max: 140) : Text(node);
            bag.Add(new CallerHit(f.ProjectName, f.SourceFilePath, f.FileName, line0 + 1, enclosing, snippet));
        }

        foreach (Node child in node.NamedChildren)
        {
            WalkCallers(child, method, childEnclosing, f, lines, bag);
        }
    }

    private static IEnumerable<Node> FindDefinitions(Node root, string method)
    {
        foreach (Node n in Descendants(root))
        {
            if ((n.Type is "method_definition" or "function_declaration" or "generator_function_declaration") && NameOf(n) == method)
            {
                yield return n;
            }
            else if (n.Type == "variable_declarator" && NameOf(n) == method
                && Field(n, "value") is { } v && v.Type is "arrow_function" or "function" or "function_expression")
            {
                yield return v;
            }
        }
    }

    private static string RenderCallers(string method, ConcurrentBag<CallerHit> bag, string? project, int max, int perFileCap)
    {
        int total = bag.Count;
        if (total == 0)
        {
            return $"call_hierarchy(TS callers): no invocations of '{method}' found{Scope(project)}. (name-based, TS/TSX only)";
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

    private static List<SourceFileIndex> Candidates(IReadOnlyList<SourceFileIndex> files, string? project) => files
        .Where(f => f.Language == CodeIndex.Models.Language.TypeScript)
        .Where(f => project is null || f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
        .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
        .ToList();

    // The simple name being called: foo(), a.b.foo(), a?.foo(), foo<T>().
    private static string? InvokedName(Node call)
    {
        Node? fn = Field(call, "function") ?? call.NamedChildren.FirstOrDefault();
        if (fn is null)
        {
            return null;
        }
        return fn.Type switch
        {
            "identifier" or "type_identifier" => Text(fn),
            "member_expression" => (Field(fn, "property") ?? fn.NamedChildren.LastOrDefault()) is { } p ? Text(p) : null,
            _ => null,
        };
    }

    private static string? NameOf(Node decl) => Field(decl, "name") is { } n ? Text(n) : null;

    private static IEnumerable<Node> Descendants(Node n)
    {
        foreach (Node c in n.NamedChildren)
        {
            yield return c;
            foreach (Node d in Descendants(c))
            {
                yield return d;
            }
        }
    }

    private static Node? Field(Node n, string field)
    {
        try { return n.GetChildForField(field); }
        catch (ArgumentException) { return null; }
    }

    private static string Text(Node n)
    {
        try { return n.Text ?? string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    private static string Scope(string? project) => project is null ? string.Empty : $" in project '{project}'";
}
