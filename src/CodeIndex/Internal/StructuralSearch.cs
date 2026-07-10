using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Structural (AST) search over C# — finds code by SYNTAX SHAPE, which text/regex search can't do reliably
/// (balanced empty blocks, async-void methods, sync-over-async calls). Rather than a full ast-grep pattern DSL
/// (a product in its own right), this exposes a curated vocabulary of high-value named patterns implemented as
/// Roslyn walkers. Parses on demand in parallel over the indexed C# files (project-scopable to bound cost);
/// generated files are excluded. TypeScript/tree-sitter structural search is deferred to a later slice.
/// </summary>
internal static class StructuralSearch
{
    private static readonly Dictionary<string, (string Desc, Func<CompilationUnitSyntax, IEnumerable<SyntaxNode>> Match)> Patterns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["empty-catch"] = ("catch blocks that swallow the exception (empty body)",
                root => root.DescendantNodes().OfType<CatchClauseSyntax>().Where(c => c.Block is { Statements.Count: 0 })),

            ["catch-all"] = ("broad catches — `catch {}` or `catch (Exception)`",
                root => root.DescendantNodes().OfType<CatchClauseSyntax>().Where(IsCatchAll)),

            ["async-void"] = ("`async void` methods (unobservable exceptions / fire-and-forget footgun)",
                root => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsAsyncVoid)),

            ["blocking-async"] = ("sync-over-async blocking calls: `.Wait()` / `.GetAwaiter().GetResult()` (deadlock risk). NOTE: `.Task.Result` is intentionally NOT matched — syntactically it is indistinguishable from any property named Result without a semantic model, and flags hundreds of false positives (WPF `e.Result`, MVC `context.Result`, …).",
                root => BlockingAsync(root)),

            ["public-field"] = ("public mutable fields (non-const, non-readonly — encapsulation smell)",
                root => root.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(IsPublicMutableField)),

            ["throw-in-finally"] = ("`throw` inside a `finally` block (masks the in-flight exception)",
                root => root.DescendantNodes().OfType<ThrowStatementSyntax>().Where(t => t.Ancestors().OfType<FinallyClauseSyntax>().Any())),

            ["not-implemented"] = ("`throw new NotImplementedException(...)` — unfinished stubs",
                root => root.DescendantNodes().OfType<ThrowStatementSyntax>().Where(IsNotImplemented)),
        };

    public static bool HasPattern(string pattern) => !string.IsNullOrWhiteSpace(pattern) && Patterns.ContainsKey(pattern.Trim());

    public static string PatternHelp() =>
        string.Join("\n", Patterns.Select(p => $"  {p.Key} — {p.Value.Desc}"));

    public static string Search(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string pattern, string? project, int max, int perFileCap)
    {
        if (string.IsNullOrWhiteSpace(pattern) || !Patterns.TryGetValue(pattern.Trim(), out (string Desc, Func<CompilationUnitSyntax, IEnumerable<SyntaxNode>> Match) entry))
        {
            return $"Unknown structural pattern '{pattern}'.\n{PatternHelp()}";
        }

        List<SourceFileIndex> candidates = files
            .Where(f => f.Language == Language.CSharp)
            .Where(f => project is null || f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
            .ToList();

        FileReader reader = new(fileSystem);
        ConcurrentBag<Hit> bag = new();
        Parallel.ForEach(candidates, f =>
        {
            FileReader.ReadResult read = reader.ReadFileLines(f.SourceFilePath);
            if (!read.Success)
            {
                return;
            }

            string[] lines = read.Lines!;
            CompilationUnitSyntax root;
            try
            {
                root = CSharpSyntaxTree.ParseText(string.Join("\n", lines), path: f.SourceFilePath).GetCompilationUnitRoot();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return; // unparseable — skip, keep scanning the rest
            }

            foreach (SyntaxNode node in entry.Match(root))
            {
                int line0 = SnippetLine(node);
                string snippet = line0 >= 0 && line0 < lines.Length
                    ? Output.ClipLine(lines[line0].Trim(), max: 160)
                    : Output.ClipLine(node.ToString(), max: 160);
                bag.Add(new Hit(f.ProjectName, f.SourceFilePath, f.FileName, line0 + 1, snippet));
            }
        });

        return Render(pattern, entry.Desc, bag, project, max, perFileCap);
    }

    // Report the line of the meaningful token, not the node span start — a declaration's span begins at its
    // attribute lists, so `[Theory]`-style attributes would otherwise be shown instead of the signature.
    private static int SnippetLine(SyntaxNode node) => (node switch
    {
        MethodDeclarationSyntax m => m.Identifier.GetLocation(),
        FieldDeclarationSyntax f => f.Declaration.GetLocation(),
        _ => node.GetLocation(),
    }).GetLineSpan().StartLinePosition.Line;

    private readonly record struct Hit(string Project, string Path, string FileName, int Line, string Snippet);

    private static string Render(string pattern, string desc, ConcurrentBag<Hit> bag, string? project, int max, int perFileCap)
    {
        int total = bag.Count;
        string scope = project is null ? string.Empty : $" in project '{project}'";
        if (total == 0)
        {
            return $"No '{pattern}' matches{scope}. ({desc})";
        }

        List<(string FileName, string Project, int Count, List<Hit> Samples)> byFile = bag
            .GroupBy(h => h.Path)
            .Select(g => (g.First().FileName, g.First().Project, Count: g.Count(), Samples: g.OrderBy(h => h.Line).ToList()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        StringBuilder sb = new();
        sb.AppendLine($"{pattern}: {total} matches in {byFile.Count} files{scope} — {desc}");
        sb.AppendLine();

        int emitted = 0, filesShown = 0;
        foreach ((string fileName, string proj, int count, List<Hit> samples) in byFile)
        {
            if (emitted >= max)
            {
                break;
            }

            filesShown++;
            sb.AppendLine($"== {fileName} ({proj}) — {count} ==");
            int perFile = 0;
            foreach (Hit h in samples)
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

    private static bool IsCatchAll(CatchClauseSyntax c)
    {
        if (c.Declaration is null)
        {
            return true; // `catch { }`
        }

        string type = c.Declaration.Type.ToString();
        return type is "Exception" or "System.Exception";
    }

    private static bool IsAsyncVoid(MethodDeclarationSyntax m) =>
        m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword))
        && m.ReturnType is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static IEnumerable<SyntaxNode> BlockingAsync(CompilationUnitSyntax root) =>
        // `.Wait()` and `.GetAwaiter().GetResult()` are method CALLS that are almost always Task-blocking — precise.
        // `.Result` (a property) is deliberately excluded: without a semantic model it can't be told apart from any
        // Result property and produced a false-positive flood on the real repo.
        root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax callee
                && callee.Name.Identifier.Text is "Wait" or "GetResult");

    private static bool IsPublicMutableField(FieldDeclarationSyntax f)
    {
        bool isPublic = false, isConstOrReadonly = false;
        foreach (SyntaxToken mod in f.Modifiers)
        {
            if (mod.IsKind(SyntaxKind.PublicKeyword))
            {
                isPublic = true;
            }
            else if (mod.IsKind(SyntaxKind.ConstKeyword) || mod.IsKind(SyntaxKind.ReadOnlyKeyword))
            {
                isConstOrReadonly = true;
            }
        }

        return isPublic && !isConstOrReadonly;
    }

    private static bool IsNotImplemented(ThrowStatementSyntax t) =>
        t.Expression is ObjectCreationExpressionSyntax oce
        && oce.Type.ToString().EndsWith("NotImplementedException", StringComparison.Ordinal);
}
