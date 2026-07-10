using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using TypeInfo = CodeIndex.Models.TypeInfo;

namespace CodeIndex.Parsing;

/// <summary>
/// Lightweight hand-rolled SCSS tokenizer (no tree-sitter SCSS grammar is bundled and the corpus is regular).
/// Extracts the symbols agents actually look up — class selectors (.x, which become typed JS imports in
/// *.module.scss), @mixin, @function, $variables, %placeholders — as TypeInfo entries in the shared
/// SourceFileIndex shape, so search_symbol / find_references work over SCSS unchanged. Comment-aware; first
/// occurrence of each (name, kind) wins.
///
/// Stateless and thread-safe apart from the injected <see cref="IFileSystem"/> reference, so the store can
/// call <see cref="Parse"/> concurrently from PLINQ.
/// </summary>
public sealed partial class ScssParser
{
    [GeneratedRegex(@"@mixin\s+([A-Za-z_][\w-]*)")] private static partial Regex MixinPattern();
    [GeneratedRegex(@"@function\s+([A-Za-z_][\w-]*)")] private static partial Regex FunctionPattern();
    [GeneratedRegex(@"(?:^|;|\{)\s*\$([A-Za-z_][\w-]*)\s*:")] private static partial Regex VariablePattern();
    [GeneratedRegex(@"%([A-Za-z_][\w-]*)")] private static partial Regex PlaceholderPattern();
    [GeneratedRegex(@"\.([A-Za-z_][\w-]*)")] private static partial Regex ClassPattern();

    private readonly IFileSystem _fileSystem;

    public ScssParser(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public SourceFileIndex? Parse(string filePath, string projectName)
    {
        string[] lines;
        try
        {
            lines = _fileSystem.ReadAllLines(filePath);
        }
        catch
        {
            return null;
        }

        List<TypeInfo> types = new();
        HashSet<(string, SymbolKind)> seen = new();
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string clean = StripComments(lines[i], ref inBlockComment);
            if (clean.Length == 0)
            {
                continue;
            }

            int line = i + 1;

            foreach (Match m in MixinPattern().Matches(clean))
            {
                Add(types, seen, m.Groups[1].Value, SymbolKind.ScssMixin, "@mixin", line);
            }

            foreach (Match m in FunctionPattern().Matches(clean))
            {
                Add(types, seen, m.Groups[1].Value, SymbolKind.ScssFunction, "@function", line);
            }

            foreach (Match m in VariablePattern().Matches(clean))
            {
                Add(types, seen, m.Groups[1].Value, SymbolKind.ScssVariable, "$", line);
            }

            foreach (Match m in PlaceholderPattern().Matches(clean))
            {
                Add(types, seen, m.Groups[1].Value, SymbolKind.ScssPlaceholder, "%", line);
            }

            // Class selectors only on rule-opening lines (contain '{'), from the selector portion, to cut noise.
            int brace = clean.IndexOf('{');
            if (brace >= 0)
            {
                foreach (Match m in ClassPattern().Matches(clean[..brace]))
                {
                    Add(types, seen, m.Groups[1].Value, SymbolKind.ScssSelector, ".", line);
                }
            }
        }

        return new SourceFileIndex
        {
            FileName = Path.GetFileName(filePath),
            SourceFilePath = filePath,
            ProjectName = projectName,
            Namespace = null,
            Types = types,
            Language = Language.Scss,
        };
    }

    private static void Add(List<TypeInfo> types, HashSet<(string, SymbolKind)> seen, string name, SymbolKind kind, string prefix, int line)
    {
        if (name.Length == 0 || !seen.Add((name, kind)))
        {
            return;
        }

        types.Add(new TypeInfo
        {
            Name = name,
            Kind = kind,
            TypeKeyword = prefix,
            StartLine = line,
            LineCount = 1,
            Namespace = null,
            Members = [],
        });
    }

    // Remove /* */ (possibly multi-line) and // line comments, preserving column positions is unnecessary here.
    private static string StripComments(string line, ref bool inBlockComment)
    {
        StringBuilder sb = new(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            // `//` starts a line comment — EXCEPT when it's a URL scheme (`http://`, `//cdn/...`), where the `//`
            // is preceded by `:` or `/`. Guarding on that keeps a trailing `$var`/`@include` on the same physical
            // line from being swallowed by a value like `url(http://…)`. (Class-selector harvest is unaffected — it
            // scans before the `{`.)
            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/'
                && (i == 0 || (line[i - 1] != ':' && line[i - 1] != '/')))
            {
                break;
            }

            sb.Append(line[i]);
        }

        return sb.ToString();
    }
}
