using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using TreeSitter;
using TSLanguage = TreeSitter.Language;

namespace CodeIndex.Internal;

/// <summary>
/// Structural (AST) search over TypeScript/TSX via tree-sitter — the TS twin of <see cref="StructuralSearch"/>.
/// Its curated vocabulary encodes this repo's own TS house rules (inline styles, @ts-*-ignore, default exports,
/// className string-interpolation) plus generic smells (console.*, `any`, empty catch), catching things grep can't
/// match reliably. Parses on demand in parallel over indexed .ts/.tsx files (project-scopable; generated excluded).
/// </summary>
internal static partial class TsStructuralSearch
{
    private static readonly Lazy<TSLanguage> TsLang = new(() => new TSLanguage("tree-sitter-typescript", "tree_sitter_typescript"));
    private static readonly Lazy<TSLanguage> TsxLang = new(() => new TSLanguage("tree-sitter-tsx", "tree_sitter_tsx"));

    [GeneratedRegex(@"^\s*export\s+default\b")] private static partial Regex DefaultExport();

    private static readonly Dictionary<string, (string Desc, Func<Node, bool> Match)> Patterns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["inline-style"] = ("JSX `style={{…}}` inline styles (TS9: use *.module.scss instead)",
                n => n.Type == "jsx_attribute" && AttrName(n) == "style" && HasChildKind(AttrValue(n), "object")),

            ["classname-interp"] = ("`className={`… ${x}`}` string interpolation (use the classnames package)",
                n => n.Type == "jsx_attribute" && AttrName(n) == "className" && HasChildKind(AttrValue(n), "template_string")),

            ["ts-ignore"] = ("`// @ts-ignore` / `@ts-nocheck` / `@ts-strict-ignore` suppressions (TS8: remove when possible)",
                n => n.Type == "comment" && IsTsIgnore(Text(n))),

            ["default-export"] = ("`export default …` (house rule: named exports only)",
                n => n.Type == "export_statement" && DefaultExport().IsMatch(Text(n))),

            ["console-log"] = ("`console.*` calls (console.log/warn/error left in shipped code)",
                IsConsoleCall),

            ["any-type"] = ("`any` type usage (`: any`, `as any`, `<any>`) — defeats type safety",
                n => n.Type == "predefined_type" && Text(n) == "any"),
            // NOTE: no `empty-catch` here — that name belongs to the C# vocabulary (dispatched first). TS empty
            // catches are lower-value than the house rules above and the collision would silently shadow this one.
        };

    public static bool HasPattern(string pattern) => !string.IsNullOrWhiteSpace(pattern) && Patterns.ContainsKey(pattern.Trim());

    public static string PatternHelp() =>
        string.Join("\n", Patterns.Select(p => $"  {p.Key} — {p.Value.Desc}"));

    public static string Search(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string pattern, string? project, int max, int perFileCap)
    {
        (string Desc, Func<Node, bool> Match) entry = Patterns[pattern.Trim()];

        List<SourceFileIndex> candidates = files
            .Where(f => f.Language == CodeIndex.Models.Language.TypeScript)   // .ts/.tsx only (SCSS has no AST here)
            .Where(f => project is null || f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
            .ToList();

        ConcurrentBag<Hit> bag = new();
        Parallel.ForEach(candidates, f =>
        {
            string source;
            try
            {
                source = fileSystem.ReadAllText(f.SourceFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            string[] lines = source.Replace("\r\n", "\n").Split('\n');

            TSLanguage lang = f.SourceFilePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? TsxLang.Value : TsLang.Value;
            using Parser parser = new(lang);
            using Tree? tree = parser.Parse(source);
            if (tree is null)
            {
                return;
            }

            foreach (Node node in Descendants(tree.RootNode))
            {
                if (!entry.Match(node))
                {
                    continue;
                }
                int line0 = (int)node.StartPosition.Row;
                string snippet = line0 >= 0 && line0 < lines.Length
                    ? Output.ClipLine(lines[line0].Trim(), max: 160)
                    : Output.ClipLine(Text(node), max: 160);
                bag.Add(new Hit(f.ProjectName, f.SourceFilePath, f.FileName, line0 + 1, snippet));
            }
        });

        return Render(pattern, entry.Desc, bag, project, max, perFileCap);
    }

    private readonly record struct Hit(string Project, string Path, string FileName, int Line, string Snippet);

    private static string Render(string pattern, string desc, ConcurrentBag<Hit> bag, string? project, int max, int perFileCap)
    {
        int total = bag.Count;
        string scope = project is null ? string.Empty : $" in project '{project}'";
        if (total == 0)
        {
            return $"No '{pattern}' matches{scope}. ({desc})";
        }

        List<(string FileName, string Project, int Count, List<Hit> Hits)> byFile = bag
            .GroupBy(h => h.Path)
            .Select(g => (g.First().FileName, g.First().Project, Count: g.Count(), Hits: g.OrderBy(h => h.Line).ToList()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        StringBuilder sb = new();
        sb.AppendLine($"{pattern}: {total} matches in {byFile.Count} files{scope} — {desc}");
        sb.AppendLine();

        int emitted = 0, filesShown = 0;
        foreach ((string fileName, string proj, int count, List<Hit> hits) in byFile)
        {
            if (emitted >= max)
            {
                break;
            }
            filesShown++;
            sb.AppendLine($"== {fileName} ({proj}) — {count} ==");
            int perFile = 0;
            foreach (Hit h in hits)
            {
                if (emitted >= max || perFile >= perFileCap)
                {
                    break;
                }
                sb.AppendLine($"  {h.Line}| {h.Snippet}");
                emitted++;
                perFile++;
            }
        }

        if (filesShown < byFile.Count || emitted < total)
        {
            sb.AppendLine($"\n… showing {emitted} of {total} matches across {filesShown}/{byFile.Count} files (raise max, or scope with project=).");
        }
        return sb.ToString();
    }

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

    private static bool IsConsoleCall(Node n)
    {
        if (n.Type != "call_expression")
        {
            return false;
        }
        Node? callee = n.NamedChildren.FirstOrDefault();
        if (callee is null || callee.Type != "member_expression")
        {
            return false;
        }
        Node? obj = callee.NamedChildren.FirstOrDefault();
        return obj is not null && obj.Type == "identifier" && Text(obj) == "console";
    }

    private static string? AttrName(Node jsxAttr) =>
        jsxAttr.NamedChildren.FirstOrDefault(c => c.Type == "property_identifier") is { } p ? Text(p) : null;

    private static Node? AttrValue(Node jsxAttr) =>
        jsxAttr.NamedChildren.FirstOrDefault(c => c.Type == "jsx_expression");

    private static bool HasChildKind(Node? n, string kind) =>
        n is not null && n.NamedChildren.Any(c => c.Type == kind);

    private static bool IsTsIgnore(string text) =>
        text.Contains("@ts-ignore", StringComparison.Ordinal)
        || text.Contains("@ts-nocheck", StringComparison.Ordinal)
        || text.Contains("@ts-strict-ignore", StringComparison.Ordinal);

    private static string Text(Node n)
    {
        try
        {
            return n.Text ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
